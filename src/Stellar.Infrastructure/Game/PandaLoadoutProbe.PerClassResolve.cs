using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Per-class gear/module resolution for <see cref="PandaLoadoutProbe"/> — split out of
/// <c>PandaLoadoutProbe.cs</c> (2026-08-23) to keep that file under the 500-LoC standards gate while
/// the container-merge change-event plumbing landed. Behaviour is unchanged by the move.
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    // Resolves each plan's per-class gear + modules from its slot→uuid maps via the injected resolver, and
    // overlays the CURRENT class with its live equipped set. FULLY EVENT-DRIVEN — no polling: runs only when
    // _resolvePending is set, which happens on a new parse and on OnGearChanged (fired from SelfGearChanged =
    // the container-merge event). The FIRST container sync (method-21, which latches the item container this
    // resolve reads) fires that SAME event — so a resolve that ran before the container was ready simply
    // re-runs when the sync lands. No retry loop, no per-tick scan: the flag gates the one
    // (whole-item-container) scan to exactly the events that change its inputs.
    //
    // The sibling partials only ever SET this flag (it is the arm). The drain tick CONSUMES it once per
    // tick into _resolveArmed — see ObservePerClassResolveArm — so the debounce below can tell a fresh
    // delta from one it has already counted. TryResolvePerClassDetails still reads and clears it, so the
    // regression pins that invoke that method directly keep working unchanged.
    private bool _resolvePending;

    // Sentinel identity for the synthesized current-class entry: the class the player is actively using
    // when it has NO saved loadout plan. A negative index cannot collide with a real (positive) planId, and
    // it is never a switch target (there is no server-side plan to switch to). Owner requirement 2026-08-05:
    // the current class must always reflect the live equipped set, saved loadout or not.
    private const int LiveCurrentIndex = -1;
    private const string LiveCurrentName = "Current";

    // DEBOUNCE the resolve on the TRAILING edge of the delta burst.
    //
    // The resolve is a WHOLE-item-container walk: PandaInventoryPullReader.PerClassLoadout builds a uuid
    // index over every item in every package and then reads a gear instance per slot per plan. It is
    // event-driven (never polled), but the events come in BURSTS: one loadout switch = a full server
    // re-equip = a CharSerialize delta per changed field, each one an OnGearChanged → _resolvePending, so
    // ungated the walk ran at the full drain rate (~30 Hz) for the length of the burst — the allocation
    // class measured at ~1.8 MB/s → 100-475 ms GC frames in the 2026-07-25 A/B. Owner report 2026-09-05:
    // three same-loadout switches in a row overlapped their bursts into a 3,261 ms frametime spike.
    //
    // A LEADING-edge cooldown (walk now, then wait) still walked once per window THROUGH the burst — a
    // ~1 s burst cost two walks, both against half-applied state. Trailing edge instead: every new arm
    // restarts a quiet timer and the walk runs only once the burst has STOPPED, so one loadout switch =
    // ONE walk, against the burst's final state.
    //
    // DEFER, NEVER DROP — the two guarantees that make that safe:
    //   • the arm is never lost. _resolvePending is consumed per tick into _resolveArmed (our sticky
    //     copy, cleared ONLY by the walk), which is what turns "still armed" into "armed AGAIN this
    //     tick" — a plain bool cannot distinguish those, and the arming sites live in sibling partials.
    //   • lateness is BOUNDED. ResolveMaxDeferTicks forces a walk during an endless delta stream, so
    //     "late, never stale" still holds with a hard ceiling of ~2 s.
    private bool _resolveArmed;
    private int  _resolveQuietTicks;
    private int  _resolveDeferTicks;
    private const int ResolveQuietTicks    = 15;   // ~0.5 s at the 30 Hz loadout drain — burst is over
    private const int ResolveMaxDeferTicks = 60;   // ~2 s — hard ceiling under a never-ending stream

    // ALWAYS ON, and deliberately NOT behind StellarDiagnostics in the .Diagnostics.cs partial: the line
    // is SELF-LIMITING (a healthy walk logs nothing at all), and it must be readable in the owner's very
    // next log WITHOUT a diagnostics restart — its whole job is to say whether this walk is the frame
    // spike being reported.
    private const long SlowWalkMs = 33;   // ~2 frames at 60 Hz

    /// <summary>Outcome of the debounce gate on the per-class resolve — see <see cref="DecideResolve"/>.</summary>
    internal enum ResolveOutcome { Wait, Resolve }

    /// <summary>Pure decision for the trailing-edge debounced per-class resolve (pinned by
    /// <c>PandaLoadoutProbeResolveGateTests</c>; origin = owner report 2026-09-05, same-loadout re-press
    /// froze the client for 3,261 ms). An armed resolve WAITS while the delta burst is still arriving
    /// (<paramref name="quietTicks"/> below <see cref="ResolveQuietTicks"/>) and runs once it goes quiet;
    /// <paramref name="deferTicks"/> at <see cref="ResolveMaxDeferTicks"/> forces the walk so lateness is
    /// bounded. Deferred, never dropped.</summary>
    internal static ResolveOutcome DecideResolve(
        bool resolveArmed, bool resolverAttached, bool hasInputs, int quietTicks, int deferTicks)
    {
        if (!resolveArmed || !resolverAttached) return ResolveOutcome.Wait;
        if (!hasInputs) return ResolveOutcome.Wait;                     // nothing saved and nothing equipped
        if (deferTicks >= ResolveMaxDeferTicks) return ResolveOutcome.Resolve;   // bounded lateness
        if (quietTicks < ResolveQuietTicks) return ResolveOutcome.Wait; // burst still arriving — stays armed
        return ResolveOutcome.Resolve;
    }

    /// <summary>One drain tick of the debounced resolve: fold this tick's arm into the sticky flag,
    /// advance the quiet/defer counters, ask <see cref="DecideResolve"/>, and run the walk when it says
    /// so. The gate lives HERE, around the walk, rather than inside it —
    /// <see cref="TryResolvePerClassDetails"/> stays the pure "do it now" step the live-state /
    /// Deep-Slumber regression pins drive directly.</summary>
    private void TryResolvePerClassDetailsIfDue()
    {
        ObservePerClassResolveArm();

        var hasInputs = _parsedPlans.Count > 0 || _liveEquipUuids.Count > 0 || _liveModUuids.Count > 0;
        if (DecideResolve(_resolveArmed, _resolveGear is not null, hasInputs, _resolveQuietTicks, _resolveDeferTicks)
            == ResolveOutcome.Wait) return;

        _resolveArmed = false;
        _resolveQuietTicks = 0;
        _resolveDeferTicks = 0;
        _resolvePending = true;   // the walk's own guard — TryResolvePerClassDetails clears it again
        RunPerClassResolveTimed();
    }

    // Consume the shared arm flag ONCE per tick. OnGearChanged sets _resolvePending from the network
    // thread on every container merge; reading-and-clearing it here is what makes a re-arm observable to
    // the debounce (the sibling partials that arm it are outside this file, and a bool cannot carry
    // "again"). The arm itself is not lost — it moves into _resolveArmed, which only the walk clears.
    private void ObservePerClassResolveArm()
    {
        if (_resolvePending)
        {
            _resolvePending = false;
            if (!_resolveArmed) { _resolveArmed = true; _resolveDeferTicks = 0; }
            _resolveQuietTicks = 0;   // a new delta — restart the quiet window
        }
        else if (_resolveArmed)
        {
            if (_resolveQuietTicks < ResolveQuietTicks) _resolveQuietTicks++;   // clamped — cannot overflow
        }

        // The defer cap counts EVERY armed tick, delta or not. Counting only quiet ticks would let a
        // dense-enough stream hold the cap back indefinitely — a delta on every tick would starve the
        // walk outright, which is the exact failure the cap exists to prevent.
        if (_resolveArmed && _resolveDeferTicks < ResolveMaxDeferTicks) _resolveDeferTicks++;
    }

    // The walk, timed. See SlowWalkMs for why the line is always on rather than diagnostics-gated.
    private void RunPerClassResolveTimed()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        TryResolvePerClassDetails();
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000L / System.Diagnostics.Stopwatch.Frequency;
        if (ms > SlowWalkMs) _log.Info($"[PerClassLoadout] walk {ms}ms plans={_parsedPlans.Count}");
    }

    private void TryResolvePerClassDetails()
    {
        if (!_resolvePending || _resolveGear is null) return;
        var hasLive = _liveEquipUuids.Count > 0 || _liveModUuids.Count > 0;
        if (_parsedPlans.Count == 0 && !hasLive) return;   // nothing saved and nothing equipped — nothing to resolve
        _resolvePending = false;   // one attempt per event; if the container isn't ready, the next sync re-arms it

        var request = new List<(IReadOnlyDictionary<int, long>, IReadOnlyDictionary<int, long>)>(_parsedPlans.Count + 1);
        foreach (var p in _parsedPlans) request.Add((p.EquipUuids, p.ModUuids));
        // Append the CURRENT class's LIVE set as the LAST entry so it resolves in the SAME item-index pass.
        if (hasLive) request.Add((_liveEquipUuids, _liveModUuids));

        var results = _resolveGear(request);   // one pass; builds the item index once
        var ready = false;
        foreach (var (gear, modules) in results)
            if (gear.Count > 0 || modules.Count > 0) { ready = true; break; }
        if (!ready) return;   // container not synced yet — keep base entries; the container-sync event re-arms us

        // The live equipped result (last request entry), if it resolved to anything.
        (IReadOnlyList<GearInstance> Gear, IReadOnlyDictionary<int, ModuleInfo> Modules)? live =
            hasLive && results.Count > _parsedPlans.Count ? results[_parsedPlans.Count] : null;

        _loadouts = BuildUpgradedEntries(results, live);
        // The served gear/modules now reflect the live read that armed this resolve — the ONLY point at
        // which ILoadout.LiveStateChanged may be raised (see _liveStatePendingPublish). Every early return
        // above leaves the change armed, so it is delivered on the tick the data actually lands.
        PublishLiveStateChangeIfArmed();
        LogPerClassResolved(_loadouts);   // no-op unless STELLAR_DIAGNOSTICS
    }

    // Projects the resolver's per-plan results onto the parsed plans, overlaying the CURRENT plan with the
    // live equipped set and synthesizing a "Current" entry when the active class has no saved plan.
    private List<LoadoutEntry> BuildUpgradedEntries(
        IReadOnlyList<(IReadOnlyList<GearInstance> Gear, IReadOnlyDictionary<int, ModuleInfo> Modules)> results,
        (IReadOnlyList<GearInstance> Gear, IReadOnlyDictionary<int, ModuleInfo> Modules)? live)
    {
        var upgraded = new List<LoadoutEntry>(_parsedPlans.Count + 1);
        var currentClassCovered = false;
        for (var i = 0; i < _parsedPlans.Count; i++)
        {
            var p = _parsedPlans[i];
            var (gear, modules) = i < results.Count ? results[i] : (Array.Empty<GearInstance>(), (IReadOnlyDictionary<int, ModuleInfo>)EmptyModules);
            // Overlay: the CURRENT plan uses its LIVE equipped set (reflects manual edits) when the live
            // resolve produced anything; other plans keep their saved-loadout gear/modules.
            if (p.Index == _currentId && live is { } lv && (lv.Gear.Count > 0 || lv.Modules.Count > 0))
                (gear, modules) = lv;
            if (p.ProfessionId == _liveProfessionId && _liveProfessionId != 0) currentClassCovered = true;
            upgraded.Add(new LoadoutEntry(p.Index, p.Name, p.ProfessionId, p.TalentStageId, p.TalentNodes, gear, modules));
        }

        // Owner requirement (2026-08-05): when the CURRENT class has NO saved plan, the capture must still
        // carry what the player is actually using — so synthesize a current-class entry straight from the
        // live equipped gear/modules + live talents. Without this, a class with no saved loadout produced no
        // entry at all, and the plugin uploaded gear=0 modules=0 talentNodes=null.
        if (!currentClassCovered && _liveProfessionId != 0
            && live is { } lc && (lc.Gear.Count > 0 || lc.Modules.Count > 0))
        {
            upgraded.Add(new LoadoutEntry(
                LiveCurrentIndex, LiveCurrentName, _liveProfessionId, _liveTalentStageId, _liveTalentNodes,
                lc.Gear, lc.Modules));
        }

        return upgraded;
    }

    private static readonly IReadOnlyDictionary<int, ModuleInfo> EmptyModules = new Dictionary<int, ModuleInfo>(0);
}
