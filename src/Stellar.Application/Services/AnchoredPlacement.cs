using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>The overlay canvas in canvas units (screen ÷ scaleFactor) plus the UI-scale slider value.</summary>
internal readonly record struct CanvasDims(float Width, float Height, float UiScale);

/// <summary>
/// Anchor-relative window placement (framework 2.22.0, owner report 2026-10-10: the Photo Studio free-camera HUD pills
/// sat 220 px right of centre). A saved position used to be the absolute top-left only, so a top-centred window kept its
/// old left edge when its width changed (800 → 1000 → 1240) and its centre drifted. A window anchored to a canvas point
/// now also saves the offset of its matching point (top-centre for <see cref="WindowAnchor.Top"/>, …) from the canvas's
/// anchor point, and is re-placed from that offset with the CURRENT canvas and width — so a centred window stays centred
/// at every resolution, aspect ratio, UI scale and width. The same maths as <c>ResolveAnchoredDefault</c>: offsets are in
/// design units (canvas units × UI scale) so the UI-scale slider grows a window in place. Pure — unit-tested.
/// </summary>
internal static class AnchoredPlacement
{
    /// <summary>Where on the canvas (and on the window) an anchor sits, as fractions of width / height.</summary>
    internal static (float Fx, float Fy) Fractions(WindowAnchor anchor) => anchor switch
    {
        WindowAnchor.Top => (0.5f, 0f),
        WindowAnchor.TopRight => (1f, 0f),
        WindowAnchor.Left => (0f, 0.5f),
        WindowAnchor.Center => (0.5f, 0.5f),
        WindowAnchor.Right => (1f, 0.5f),
        WindowAnchor.BottomLeft => (0f, 1f),
        WindowAnchor.Bottom => (0.5f, 1f),
        WindowAnchor.BottomRight => (1f, 1f),
        _ => (0f, 0f),
    };

    /// <summary>The offset (design units) of <paramref name="rect"/>'s anchor point from the canvas's anchor point.</summary>
    internal static (float X, float Y) OffsetOf(WindowAnchor anchor, WindowRect rect, CanvasDims canvas)
    {
        var (fx, fy) = Fractions(anchor);
        var u = canvas.UiScale > 0f ? canvas.UiScale : 1f;
        return ((rect.X + fx * rect.Width - fx * canvas.Width) * u, (rect.Y + fy * rect.Height - fy * canvas.Height) * u);
    }

    /// <summary>The top-left rect of a <paramref name="width"/> × <paramref name="height"/> window whose anchor point sits
    /// <paramref name="offset"/> (design units) from the canvas's anchor point.</summary>
    internal static WindowRect Place(WindowAnchor anchor, (float X, float Y) offset, (float Width, float Height) size, CanvasDims canvas)
    {
        var (fx, fy) = Fractions(anchor);
        var u = canvas.UiScale > 0f ? canvas.UiScale : 1f;
        return new WindowRect(fx * canvas.Width + offset.X / u - fx * size.Width,
                              fy * canvas.Height + offset.Y / u - fy * size.Height, size.Width, size.Height);
    }
}
