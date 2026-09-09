using Stellar.Abstractions.Domain;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Meter-row bar: soft dark fades under the two value labels.
//
// The values drawn ON a meter row's bar (Primary at the left, Secondary at the right) are plain white uGUI Text
// straight on the fill. Since CombatMeter 2.10.0 the fill can be a class crest colour (Marksman #f2dc3a, Verdant
// #5ccf36, Beat Performer #f5761c) where white text has a WCAG contrast of 1.4–2.0 — owner 2026-09-09: "text on
// meter is barely readable". A uGUI Outline was tried first and rejected in-game ("readable but the text looks
// rough, no smooth at all"): legacy-Text outlines are four hard-edged 1 px copies of a small bitmap glyph, so they
// stair-step. This instead darkens the strip of bar UNDER each value with a horizontal gradient (plateau under the
// digits, then a smooth fade), drawn above the fill + sheen and below the text: the glyphs stay untouched and
// anti-aliased, the fill keeps the exact crest colour everywhere else, and over the dark track (a short bar, or the
// right end before the fill reaches it) the fade is invisible. Both fades are children of the BAR, not of the
// width-clipped fill, so they never move with the fraction; the right one is shown/hidden with the Secondary label.
//
// The STRENGTH follows the fill's luminance (second in-game round, owner: "text look smooth but shadow is too
// much"): a fixed 0.58 shade was right on yellow but pointless on red / blue / purple, where white text already
// reads. So the fade is faint on dark fills and only grows toward bright ones — see LabelFadeAlpha.
internal sealed partial class WindowBuilder
{
    private const int   LabelFadeW       = 64;    // band width at font scale 1 (≈ "893.5K" + inset, then the fade)
    private const float LabelFadePlateau = 0.50f; // fraction of the band held at full strength (under the digits)
    private const float LabelFadeMin     = 0.12f; // strength on a black fill (role red/blue land near here)
    private const float LabelFadeMax     = 0.52f; // strength on a white fill (Marksman yellow ≈ 0.40)
    private static readonly Color MeterLabelFadeRgb = new(0.04f, 0.05f, 0.07f, 1f); // the row's own dark; alpha set per fill

    /// Adds the left + right fades under the bar's value labels; the row binding keeps both to retint them per
    /// fill colour and toggles the right one together with the Secondary label.
    private (RawImage left, RawImage right) AddLabelFades(Transform bar)
    {
        var tex = LabelFadeTexture();
        return (AddLabelFade(bar, "LabelFadeL", tex, left: true), AddLabelFade(bar, "LabelFadeR", tex, left: false));
    }

    private RawImage AddLabelFade(Transform bar, string nm, Texture2D tex, bool left)
    {
        var go = UGuiPrimitives.NewChild(nm, bar);
        var rt = go.GetComponent<RectTransform>();
        float ax = left ? 0f : 1f;
        rt.anchorMin = new Vector2(ax, 0f); rt.anchorMax = new Vector2(ax, 1f); rt.pivot = new Vector2(ax, 0.5f);
        rt.sizeDelta = new Vector2(Scaled(LabelFadeW), 0f); rt.anchoredPosition = Vector2.zero;
        var img = go.AddComponent<RawImage>();
        img.texture = tex; img.color = LabelFadeTint(LabelFadeMin); img.raycastTarget = false;
        if (!left) img.uvRect = new Rect(1f, 0f, -1f, 1f);                       // mirror: plateau at the right edge
        return img;
    }

    /// Fade tint for a given fill colour: the fixed dark RGB with an alpha that rises with the fill's relative
    /// luminance, so white digits get help exactly where they need it and dark fills stay almost untouched.
    internal static Color LabelFadeTint(ColorRgba fill)
    {
        float lum = Mathf.Clamp01(0.2126f * fill.R + 0.7152f * fill.G + 0.0722f * fill.B);
        return LabelFadeTint(Mathf.Lerp(LabelFadeMin, LabelFadeMax, lum));
    }

    private static Color LabelFadeTint(float alpha)
        => new(MeterLabelFadeRgb.r, MeterLabelFadeRgb.g, MeterLabelFadeRgb.b, alpha);

    // Horizontal alpha ramp (shape only — strength comes from the RawImage tint), built once and shared by every
    // row's two fades (the right one samples it mirrored): full over the plateau, then a smooth fall to 0.
    private Texture2D? _labelFadeTex;
    private Texture2D LabelFadeTexture()
    {
        if (_labelFadeTex != null) return _labelFadeTex;
        const int w = 64, h = 4;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
        { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (var x = 0; x < w; x++)
        {
            float u = x / (float)(w - 1);
            float a = u <= LabelFadePlateau ? 1f : Mathf.SmoothStep(1f, 0f, (u - LabelFadePlateau) / (1f - LabelFadePlateau));
            var c = new Color(1f, 1f, 1f, a);
            for (var y = 0; y < h; y++) t.SetPixel(x, y, c);
        }
        t.Apply();
        _labelFadeTex = t;
        return t;
    }
}
