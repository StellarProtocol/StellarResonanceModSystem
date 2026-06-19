using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Procedural per-kind notification icon baker. Draws a simple, legible glyph inside a
/// filled disc tinted with the kind colour — Info "i", Success check, Warning "!", Error "×".
/// Anti-aliased via 4× supersampling. Pure + static; each call returns a fresh
/// <see cref="Texture2D"/> flagged HideAndDontSave (caller owns destroy). Mirrors the
/// supersampling approach in <see cref="Theme.RoundedTextureBaker"/>.
/// </summary>
internal static class ToastIconBaker
{
    private const int SS = 4;

    /// <summary>Bake a <paramref name="size"/>×<paramref name="size"/> icon for <paramref name="kind"/>,
    /// tinted with <paramref name="colour"/> (the kind's fixed semantic colour). The glyph is drawn
    /// in a near-white ink over a filled disc so it reads at the 16px on-screen size.</summary>
    internal static Texture2D Bake(int size, NotificationKind kind, ColorRgba colour)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        var disc = new Color(colour.R, colour.G, colour.B, 1f);
        var ink = new Color(0.97f, 0.98f, 1f, 1f);
        var px = new Color[size * size];
        float c = size * 0.5f;
        float r = size * 0.46f;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int inDisc = 0, inGlyph = 0;
            for (int sy = 0; sy < SS; sy++)
            for (int sx = 0; sx < SS; sx++)
            {
                float fx = x + (sx + 0.5f) / SS;
                float fy = y + (sy + 0.5f) / SS;
                float dx = fx - c, dy = fy - c;
                if (dx * dx + dy * dy <= r * r) inDisc++;
                if (InGlyph(kind, fx, fy, size)) inGlyph++;
            }
            float discA = inDisc / (float)(SS * SS);
            float glyphA = inGlyph / (float)(SS * SS);
            // Composite: ink over disc; disc over transparent.
            var bg = new Color(disc.r, disc.g, disc.b, disc.a * discA);
            var col = Color.Lerp(bg, ink, glyphA);
            col.a = Mathf.Max(bg.a, glyphA);
            px[y * size + x] = col;
        }
        tex.SetPixels(px);
        tex.Apply(updateMipmaps: true);
        return tex;
    }

    // Glyph silhouette test in pixel coords (origin bottom-left, y up). Strokes are a fixed
    // fraction of size so the glyph scales with the icon.
    private static bool InGlyph(NotificationKind kind, float fx, float fy, int size)
    {
        float c = size * 0.5f;
        float t = size * 0.10f;   // stroke half-thickness
        // Normalise to [-1,1] about centre for shape math.
        float nx = (fx - c) / (size * 0.5f);
        float ny = (fy - c) / (size * 0.5f);
        return kind switch
        {
            NotificationKind.Success => InCheck(fx, fy, c, t, size),
            NotificationKind.Error   => InCross(nx, ny, t / (size * 0.5f)),
            NotificationKind.Warning => InBang(nx, ny, t / (size * 0.5f)),
            _                        => InInfo(nx, ny, t / (size * 0.5f)),
        };
    }

    // "i": dot near top + vertical stem below.
    private static bool InInfo(float nx, float ny, float ht)
    {
        bool dot = nx * nx + (ny - 0.42f) * (ny - 0.42f) <= (ht * 1.3f) * (ht * 1.3f);
        bool stem = Mathf.Abs(nx) <= ht && ny <= 0.18f && ny >= -0.45f;
        return dot || stem;
    }

    // "!": vertical stem on top + dot at bottom.
    private static bool InBang(float nx, float ny, float ht)
    {
        bool stem = Mathf.Abs(nx) <= ht && ny <= 0.45f && ny >= -0.12f;
        bool dot = nx * nx + (ny + 0.36f) * (ny + 0.36f) <= (ht * 1.3f) * (ht * 1.3f);
        return stem || dot;
    }

    // "×": two diagonals through centre.
    private static bool InCross(float nx, float ny, float ht)
    {
        float lim = 0.42f;
        if (Mathf.Abs(nx) > lim || Mathf.Abs(ny) > lim) return false;
        bool d1 = Mathf.Abs(nx - ny) <= ht * 1.6f;
        bool d2 = Mathf.Abs(nx + ny) <= ht * 1.6f;
        return d1 || d2;
    }

    // "check": short down-right stroke + longer up-right stroke, in pixel coords.
    private static bool InCheck(float fx, float fy, float c, float t, float size)
    {
        // Vertices (pixel space, y up): low-left, bottom-vertex, top-right.
        var p0 = new Vector2(c - size * 0.22f, c + size * 0.02f);
        var p1 = new Vector2(c - size * 0.05f, c - size * 0.18f);
        var p2 = new Vector2(c + size * 0.26f, c + size * 0.22f);
        var p = new Vector2(fx, fy);
        return SegDist(p, p0, p1) <= t || SegDist(p, p1, p2) <= t;
    }

    private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.sqrMagnitude;
        float u = len2 <= 0.0001f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return Vector2.Distance(p, a + ab * u);
    }
}
