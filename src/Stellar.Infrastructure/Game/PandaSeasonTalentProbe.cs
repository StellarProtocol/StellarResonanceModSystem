using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-based <see cref="IDeepSlumberWriteProbe"/>. Drives the Deep-Slumber (season cultivate /
/// Psychoscope) write verbs — enable a cultivate line, socket/unsocket a phantom factor — through the
/// game's own <b>WorldProxy</b> RPCs directly (<c>zproxy.world_proxy</c>), "Approach A" per
/// <c>docs/driving-game-actions.md</c> § CONFIRMED (spike 2026-08-24): the <c>season_talent</c> VM
/// wrappers (<c>AsyncEnableCultivateLine</c> et al.) SWALLOW their reply (<c>ShowErrorCode(reply)</c>
/// then no <c>return</c>), so driving the VM wrapper gives nothing to act on. The raw RPC instead
/// returns the bare reply code inline (<c>0</c> = ok) — exactly the completion signal this probe
/// needs. This is safe here (unlike the loadout/Role-Plan switch, which must go through its VM
/// wrapper): the VM's only post-RPC work is toasts, and the server pushes the new cultivate state
/// back via <c>CharSerialize</c>, which the existing Deep-Slumber capture
/// (<see cref="PandaLoadoutProbe"/>) already latches.
///
/// <para><b>Ordering:</b> <see cref="DrainPending"/> dispatches at most ONE op per Update tick and
/// will not fire the next queued op until the previous one has completed or timed out — so an
/// enable-then-socket sequence (the reconciler's op order) can never overlap two coroutines against
/// the same Lua VM.</para>
///
/// <para><b>Cancellation never throws.</b> Every completion path (bridge unresolved, dispatch
/// failure, timeout, or a caller's token firing) completes the awaiting <c>Task&lt;int&gt;</c> with a
/// non-zero sentinel code — never <see cref="OperationCanceledException"/> — because
/// <c>DeepSlumberService.ApplySetupAsync</c> detects cancellation via a pre-dispatch poll of the
/// token, not by catching it.</para>
///
/// <para>SOLID partial layout — Lua-bridge reflection + chunk builders + reply parsing live in
/// <c>PandaSeasonTalentProbe.Resolution.cs</c>; gated diagnostic logging in
/// <c>PandaSeasonTalentProbe.Diagnostics.cs</c>.</para>
/// </summary>
internal sealed partial class PandaSeasonTalentProbe : IDeepSlumberWriteProbe
{
    // A coroutine that never resumes (e.g. fired during the post-login cold-start window — see
    // docs/driving-game-actions.md § Service-readiness cold-start hang) fails the op instead of
    // hanging the caller forever. Mirrors PandaLoadoutProbe's CompletionTimeout pattern for its
    // VM-wrapper switch; sized a little larger to cover one cold-start stall with no re-fire (writes
    // are not safe to blindly re-fire the way a read is).
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    // Non-zero sentinel codes this probe completes an op with when it cannot obtain a real game
    // reply. Any non-zero code surfaces to DeepSlumberService as a failed op (Refused/PartialFailure)
    // — never a hang, and never a thrown OperationCanceledException.
    private const int UnavailableCode = -1;
    private const int TimeoutCode = -2;
    private const int CancelledCode = -3;

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    // Dispatches enqueued by EnableLineAsync/SocketFactorAsync/UnsocketFactorAsync (any thread) and
    // drained one-at-a-time on the Update tick — the game's Lua VM is main-thread-only.
    private readonly ConcurrentQueue<PendingOp> _toDispatch = new();
    private PendingOp? _active;
    private int _nextOpId;

    public PandaSeasonTalentProbe(IPluginLog log, IGameTypeRegistry typeRegistry)
    {
        _log = log;
        _typeRegistry = typeRegistry;
    }

    public bool IsResolved => _bridgeResolved;

    public Task<int> EnableLineAsync(int areaId, CancellationToken ct)
        => Enqueue(opId => EnableChunk(areaId, ResultGlobal(opId)), ct);

    public Task<int> SocketFactorAsync(int nodeId, int itemId, CancellationToken ct)
        => Enqueue(opId => SocketChunk(nodeId, itemId, ResultGlobal(opId)), ct);

    // currentItemId is unused by the raw RPC — the server request carries nodeId ONLY (the VM's
    // configId arg is toast-only). Kept in the signature to satisfy IDeepSlumberWriteProbe.
    public Task<int> UnsocketFactorAsync(int nodeId, int currentItemId, CancellationToken ct)
        => Enqueue(opId => UnsocketChunk(nodeId, ResultGlobal(opId)), ct);

    private Task<int> Enqueue(Func<int, string> buildChunk, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(CancelledCode);
        }

        var opId = Interlocked.Increment(ref _nextOpId);
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingOp(opId, buildChunk(opId), tcs);

        if (ct.CanBeCanceled)
        {
            pending.AttachCancellation(ct, this);
        }

        _toDispatch.Enqueue(pending);
        return tcs.Task;
    }

    /// <summary>
    /// Called per Update tick from the Host service tick (the Unity main thread). Resolves the Lua
    /// bridge (throttled), services the in-flight op if any (reads its reply global / checks its
    /// timeout), then dispatches the next queued op once the previous one has cleared.
    /// </summary>
    public void DrainPending()
    {
        TryResolveBridgeIfDue();

        if (!_bridgeResolved)
        {
            FailAllQueued(UnavailableCode);
            return;
        }

        ServiceActive();
        DispatchNextIfFree();
    }

    // Reads the in-flight op's reply global, completing it on a result or a timeout. No-op while an
    // op is still genuinely pending.
    private void ServiceActive()
    {
        var active = _active;
        if (active is null) return;

        var reply = ReadLuaGlobalString(active.Global);
        if (reply is not null)
        {
            _active = null;
            var code = ParseCode(reply);
            DiagResult(active.OpId, code, active.Elapsed.TotalMilliseconds);
            active.Complete(code, this);
            return;
        }

        if (active.Elapsed >= CompletionTimeout)
        {
            _active = null;
            DiagResult(active.OpId, TimeoutCode, active.Elapsed.TotalMilliseconds);
            active.Complete(TimeoutCode, this);
        }
    }

    // Fires the next queued op's chunk once no op is in flight. Skips any entry already completed
    // (cancelled/superseded before it reached the VM) without dispatching it.
    private void DispatchNextIfFree()
    {
        if (_active is not null) return;

        while (_toDispatch.TryDequeue(out var pending))
        {
            if (pending.IsCompleted) continue;

            if (InvokeChunk(pending.Chunk))
            {
                _active = pending;
                DiagDispatched(pending.OpId, pending.Chunk);
            }
            else
            {
                pending.Complete(UnavailableCode, this);
                continue;
            }
            return;
        }
    }

    private void FailAllQueued(int code)
    {
        while (_toDispatch.TryDequeue(out var pending))
        {
            pending.Complete(code, this);
        }
    }

    // A distinct result global per op-id so a late-resuming (timed-out) coroutine can never write
    // into the global a LATER op is polling.
    private static string ResultGlobal(int opId)
        => "_StellarSeasonTalentResult" + opId.ToString(CultureInfo.InvariantCulture);

    private void RemoveActiveIfMatches(PendingOp pending)
    {
        if (ReferenceEquals(_active, pending)) _active = null;
    }

    // One in-flight write op: its reply global (embedded in Chunk), the chunk to dispatch, and the
    // awaiting TaskCompletionSource. Completion is idempotent and ALWAYS sets a value — never a
    // cancellation exception — so an OperationCanceledException can never escape this probe.
    private sealed class PendingOp
    {
        private readonly TaskCompletionSource<int> _tcs;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private CancellationTokenRegistration _ctReg;
        private int _completed;

        public PendingOp(int opId, string chunk, TaskCompletionSource<int> tcs)
        {
            OpId = opId;
            Global = ResultGlobal(opId);
            Chunk = chunk;
            _tcs = tcs;
        }

        public int OpId { get; }
        public string Global { get; }
        public string Chunk { get; }
        public bool IsCompleted => Volatile.Read(ref _completed) != 0;
        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public void AttachCancellation(CancellationToken ct, PandaSeasonTalentProbe owner)
        {
            _ctReg = ct.Register(static state =>
            {
                var (self, probe) = ((PendingOp, PandaSeasonTalentProbe))state!;
                self.Complete(CancelledCode, probe);
            }, (this, owner));
        }

        public void Complete(int code, PandaSeasonTalentProbe owner)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _stopwatch.Stop();
            owner.RemoveActiveIfMatches(this);
            _tcs.TrySetResult(code);
            try { _ctReg.Dispose(); } catch { /* registration already gone */ }
        }
    }
}
