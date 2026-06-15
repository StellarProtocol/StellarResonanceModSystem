using Stellar.Abstractions.Domain;
using UnityEngine;
using SysMath = System.Math;

namespace Stellar.Infrastructure.Theme;

/// <summary>
/// Phase 9b GlassMenu texture bake. Produces a 256×256 RGBA32 texture whose
/// colour channel carries a 135° 3-stop linear gradient (top-left → bottom-right
/// per <see cref="ThemePresets.GlassMenuGradients"/>) plus a corner-following
/// 1-texel <c>MenuBorder</c> ring baked into the rounded edge, and whose alpha
/// channel carries the rounded-corner cutout. 9-sliced at draw time via
/// <c>_glassMenuBgStyle</c>'s <c>GUIStyle.border = RectOffset(R,R,R,R)</c> so
/// the corner curve + its border survive stretch to any window dimension. The
/// baked border replaces the old separate rectangular border draw, which
/// produced a double edge (rounded gradient + straight rect frame).
/// </summary>
internal sealed partial class ThemeRenderer
{
    private const int GlassMenuBgTexSize = 256;
    private const int GlassMenuRadius    = 12;  // 9-slice corner; drawn ~1:1 so ≈12 px on-screen
    private const int GlassMenuBorderTexels = 1; // ~1 px border on-screen (corners draw 1:1)

    private Texture2D? _glassMenuBgTex;

    private void BakeGlassMenuTextures()
    {
        // Derive the 3-stop gradient from the live MenuBackground token so the
        // editable "Panel background" colour (and its alpha) actually drives the
        // panel. Top = the token; mid/bottom darken slightly for glass depth.
        // (The static ThemePresets.GlassMenuGradients top stops equal each
        // preset's MenuBackground, so the default look is preserved; editing or
        // making it translucent now takes effect on the next rebake.)
        var bg = Colors.MenuBackground;
        _glassMenuBgTex = MakeGlassMenuBgTexture(
            GlassMenuBgTexSize, bg, Scale(bg, 0.93f), Scale(bg, 0.97f), Colors.MenuBorder);
    }

    // Multiply RGB by a factor for a subtle vertical gradient; alpha preserved
    // so a translucent MenuBackground yields a translucent panel.
    private static ColorRgba Scale(ColorRgba c, float f) => new(c.R * f, c.G * f, c.B * f, c.A);

    /// <summary>
    /// Bakes the 135° 3-stop gradient, a corner-following <paramref name="border"/>
    /// ring, and the rounded-corner alpha cutout into one RGBA32 texture.
    /// Diagonal: <c>t = (tx + screenY) / (2(size-1))</c> with
    /// <c>screenY = (size-1) - ty</c> (Unity y=0 at texture bottom). The border
    /// is the ring between the outer rounded-rect coverage and a 1-texel-inset
    /// inner one, composited over the gradient at the border's own alpha — so
    /// there is a single edge that follows the corners, not a rect + curve.
    /// </summary>
    private static Texture2D MakeGlassMenuBgTexture(int size, ColorRgba top, ColorRgba mid, ColorRgba bottom, ColorRgba border)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        var pixels = new Color[size * size];
        float denom = 2f * (size - 1);
        float half = size * 0.5f;
        int b = GlassMenuBorderTexels;

        for (int ty = 0; ty < size; ty++)
        {
            int screenY = (size - 1) - ty;
            for (int tx = 0; tx < size; tx++)
            {
                float t = (tx + screenY) / denom;
                ColorRgba g = t <= 0.5f ? Lerp(top, mid, t / 0.5f) : Lerp(mid, bottom, (t - 0.5f) / 0.5f);

                float px = (tx + 0.5f) - half;
                float py = (screenY + 0.5f) - half;
                float outer = RoundedCoverage(px, py, half, GlassMenuRadius);
                float inner = RoundedCoverage(px, py, half - b, GlassMenuRadius - b);
                float ring = Mathf.Clamp01(outer - inner);

                // Border = MenuBorder composited over the gradient, mixed in by
                // the ring coverage; interior stays pure gradient.
                float br = g.R + (border.R - g.R) * border.A;
                float bg = g.G + (border.G - g.G) * border.A;
                float bb = g.B + (border.B - g.B) * border.A;
                pixels[ty * size + tx] = new Color(
                    g.R + (br - g.R) * ring,
                    g.G + (bg - g.G) * ring,
                    g.B + (bb - g.B) * ring,
                    outer * g.A);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false);
        return tex;
    }

    /// <summary>
    /// Signed-distance coverage of a rounded square centred on the texture,
    /// half-side <paramref name="half"/>, corner <paramref name="radius"/>.
    /// 1 inside, 0 outside, ~1 px analytic AA at the boundary — covers the
    /// straight edges AND corners so the baked border is even all the way
    /// round (a corner-only cutout would leave the straight edges unbordered).
    /// </summary>
    private static float RoundedCoverage(float px, float py, float half, float radius)
    {
        float qx = Mathf.Abs(px) - (half - radius);
        float qy = Mathf.Abs(py) - (half - radius);
        float ax = Mathf.Max(qx, 0f), ay = Mathf.Max(qy, 0f);
        float d = Mathf.Sqrt(ax * ax + ay * ay) + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
        return Mathf.Clamp01(0.5f - d);
    }

    private static ColorRgba Lerp(ColorRgba a, ColorRgba b, float t)
    {
        t = (float)SysMath.Max(0f, SysMath.Min(1f, t));
        return new ColorRgba(
            a.R + (b.R - a.R) * t,
            a.G + (b.G - a.G) * t,
            a.B + (b.B - a.B) * t,
            a.A + (b.A - a.A) * t);
    }
}
