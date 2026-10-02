namespace Stellar.Infrastructure.Game;

/// <summary>The unfreeze steps <see cref="FreezeTeardown.Run"/> calls in its pinned order (explicit, so they stay off the
/// backend's own surface).</summary>
internal sealed partial class GameFreezeBackend
{
    void IFreezeTeardownSteps.DisarmGate()
    {
        _speedGate.Disarm();
        _ecsGate.Disarm();   // the tracked models stay for RestoreEcsLayers
    }

    void IFreezeTeardownSteps.StopHold() => StopHold();

    void IFreezeTeardownSteps.RestoreDrawnSpeeds() => RestoreDrawnSpeeds();

    void IFreezeTeardownSteps.RestoreFactors() => RestoreFactors();

    void IFreezeTeardownSteps.RestoreEcsLayers() => RestoreEcsLayers();

    void IFreezeTeardownSteps.UnfreezeEffects() => UnfreezeEffects();

    void IFreezeTeardownSteps.ClearLedger()
    {
        _ledger.Clear();
        _ecsGate.Clear();
        _fxInstances.Clear();
    }
}
