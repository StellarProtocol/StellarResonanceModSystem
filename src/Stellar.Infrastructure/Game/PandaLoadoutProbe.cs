using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-based <see cref="ILoadoutProbe"/>. Reads + switches the player's active
/// loadout — internally the game's <b>Role Plan</b> system on the <c>weapon</c> Lua VM —
/// through the game's own Lua bridge + <c>WorldProxy</c> RPCs rather than constructing
/// packets (mirror of <see cref="PandaModuleEquipProbe"/>).
///
/// <para><b>Mechanism (CONFIRMED — <c>recon/loadout-switch-findings.md</c> § CONFIRMED
/// MECHANISM):</b> the plan list + current id come from a <c>SyncProjectList</c> RPC
/// cached in the <c>weapon_data</c> model; the switch goes through the game's OWN VM
/// wrapper <c>Z.VMMgr.GetVM("weapon").AsyncSwitchRolePlan(planId, token)</c> — exactly
/// what clicking the in-game dropdown does. The wrapper internally calls
/// <c>WorldProxy.SwitchProject</c>, then runs the client-side post-switch handling
/// (current-project sync cache, event dispatch) and shows the game's own success/error
/// toast (calling the raw RPC directly skipped that and corrupted local player state).
/// The wrapper returns a bool (true = success); the server runs every validation
/// (combat-lock 5018, no-such-plan 5022, profession-change 5026, …) and toasts the
/// reason itself — we never bypass it.</para>
///
/// <para><b>Read path:</b> the refresh chunk fires <c>SyncProjectList</c> ON DEMAND —
/// once the first time the bridge resolves in-world, and again immediately after a
/// successful switch — and serializes <c>CurPlanId</c> + each plan's id/name into the
/// <c>_StellarLoadoutData</c> Lua global (NOT on a recurring timer — an unprompted
/// recurring RPC is a policy violation). Each tick C# reads + parses the global into the
/// cache that <see cref="ReadLoadouts"/> / <see cref="ReadCurrentIndex"/> return (a cheap
/// read, no RPC). The async RPC writes the global a frame+ after firing, so reading the
/// previous result each tick is correct.</para>
///
/// <para>SOLID partial layout — Lua-bridge reflection + chunk builders + Lua-global
/// reads live in <c>PandaLoadoutProbe.Resolution.cs</c>; gated per-event logging in
/// <c>PandaLoadoutProbe.Diagnostics.cs</c>.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe : ILoadoutProbe
{
    // Loadout switch goes through the game's weapon-VM wrapper (AsyncSwitchRolePlan).
    // Poll CurPlanId == target (authoritative success) + the wrapper's bool result
    // global until the switch resolves or this elapses.
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(8);

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    // Parsed read cache (written on the Update tick, read by the Application layer
    // tick — both on the main thread, so plain fields are fine).
    private IReadOnlyList<LoadoutEntry> _loadouts = Array.Empty<LoadoutEntry>();
    private int? _currentId;
    private string? _lastDataRaw;

    // SyncProjectList is fired ON DEMAND only: once when the bridge first resolves
    // in-world, and again after a successful switch. No recurring timer (an unprompted
    // recurring RPC is a policy violation). _refreshPending is set on first resolve +
    // post-switch and cleared after the chunk fires.
    private bool _refreshedOnce;
    private bool _refreshPending;

    // Single in-flight switch. The whole loadout is one server-side id, so only one
    // switch can be outstanding at a time; a new dispatch supersedes the old.
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

    public IReadOnlyList<LoadoutEntry> ReadLoadouts() => _loadouts;

    public int? ReadCurrentIndex() => _currentId;

    public Task<LoadoutResult> CallApplyAsync(int index, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(LoadoutResult.Cancelled);
        }

        if (!EnsureBridgeResolved())
        {
            return Task.FromResult(LoadoutResult.GameApiUnavailable);
        }

        // Already on the requested loadout → immediate success (no-op switch).
        if (_currentId == index)
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
    /// Resolves the bridge (throttled), fires the throttled refresh, reads back the
    /// cached loadout data, fires any deferred switch dispatch, then polls the switch
    /// result global + current id for completion of the in-flight switch.
    /// </summary>
    public void DrainPendingCompletions()
    {
        TryResolveBridgeIfDue();
        if (!_bridgeResolved) return;

        RefreshIfDue();
        ParseLoadoutData();
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

    // Fire the SyncProjectList refresh chunk ON DEMAND only: once the first time the
    // bridge is resolved in-world (so weapon_data.rolePlanServerData_ populates), and
    // again whenever a switch flags a refresh (post-success). No recurring timer.
    private void RefreshIfDue()
    {
        if (!_refreshedOnce)
        {
            _refreshedOnce = true;
            _refreshPending = false;
            InvokeChunk(RefreshChunk);
            return;
        }
        if (_refreshPending)
        {
            _refreshPending = false;
            InvokeChunk(RefreshChunk);
        }
    }

    // Read + parse the data global written by the refresh chunk. Skips reparse when
    // the raw string is unchanged. First line is "CUR=<int>"; each subsequent
    // "<planId>\t<name>" line is a LoadoutEntry.
    private void ParseLoadoutData()
    {
        var raw = ReadLuaGlobalString(DataGlobal);
        if (string.IsNullOrEmpty(raw) || raw == _lastDataRaw) return;
        _lastDataRaw = raw;

        int? current = null;
        var entries = new List<LoadoutEntry>();
        foreach (var line in raw!.Split('\n'))
        {
            if (line.StartsWith("CUR=", StringComparison.Ordinal))
            {
                if (int.TryParse(line.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                {
                    current = c;
                }
                continue;
            }
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            if (!int.TryParse(line.AsSpan(0, tab), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            var name = line.Substring(tab + 1);
            entries.Add(new LoadoutEntry(id, name.Length == 0 ? $"Loadout {id}" : name));
        }

        // Sort by planId so hotkey N → a deterministic loadout. PlanDataDict is a Lua
        // map (pairs order is unspecified, and planIds go sparse after delete/recreate),
        // so without this the hotkey→loadout mapping is unstable across sessions.
        entries.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        _currentId = current;
        _loadouts = entries;
    }

    private void DrainPendingDispatches()
    {
        while (_toDispatch.TryDequeue(out var pending))
        {
            if (pending.IsCompleted) continue;

            // Clear the stale result before dispatching so the poll only sees this
            // switch's wrapper bool result.
            InvokeChunk(ClearSwitchGlobalChunk);
            if (InvokeChunk(BuildSwitchChunk(pending.TargetId)))
            {
                DiagDispatched(pending.TargetId);
            }
            else
            {
                pending.Complete(LoadoutResult.GameApiUnavailable, this);
            }
        }
    }

    // Decide an in-flight switch's outcome, or null to keep waiting. The weapon-VM
    // wrapper (AsyncSwitchRolePlan) returns a bool (true = success), not an errCode —
    // the game itself toasts the refusal reason (combat lock etc.), so we just need a
    // coarse success/rejected/timeout outcome:
    //   • CurPlanId flips to the target → Success (authoritative — the game applied it).
    //   • else the wrapper-result global is "false" → Rejected (the game showed why).
    //   • else after the timeout → Timeout.
    private LoadoutResult? Evaluate(PendingSwitch pending)
    {
        if (pending.IsCompleted) return null;

        if (_currentId == pending.TargetId)
        {
            TriggerRefreshAfterSwitch();
            return LoadoutResult.Success;
        }

        var ok = ReadLuaGlobalString(SwitchGlobal);
        if (string.Equals(ok, "false", StringComparison.OrdinalIgnoreCase))
        {
            return LoadoutResult.Rejected;
        }

        if (pending.Elapsed >= CompletionTimeout)
        {
            return LoadoutResult.Timeout;
        }

        return null;
    }

    // Flag the next tick to re-fire SyncProjectList so the list + current id reflect
    // the switch promptly. This is the only re-fetch besides the first-resolve one.
    private void TriggerRefreshAfterSwitch() => _refreshPending = true;

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
