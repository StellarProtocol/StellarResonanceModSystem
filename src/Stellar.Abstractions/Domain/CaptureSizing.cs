using System;

namespace Stellar.Abstractions.Domain;

/// <summary>
/// Pure size and framing maths for <see cref="CaptureRequest"/> (BCL only, unit-tested on CI). The framework's capture
/// uses exactly these functions, so a plugin can show the real output size and a truthful frame guide before the shot.
/// </summary>
public static class CaptureSizing
{
    /// <summary>Longest side a capture may have, in pixels (also lowered to the GPU's own texture limit).</summary>
    public const int MaxLongSide = 16384;

    /// <summary>Total output pixels (64 MP = a 256 MB RGBA frame); above it a supersampled grab risks running out of memory.</summary>
    public const long MaxPixels = 64_000_000;

    /// <summary>
    /// The scale a window-shaped capture (no <see cref="CaptureRequest.Aspect"/>) really uses: 4× → 2× → 1× until the
    /// image fits the long-side limit (<see cref="MaxLongSide"/>, or a smaller <paramref name="maxTextureSize"/>) and
    /// <see cref="MaxPixels"/>. Scale 1 is never lowered.
    /// </summary>
    /// <param name="screenWidth">Window width in pixels.</param>
    /// <param name="screenHeight">Window height in pixels.</param>
    /// <param name="scale">Requested scale (1, 2 or 4).</param>
    /// <param name="maxTextureSize">The GPU's largest texture side; 0 or less = <see cref="MaxLongSide"/>.</param>
    public static int EffectiveScale(int screenWidth, int screenHeight, int scale, int maxTextureSize = MaxLongSide)
    {
        var limit = LongSideLimit(maxTextureSize);
        var longSide = (long)Math.Max(screenWidth, screenHeight);
        while (scale > 1 && (longSide * scale > limit || (long)screenWidth * screenHeight * scale * scale > MaxPixels)) scale /= 2;
        return scale;
    }

    /// <summary>
    /// The photo's real size in pixels. No <paramref name="aspect"/> = the window × the effective scale
    /// (<see cref="EffectiveScale"/>). With a shape: the LONG side is the window's long side × <paramref name="scale"/>
    /// and the short side follows the shape, rounded to an even pixel count (2× on 1920 × 1080: 9:16 = 2160 × 3840,
    /// 21:9 = 3840 × 1646). When that exceeds the long-side limit or <see cref="MaxPixels"/>, both sides shrink by the
    /// same factor until it fits (the shape is kept). An invalid shape or a non-positive window gives an empty size.
    /// </summary>
    /// <param name="screenWidth">Window width in pixels.</param>
    /// <param name="screenHeight">Window height in pixels.</param>
    /// <param name="scale">Requested scale (1, 2 or 4).</param>
    /// <param name="aspect">The photo shape, or null for the window's shape.</param>
    /// <param name="maxTextureSize">The GPU's largest texture side; 0 or less = <see cref="MaxLongSide"/>.</param>
    public static CaptureSize OutputSize(int screenWidth, int screenHeight, int scale, CaptureAspect? aspect, int maxTextureSize = MaxLongSide)
    {
        if (screenWidth <= 0 || screenHeight <= 0 || scale <= 0) return default;
        if (aspect is not { } a)
        {
            var s = EffectiveScale(screenWidth, screenHeight, scale, maxTextureSize);
            return new CaptureSize(screenWidth * s, screenHeight * s);
        }
        if (!a.IsValid) return default;
        var limit = LongSideLimit(maxTextureSize);
        var longSide = (long)Math.Max(screenWidth, screenHeight) * scale;
        var size = Shaped(longSide, a);
        if (Fits(size, limit)) return size;
        var factor = Math.Min((double)limit / longSide, Math.Sqrt(MaxPixels / ((double)size.Width * size.Height)));
        var reduced = Math.Max(2L, (long)Math.Floor(longSide * factor / 2d) * 2L);
        size = Shaped(reduced, a);
        while (!Fits(size, limit) && reduced > 2) size = Shaped(reduced -= 2, a);
        return size;
    }

    /// <summary>
    /// Where the photo sits on the screen: the largest rectangle of <paramref name="aspect"/> centred in the window
    /// (full height for shapes narrower than the window, full width for wider ones). The capture frames exactly this
    /// rectangle of the on-screen view, so it is the frame guide. No shape (or an invalid one / empty window) = the
    /// whole window.
    /// </summary>
    /// <param name="screenWidth">Window width in pixels.</param>
    /// <param name="screenHeight">Window height in pixels.</param>
    /// <param name="aspect">The photo shape, or null for the window's shape.</param>
    public static NormalizedRect GuideRect(int screenWidth, int screenHeight, CaptureAspect? aspect)
    {
        if (aspect is not { IsValid: true } a || screenWidth <= 0 || screenHeight <= 0) return new NormalizedRect(0f, 0f, 1f, 1f);
        var screen = (double)screenWidth / screenHeight;
        var target = a.Ratio;
        if (target <= screen)
        {
            var w = (float)(target / screen);
            return new NormalizedRect((1f - w) / 2f, 0f, w, 1f);
        }
        var h = (float)(screen / target);
        return new NormalizedRect(0f, (1f - h) / 2f, 1f, h);
    }

    /// <summary>
    /// The camera's vertical view angle for the capture render, so the photo frames <see cref="GuideRect"/>: a shape
    /// narrower than (or as wide as) the window keeps the on-screen vertical angle (same scene height, narrower); a wider
    /// shape keeps the on-screen HORIZONTAL angle, so its vertical angle narrows (same scene width, shorter).
    /// </summary>
    /// <param name="screenFovDegrees">The camera's on-screen vertical field of view, in degrees.</param>
    /// <param name="screenAspect">Window width ÷ height.</param>
    /// <param name="targetAspect">Photo width ÷ height.</param>
    public static float VerticalFieldOfView(float screenFovDegrees, double screenAspect, double targetAspect)
    {
        if (targetAspect <= screenAspect || screenAspect <= 0d || screenFovDegrees <= 0f) return screenFovDegrees;
        var half = screenFovDegrees * Math.PI / 360d;
        return (float)(Math.Atan(Math.Tan(half) * screenAspect / targetAspect) * 360d / Math.PI);
    }

    private static int LongSideLimit(int maxTextureSize) => maxTextureSize > 0 ? Math.Min(MaxLongSide, maxTextureSize) : MaxLongSide;

    private static bool Fits(CaptureSize s, int limit) =>
        Math.Max(s.Width, s.Height) <= limit && (long)s.Width * s.Height <= MaxPixels;

    private static CaptureSize Shaped(long longSide, CaptureAspect a)
    {
        var l = (int)Math.Min(longSide, int.MaxValue);
        // The short side never exceeds the long one (a 1:1 shape on an odd long side stays square).
        if (a.Width >= a.Height) return new CaptureSize(l, Math.Min(l, Even(l * (double)a.Height / a.Width)));
        return new CaptureSize(Math.Min(l, Even(l * (double)a.Width / a.Height)), l);
    }

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2d, MidpointRounding.AwayFromZero) * 2);
}
