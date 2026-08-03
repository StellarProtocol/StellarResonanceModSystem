using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>Resolves EVERY saved loadout's per-class gear + modules from their slot → uuid maps in one
/// pass. Host wires this to <see cref="PandaInventoryProbe.ResolvePlanLoadouts"/> (which owns the item
/// container reflection + builds the uuid index once). Returns a same-length all-empty list until the
/// container is resolved.</summary>
internal delegate IReadOnlyList<(IReadOnlyList<GearInstance> Gear, IReadOnlyDictionary<int, ModuleInfo> Modules)>
    PerClassLoadoutResolver(
        IReadOnlyList<(IReadOnlyDictionary<int, long> Equip, IReadOnlyDictionary<int, long> Mod)> plans);

/// <summary>One parsed loadout row from the Lua data global: the base fields PLUS the plan's
/// equip/mod slot → uuid maps (from <c>equipInfoMap</c>/<c>modInfoMap</c>), which the per-class
/// resolver turns into full gear/modules.</summary>
internal readonly record struct ParsedPlan(
    int Index,
    string Name,
    int ProfessionId,
    int TalentStageId,
    IReadOnlyList<int>? TalentNodes,
    IReadOnlyDictionary<int, long> EquipUuids,
    IReadOnlyDictionary<int, long> ModUuids);

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

    // Per-class gear/modules (2026-08-03). The parsed plans carry each plan's equip/mod slot→uuid maps;
    // _resolveGear (wired by Host to PandaInventoryProbe.ResolvePlanLoadout) turns those uuids into full
    // GearInstance/ModuleInfo via the item container. Resolution is retried each tick until the container
    // is ready (any non-empty result), then latched via _detailsResolved until the raw data changes.
    private List<ParsedPlan> _parsedPlans = new();
    private bool _detailsResolved;
    private PerClassLoadoutResolver? _resolveGear;

    /// <summary>Wires the per-class gear/module resolver (Host → <c>PandaInventoryProbe.ResolvePlanLoadout</c>).
    /// Late-bound because the inventory probe is built after the loadout probe. Safe to call once.</summary>
    public void AttachGearResolver(PerClassLoadoutResolver resolver)
    {
        _resolveGear = resolver;
        _detailsResolved = false;   // re-resolve now that a resolver is available
        _resolveTickGate = 0;
        _resolveAttempts = 0;
    }

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

        // NOTE: deliberately NO "_currentId == index → no-op" fast-path here. _currentId
        // can be stale (it only refreshes after a plugin switch, never after an in-game
        // dropdown switch), and a stale match silently swallowed the dispatch — making the
        // login-current loadout permanently un-switchable. Always dispatch; the game itself
        // cheaply no-ops a switch to the already-active loadout.
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

        TickLiveGearDiag();   // measure-first live-container trace — no-op unless STELLAR_DIAGNOSTICS
        RefreshIfDue();
        ParseLoadoutData();
        TryResolvePerClassDetails();
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
    // the raw string is unchanged.
    private void ParseLoadoutData()
    {
        var raw = ReadLuaGlobalString(DataGlobal);
        if (string.IsNullOrEmpty(raw) || raw == _lastDataRaw) return;
        _lastDataRaw = raw;

        var (current, plans) = ParseLoadoutData(raw!);
        _currentId = current;
        _parsedPlans = plans;
        _loadouts = BuildBaseEntries(plans);   // gear/modules null until TryResolvePerClassDetails fills them
        _detailsResolved = false;              // new data → re-resolve per-class gear/modules
        _resolveTickGate = 0;
        _resolveAttempts = 0;                  // fresh attempt budget for this snapshot
        LogEquipProbe();   // per-class gear RE — no-op unless STELLAR_DIAGNOSTICS; data is populated here
    }

    // Base entries carry class/talent only; per-class Gear/Modules are attached later (they need the
    // item container, which resolves a beat after the loadout list). Consumers see a valid list
    // immediately (no gear) and the upgraded one once TryResolvePerClassDetails runs.
    private static List<LoadoutEntry> BuildBaseEntries(List<ParsedPlan> plans)
    {
        var list = new List<LoadoutEntry>(plans.Count);
        foreach (var p in plans)
            list.Add(new LoadoutEntry(p.Index, p.Name, p.ProfessionId, p.TalentStageId, p.TalentNodes));
        return list;
    }

    // Resolves each plan's per-class gear + modules from its slot→uuid maps via the injected resolver
    // (Host → PandaInventoryProbe.ResolvePlanLoadout). Retried every tick until the item container is
    // ready (any plan yields non-empty gear/modules), then latched. A no-op once resolved or with no
    // resolver/plans. Cheap after latch (never re-runs until the raw data changes).
    // The resolve reflects the WHOLE item container (BuildUuidIndex scans every package/item) — the same
    // expensive scan the sibling inventory refresh deliberately runs at 1 Hz. Throttle this retry to a
    // similar cadence (NOT the up-to-30 Hz loadout drain) and cap attempts per data snapshot, so a plan
    // set that never resolves (impossible for real saved loadouts, but guards a container hiccup) can't
    // scan forever. Both counters reset on a new parse (_lastDataRaw change) and on AttachGearResolver.
    private int _resolveTickGate;
    private int _resolveAttempts;
    private const int ResolveEveryTicks = 30;   // ~1 Hz at the default 30 Hz loadout drain
    private const int MaxResolveAttempts = 60;  // give up after ~1 min of throttled tries; keep base entries

    private void TryResolvePerClassDetails()
    {
        if (_detailsResolved || _resolveGear is null || _parsedPlans.Count == 0) return;
        if (_resolveTickGate++ % ResolveEveryTicks != 0) return;                              // throttle the scan
        if (_resolveAttempts++ >= MaxResolveAttempts) { _detailsResolved = true; return; }   // give up; keep base

        var request = new List<(IReadOnlyDictionary<int, long>, IReadOnlyDictionary<int, long>)>(_parsedPlans.Count);
        foreach (var p in _parsedPlans) request.Add((p.EquipUuids, p.ModUuids));

        var results = _resolveGear(request);   // one pass; builds the item index once
        var ready = false;
        foreach (var (gear, modules) in results)
            if (gear.Count > 0 || modules.Count > 0) { ready = true; break; }
        if (!ready) return;   // container not resolved yet — keep the base entries, retry next throttled tick

        var upgraded = new List<LoadoutEntry>(_parsedPlans.Count);
        for (var i = 0; i < _parsedPlans.Count; i++)
        {
            var p = _parsedPlans[i];
            var (gear, modules) = i < results.Count ? results[i] : (Array.Empty<GearInstance>(), (IReadOnlyDictionary<int, ModuleInfo>)EmptyModules);
            upgraded.Add(new LoadoutEntry(p.Index, p.Name, p.ProfessionId, p.TalentStageId, p.TalentNodes, gear, modules));
        }
        _loadouts = upgraded;
        _detailsResolved = true;
        LogPerClassResolved(upgraded);   // no-op unless STELLAR_DIAGNOSTICS
    }

    private static readonly IReadOnlyDictionary<int, ModuleInfo> EmptyModules = new Dictionary<int, ModuleInfo>(0);

    // Pure row parser — internal (not private) so it's directly unit-testable without
    // the Lua bridge. First line is "CUR=<int>"; each subsequent row is
    // "<planId>\t<name>\t<professionId>\t<talentStageId>\t<talentNodeIds csv>\t<equip slot:uuid csv>\t<mod slot:uuid csv>".
    // Tolerates the OLD 2/4/5-column forms (a stale in-flight read from before an enrichment shipped) —
    // the missing columns simply default to 0/empty, never throw.
    internal static (int? Current, List<ParsedPlan> Plans) ParseLoadoutData(string raw)
    {
        int? current = null;
        var plans = new List<ParsedPlan>();
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith("CUR=", StringComparison.Ordinal))
            {
                if (int.TryParse(line.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                {
                    current = c;
                }
                continue;
            }

            var cols = line.Split('\t');
            if (cols.Length < 2) continue;
            if (!int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;

            var name = cols[1];
            var professionId = cols.Length > 2
                && int.TryParse(cols[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prof) ? prof : 0;
            var talentStageId = cols.Length > 3
                && int.TryParse(cols[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stage) ? stage : 0;
            var talentNodes = cols.Length > 4 ? ParseNodeCsv(cols[4]) : null;
            var equipUuids = cols.Length > 5 ? ParseUuidMap(cols[5]) : EmptyUuidMap;
            var modUuids = cols.Length > 6 ? ParseUuidMap(cols[6]) : EmptyUuidMap;

            plans.Add(new ParsedPlan(id, name.Length == 0 ? $"Loadout {id}" : name,
                professionId, talentStageId, talentNodes, equipUuids, modUuids));
        }

        // Sort by planId so hotkey N → a deterministic loadout. PlanDataDict is a Lua
        // map (pairs order is unspecified, and planIds go sparse after delete/recreate),
        // so without this the hotkey→loadout mapping is unstable across sessions.
        plans.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        return (current, plans);
    }

    private static readonly IReadOnlyDictionary<int, long> EmptyUuidMap = new Dictionary<int, long>(0);

    // Parses a "slot:uuid,slot:uuid" list into a slot→uuid map. Malformed pairs are skipped, never
    // thrown; an empty/absent field yields the shared empty map (no allocation).
    private static IReadOnlyDictionary<int, long> ParseUuidMap(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return EmptyUuidMap;
        Dictionary<int, long>? map = null;
        foreach (var pair in csv.Split(','))
        {
            var colon = pair.IndexOf(':');
            if (colon <= 0 || colon >= pair.Length - 1) continue;
            if (int.TryParse(pair.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot)
                && long.TryParse(pair.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uuid))
            {
                (map ??= new Dictionary<int, long>()).Add(slot, uuid);
            }
        }
        return map ?? EmptyUuidMap;
    }

    // Parse a comma-separated node-id list ("233002,5205,...") into ints; returns null when the
    // field is empty (no allocation captured) so LoadoutEntry.TalentNodes stays null rather than
    // an empty list. Non-numeric parts are skipped, never thrown.
    private static List<int>? ParseNodeCsv(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return null;
        List<int>? nodes = null;
        foreach (var part in csv.Split(','))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                (nodes ??= new List<int>()).Add(n);
            }
        }
        return nodes;
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

        // The wrapper bool is the AUTHORITATIVE completion signal — it's written when
        // AsyncSwitchRolePlan returns. Check it FIRST: relying on _currentId flipping
        // would deadlock (it only refreshes AFTER a success → it could never become the
        // target). "true" → success + refresh the cache so _currentId catches up.
        var ok = ReadLuaGlobalString(SwitchGlobal);
        if (string.Equals(ok, "true", StringComparison.OrdinalIgnoreCase))
        {
            TriggerRefreshAfterSwitch();
            return LoadoutResult.Success;
        }
        if (string.Equals(ok, "false", StringComparison.OrdinalIgnoreCase))
        {
            return LoadoutResult.Rejected;
        }

        // Fallback: the server-synced current id already matches (e.g. switch landed
        // before we read the bool).
        if (_currentId == pending.TargetId)
        {
            TriggerRefreshAfterSwitch();
            return LoadoutResult.Success;
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
