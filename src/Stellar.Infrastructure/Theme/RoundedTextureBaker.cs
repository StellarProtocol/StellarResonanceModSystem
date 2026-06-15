using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.Infrastructure.Theme;

/// <summary>
/// Pure procedural rounded-rectangle texture bakers, extracted from
/// <see cref="ThemeRenderer"/> so both the IMGUI theme renderer and the uGUI
/// HUD sprite provider (<c>HudThemeAssets</c>) bake corners with one shared,
/// anti-aliased formula — no divergence if the corner math is ever tuned.
/// All methods are static + side-effect free; each returns a fresh
/// <see cref="Texture2D"/> flagged <c>HideAndDontSave</c>. Callers own destroy.
/// </summary>
internal static class RoundedTextureBaker
{
    /// <summary>
    /// Bakes a rounded-rectangle texture for 9-slice use. The texture is
    /// <paramref name="size"/>×<paramref name="size"/> with quarter-circle
    /// corners of <paramref name="radius"/> px. Anti-aliased via 4× super-
    /// sampling so the curve is smooth at the rendered size. Pair with a
    /// 9-slice border of <paramref name="radius"/> to keep the corners fixed
    /// while the middle stretches with content width.
    /// </summary>
    internal static Texture2D Rounded(int size, int radius, ColorRgba colour)
    {
        const int SS = 4;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags  = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp,
        };
        var c = new Color(colour.R, colour.G, colour.B, colour.A);
        var pixels = new Color[size * size];

        for (int py = 0; py < size; py++)
        for (int px = 0; px < size; px++)
        {
            int covered = 0;
            for (int sy = 0; sy < SS; sy++)
            for (int sx = 0; sx < SS; sx++)
            {
                float fx = px + (sx + 0.5f) / SS;
                float fy = py + (sy + 0.5f) / SS;
                if (InsideRoundedRect(fx, fy, new RoundedRectBounds(0f, 0f, size, size, radius))) covered++;
            }
            float alpha = covered / (float)(SS * SS);
            pixels[py * size + px] = new Color(c.r, c.g, c.b, c.a * alpha);
        }
        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false);
        return tex;
    }

    /// <summary>
    /// Rounded chip with a corner-following border baked in. Outer rounded-rect
    /// coverage = fill silhouette; an inset concentric rounded-rect (inset by
    /// borderPx, radius - borderPx) is the interior; the ring between them is the
    /// border. Border colour is composited opaque so it reads crisp regardless of
    /// the fill's translucency. 9-sliceable: corners (radius px) hold the rounded
    /// border, only the straight middle bands stretch. See Unity GUIStyle.border.
    /// </summary>
    internal static Texture2D RoundedBordered(int size, int radius, int borderPx, ColorRgba fill, ColorRgba border)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        const int SS = 4; // supersample (match Rounded)
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float outer = 0f, inner = 0f;
            for (int sy = 0; sy < SS; sy++)
            for (int sx = 0; sx < SS; sx++)
            {
                float fx = x + (sx + 0.5f) / SS;
                float fy = y + (sy + 0.5f) / SS;
                if (InsideRoundedRect(fx, fy, new RoundedRectBounds(0f, 0f, size, size, radius))) outer += 1f;
                if (InsideRoundedRect(fx, fy, new RoundedRectBounds(borderPx, borderPx, size - borderPx, size - borderPx, radius - borderPx))) inner += 1f;
            }
            float oa = outer / (SS * SS);
            float ia = inner / (SS * SS);
            float ringA = Mathf.Clamp01(oa - ia);          // border ring coverage
            // Interior fill (premultiplied by ia) + opaque accent ring (premult by ringA).
            float r = fill.R * fill.A * ia + border.R * ringA;
            float g = fill.G * fill.A * ia + border.G * ringA;
            float b = fill.B * fill.A * ia + border.B * ringA;
            float a = fill.A * ia + ringA;                  // ring is opaque (border.A treated as 1)
            // un-premultiply for Color (Unity GUI uses straight alpha)
            if (a > 0.0001f) { r /= a; g /= a; b /= a; }
            px[y * size + x] = new Color(r, g, b, Mathf.Clamp01(a));
        }
        tex.SetPixels(px);
        tex.Apply(false);
        return tex;
    }

    /// <summary>
    /// Axis-aligned rounded-rect bounds for <see cref="InsideRoundedRect"/>:
    /// the interval [<see cref="X0"/>,<see cref="X1"/>] × [<see cref="Y0"/>,
    /// <see cref="Y1"/>] with corner <see cref="Radius"/>. Bundled into a
    /// parameter object so the coverage test stays under STELLAR0003's 5-param
    /// cap while both bakers share one rounding formula.
    /// </summary>
    private readonly struct RoundedRectBounds
    {
        public readonly float X0, Y0, X1, Y1, Radius;
        public RoundedRectBounds(float x0, float y0, float x1, float y1, float radius)
        {
            X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; Radius = radius;
        }
    }

    /// <summary>
    /// True when the sub-pixel sample (<paramref name="fx"/>,<paramref name="fy"/>)
    /// lies inside the rounded rectangle <paramref name="b"/>. A non-positive
    /// radius degenerates to a plain rectangle test (used for the inset interior
    /// when borderPx ≥ radius).
    /// </summary>
    private static bool InsideRoundedRect(float fx, float fy, in RoundedRectBounds b)
    {
        if (fx < b.X0 || fx >= b.X1 || fy < b.Y0 || fy >= b.Y1) return false;
        if (b.Radius <= 0f) return true;
        bool inCornerX = fx < b.X0 + b.Radius || fx >= b.X1 - b.Radius;
        bool inCornerY = fy < b.Y0 + b.Radius || fy >= b.Y1 - b.Radius;
        if (!inCornerX || !inCornerY) return true;
        float cx = (fx < b.X0 + b.Radius) ? b.X0 + b.Radius : b.X1 - b.Radius;
        float cy = (fy < b.Y0 + b.Radius) ? b.Y0 + b.Radius : b.Y1 - b.Radius;
        float dx = fx - cx, dy = fy - cy;
        return dx * dx + dy * dy <= b.Radius * b.Radius;
    }
}
