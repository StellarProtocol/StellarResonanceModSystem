using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game;

internal sealed partial class GameEffectVisibility
{
    private const int MaxClassifiedLines = 400;
    private const int MaxClassifiedWithCasterUids = 300;
    private int _classifiedLines;
    // First-sighting cap keyed by uid, not a per-call counter: Sweep re-classifies every listed uid on every
    // Apply/Reassert, so a counter of calls fills the budget in a handful of sweeps and a live effect further
    // down the list (a summon's, a party member's) is never logged. Bounded at MaxClassifiedWithCasterUids entries.
    private readonly HashSet<long> _classifiedWithCasterUids = new();
    private int _mineHidden, _partyHidden, _othersHidden, _monstersHidden;   // reset every OnSwept — per-sweep, not cumulative

    partial void OnClassified(long uid, long from, long belong, VisibilityLayers owner)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        LogUnresolved(uid, from, belong, owner);
        LogClassifiedWithCaster(uid, from, belong, owner);
    }

    private void LogUnresolved(long uid, long from, long belong, VisibilityLayers owner)
    {
        if (owner != VisibilityLayers.None || _classifiedLines >= MaxClassifiedLines) return;
        if (from == 0 && belong == 0) return;   // scenery: expected, not interesting
        _classifiedLines++;
        _log.Info($"[EffectHide] unresolved uid={uid} from={from} belong={belong}");
    }

    /// <summary>The owner's in-game pass could not be run for this change — this is its replacement: the first 300
    /// DISTINCT classified effects that had a caster (one line per uid, the first time it is seen), so a wrong
    /// owner can be spotted straight from the log (never from the unresolved-only line above, which only fires when
    /// owner is None) without the budget being consumed by the same handful of long-lived effects re-classified on
    /// every sweep.</summary>
    private void LogClassifiedWithCaster(long uid, long from, long belong, VisibilityLayers owner)
    {
        if (from == 0 && belong == 0) return;   // scenery: never logged, never counted
        if (_classifiedWithCasterUids.Contains(uid)) return;   // already logged this uid once
        if (_classifiedWithCasterUids.Count >= MaxClassifiedWithCasterUids) return;   // budget exhausted
        _classifiedWithCasterUids.Add(uid);
        _log.Info($"[EffectHide] classified uid={uid} from={from}({Kind(from)}) belong={belong}({Kind(belong)}) owner={owner}");
    }

    /// <summary>Simple, low-bits-only classification (EntityId's own 640=player / 64,32832=monster markers) for a log
    /// label. Deliberately never relabels a caster "self" from <paramref name="uuid"/>'s own uuid — <c>owner</c> is
    /// printed separately so summon resolution (a monster-uuid caster whose owner is EffectsMine) stays visible.</summary>
    private static string Kind(long uuid)
    {
        if (uuid == 0) return "none";
        var low = uuid & 0xFFFF;
        if (low == 640) return "player";
        if (low == 64 || low == 32832) return "monster";
        return $"other(low={low})";
    }

    partial void OnSweepHidden(VisibilityLayers owner)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        switch (owner)
        {
            case VisibilityLayers.EffectsMine: _mineHidden++; break;
            case VisibilityLayers.EffectsParty: _partyHidden++; break;
            case VisibilityLayers.EffectsOthers: _othersHidden++; break;
            case VisibilityLayers.EffectsMonsters: _monstersHidden++; break;
        }
    }

    partial void OnSwept(VisibilityLayers wanted, int hidden, int shown, int held)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[EffectHide] apply wanted={wanted} hidden={hidden} shown={shown} held={held} " +
                  $"mine={_mineHidden} party={_partyHidden} others={_othersHidden} monsters={_monstersHidden}");
        _mineHidden = _partyHidden = _othersHidden = _monstersHidden = 0;
    }
}
