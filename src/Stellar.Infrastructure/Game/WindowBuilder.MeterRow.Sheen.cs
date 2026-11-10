using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Meter-row sheen — split out of WindowBuilder.MeterRow.cs (which sat at the 500-LoC file gate). A partial of
// the same WindowBuilder class: builds the soft white band that sweeps across a width-clipped bar fill, drives it
// per frame from the ticker pulse hook, and owns the shared gradient texture. Also used by the Modern preview
// bar (WindowBuilder.Preview.cs BuildModernSheen).
internal sealed partial class WindowBuilder
{
    // Sheen: a soft white band scrolled left→right within the clipped fill (per-frame, ticker-driven via the
    // pulse hook). Null in the sandbox → renders static at its rest position. Returns the band's GameObject so a
    // binding can hide it (MeterRowData.HideSheen) — an inactive band is skipped by the driver.
    //
    // PERF (raid 2026-11-09: the sheen was the top uiGraphic dirtier, ~15×/frame, canvasRebuild avg 3-5 ms):
    // moving a graphic every frame dirtied the WHOLE window canvas, so uGUI re-batched every graphic in the
    // window each frame. The band now carries its OWN nested Canvas — a batching boundary — so the per-frame move
    // re-batches only this one quad. overrideSorting stays FALSE: the canvas inherits the parent's sorting and
    // draws in hierarchy order (above the fill, below the shield/fades/labels, as before), and — load-bearing —
    // MaskUtilities.GetRectMaskForClippable only drops a parent RectMask2D across an intervening canvas when that
    // canvas has overrideSorting=true, so FillClip's RectMask2D still clips (and culls) the band. No
    // GraphicRaycaster: the band is raycastTarget=false. Same reasoning as WindowRenderer's IsolateCanvas.
    //
    // The clip lives HERE, not in the fill builders: every bar fill is a flat spriteless Image stretched to its
    // width-sized container, so it needs no clipping — only this band does. One RectMask2D per sheen, none per plain
    // fill (each enabled RectMask2D is a registered clipper re-evaluated every frame in PerformClipping). A sliding
    // uvRect crop instead of the mask was considered and rejected: the band is partly outside the fill almost all of
    // the time, so the crop would re-mesh the quad every frame (a uiGraphic rebuild per row per frame).
    private GameObject BuildMeterSheen(RectTransform clipRt, WindowToken token, Graphic? cullProbe)
    {
        if (clipRt.GetComponent<RectMask2D>() == null) clipRt.gameObject.AddComponent<RectMask2D>();
        var sheenGo = UGuiPrimitives.NewChild("Sheen", clipRt.transform);
        var sheenRt = sheenGo.GetComponent<RectTransform>();
        sheenRt.anchorMin = new Vector2(0f, 0f); sheenRt.anchorMax = new Vector2(0f, 1f); sheenRt.pivot = new Vector2(0f, 0.5f);
        sheenRt.sizeDelta = new Vector2(60f, 0f); sheenRt.anchoredPosition = Vector2.zero;
        var sheen = sheenGo.AddComponent<RawImage>(); sheen.texture = SheenTexture(); sheen.raycastTarget = false; sheen.color = Color.white;
        sheenGo.AddComponent<Canvas>();   // batching boundary only — overrideSorting left false (see above)
        var driver = new SheenDriver(clipRt, sheenRt, sheen, cullProbe != null ? cullProbe.canvasRenderer : null);
        System.Action<float> sweep = _ => driver.Drive();
        token.Pulses.Add(sweep); _registerPulse?.Invoke(sweep);
        return sheenGo;
    }

    private const float SheenPeriod = 2.4f;

    // Per-band drive state. Moves the band across the (variable-width) clipped fill from time — mirrors
    // MeterRowView.DrawSheen. Writes are minimised: sizeDelta only when the band width changes (it tracks the
    // fill width, which moves at the refresh cadence, not per frame); nothing at all when the band is inactive
    // (row/window hidden, HideSheen), the fill is too short to show it, or the fill is culled (scrolled out of a
    // list viewport — RectMask2D culls the fill's CanvasRenderer, a cached read with no hierarchy walk).
    private sealed class SheenDriver
    {
        private readonly RectTransform _clip, _sheen;
        private readonly RawImage _img;
        private readonly CanvasRenderer? _cullProbe;
        private float _lastBand = -1f;

        internal SheenDriver(RectTransform clip, RectTransform sheen, RawImage img, CanvasRenderer? cullProbe)
        { _clip = clip; _sheen = sheen; _img = img; _cullProbe = cullProbe; }

        internal void Drive()
        {
            if (_clip == null || _sheen == null || _img == null || !_sheen.gameObject.activeInHierarchy) return;
            if (_cullProbe != null && _cullProbe.cull) return;   // fill scrolled out of view → band invisible too
            float w = _clip.rect.width;
            if (w < 8f) { if (_img.enabled) _img.enabled = false; return; }
            if (!_img.enabled) _img.enabled = true;
            float band = Mathf.Max(50f, w * 0.55f);
            float p = (Time.realtimeSinceStartup % SheenPeriod) / SheenPeriod;
            if (!Mathf.Approximately(band, _lastBand)) { _sheen.sizeDelta = new Vector2(band, 0f); _lastBand = band; }
            _sheen.anchoredPosition = new Vector2(-band + p * (w + band), 0f);
        }
    }

    // Soft white horizontal gradient band (alpha peaks at the centre) — built once, shared by every row's sheen.
    private Texture2D? _sheenTex;
    private Texture2D SheenTexture()
    {
        if (_sheenTex != null) return _sheenTex;
        const int w = 64, h = 4;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
        { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (var x = 0; x < w; x++)
        {
            float d = Mathf.Abs(x - w * 0.5f) / (w * 0.5f);
            float a = Mathf.Clamp01(1f - d); a = a * a * 0.42f;
            var c = new Color(1f, 1f, 1f, a);
            for (var y = 0; y < h; y++) t.SetPixel(x, y, c);
        }
        t.Apply();
        _sheenTex = t;
        return t;
    }
}
