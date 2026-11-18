using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// BarStyle.Modern window-bar opt-ins (framework 2.34.0): gradient fill, static gloss, soft glow, damage trail, striped
// overlay segment and tick dividers. Every piece is built only when its BarElement property is set, so a bar that sets
// none is built exactly as before (BuildBarModernWindow in WindowBuilder.Preview.cs calls in here).
//
// Cost model (Plugin-Performance-Rules §5): everything is static geometry except the trail / glow segments, which ride
// their own BarBinding (anchor write only when the fraction moved, no allocation). The small baked textures (gradient,
// gloss, glow ring, stripe tile) are cached for the process in static maps keyed by their inputs — baked once at window
// build, never per refresh — and flagged HideAndDontSave so a scene's UnloadUnusedAssets never sweeps one.
internal sealed partial class WindowBuilder
{
    private const int BarGradientTexH = 64;
    private static readonly Dictionary<string, Sprite> BarFxSprites = new();

    // Gradient fill: the fill Image gets a 1×64 vertical-gradient sprite drawn white (FillColorFn, when set, multiplies it).
    private static void ApplyBarGradient(Graphic fill, IReadOnlyList<GradientStop> stops)
    {
        if (fill is not Image img || BarGradientSprite(stops) is not { } sprite) return;
        img.sprite = sprite; img.type = Image.Type.Simple; img.color = Color.white;
    }

    // Static gloss: white 35 % → 0 over the top 40 % of the fill, stretched over the (anchor-sized) fill clip.
    private static void AddBarGloss(RectTransform clipRt)
    {
        var sprite = CachedBarFx("gloss", () =>
        {
            var px = new Color[BarGradientTexH];
            for (var y = 0; y < BarGradientTexH; y++)
            {
                var t = 1f - (y + 0.5f) / BarGradientTexH;            // 0 = top
                px[y] = new Color(1f, 1f, 1f, t < 0.4f ? 0.35f * (1f - t / 0.4f) : 0f);
            }
            return BakeColumn(px);
        });
        var go = UGuiPrimitives.NewChild("Gloss", clipRt);
        UGuiPrimitives.Stretch(go);
        var img = go.AddComponent<Image>();
        img.sprite = sprite; img.type = Image.Type.Simple; img.raycastTarget = false;
    }

    // Soft glow behind a segment [start, end] of the track: a 9-sliced ring (transparent centre) expanded by r+1 px past
    // the segment, driven by its own BarBinding (same Fraction/Start as the segment) and hidden while the segment is empty.
    // overlay = glow the Overlay01 segment (OverlayGlow, [OverlayStart01, Overlay01]); else the main fill (FillGlow, [0, Fraction01]).
    private static void AddBarGlow(BarElement b, Transform track, int siblingIndex, bool overlay, WindowToken token)
    {
        var color = (overlay ? b.OverlayGlow : b.FillGlow) ?? default;
        var end = overlay ? b.Overlay01! : b.Fraction01;
        var start = overlay ? b.OverlayStart01 : null;
        var r = Mathf.Clamp(Mathf.RoundToInt(b.GlowRadius > 0f ? b.GlowRadius : 6f), 1, 32);
        var go = UGuiPrimitives.NewChild("Glow", track);
        go.transform.SetSiblingIndex(siblingIndex);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 0.5f);
        rt.offsetMin = new Vector2(-(r + 1), -(r + 1)); rt.offsetMax = new Vector2(r + 1, r + 1);
        var img = go.AddComponent<Image>();
        img.sprite = GlowRingSprite(r); img.type = Image.Type.Sliced; img.fillCenter = false; img.raycastTarget = false;
        img.color = new Color(color.R, color.G, color.B, color.A);
        token.Bars.Add(new BarBinding { FillRect = rt, Fraction = end, Start = start, HideWhenEmpty = img });
    }

    // Damage trail: a flat band [0, Trail01] drawn first on the track (behind every fill and the overlay).
    private static void BuildModernTrail(BarElement b, Transform track, WindowToken token)
    {
        var rt = BuildOverlayClip(track, b.TrailColor);
        rt.name = "TrailClip";
        rt.anchorMax = new Vector2(Mathf.Clamp01(b.Trail01!()), 1f);
        rt.SetAsFirstSibling();
        token.Bars.Add(new BarBinding { FillRect = rt, Fraction = b.Trail01! });
    }

    // Striped overlay: the overlay's flat fill becomes a 135° stripe tile (Image.Type.Tiled at 1 texel = 1 px).
    private static void ApplyOverlayStripes(RectTransform overlayClip, ColorRgba a, ColorRgba b, float width)
    {
        if (overlayClip.childCount == 0 || overlayClip.GetChild(0).GetComponent<Image>() is not { } img) return;
        img.sprite = StripeTileSprite(a, b, width > 0f ? width : 4f);
        img.type = Image.Type.Tiled; img.color = Color.white;
    }

    // N−1 static dividers at i/N across the track (anchor-positioned: follow every width with no per-refresh work).
    private static void BuildBarTicks(BarElement b, Transform track)
    {
        var n = Math.Min(b.Ticks, 100);
        var half = (b.TickWidth > 0f ? b.TickWidth : 1f) * 0.5f;
        var col = new Color(b.TickColor.R, b.TickColor.G, b.TickColor.B, b.TickColor.A);
        for (var i = 1; i < n; i++)
        {
            var go = UGuiPrimitives.NewChild("Tick", track);
            var rt = go.GetComponent<RectTransform>();
            var x = (float)i / n;
            rt.anchorMin = new Vector2(x, 0f); rt.anchorMax = new Vector2(x, 1f);
            rt.offsetMin = new Vector2(-half, 0f); rt.offsetMax = new Vector2(half, 0f);
            var img = go.AddComponent<Image>();
            img.color = col; img.raycastTarget = false;
        }
    }

    // ---- bakes (build-time only, cached for the process) ----

    private static Sprite? BarGradientSprite(IReadOnlyList<GradientStop> stops)
    {
        var key = "grad";
        for (var i = 0; i < stops.Count; i++) key += "|" + stops[i].At + ":" + stops[i].Color;
        return CachedBarFx(key, () =>
        {
            var px = new Color[BarGradientTexH];
            for (var y = 0; y < BarGradientTexH; y++)
            {
                var t = 1f - (y + 0.5f) / BarGradientTexH;            // texture y-up → 0 = top of the bar
                var c = SampleStops(stops, t);
                px[y] = new Color(c.R, c.G, c.B, c.A);
            }
            return BakeColumn(px);
        });
    }

    private static ColorRgba SampleStops(IReadOnlyList<GradientStop> s, float t)
    {
        if (t <= s[0].At) return s[0].Color;
        for (var i = 1; i < s.Count; i++)
        {
            if (t > s[i].At) continue;
            var span = s[i].At - s[i - 1].At;
            var k = span > 1e-5f ? (t - s[i - 1].At) / span : 1f;
            ColorRgba a = s[i - 1].Color, b = s[i].Color;
            return new ColorRgba(a.R + (b.R - a.R) * k, a.G + (b.G - a.G) * k, a.B + (b.B - a.B) * k, a.A + (b.A - a.A) * k);
        }
        return s[s.Count - 1].Color;
    }

    // Ring of soft falloff around a transparent core: size 2r+4, border r+1 → the falloff starts exactly at the segment edge.
    private static Sprite GlowRingSprite(int r) => CachedBarFx("glow" + r, () =>
    {
        var size = 2 * r + 4;
        var t = NewFxTexture(size, size, TextureWrapMode.Clamp);
        var half = size * 0.5f;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = Mathf.Max(0f, Mathf.Abs(x + 0.5f - half) - 2f);
                var dy = Mathf.Max(0f, Mathf.Abs(y + 0.5f - half) - 2f);
                var d = Mathf.Sqrt(dx * dx + dy * dy);
                var k = d <= 0f ? 0f : Mathf.Clamp01(1f - d / (r + 0.5f));
                t.SetPixel(x, y, new Color(1f, 1f, 1f, k * k));
            }
        t.Apply(false, false);
        var b = r + 1;
        return Sprite.Create(t, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
    });

    // 135° stripes (CSS repeating-linear-gradient(135deg)): constant (x − y) in y-up texture space; the period divides
    // the tile in both axes → seamless. Horizontal period = 2·w·√2 so each stripe is w px wide across the stripe.
    private static Sprite StripeTileSprite(ColorRgba a, ColorRgba b, float w) => CachedBarFx("stripe" + w + a + b, () =>
    {
        var p = Mathf.Max(2, Mathf.RoundToInt(2f * w * 1.41421356f));
        var t = NewFxTexture(p, p, TextureWrapMode.Repeat);
        var ca = new Color(a.R, a.G, a.B, a.A); var cb = new Color(b.R, b.G, b.B, b.A);
        for (var y = 0; y < p; y++)
            for (var x = 0; x < p; x++)
                t.SetPixel(x, y, ((x - y) % p + p) % p < p * 0.5f ? ca : cb);
        t.Apply(false, false);
        return Sprite.Create(t, new Rect(0, 0, p, p), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    });

    private static Sprite BakeColumn(Color[] px)
    {
        var t = NewFxTexture(1, px.Length, TextureWrapMode.Clamp);
        for (var y = 0; y < px.Length; y++) t.SetPixel(0, y, px[y]);
        t.Apply(false, false);
        return Sprite.Create(t, new Rect(0, 0, 1, px.Length), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    }

    private static Texture2D NewFxTexture(int w, int h, TextureWrapMode wrap) => new(w, h, TextureFormat.RGBA32, false)
        { hideFlags = HideFlags.HideAndDontSave, wrapMode = wrap, filterMode = FilterMode.Bilinear };

    private static Sprite CachedBarFx(string key, Func<Sprite> bake)
    {
        if (BarFxSprites.TryGetValue(key, out var s) && s != null) return s;
        s = bake();
        s.hideFlags = HideFlags.HideAndDontSave;
        BarFxSprites[key] = s;
        return s;
    }
}
