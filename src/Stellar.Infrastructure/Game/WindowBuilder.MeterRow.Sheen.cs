using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Meter-row sheen — split out of WindowBuilder.MeterRow.cs (which sat at the 500-LoC file gate). A partial of
// the same WindowBuilder class: builds the soft white band that sweeps across a width-clipped bar fill, drives it
// per frame from the ticker pulse hook, and owns the shared gradient texture. Also reused by the Modern preview
// bar (WindowBuilder.Preview.cs BuildModernSheen).
internal sealed partial class WindowBuilder
{
    // Sheen: a soft white band scrolled left→right within the clipped fill (per-frame, ticker-driven via the
    // pulse hook). Null in the sandbox → renders static at its rest position.
    private void BuildMeterSheen(RectTransform clipRt, WindowToken token)
    {
        var sheenGo = UGuiPrimitives.NewChild("Sheen", clipRt.transform);
        var sheenRt = sheenGo.GetComponent<RectTransform>();
        sheenRt.anchorMin = new Vector2(0f, 0f); sheenRt.anchorMax = new Vector2(0f, 1f); sheenRt.pivot = new Vector2(0f, 0.5f);
        sheenRt.sizeDelta = new Vector2(60f, 0f); sheenRt.anchoredPosition = Vector2.zero;
        var sheen = sheenGo.AddComponent<RawImage>(); sheen.texture = SheenTexture(); sheen.raycastTarget = false; sheen.color = Color.white;
        System.Action<float> sweep = _ => DriveSheen(clipRt, sheenRt, sheen);
        token.Pulses.Add(sweep); _registerPulse?.Invoke(sweep);
    }

    private const float SheenPeriod = 2.4f;

    // Move the sheen band across the (variable-width) clipped fill from time — mirrors MeterRowView.DrawSheen.
    private static void DriveSheen(RectTransform clip, RectTransform sheen, RawImage img)
    {
        if (clip == null || sheen == null || img == null || !sheen.gameObject.activeInHierarchy) return;
        float w = clip.rect.width;
        if (w < 8f) { if (img.enabled) img.enabled = false; return; }
        if (!img.enabled) img.enabled = true;
        float band = Mathf.Max(50f, w * 0.55f);
        float p = (Time.realtimeSinceStartup % SheenPeriod) / SheenPeriod;
        sheen.sizeDelta = new Vector2(band, 0f);
        sheen.anchoredPosition = new Vector2(-band + p * (w + band), 0f);
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
