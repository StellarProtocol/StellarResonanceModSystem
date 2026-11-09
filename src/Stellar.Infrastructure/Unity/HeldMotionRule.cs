namespace Stellar.Infrastructure.Unity;

/// <summary>Whether a frame counts as USER motion for pixelPerfect (keeps the owning canvas OFF this frame), for a
/// press-driven gesture: a list drag-scroll, a window move (title bar / frame) or a window resize. The gesture is one
/// unit: the FIRST real move (&gt; 0.5 px) while the button is held "engages" the hold, and from then until the button is
/// released every frame counts — even frames where nothing moves. Before, only frames that actually moved counted, so a
/// slow drag that paused &gt; the 0.15 s settle between steps re-enabled pixelPerfect and the next step disabled it again:
/// 17 flips (≈2 ms write + 15 ms canvas rebuild each) on one launcher drag-scroll (71728ac log). A press that never moves
/// anything flips nothing. Once released, only a list glide counts (moving with fling velocity; windows never glide), so
/// pixelPerfect returns after the normal settle once the release / glide ends.</summary>
internal static class HeldMotionRule
{
    /// <param name="pressed">The button is held since a press on this list / drag handle / resize grip.</param>
    /// <param name="moved">The list content (or window position/size) moved &gt; 0.5 px since last frame.</param>
    /// <param name="gliding">The list's ScrollRect still has fling velocity (only a user drag creates it); false for windows.</param>
    /// <param name="engaged">Per-gesture hold state: set by the first move of a hold, cleared once released.</param>
    public static bool CountsAsMotion(bool pressed, bool moved, bool gliding, ref bool engaged)
    {
        if (!pressed) { engaged = false; return moved && gliding; }
        if (moved) engaged = true;
        return engaged;
    }
}
