namespace Stellar.Application.Services;

/// <summary>
/// Pure mapping for <c>XYPadElement</c> between a pointer position in the pad — normalized 0..1 with the origin at the
/// BOTTOM-left, as uGUI and the window interaction ticker report it — and the value pair over [min, max] on both axes.
/// Y up is positive. Everything is clamped to the pad; no I/O.
/// </summary>
internal static class XYPadMapping
{
    /// <summary>The value at a pointer position. Outside the pad (or NaN) clamps to the nearest edge (NaN → min).</summary>
    internal static (float X, float Y) FromPointer(float nx, float ny, float min, float max) =>
        (Lerp(min, max, Clamp01(nx)), Lerp(min, max, Clamp01(ny)));

    /// <summary>Where the dot for a value sits, normalized (bottom-left origin). Values outside the range sit on the
    /// edge; NaN, or an empty range, is drawn at the centre.</summary>
    internal static (float Nx, float Ny) ToNormalized(float x, float y, float min, float max) =>
        (Normalize(x, min, max), Normalize(y, min, max));

    private static float Normalize(float v, float min, float max)
    {
        var range = max - min;
        if (range == 0f || float.IsNaN(range)) return 0.5f;
        var t = (v - min) / range;
        return float.IsNaN(t) ? 0.5f : Clamp01(t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // NaN fails both comparisons and lands on 0.
    private static float Clamp01(float v) => v > 0f ? (v < 1f ? v : 1f) : 0f;
}
