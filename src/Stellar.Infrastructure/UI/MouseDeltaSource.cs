namespace Stellar.Infrastructure.UI;

/// <summary>Chooses the free-camera mouse delta. The game's ZMouseManager locks the cursor while RMB is held over the game
/// view, so Input.mousePosition freezes; the raw "Mouse X"/"Mouse Y" axes (defined in the game's InputManager) keep
/// reporting movement. Axis units are counts × the axis sensitivity (Unity default 0.1), scaled back to ~pixels.</summary>
internal static class MouseDeltaSource
{
    internal const float AxisToPixels = 10f;

    /// <summary>Delta in pixels with a top-left origin (down = +).</summary>
    public static (float X, float Y) Pick(bool axisOk, float axisX, float axisY, float posDx, float posDy) =>
        axisOk ? (axisX * AxisToPixels, -axisY * AxisToPixels) : (posDx, posDy);
}
