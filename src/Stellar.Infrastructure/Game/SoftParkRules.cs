namespace Stellar.Infrastructure.Game;

/// <summary>What one apply does to a window's root on the way to shown / hidden (WindowRenderer.SetShown).</summary>
/// <param name="StaySoftParked">Hidden by its gate while soft-parked: leave it as it is (already invisible) — a
/// deactivation now would pay the whole OnDisable cascade the soft park exists to avoid.</param>
/// <param name="WasHidden">The window was not on screen before this apply (a real show if it is shown now).</param>
/// <param name="SoftUnpark">Shown from a soft park: canvas back on, nothing re-enabled, layout kept.</param>
/// <param name="PrepareReshow">Shown from a deactivated root: parked KeepAlive branches re-warm in the background.</param>
/// <param name="ResetLayout">Shown from a deactivated root: re-arm the immediate first layout.</param>
internal readonly record struct SoftParkShowPlan(bool StaySoftParked, bool WasHidden, bool SoftUnpark, bool PrepareReshow, bool ResetLayout);

/// <summary>The Unity-free decisions of the soft park (WindowRenderer.SoftPark.cs; pure, unit-tested).</summary>
internal static class SoftParkRules
{
    /// <summary>A soft-parked window with no pending background work is re-applied every Nth capped apply tick (~0.5 s).</summary>
    public const int RefreshEveryTicks = 5;

    /// <summary>Soft-park instead of SetActive(false): only a window whose root is still active (a ShouldRender-hidden
    /// root is already inactive), with its own canvas to switch off (IsolateCanvas / OverrideSortingOrder), holding
    /// KeepAlive branches (the big always-active trees whose OnDisable cascade made the close frame slow), and not a
    /// click-away popup (the ticker's Escape dismiss reads activeInHierarchy, not position).</summary>
    public static bool CanSoftPark(bool rootActive, bool hasOwnCanvas, int keepAlives, bool popup)
        => rootActive && hasOwnCanvas && keepAlives > 0 && !popup;

    /// <summary>Background apply of a soft-parked window: never on the tick it parked (it was just applied), then at once
    /// while background work is pending (budgeted bubble measures, a KeepAlive refresh, a prebuild reveal waiting for an
    /// apply), else every <see cref="RefreshEveryTicks"/>.</summary>
    public static bool ShouldRefresh(int ticksSincePark, bool pendingWork)
        => ticksSincePark > 0 && (pendingWork || ticksSincePark % RefreshEveryTicks == 0);

    /// <summary>The root transition for one apply. <paramref name="rootActive"/> = activeSelf before it.</summary>
    public static SoftParkShowPlan PlanShow(bool softParked, bool rootActive, bool hideAll)
    {
        if (hideAll && softParked) return new SoftParkShowPlan(true, true, false, false, false);
        var wasHidden = !rootActive || softParked;
        var deactivatedReshow = !hideAll && wasHidden && !softParked;
        return new SoftParkShowPlan(false, wasHidden, !hideAll && softParked, deactivatedReshow, deactivatedReshow);
    }
}
