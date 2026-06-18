using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-based <see cref="ILoadoutProbe"/>. Switches the player's active
/// loadout — internally the game's <b>Profession Project</b> system — through the
/// game's own Lua VM rather than constructing packets (mirror of
/// <see cref="PandaModuleEquipProbe"/>).
///
/// <para><b>Why the Lua bridge:</b> recon (<c>recon/loadout-switch-findings.md</c>
/// F1) established that the switch is a single server-governed RPC keyed on one int
/// (<c>CurrentProfessionProjectId</c>), invoked from Lua via
/// <c>Z.VMMgr.GetVM("&lt;vm&gt;").&lt;ApplyFn&gt;(id, token)</c> — there is no C#
/// method/RPC to reflect against (the proxy dispatches it by numeric methodId on the
/// <c>ForLua</c> path). Driving the game's own VM runs every server-side check
/// (combat-lock 5018/5019, dungeon-lock 5020, profession/name-group match 7061/7054,
/// unlock-condition 5025) instead of replicating them.</para>
///
/// <para><b>Discovery-first:</b> the C# dumps do NOT carry the Lua VM key, apply
/// function name, or saved-project list getter (they live in compiled Lua bytecode).
/// <see cref="PandaLoadoutProbe"/> ships with a one-shot in-world INTROSPECTION
/// diagnostic (<c>PandaLoadoutProbe.Diagnostics.cs</c>) that dumps the candidate VMs +
/// their members to the BepInEx log so the apply/list calls can be pinned, then
/// filled into the constants below. Until pinned, <see cref="ReadLoadouts"/> returns
/// empty and <see cref="CallApplyAsync"/> returns
/// <see cref="LoadoutResult.GameApiUnavailable"/>; the current-id read
/// (<see cref="ReadCurrentIndex"/>) is C#-reflectable and works independently.</para>
///
/// <para>SOLID partial layout — Lua-bridge reflection + chunk builders + current-id
/// container read live in <c>PandaLoadoutProbe.Resolution.cs</c>; the introspection
/// one-shot + gated per-event logging in <c>PandaLoadoutProbe.Diagnostics.cs</c>,
/// gated on <see cref="StellarDiagnostics.IsEnabled"/>.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe : ILoadoutProbe
{
    // Loadout switch is a server RPC; allow more headroom than module-equip's 6s
    // (profession/spec/gear apply is heavier). Poll CurrentProfessionProjectId
    // until it equals the requested id (Success) or this elapses (Timeout).
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(8);

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    // Single in-flight switch. The whole loadout is one server-side int, so only
    // one switch can be outstanding at a time; a new dispatch supersedes the old.
    private readonly object _pendingLock = new();
    private PendingSwitch? _pending;

    // Dispatches enqueued by CallApplyAsync (any thread) and drained on the Update
    // tick — the game's Lua VM is main-thread-only (see PandaModuleEquipProbe).
    private readonly ConcurrentQueue<PendingSwitch> _toDispatch = new();

    public PandaLoadoutProbe(IPluginLog log, IGameTypeRegistry typeRegistry)
    {
        _log = log;
        _typeRegistry = typeRegistry;
    }

    public bool IsResolved => _bridgeResolved;

    public IReadOnlyList<LoadoutEntry> ReadLoadouts()
    {
        // Until the VM list getter is pinned by introspection, return empty.
        // LoadoutService treats this as "no loadouts known" without error.
        if (!_listGetterResolved)
        {
            return Array.Empty<LoadoutEntry>();
        }
        return ReadLoadoutsViaVm();
    }

    public int? ReadCurrentIndex() => ReadCurrentProfessionProjectId();

    public Task<LoadoutResult> CallApplyAsync(int index, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(LoadoutResult.Cancelled);
        }

        // Apply requires both the Lua bridge AND a pinned apply-function name.
        if (!EnsureBridgeResolved() || !_applyFnResolved)
        {
            return Task.FromResult(LoadoutResult.GameApiUnavailable);
        }

        // Already on the requested loadout → immediate success (no-op switch).
        if (ReadCurrentProfessionProjectId() == index)
        {
            return Task.FromResult(LoadoutResult.Success);
        }

        var tcs = new TaskCompletionSource<LoadoutResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingSwitch(index, tcs, Stopwatch.StartNew());

        PendingSwitch? superseded;
        lock (_pendingLock)
        {
            superseded = _pending;
            _pending = pending;
        }
        superseded?.Complete(LoadoutResult.Cancelled, this);

        if (ct.CanBeCanceled)
        {
            pending.AttachCancellation(ct, this);
        }

        // Defer the actual Lua call to the Update tick (main thread). Touching the
        // Lua VM off the Unity main thread corrupts IL2CPP/Lua state.
        _toDispatch.Enqueue(pending);
        return tcs.Task;
    }

    /// <summary>
    /// Called per Update tick from the Host service tick (the Unity main thread).
    /// Runs the one-shot introspection (once the bridge resolves), fires any
    /// deferred Lua dispatch, then polls the current-id for completion of the
    /// in-flight switch.
    /// </summary>
    public void DrainPendingCompletions()
    {
        // Proactively resolve the bridge (throttled) so IsAvailable can flip true
        // without first needing a dispatch.
        TryResolveBridgeIfDue();

        // One-shot in-world introspection — discovers the VM/apply/list names.
        RunIntrospectionIfDue();

        DrainPendingDispatches();

        PendingSwitch? pending;
        lock (_pendingLock) { pending = _pending; }
        if (pending is null) return;

        var outcome = Evaluate(pending);
        if (outcome is { } result)
        {
            pending.Complete(result, this);
        }
    }

    private void DrainPendingDispatches()
    {
        while (_toDispatch.TryDequeue(out var pending))
        {
            if (pending.IsCompleted) continue;

            if (InvokeLuaDispatch(pending.TargetId))
            {
                DiagDispatched(pending.TargetId);
            }
            else
            {
                pending.Complete(LoadoutResult.GameApiUnavailable, this);
            }
        }
    }

    // Decide an in-flight switch's outcome from the current id, or null to keep
    // waiting. The id flipping to the target is the only positive completion
    // signal; refusals (in-combat etc.) leave the id unchanged and resolve as
    // Timeout (the game's own xpcall logs the EErrorCode under ChunkName).
    private LoadoutResult? Evaluate(PendingSwitch pending)
    {
        if (pending.IsCompleted) return null;

        if (ReadCurrentProfessionProjectId() == pending.TargetId)
        {
            return LoadoutResult.Success;
        }

        if (pending.Elapsed >= CompletionTimeout)
        {
            return LoadoutResult.Timeout;
        }

        return null;
    }

    private void RemovePending(PendingSwitch pending)
    {
        lock (_pendingLock)
        {
            if (ReferenceEquals(_pending, pending))
            {
                _pending = null;
            }
        }
    }

    // A single in-flight switch. Completion is idempotent and clears the owning
    // probe's pending slot; the cancellation registration is disposed on completion.
    private sealed class PendingSwitch
    {
        private readonly TaskCompletionSource<LoadoutResult> _tcs;
        private readonly Stopwatch _stopwatch;
        private CancellationTokenRegistration _ctReg;
        private int _completed;

        public PendingSwitch(int targetId, TaskCompletionSource<LoadoutResult> tcs, Stopwatch stopwatch)
        {
            TargetId = targetId;
            _tcs = tcs;
            _stopwatch = stopwatch;
        }

        public int TargetId { get; }
        public bool IsCompleted => Volatile.Read(ref _completed) != 0;
        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public void AttachCancellation(CancellationToken ct, PandaLoadoutProbe owner)
        {
            _ctReg = ct.Register(static state =>
            {
                var (self, probe) = ((PendingSwitch, PandaLoadoutProbe))state!;
                self.Complete(LoadoutResult.Cancelled, probe);
            }, (this, owner));
        }

        public void Complete(LoadoutResult result, PandaLoadoutProbe owner)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _stopwatch.Stop();
            owner.RemovePending(this);
            owner.DiagResult(TargetId, result, _stopwatch.ElapsedMilliseconds);
            _tcs.TrySetResult(result);
            try { _ctReg.Dispose(); } catch { /* registration already gone */ }
        }
    }
}
