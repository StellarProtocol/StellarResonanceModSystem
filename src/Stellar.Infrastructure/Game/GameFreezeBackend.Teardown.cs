namespace Stellar.Infrastructure.Game;

/// <summary>The unfreeze steps <see cref="FreezeTeardown.Run"/> calls in its pinned order (explicit, so they stay off the
/// backend's own surface).</summary>
internal sealed partial class GameFreezeBackend
{
    void IFreezeTeardownSteps.ReleaseAnim() => ReleaseAnim();

    void IFreezeTeardownSteps.StopHold() => StopHold();

    void IFreezeTeardownSteps.ResumeClock() => _clock.Resume();

    void IFreezeTeardownSteps.ClearLedger() => _ledger.Clear();
}
