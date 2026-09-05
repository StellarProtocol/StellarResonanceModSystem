using System;
using System.Collections.Generic;
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
/// <c>PandaLoadoutProbe.Diagnostics.cs</c>. The Deep-Slumber (season cultivate)
/// <see cref="Stellar.Application.Abstractions.IDeepSlumberProbe"/> reader — riding the SAME refresh
/// chunk/global — lives in <c>PandaLoadoutProbe.DeepSlumber.cs</c> +
/// <c>PandaLoadoutProbe.DeepSlumber.Diagnostics.cs</c>.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe : ILoadoutProbe
{
    // The switch WRITE path (CallApplyAsync → pre-dispatch gates → Lua dispatch → completion polling)
    // lives in PandaLoadoutProbe.Switch.cs. This file owns the READ path + the drain orchestration.

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    // Parsed read cache (written on the Update tick, read by the Application layer
    // tick — both on the main thread, so plain fields are fine).
    private IReadOnlyList<LoadoutEntry> _loadouts = Array.Empty<LoadoutEntry>();
    private int? _currentId;
    private string? _lastDataRaw;

    // Per-class gear/modules (2026-08-03). The parsed plans carry each plan's equip/mod slot→uuid maps;
    // _resolveGear (wired by Host to PandaInventoryProbe.ResolvePlanLoadouts) turns those uuids into full
    // GearInstance/ModuleInfo via the item container. Resolution is EVENT-DRIVEN (see _resolvePending +
    // TryResolvePerClassDetails): it runs on a new parse and on the container-sync/gear-change event.
    private List<ParsedPlan> _parsedPlans = new();
    private PerClassLoadoutResolver? _resolveGear;

    /// <summary>Wires the per-class gear/module resolver (Host → <c>PandaInventoryProbe.ResolvePlanLoadouts</c>).
    /// Late-bound because the inventory probe is built after the loadout probe. Safe to call once.</summary>
    public void AttachGearResolver(PerClassLoadoutResolver resolver)
    {
        _resolveGear = resolver;
        _resolvePending = true;   // resolve now that a resolver is available
    }

    /// <summary>The CONTAINER-MERGE event: the game just merged fresh <c>CharSerialize</c> data (a
    /// method-21 full sync or ANY method-22 dirty delta — the signal is field-agnostic, see
    /// <c>ContainerDirtyDeltaReader.IsMergeSignal</c>), so the Lua mirror this probe reads is now
    /// fresh. Covers a gear/module edit, a talent respec, an imagine swap, a loadout switch, and the
    /// item container becoming ready. Arms the live-state re-read (<c>_mergePending</c>, consumed
    /// COALESCED on the next drain tick) and re-arms the per-class resolve. Host wires this to
    /// <c>IInventory.SelfGearChanged</c>, which raises on the NETWORK thread — so this only flips
    /// flags and never touches game/IL2CPP state.</summary>
    public void OnGearChanged()
    {
        _mergePending = true;
        _resolvePending = true;
    }

    // SyncProjectList is fired ON DEMAND only: once when the bridge first resolves
    // in-world, and again after a successful switch. No recurring timer (an unprompted
    // recurring RPC is a policy violation). _refreshPending is set on first resolve +
    // post-switch and cleared after the chunk fires.
    private bool _refreshedOnce;
    private bool _refreshPending;

    public PandaLoadoutProbe(IPluginLog log, IGameTypeRegistry typeRegistry)
    {
        _log = log;
        _typeRegistry = typeRegistry;
    }

    public bool IsResolved => _bridgeResolved;

    public IReadOnlyList<LoadoutEntry> ReadLoadouts() => _loadouts;

    public int? ReadCurrentIndex() => _currentId;

    // The live line (ReadLiveLine) is re-read on every new parse; profession 0 = no LIVE row yet.
    public LiveLoadoutState? ReadLiveState()
        => _liveProfessionId == 0
            ? null
            : new LiveLoadoutState(_liveProfessionId, _liveTalentStageId, _liveTalentNodes);

    // ClearSession() (logout reset) lives in PandaLoadoutProbe.Session.cs — kept out of this file to
    // stay under the 500-LoC standards gate.

    // CallApplyAsync (the ILoadoutProbe write entry point) lives in PandaLoadoutProbe.Switch.cs.

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

        // THE EVENT. A container merge (gear/module edit, talent respec, imagine swap, loadout switch,
        // login full sync) re-reads the LIVE Lua containers — equipped slots, class, talents, imagine
        // hotbar — and re-fires the on-demand plan refresh. Coalesced: the flag is set on the network
        // thread, consumed once here, so a burst of deltas costs ONE read. No timer anywhere on this
        // path (owner ruling 2026-08-23) — see PandaLoadoutProbe.LiveState.cs.
        RefreshLiveStateIfArmed();

        RefreshIfDue();
        ParseLoadoutData();
        // Per-class gear/modules BASE = each saved loadout's equipInfoMap/modInfoMap resolved via the item
        // container (distinct per class — correct for loadout switching). Resolves once when the loadout
        // data + item container are both ready, then LATCHES (bounded retry — not continuous polling). The
        // CURRENT class is overlaid with its LIVE equipped set (manual edits) inside this call. COALESCED:
        // a re-equip burst arms the resolve ~30x/s and the walk is whole-item-container, so it runs at most
        // once per ResolveCooldownTicks window — deferred, never dropped (owner report 2026-09-05).
        TryResolvePerClassDetailsIfDue();
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
    // Coalesce refresh re-fires: a loadout switch emits a BURST of gear deltas (each sets _refreshPending
    // via OnGearChanged), and the refresh fires a SyncProjectList RPC — firing it every tick through a burst
    // would spam RPCs. After a refresh, wait this many ticks before the next; _refreshPending stays set
    // meanwhile, so exactly one refresh fires per window (which re-reads the fresh current plan + live set).
    private int _refreshCooldown;
    private const int RefreshCooldownTicks = 20;   // ~0.66 s at the 30 Hz loadout drain

    // Cached so the per-tick RefreshIfDue does not allocate a fresh Func<bool> from the instance method
    // group every drain tick. DecideRefresh only INVOKES it once a refresh is otherwise due.
    private Func<bool>? _inCombatProbe;

    // Outcome of the combat-gated refresh state machine — see DecideRefresh.
    internal enum RefreshOutcome { Wait, Fire, DeferForCombat, DeferForDsWrite }

    // Set by Host to the season-talent write probe's HasPendingWrites. While a plugin-driven Deep-Slumber
    // apply is in flight, the server pushes a CharSerialize delta per op (reset + one-per-anchor activate +
    // sockets), each re-arming _refreshPending; without this gate the full-container RefreshChunk walk
    // re-fires every cooldown (~0.66 s) through the whole 1-2 s apply → 3-5 frame hitches. Defer (like
    // combat) so the burst collapses into ONE refresh after the apply settles — the intermediate cultivate
    // states are transient and never fought-with. Default: never in flight (no plugin / not wired in tests).
    internal Func<bool> DsWriteInFlightProbe { get; set; } = static () => false;

    private void RefreshIfDue()
    {
        _inCombatProbe ??= IsLocalPlayerInCombat;
        if (_refreshCooldown > 0) _refreshCooldown--;

        // The SyncProjectList RPC is COMBAT-GATED server-side (ErrStateIllegal 3202): the server rejects
        // role-plan RPCs during combat and the game's own weapon-VM wrapper toasts "Cannot perform this
        // action during combat". In combat the server drips CharSerialize deltas ~every 5 s, each re-arming
        // _refreshPending (PandaLoadoutProbe.LiveState.cs), so the rejected RPC — and its toast — recurred on
        // that cadence with ZERO plugins (Discord issue 2026-08-25, framework-only, owner-validated). DEFER
        // the RPC while the local player is in combat, keeping _refreshPending / !_refreshedOnce armed so
        // exactly ONE refresh fires the moment combat ends. Nothing is lost: an in-game loadout switch is
        // itself combat-locked, so plans only change out of combat; the RPC-free live-state re-read
        // (RefreshLiveStateIfArmed) keeps running in combat. The (Lua-backed) combat read is consulted only
        // when a refresh is otherwise due — DecideRefresh short-circuits on cooldown / nothing-pending first.
        switch (DecideRefresh(_refreshedOnce, _refreshPending, _refreshCooldown > 0, _inCombatProbe, DsWriteInFlightProbe))
        {
            case RefreshOutcome.DeferForCombat:
            case RefreshOutcome.DeferForDsWrite:
                _refreshCooldown = RefreshCooldownTicks;   // re-check ~0.66 s later, never every drain tick
                break;
            case RefreshOutcome.Fire:
                _refreshedOnce = true;
                _refreshPending = false;
                _refreshCooldown = RefreshCooldownTicks;
                InvokeChunk(RefreshChunk);
                break;
        }
    }

    /// <summary>Pure decision for the combat-gated on-demand <c>SyncProjectList</c> refresh (extracted for
    /// <c>PandaLoadoutProbeRefreshGateTests</c> — pins the invariant that the RPC is DEFERRED, never dropped,
    /// while in combat and fires exactly once combat ends). <paramref name="inCombat"/> is a delegate so the
    /// Lua-backed combat read runs ONLY when a refresh is otherwise due — never on a cooldown tick and never
    /// when nothing is pending.</summary>
    internal static RefreshOutcome DecideRefresh(bool refreshedOnce, bool refreshPending, bool cooldownActive,
        Func<bool> inCombat, Func<bool> dsWriteInFlight)
    {
        if (cooldownActive) return RefreshOutcome.Wait;
        if (refreshedOnce && !refreshPending) return RefreshOutcome.Wait;   // nothing due — no combat/write read
        // A plugin DS apply mutates cultivate state op-by-op; defer the walk until it settles (checked
        // before the combat read — a DS apply only runs out of combat, so the Lua combat read is moot here).
        if (dsWriteInFlight()) return RefreshOutcome.DeferForDsWrite;
        return inCombat() ? RefreshOutcome.DeferForCombat : RefreshOutcome.Fire;
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
        // Cross-thread mirror of the SAME value for the pre-dispatch switch gate: CallApplyAsync may run
        // off the main thread, and int? is not a torn-read-safe cross-thread read (see
        // PandaLoadoutProbe.Switch.cs § _liveCurrentPlanId).
        _liveCurrentPlanId = current ?? UnknownPlanId;
        _parsedPlans = plans;
        // CURRENT class's live equipped set + talents + equipped imagines. Shared with the merge-event
        // read path (PandaLoadoutProbe.LiveState.cs) so BOTH paths apply the same rows and run the same
        // structural change detection — this dump carries no "RESSLOT" row, hence slotsRow: null (the
        // hotbar latch is left alone; see SelectInstalledSource).
        ApplyLiveRows(raw!, slotsRow: null);
        UpdateDeepSlumberState(raw!);           // Deep-Slumber Psychoscope (season cultivate) via the SAME Lua bridge
        _loadouts = BuildBaseEntries(plans);   // gear/modules null until TryResolvePerClassDetails fills them
        _resolvePending = true;                // new data → resolve (event-driven; runs next tick)
        LogEquipProbe();   // per-class gear RE — no-op unless STELLAR_DIAGNOSTICS; data is populated here
        LogLiveContainerProbe();   // partial-account modules/talents RE (2026-08-05) — no-op unless diagnostics
    }

    // The CURRENT class's LIVE equipped set + talents (from cs.equip.equipList / cs.mod.modSlots /
    // cs.professionList.talentList[curProf] via the Lua bridge — the working live source, not the stale C#
    // latch). Overlays the current plan's saved-loadout gear so a manual equip/refine/removal shows, AND is
    // the sole source of the current class's loadout when that class has NO saved plan. Parsed from the
    // "LIVE\t<eq>\t<mod>\t<curProf>\t<stage>\t<nodes>" row; the static plan parser skips that row (its "LIVE"
    // first column fails the int-parse), so the plan-parse tests are unaffected.
    private IReadOnlyDictionary<int, long> _liveEquipUuids = EmptyUuidMap;
    private IReadOnlyDictionary<int, long> _liveModUuids = EmptyUuidMap;
    private int _liveProfessionId;
    private int _liveTalentStageId;
    private IReadOnlyList<int>? _liveTalentNodes;

    private void ReadLiveLine(string raw)
    {
        var live = ParseLiveLine(raw);   // pure static parser (unit-tested in PandaLoadoutProbeParseTests)
        _liveEquipUuids = live.Equip;
        _liveModUuids = live.Mod;
        _liveProfessionId = live.ProfessionId;
        _liveTalentStageId = live.TalentStageId;
        _liveTalentNodes = live.TalentNodes;
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

    // Per-class gear/module resolution (TryResolvePerClassDetails + _resolvePending) lives in
    // PandaLoadoutProbe.PerClassResolve.cs; served-change detection in PandaLoadoutProbe.StateChange.cs.
    // The switch write path (CallApplyAsync + its gates + PendingSwitch) lives in
    // PandaLoadoutProbe.Switch.cs.
}
