namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound port implemented in Infrastructure (<see cref="Stellar.Infrastructure.Unity.FrameRateReconciler"/>).
/// Re-enforces the desired frame-rate / vSync state every tick so the game's own FPS-cap re-application
/// (on graphics-settings change / scene load / login) does not override the user's Stellar Perf toggle.
/// </summary>
internal interface IFrameRateLimiter
{
    /// <summary>
    /// Reconciles <c>QualitySettings.vSyncCount</c> and <c>Application.targetFrameRate</c> against the
    /// current <see cref="Stellar.Abstractions.Diagnostics.PerfControls.Uncap"/> flag. Cheap no-op when the
    /// live values already match; writes only on divergence. Logs once on each ON→OFF / OFF→ON transition.
    /// Must be called every tick, before the scene-transition gate.
    /// </summary>
    void Reconcile();
}
