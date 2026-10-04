namespace Stellar.Application.Services;

/// <summary>
/// The player-readable notes a ReShade capture can carry in <c>CaptureResult.Notes</c>. They are English framework
/// strings with no codes: a plugin that shows them in the player's language matches the exact text, so a text here
/// must never change once released (add a new one instead).
/// </summary>
internal static class ReShadeCaptureNotes
{
    /// <summary>The warm-up did not get ReShade to draw within its deadline (fw 2.17.0 text; plugins match it).</summary>
    internal const string NotReady = "ReShade was not ready — photo taken without it.";

    /// <summary>The bridge reported a hard error (no ReShade runtime, or the photo's render target was unusable).</summary>
    internal const string Error = "ReShade could not draw into the photo — photo taken without it.";

    /// <summary>The real render kept drawing nothing until the deadline (ReShade kept reloading at the photo's size).</summary>
    internal const string DrewNothing = "ReShade drew nothing into the photo in time — photo taken without it.";

    /// <summary>Every active technique had to be left out of this photo shape (depth or screen-size only).</summary>
    internal const string NothingToDraw = "None of the active ReShade effects work in this photo shape — photo taken without them.";

    /// <summary>A window-shaped 2×/4× photo with a size-locked technique on was taken at 1× (screen size) with ReShade.</summary>
    internal const string ScreenSizeOnly = "Some ReShade effects only work at screen size, so this photo was taken at 1×.";

    /// <summary>A shaped photo (never screen-sized) left its size-locked techniques out.</summary>
    internal const string ScreenSizeOnlySkipped = "Some ReShade effects only work at screen size, so they were left out of this photo.";

    /// <summary>The note for a plan that ended without ReShade, or null when it drew (or is still running).</summary>
    internal static string? For(ReShadeWarmUpOutcome outcome) => outcome switch
    {
        ReShadeWarmUpOutcome.TimedOut => NotReady,
        ReShadeWarmUpOutcome.Error => Error,
        ReShadeWarmUpOutcome.RenderDrewNothing => DrewNothing,
        ReShadeWarmUpOutcome.NothingActive => NothingToDraw,
        _ => null,
    };
}
