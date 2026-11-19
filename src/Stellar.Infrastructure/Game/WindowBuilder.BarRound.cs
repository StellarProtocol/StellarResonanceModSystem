using System.Collections.Generic;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// BarElement.CornerRadius on BarStyle.Modern WINDOW bars (framework 2.34.0, opt-in; 0 = the square Modern bar, unchanged).
//
// There is no clip-to-rounded-rect in the toolkit, so the rounding is baked into the sprites instead: one horizontal-only
// 3-slice sprite per (radius, bar height, gradient, gloss) — the texture is exactly the bar's height (1 texel = 1 px) and
// 2r+4 wide, with the rounded corners as antialiased alpha (4×4 supersampled) in the r+1 px end borders and two solid
// centre columns that stretch. The fill uses it inside its width clip, so the fill keeps rounded ends at every width (a
// fill narrower than both ends is drawn with the ends scaled down — Unity's Sliced rule). The vertical FillGradient and
// the static Gloss are baked into the fill sprite (a separate stretched gloss Image would poke out of the round corners).
// Track, trail and a flat Overlay01 segment use the same shape in white, tinted by their colour (a striped overlay stays square).
internal sealed partial class WindowBuilder
{
    private static void ApplyModernRounding(BarElement b, Transform track, Graphic fill, int height)
    {
        var h = Mathf.Max(2, height);
        var r = Mathf.Clamp(Mathf.RoundToInt(b.CornerRadius), 1, h / 2);
        if (track.GetComponent<Image>() is { } trackImg) { trackImg.sprite = RoundedBarSprite(r, h, null, false); trackImg.type = Image.Type.Sliced; }
        if (fill is not Image fillImg) return;
        var grad = b.FillGradient is { Count: >= 2 } g ? g : null;
        fillImg.sprite = RoundedBarSprite(r, h, grad, b.Gloss);
        fillImg.type = Image.Type.Sliced;
        if (grad != null) fillImg.color = Color.white;   // the gradient IS the colour (FillColorFn still multiplies)
    }

    // The trail band (and a flat overlay) as the plain rounded shape, tinted by the Image colour.
    private static void RoundSegment(RectTransform clip, BarElement b, int height)
    {
        if (b.CornerRadius <= 0f || clip.childCount == 0 || clip.GetChild(0).GetComponent<Image>() is not { } img) return;
        var h = Mathf.Max(2, height);
        img.sprite = RoundedBarSprite(Mathf.Clamp(Mathf.RoundToInt(b.CornerRadius), 1, h / 2), h, null, false);
        img.type = Image.Type.Sliced;
    }

    private static Sprite RoundedBarSprite(int r, int h, IReadOnlyList<GradientStop>? stops, bool gloss)
    {
        var key = "round" + r + "x" + h + (gloss ? "g" : "");
        if (stops != null) for (var i = 0; i < stops.Count; i++) key += "|" + stops[i].At + ":" + stops[i].Color;
        return CachedBarFx(key, () =>
        {
            var w = 2 * r + 4;
            var t = NewFxTexture(w, h, TextureWrapMode.Clamp);
            for (var y = 0; y < h; y++)
            {
                var top = 1f - (y + 0.5f) / h;   // texture y-up → 0 = top of the bar
                var c = stops != null ? SampleStops(stops, top) : new Abstractions.Domain.ColorRgba(1f, 1f, 1f, 1f);
                var col = new Color(c.R, c.G, c.B, c.A);
                if (gloss && top < GlossSpan) col = Color.Lerp(col, Color.white, GlossAlpha * (1f - top / GlossSpan));
                for (var x = 0; x < w; x++)
                    t.SetPixel(x, y, new Color(col.r, col.g, col.b, col.a * RoundCoverage(x, y, w, h, r)));
            }
            t.Apply(false, false);
            var b = r + 1;
            return Sprite.Create(t, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, 0, b, 0));
        });
    }

    // Share of texel (x, y) inside a w×h rounded rect of radius r (4×4 samples).
    private static float RoundCoverage(int x, int y, int w, int h, int r)
    {
        var inside = 0;
        for (var sy = 0; sy < 4; sy++)
            for (var sx = 0; sx < 4; sx++)
            {
                float px = x + (sx + 0.5f) / 4f, py = y + (sy + 0.5f) / 4f;
                float cx = Mathf.Clamp(px, r, w - r), cy = Mathf.Clamp(py, r, h - r);
                float dx = px - cx, dy = py - cy;
                if (dx * dx + dy * dy <= r * r) inside++;
            }
        return inside / 16f;
    }
}
