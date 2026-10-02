using System;
using Stellar.Abstractions.Services;
namespace Stellar.Infrastructure.Game;

/// <summary>Restores: every entity on unfreeze (drawn speeds first, then the factors — players' speed is recomputed from
/// the factor; the order is <see cref="FreezeTeardown"/>'s, pinned), and ONE entity mid-freeze when it turns out to be
/// excluded after the press froze it (review M1: the local player when the press read uuid 0; review I1: the mount the
/// local player rides or drives, learned at stage 2 or by the game's vehicle ride event). The <c>set_Speed</c> gate is disarmed / untracked before any restore write. Each entity
/// runs in its own try: one failure never skips the rest.</summary>
internal sealed partial class GameFreezeBackend
{
    private void RestoreDrawnSpeeds()
    {
        if (_setSpeed is null) return;
        foreach (var kv in _ledger.Speeds) RestoreSpeedOf(kv.Key, kv.Value);
    }

    private void RestoreFactors()
    {
        foreach (var kv in _ledger.Factors) RestoreFactorOf(kv.Key, kv.Value);
    }

    private void RestoreSpeedOf(long uuid, float prior)
    {
        try
        {
            if (_entities.LiveModel(_entities.EntityByUuid(uuid)) is not { } m || _animComp!(m) is not { } comp) return;
            if (FreezeLedger.RestoreSpeed(prior, _getSpeed!(comp)) is float restore) WriteSpeed(comp, restore);
        }
        catch (Exception ex) { WarnOnce("speedrestore", "could not restore an entity's drawn speed: " + ex.Message); }
    }

    private void RestoreFactorOf(long uuid, float prior)
    {
        try
        {
            if (_entities.EntityByUuid(uuid) is not { } entity) return;   // left / despawned: nothing to restore
            var current = Convert.ToSingle(_getFactor!.Invoke(null, new[] { entity }));
            if (FreezeLedger.RestoreValue(prior, current) is not float restore) return;
            _setFactor!.Invoke(null, new object[] { entity, restore });
            Recalc(entity);
        }
        catch (Exception ex) { WarnOnce("animrestore", "could not restore an entity's animation: " + (ex.InnerException ?? ex).Message); }
    }

    /// <summary>Stage 2: <see cref="FreezeTargets.Recheck"/> (local player learned late, the mount they now ride), then each
    /// newly excluded entity is released and dropped from this press's list.</summary>
    private void ReleaseLateExclusions()
    {
        FreezeTargets.Recheck(_entities, _ledger, _ids, _released);
        foreach (var uuid in _released) ReleaseEntity(uuid, "stage 2 re-check");
        if (_released.Count > 0) _ledger.WithoutSelf(_ids);
    }

    /// <summary>Undoes what this freeze did to one now-excluded entity: off the gate, drawn speed and factor restored,
    /// out of the hold (snapped to its logical position), forgotten by the ledger.</summary>
    private void ReleaseEntity(long uuid, string why)
    {
        _speedGate.Untrack(uuid);
        if (_ledger.Speeds.TryGetValue(uuid, out var speed) && _setSpeed is not null) RestoreSpeedOf(uuid, speed);
        if (_ledger.Factors.TryGetValue(uuid, out var factor)) RestoreFactorOf(uuid, factor);
        _ledger.Forget(uuid);
        Unhold(uuid);
        OnReleased(uuid, why);   // the kind is read inside the diagnostics gate
    }

    /// <summary>Our own <c>set_Speed</c> write: passes the gate untouched.</summary>
    private void WriteSpeed(object comp, float value)
    {
        _speedGate.OwnWrite = true;
        try { _setSpeed!(comp, value); }
        finally { _speedGate.OwnWrite = false; }
    }
}
