using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Implements <see cref="IFrameRateLimiter"/> by writing to Unity's
/// <c>QualitySettings.vSyncCount</c> and <c>Application.targetFrameRate</c>.
/// Called every tick (before the scene-transition gate) so any game-side FPS-cap
/// re-application (on graphics-settings change / scene load / login) is immediately
/// overridden while the uncap toggle is ON. Cheap no-op when values already match.
/// </summary>
internal sealed class FrameRateReconciler : IFrameRateLimiter
{
    private const int UncapTargetFps = 1000; // effectively uncapped (well above any monitor refresh)

    private readonly IPluginLog _log;

    // Diff-state: tracks the currently-applied uncap state so the tick only touches
    // QualitySettings when it diverges from PerfControls.Uncap (the Settings toggle /
    // boot-loaded config). Originals are captured once, on the first apply, so a
    // toggle-off restores the game's real cap rather than a guessed default.
    private bool _uncapApplied;
    private bool _uncapOrigCaptured;
    private int _origVSyncCount;
    private int _origTargetFrameRate;

    public FrameRateReconciler(IPluginLog log)
    {
        _log = log;
    }

    /// <inheritdoc/>
    public void Reconcile()
    {
        try
        {
            var on = Stellar.Abstractions.Diagnostics.PerfControls.Uncap;
            if (!_uncapOrigCaptured)
            {
                _origVSyncCount = UnityEngine.QualitySettings.vSyncCount;
                _origTargetFrameRate = UnityEngine.Application.targetFrameRate;
                _uncapOrigCaptured = true;
            }
            if (on)
            {
                if (UnityEngine.QualitySettings.vSyncCount != 0) UnityEngine.QualitySettings.vSyncCount = 0;
                if (UnityEngine.Application.targetFrameRate != UncapTargetFps) UnityEngine.Application.targetFrameRate = UncapTargetFps;
            }
            else if (_uncapApplied)   // restore once on toggle-off
            {
                UnityEngine.QualitySettings.vSyncCount = _origVSyncCount;
                UnityEngine.Application.targetFrameRate = _origTargetFrameRate;
            }
            if (on != _uncapApplied)
            {
                _uncapApplied = on;
                _log.Info($"[Perf] uncap {(on ? "ON (re-enforced each tick)" : "OFF")}: " +
                          $"vSyncCount={UnityEngine.QualitySettings.vSyncCount} targetFrameRate={UnityEngine.Application.targetFrameRate}");
            }
        }
        catch (Exception ex) { _log.Warning($"[Perf] uncap reconcile threw: {ex.Message}"); }
    }
}
