using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// <para><b>Ordering &amp; pacing:</b> <see cref="DrainPending"/> dispatches at most ONE <i>new</i> op
/// per Update tick and keeps at most <see cref="MaxInFlight"/> ops in flight at once. Each op has its
/// own reply global (<c>_StellarSeasonTalentResult&lt;id&gt;</c>), so concurrent coroutines never
/// collide — this is exactly how the game fires its own RPCs. One-dispatch-per-tick paces requests
/// roughly a frame apart (a burst of simultaneous RPCs is what trips a server drop), while the bounded
/// window overlaps their round-trips so a many-factor switch is not paid serially. The reconciler's
/// cross-phase invariant (all unsockets before any socket — scarce single-copy factors freed first) is
/// enforced caller-side by <c>DeepSlumberService</c>'s phase barrier, which only fires a phase's ops
/// once the prior phase has fully drained; this probe is order-agnostic within whatever it is handed.</para>
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

    // How many ops may be in flight (dispatched, awaiting their server reply) at once. The ops overlap
    // their round-trips inside this window; kept modest so a switch stays gentle on the server (the
    // caller retries a genuine drop). One-dispatch-per-tick, below, ramps up to it a frame at a time.
    private const int MaxInFlight = 5;

    // Non-zero sentinel codes this probe completes an op with when it cannot obtain a real game reply
    // (single source of truth in DeepSlumberWriteCode). Any non-zero code surfaces to
    // DeepSlumberService as a failed op — never a hang, never a thrown OperationCanceledException.
    private const int UnavailableCode = DeepSlumberWriteCode.Unavailable;
    private const int TimeoutCode = DeepSlumberWriteCode.Timeout;
    private const int CancelledCode = DeepSlumberWriteCode.Cancelled;

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    // Dispatches enqueued by EnableLineAsync/SocketFactorAsync/UnsocketFactorAsync (any thread) and
    // drained on the Update tick — the game's Lua VM is main-thread-only. _inflight holds the
    // currently-dispatched ops (≤ MaxInFlight); it is mutated ONLY on the main thread (a cancellation
    // completing an op from another thread just flips its IsCompleted, and the next tick reaps it).
    private readonly ConcurrentQueue<PendingOp> _toDispatch = new();
    private readonly List<PendingOp> _inflight = new(MaxInFlight);
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
            pending.AttachCancellation(ct);
        }

        _toDispatch.Enqueue(pending);
        return tcs.Task;
    }

    /// <summary>
    /// Called per Update tick from the Host service tick (the Unity main thread). Resolves the Lua
    /// bridge (throttled), services every in-flight op (reads its reply global / checks its timeout /
    /// reaps a cancelled one), then dispatches at most one new queued op if the in-flight window has
    /// room.
    /// </summary>
    public void DrainPending()
    {
        TryResolveBridgeIfDue();

        if (!_bridgeResolved)
        {
            FailAllQueued(UnavailableCode);
            return;
        }

        ServiceInflight();
        DispatchOneIfCapacity();
    }

    // Services every in-flight op: completes it on a reply or a timeout, and reaps any already
    // completed out-of-band (cancelled from another thread). _inflight is small (≤ MaxInFlight);
    // iterate backwards so RemoveAt does not disturb the unvisited indices.
    private void ServiceInflight()
    {
        for (var i = _inflight.Count - 1; i >= 0; i--)
        {
            var op = _inflight[i];

            if (op.IsCompleted)   // cancelled (or otherwise completed) off the main thread — just reap it
            {
                _inflight.RemoveAt(i);
                continue;
            }

            var reply = ReadLuaGlobalString(op.Global);
            if (reply is not null)
            {
                var code = ParseCode(reply);
                DiagResult(op.OpId, code, op.Elapsed.TotalMilliseconds);
                op.Complete(code);
                _inflight.RemoveAt(i);
                continue;
            }

            if (op.Elapsed >= CompletionTimeout)
            {
                DiagResult(op.OpId, TimeoutCode, op.Elapsed.TotalMilliseconds);
                op.Complete(TimeoutCode);
                _inflight.RemoveAt(i);
            }
        }
    }

    // Fires at most ONE queued op's chunk per tick while the in-flight window has room — spreading
    // dispatches ~a frame apart. Skips any entry already completed (cancelled before it reached the
    // VM) without dispatching it, and keeps skipping until it either dispatches one or drains the queue.
    private void DispatchOneIfCapacity()
    {
        if (_inflight.Count >= MaxInFlight) return;

        while (_toDispatch.TryDequeue(out var pending))
        {
            if (pending.IsCompleted) continue;

            if (InvokeChunk(pending.Chunk))
            {
                _inflight.Add(pending);
                DiagDispatched(pending.OpId, pending.Chunk);
            }
            else
            {
                pending.Complete(UnavailableCode);
                continue;
            }
            return;
        }
    }

    private void FailAllQueued(int code)
    {
        while (_toDispatch.TryDequeue(out var pending))
        {
            pending.Complete(code);
        }
    }

    // A distinct result global per op-id so a late-resuming (timed-out) coroutine can never write
    // into the global a LATER op is polling.
    private static string ResultGlobal(int opId)
        => "_StellarSeasonTalentResult" + opId.ToString(CultureInfo.InvariantCulture);

    // One in-flight write op: its reply global (embedded in Chunk), the chunk to dispatch, and the
    // awaiting TaskCompletionSource. Completion is idempotent and ALWAYS sets a value — never a
    // cancellation exception — so an OperationCanceledException can never escape this probe. Complete
    // never touches _inflight: the main-thread ServiceInflight reaps a completed op by its IsCompleted
    // flag, so a cross-thread cancellation callback never mutates the list.
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

        public void AttachCancellation(CancellationToken ct)
        {
            _ctReg = ct.Register(static state => ((PendingOp)state!).Complete(CancelledCode), this);
        }

        public void Complete(int code)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _stopwatch.Stop();
            _tcs.TrySetResult(code);
            try { _ctReg.Dispose(); } catch { /* registration already gone */ }
        }
    }
}
