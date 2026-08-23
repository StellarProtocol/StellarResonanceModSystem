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
    private bool _resolvePending;

    // Sentinel identity for the synthesized current-class entry: the class the player is actively using
    // when it has NO saved loadout plan. A negative index cannot collide with a real (positive) planId, and
    // it is never a switch target (there is no server-side plan to switch to). Owner requirement 2026-08-05:
    // the current class must always reflect the live equipped set, saved loadout or not.
    private const int LiveCurrentIndex = -1;
    private const string LiveCurrentName = "Current";

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
