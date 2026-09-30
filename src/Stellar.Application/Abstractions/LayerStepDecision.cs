namespace Stellar.Application.Abstractions;

/// <summary>
/// Decides whether <c>GameVisibilityBackend.Step</c> must invoke a layer's game call this tick, and how the
/// layer's "restore pending" bit should move afterward. Pure — no Unity/game types — so the decision that used
/// to strand a layer hidden (a failed restore never retried because <c>Reassert</c> returned early when nothing
/// was reported held) is unit-tested directly. See also <c>EntityShowPlan</c> (the same shape for the
/// OtherPlayers layer) and <see cref="VisibilityProbeDecision"/> (probe-availability decisions).
/// </summary>
internal static class LayerStepDecision
{
    /// <param name="want">The caller currently wants this layer hidden.</param>
    /// <param name="have">The backend's last-known applied state for this layer (<c>_applied</c>).</param>
    /// <param name="force">Re-assert: forces a HELD hide to re-issue its game call even when unchanged.</param>
    /// <param name="restorePending">A previous restore (show) attempt for this layer failed and none has
    /// succeeded since — the call must keep retrying even while <paramref name="have"/> is false.</param>
    public static bool ShouldInvoke(bool want, bool have, bool force, bool restorePending) =>
        want != have || (force && want) || (!want && restorePending);

    /// <summary>
    /// The restore-pending bit after a call was made. Only a restore attempt (<paramref name="want"/> is false)
    /// resolves it: still pending when the call failed, cleared the moment it succeeds. A hide call
    /// (<paramref name="want"/> is true) leaves whatever pending state already existed untouched.
    /// </summary>
    public static bool NextRestorePending(bool want, bool ok, bool wasPending) => !want ? !ok : wasPending;
}
