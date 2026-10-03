using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game;

internal sealed partial class GameEffectVisibility
{
    private const int MaxClassifiedLines = 400;
    private const int MaxClassifiedWithCasterLines = 300;
    private int _classifiedLines;
    private int _classifiedWithCasterLines;
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
    /// classified effects that had a caster, so a wrong owner can be spotted straight from the log (never from the
    /// unresolved-only line above, which only fires when owner is None).</summary>
    private void LogClassifiedWithCaster(long uid, long from, long belong, VisibilityLayers owner)
    {
        if ((from == 0 && belong == 0) || _classifiedWithCasterLines >= MaxClassifiedWithCasterLines) return;
        _classifiedWithCasterLines++;
        var fromIsCaster = from != 0;   // ReadContext's own rule: caster = FromUuid, else BelongUuid
        _log.Info($"[EffectHide] classified uid={uid} from={from}({Kind(from, fromIsCaster, owner)}) " +
                  $"belong={belong}({Kind(belong, !fromIsCaster, owner)}) owner={owner}");
    }

    /// <summary>Simple, low-bits-only classification (EntityId's own 640=player / 64,32832=monster markers) for a
    /// log label — "self" only for the slot that actually served as caster when the real classifier (which also
    /// walks summon ownership) already resolved that effect to EffectsMine.</summary>
    private static string Kind(long uuid, bool isCaster, VisibilityLayers owner)
    {
        if (uuid == 0) return "none";
        if (isCaster && owner == VisibilityLayers.EffectsMine) return "self";
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
