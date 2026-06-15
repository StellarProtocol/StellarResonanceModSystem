using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-based <see cref="IModuleEquipProbe"/>. Dispatches equip /
/// uninstall through the game's own Lua VM rather than constructing packets.
///
/// <para><b>Why the Lua bridge:</b> recon (<c>recon/phase-7-types.md</c> §§ 2-4)
/// established that <c>WorldProxy.InstallMod</c> / <c>UninstallMod</c> exist
/// only as Lua functions — there is no C# method to reflect against. The C#
/// layer beneath (<c>ZRpcCtrl.LuaProxyCall</c>) takes raw protobuf bytes;
/// invoking it directly would mean hand-encoding the request AND replicating
/// the Lua pre-flight validation (slot-unlock, mod-only conflict, category
/// cap, replace-confirm dialog) — i.e. packet construction by another name.
/// Instead we drive the game's own equip ViewModel exactly as its UI buttons
/// do: <c>Z.VMMgr.GetVM("mod").AsyncEquipMod(uuid, slot)</c> /
/// <c>.AsyncUninstallMod(slot)</c>, launched in a child coroutine via the
/// canonical <c>Z.CoroUtil.create_coro_xpcall(fn)()</c> idiom and executed
/// through <c>ZLuaFramework.LuaState.DoString</c>, which runs every one of the
/// game's checks (spec D7). The coroutine is required — the equip path calls
/// <c>coro_util.async_to_sync</c>, which <c>yield</c>s until the RPC replies,
/// and <c>DoString</c> runs on the main thread where <c>yield</c> would throw.</para>
///
/// <para><b>Why DoString and not a dotted-path Call:</b> <c>ModVM</c> is a
/// module-<i>local</i> (<c>local ModVM = {}</c> … <c>return ModVM</c> in
/// <c>lua/ui/view_model/mod_vm.lua</c>), reachable only via the function call
/// <c>Z.VMMgr.GetVM("mod")</c> (<c>vm_mgr.lua</c> caches VMs in a module-local
/// table). tolua#'s <c>Call</c> / <c>CallTableFunc</c> / <c>GetTable</c> resolve
/// dotted paths against Lua <i>globals</i> — there is no global handle to
/// <c>ModVM</c>, so any <c>Call("ModVM.AsyncEquipMod", …)</c> silently resolves
/// nil and no-ops. <c>DoString</c> runs a real Lua chunk, so the
/// <c>GetVM("mod")</c> call mid-path works.</para>
///
/// <para><b>Completion signal (B2):</b> the Lua <c>OnModInstall</c> event is
/// Lua-only (recon § 5); rather than inject a Lua callback shim (B1 — deferred
/// polish, needs in-world iteration to pin the registration API), this probe
/// polls the live <c>Mod.ModSlots</c> map captured by
/// <see cref="PandaInventoryProbe"/>. After dispatching an equip the probe
/// watches the slot until it matches the expected uuid (install) / becomes
/// absent (uninstall), up to a 6s timeout (matching the Lua
/// <c>coro_util.async_to_sync(..., 6)</c> budget). Polling is nearly free —
/// the capture hook keeps the long-lived proto object fresh via
/// SyncContainerDirtyData.</para>
///
/// <para>SOLID partial layout — Lua-bridge reflection lives in
/// <c>PandaModuleEquipProbe.Resolution.cs</c>; diagnostic logging in
/// <c>PandaModuleEquipProbe.Diagnostics.cs</c>, gated on
/// <see cref="StellarDiagnostics.IsEnabled"/>.</para>
/// </summary>
internal sealed partial class PandaModuleEquipProbe : IModuleEquipProbe
{
    // Matches the Lua coro_util.async_to_sync(..., 6) RPC budget.
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(6);

    // VMMgr key + function names the DoString chunk drives (mod_vm.lua). The VM
    // is fetched via Z.VMMgr.GetVM("mod") — its only reachable accessor — then
    // the function is called on the returned table (see Resolution.cs).
    private const string ModVmName = "mod";
    private const string AsyncEquipModFunc = "AsyncEquipMod";
    private const string AsyncUninstallModFunc = "AsyncUninstallMod";

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    // B2 completion source: reads the freshest equipped slot → uuid map off the
    // inventory probe's captured CharSerialize. Returns null until a sync lands.
    private readonly Func<IReadOnlyDictionary<int, long>?> _readEquippedSlots;

    // In-flight dispatches, keyed by slot. A given slot can only host one
    // outstanding equip at a time, so the slot is a sufficient key. Drained on
    // the Update thread by DrainPendingCompletions.
    private readonly object _pendingLock = new();
    private readonly Dictionary<int, PendingEquip> _pending = new();

    // Dispatches enqueued by Dispatch() (any thread) and drained on the Update
    // tick. The game's Lua VM is main-thread-only, so the actual DoString must
    // run there — see DrainPendingDispatches.
    private readonly ConcurrentQueue<PendingEquip> _toDispatch = new();

    public PandaModuleEquipProbe(
        IPluginLog log,
        IGameTypeRegistry typeRegistry,
        Func<IReadOnlyDictionary<int, long>?> readEquippedSlots)
    {
        _log = log;
        _typeRegistry = typeRegistry;
        _readEquippedSlots = readEquippedSlots;
    }

    public bool IsResolved => _bridgeResolved;

    public Task<EquipResult> CallInstallAsync(int slotId, long moduleUuid, CancellationToken ct)
        => Dispatch(new EquipRequest(slotId, moduleUuid, IsUninstall: false), ct);

    public Task<EquipResult> CallUninstallAsync(int slotId, CancellationToken ct)
        => Dispatch(new EquipRequest(slotId, ModuleUuid: 0L, IsUninstall: true), ct);

    private Task<EquipResult> Dispatch(EquipRequest request, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(EquipResult.Cancelled);
        }

        if (!EnsureBridgeResolved())
        {
            return Task.FromResult(EquipResult.GameApiUnavailable);
        }

        // Snapshot the slot's current uuid so uninstall can distinguish
        // "already empty" (SlotEmpty) from "became empty after dispatch".
        var slotsBefore = SafeReadEquippedSlots();
        var hadModule = slotsBefore is not null && slotsBefore.ContainsKey(request.SlotId);

        var tcs = new TaskCompletionSource<EquipResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingEquip(request, tcs, Stopwatch.StartNew(), hadModule);

        // Replace any stale pending for the same slot (last writer wins) — a
        // superseded dispatch resolves with RpcError so its awaiter unblocks.
        PendingEquip? superseded = null;
        lock (_pendingLock)
        {
            if (_pending.TryGetValue(request.SlotId, out var existing))
            {
                superseded = existing;
            }
            _pending[request.SlotId] = pending;
        }
        superseded?.Complete(EquipResult.RpcError, this);

        // Cancellation completes the Task and drops the pending entry. The Lua
        // dispatch can't be unsent, but the awaiter stops waiting.
        if (ct.CanBeCanceled)
        {
            pending.AttachCancellation(ct, this);
        }

        // Defer the actual Lua call to the Update tick. Dispatch() may be invoked
        // from a thread-pool thread (a plugin's apply-flow await continuation
        // resumes off the main thread — BepInEx installs no SynchronizationContext),
        // and touching the game's Lua VM off the Unity main thread corrupts the
        // IL2CPP/Lua state and hard-crashes the game (access violation).
        // DrainPendingDispatches runs the DoString on the Update thread.
        _toDispatch.Enqueue(pending);
        return tcs.Task;
    }

    /// <summary>
    /// Called per Update tick from BootstrapPlugin (the Unity main thread). Runs
    /// any deferred Lua dispatches, then polls the captured equipped-slots map and
    /// resolves any pending Task whose target state has been reached or whose 6s
    /// deadline has elapsed.
    /// </summary>
    public void DrainPendingCompletions()
    {
        // Proactively resolve the Lua bridge (throttled) so the Apply button —
        // gated on IsResolved — can become enabled without first needing a
        // dispatch (which it can't get while disabled). No-op once resolved.
        TryResolveBridgeIfDue();

        // Run the actual Lua call for anything Dispatch() enqueued (possibly from
        // an off-main-thread await continuation) here, on the main thread.
        DrainPendingDispatches();

        // Cheap early-out: nothing to do until something is in flight.
        if (PendingCount == 0)
        {
            return;
        }

        var slots = SafeReadEquippedSlots();

        // Copy the in-flight set under lock, evaluate outside it (Complete
        // re-enters the lock to remove the entry).
        List<PendingEquip> snapshot;
        lock (_pendingLock)
        {
            snapshot = new List<PendingEquip>(_pending.Values);
        }

        foreach (var pending in snapshot)
        {
            var outcome = Evaluate(pending, slots);
            if (outcome is { } result)
            {
                pending.Complete(result, this);
            }
        }
    }

    // Runs queued Lua dispatches on the caller's thread (the Update / Unity main
    // thread). Each entry was already registered in _pending by Dispatch; here we
    // fire the DoString and either log success or complete it as unavailable.
    private void DrainPendingDispatches()
    {
        while (_toDispatch.TryDequeue(out var pending))
        {
            // Skip anything cancelled or superseded before it reached the VM.
            if (pending.IsCompleted)
            {
                continue;
            }

            if (InvokeLuaDispatch(pending.Request))
            {
                DiagDispatched(pending.Request);
            }
            else
            {
                pending.Complete(EquipResult.GameApiUnavailable, this);
            }
        }
    }

    private int PendingCount
    {
        get { lock (_pendingLock) { return _pending.Count; } }
    }

    // Decide a pending dispatch's outcome from the current slot map, or null to
    // keep waiting. Cancellation is handled by the token registration directly.
    private static EquipResult? Evaluate(PendingEquip pending, IReadOnlyDictionary<int, long>? slots)
    {
        if (pending.IsCompleted)
        {
            return null;
        }

        var req = pending.Request;

        if (slots is not null)
        {
            var present = slots.TryGetValue(req.SlotId, out var uuid);
            if (req.IsUninstall)
            {
                if (!present)
                {
                    // Slot empty now. SlotEmpty if it was already empty at
                    // dispatch (no-op uninstall); Success if we observed it
                    // clear after dispatch.
                    return pending.SlotHadModuleAtDispatch ? EquipResult.Success : EquipResult.SlotEmpty;
                }
            }
            else if (present && uuid == req.ModuleUuid)
            {
                return EquipResult.Success;
            }
        }

        if (pending.Elapsed >= CompletionTimeout)
        {
            return EquipResult.Timeout;
        }

        return null;
    }

    private IReadOnlyDictionary<int, long>? SafeReadEquippedSlots()
    {
        try { return _readEquippedSlots(); }
        catch { return null; }
    }

    private void RemovePending(int slotId, PendingEquip pending)
    {
        lock (_pendingLock)
        {
            if (_pending.TryGetValue(slotId, out var current) && ReferenceEquals(current, pending))
            {
                _pending.Remove(slotId);
            }
        }
    }

    private readonly record struct EquipRequest(int SlotId, long ModuleUuid, bool IsUninstall);

    // A single in-flight dispatch. Completion is idempotent and removes the
    // entry from the owning probe's pending map; the cancellation registration
    // is disposed on completion to avoid leaks.
    private sealed class PendingEquip
    {
        private readonly TaskCompletionSource<EquipResult> _tcs;
        private readonly Stopwatch _stopwatch;
        private CancellationTokenRegistration _ctReg;
        private int _completed;

        public PendingEquip(EquipRequest request, TaskCompletionSource<EquipResult> tcs, Stopwatch stopwatch, bool slotHadModuleAtDispatch)
        {
            Request = request;
            _tcs = tcs;
            _stopwatch = stopwatch;
            SlotHadModuleAtDispatch = slotHadModuleAtDispatch;
        }

        public EquipRequest Request { get; }
        public bool SlotHadModuleAtDispatch { get; }
        public bool IsCompleted => Volatile.Read(ref _completed) != 0;
        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public void AttachCancellation(CancellationToken ct, PandaModuleEquipProbe owner)
        {
            _ctReg = ct.Register(static state =>
            {
                var (self, probe) = ((PendingEquip, PandaModuleEquipProbe))state!;
                self.Complete(EquipResult.Cancelled, probe);
            }, (this, owner));
        }

        public void Complete(EquipResult result, PandaModuleEquipProbe owner)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }
            _stopwatch.Stop();
            owner.RemovePending(Request.SlotId, this);
            owner.DiagResult(Request, result, _stopwatch.ElapsedMilliseconds);
            _tcs.TrySetResult(result);
            try { _ctReg.Dispose(); } catch { /* registration already gone */ }
        }
    }
}
