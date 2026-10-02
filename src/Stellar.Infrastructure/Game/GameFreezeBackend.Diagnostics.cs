using System.Linq;
using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game;

/// <summary>StellarDiagnostics-gated logging for the freeze backend. The <c>freeze held:</c> line on unfreeze is the
/// owner-run proof of the combat-resume fix: per entity kind, how many game <c>set_Speed</c> writes the gate turned into 0
/// (a real speed stopped), and how many tracked entities were still moving right before the restore (<c>resumed</c>
/// should be 0 — anything else means the game moved them by a path the gate does not see).</summary>
internal sealed partial class GameFreezeBackend
{
    private int _resumedAtUnfreeze;

    partial void OnFrozen(int effects, int factors, int held)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] freeze on: effects={effects} entities={_ids.Count} attrFrozen={factors} positionsHeld={held} " +
                  $"excluded=[{string.Join(",", _ledger.Excluded)}] self={_ledger.Self} speedGate={(_speedGateInstalled ? "on" : "OFF")}");
    }

    partial void OnStage2(int drawnFrozen)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] freeze stage 2: drawn speed frozen={drawnFrozen} gateTracked={_speedGate.Tracked}");
    }

    partial void OnAppearFrozen(long uuid, int kind)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] appeared while frozen: uuid={uuid} kind={kind}");
    }

    partial void OnExcluded(long uuid, int kind, string why)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] never frozen: uuid={uuid} kind={kind} ({why}) self={_ledger.Self} riding={_entities.RiddenVehicle()}");
    }

    // Before the gate disarms: one read per tracked entity (diagnostics only).
    partial void OnUnfreezing()
    {
        _resumedAtUnfreeze = 0;
        if (!StellarDiagnostics.IsEnabled || _getSpeed is null) return;
        foreach (var uuid in _speedGate.TrackedUuids.Distinct().ToList())
        {
            try
            {
                if (_entities.LiveModel(_entities.EntityByUuid(uuid)) is { } m && _animComp!(m) is { } comp &&
                    _getSpeed(comp) > FreezeLedger.SpeedEpsilon) _resumedAtUnfreeze++;
            }
            catch { /* diagnostics only */ }
        }
    }

    partial void OnUnfrozen(int effects, int factors, int speeds)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] freeze off: effects unfrozen={effects} factors restored={factors} drawn speeds restored={speeds}");
        var g = _speedGate;
        _log.Info($"[FreeCam] freeze held: monster={g.Held(DrawnSpeedGate.Bucket.Monster)} player={g.Held(DrawnSpeedGate.Bucket.Player)} " +
                  $"npc={g.Held(DrawnSpeedGate.Bucket.Npc)} pet={g.Held(DrawnSpeedGate.Bucket.Pet)} mount={g.Held(DrawnSpeedGate.Bucket.Mount)} " +
                  $"other={g.Held(DrawnSpeedGate.Bucket.Other)} resumed={_resumedAtUnfreeze} gate={(_speedGateInstalled ? "on" : "OFF")}");
    }
}
