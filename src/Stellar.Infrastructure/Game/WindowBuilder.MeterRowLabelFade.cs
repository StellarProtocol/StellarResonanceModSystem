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
// stair-step. This instead darkens the strip of bar UNDER each value with a horizontal gradient (opaque plateau under
// the digits, then a smooth fade), drawn above the fill + sheen and below the text: the glyphs stay untouched and
// anti-aliased, the fill keeps the exact crest colour everywhere else, and over the dark track (a short bar, or the
// right end before the fill reaches it) the fade is invisible. Both fades are children of the BAR, not of the
// width-clipped fill, so they never move with the fraction; the right one is shown/hidden with the Secondary label.
internal sealed partial class WindowBuilder
{
    private const int LabelFadeW = 72;                                           // band width at font scale 1 (≈ "893.5K" + margin)
    private static readonly Color MeterLabelFade = new(0.04f, 0.05f, 0.07f, 1f); // the row's own dark, alpha from the texture

    /// Adds the left + right fades under the bar's value labels; returns the RIGHT fade's GameObject so the row
    /// binding can toggle it together with the Secondary label.
    private GameObject AddLabelFades(Transform bar)
    {
        var tex = LabelFadeTexture();
        AddLabelFade(bar, "LabelFadeL", tex, left: true);
        return AddLabelFade(bar, "LabelFadeR", tex, left: false);
    }

    private GameObject AddLabelFade(Transform bar, string nm, Texture2D tex, bool left)
    {
        var go = UGuiPrimitives.NewChild(nm, bar);
        var rt = go.GetComponent<RectTransform>();
        float ax = left ? 0f : 1f;
        rt.anchorMin = new Vector2(ax, 0f); rt.anchorMax = new Vector2(ax, 1f); rt.pivot = new Vector2(ax, 0.5f);
        rt.sizeDelta = new Vector2(Scaled(LabelFadeW), 0f); rt.anchoredPosition = Vector2.zero;
        var img = go.AddComponent<RawImage>();
        img.texture = tex; img.color = MeterLabelFade; img.raycastTarget = false;
        if (!left) img.uvRect = new Rect(1f, 0f, -1f, 1f);                       // mirror: plateau at the right edge
        return go;
    }

    // Horizontal alpha ramp, built once and shared by every row's two fades (the right one samples it mirrored):
    // 0.58 over the first 60 % (the digits), then a smooth fall to 0 — no hard edge on the fill.
    private Texture2D? _labelFadeTex;
    private Texture2D LabelFadeTexture()
    {
        if (_labelFadeTex != null) return _labelFadeTex;
        const int w = 64, h = 4;
        const float plateau = 0.60f, peak = 0.58f;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
        { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (var x = 0; x < w; x++)
        {
            float u = x / (float)(w - 1);
            float a = u <= plateau ? peak : peak * Mathf.SmoothStep(1f, 0f, (u - plateau) / (1f - plateau));
            var c = new Color(1f, 1f, 1f, a);
            for (var y = 0; y < h; y++) t.SetPixel(x, y, c);
        }
        t.Apply();
        _labelFadeTex = t;
        return t;
    }
}
