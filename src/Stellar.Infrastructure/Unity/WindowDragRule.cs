namespace Stellar.Infrastructure.Unity;

/// <summary>Whether a press on a window's drag handle starts a WINDOW drag. A title-bar-less <c>Draggable</c> window uses
/// its whole frame as the handle, so without this every press moved the window: dragging inside a scroll list moved the
/// window instead of scrolling it, and press-and-drag on a button moved the window. So in play a press on an interactive
/// control, or inside a list that actually scrolls, belongs to that control; headers, text, panel backgrounds and gaps
/// still drag the window. Layout edit mode keeps "drag from anywhere" (edit-only overlays and every window there are moved
/// as a whole, their controls aren't being used).</summary>
internal static class WindowDragRule
{
    public static bool ShouldStart(bool layoutEditing, bool onInteractive, bool onOverflowingScroll)
        => layoutEditing || (!onInteractive && !onOverflowingScroll);

    // Half a canvas unit of slack: a list whose content exactly fits (layout rounding) does not scroll, so it stays a
    // drag surface.
    private const float OverflowSlack = 0.5f;

    /// <summary>A ScrollRect axis "scrolls" only when it is enabled AND its content is larger than the viewport. A list
    /// that fits without scrolling has nothing to drag, so a press there still moves the window.</summary>
    public static bool AxisOverflows(bool axisEnabled, float contentSize, float viewportSize)
        => axisEnabled && contentSize > viewportSize + OverflowSlack;
}
