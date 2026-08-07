using System;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="HudElementBuilder"/> leaf for the opt-in <see cref="BarStyle.Modern"/> render — the CombatMeter
/// metric-bar look reproduced for the HUD toolkit: a flat translucent track, a flat role-coloured fill clipped
/// to the fraction by a width-anchored <see cref="RectMask2D"/> container (NOT an <c>Image.Type.Filled</c>
/// fill, so no corner-smoothing/animator), dual left/right overlay texts, and an optional per-frame sheen band
/// that sweeps across the fill. Ported from <c>WindowBuilder.MeterRow</c> so the HUD path stays free of any
/// WindowBuilder dependency (the two builders share no base; the meter uses flat colours + a procedural sheen,
/// so there is nothing to pull from <see cref="HudThemeAssets"/>). The <see cref="BarStyle.Default"/> path in
/// the sibling partial is untouched.
/// </summary>
internal sealed partial class HudElementBuilder
{
    // Flat meter chrome (matches WindowBuilder.MeterRow.MeterTrackBg). No sprites/gradients.
    private static readonly Color MeterTrackBg = new(1f, 1f, 1f, 0.07f);
    private const int   MeterOverlaySize  = 10;    // overlay text default px when LabelFontSize == 0
    private const float MeterOverlayInset = 5f;    // horizontal inset of the overlay texts from the track edges
    private const float SheenPeriod       = 2.4f;  // seconds per sweep (matches the CombatMeter row)

    // Meter-style bar: flat track + anchor-clipped flat fill + dual overlay text (+ optional sheen). Honours
    // Height/Width/FillWidth exactly as the Default path; Prefix is kept as a left caption column for parity so
    // no BarElement field is silently dropped. LabelInside is ignored (the meter always overlays both texts).
    private void BuildBarMeter(BarElement b, Transform parent, HudToken token)
    {
        int ls = b.LabelFontSize > 0 ? b.LabelFontSize : MeterOverlaySize;
        var (track, clipRt) = BuildMeterTrackAndFill(b, parent);

        if (b.Sheen) BuildSheen(clipRt, token);

        // Overlay texts span the full track (later siblings → drawn over the fill). 5-px horizontal inset.
        Text? primary = null, primaryShadow = null, secondary = null, secondaryShadow = null;
        if (b.Label != null)          (primary, primaryShadow)     = BuildMeterOverlay(track, ls, TextAnchor.MiddleLeft);
        if (b.SecondaryLabel != null) (secondary, secondaryShadow) = BuildMeterOverlay(track, ls, TextAnchor.MiddleRight);

        token.MeterBars.Add(new MeterBarBinding
        {
            FillClip = clipRt, Fraction = b.Fraction01,
            Primary = primary, PrimaryShadow = primaryShadow, PrimaryFn = b.Label,
            Secondary = secondary, SecondaryShadow = secondaryShadow, SecondaryFn = b.SecondaryLabel,
        });
    }

    // Row + optional prefix caption + flat translucent track + width-clipped flat fill. Returns the track
    // transform (overlay-text parent) and the fill-clip RectTransform (the binding drives its anchorMax.x, and
    // the RectMask2D on it clips both the fill and the sheen to the filled width — the GUI.BeginClip analog).
    private (Transform Track, RectTransform ClipRt) BuildMeterTrackAndFill(BarElement b, Transform parent)
    {
        float h  = b.Height > 0f ? b.Height : BarHeight;
        int   ls = b.LabelFontSize > 0 ? b.LabelFontSize : MeterOverlaySize;

        var row = UGuiPrimitives.NewChild("Bar", parent);
        var lg = row.AddComponent<HorizontalLayoutGroup>();
        lg.spacing = 6f;
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = false; lg.childForceExpandHeight = false;
        lg.childAlignment = TextAnchor.MiddleLeft;

        if (b.Prefix != null)
        {
            var (pslot, pfg, pshadow) = MakeShadowedText(row.transform, ls, TextAnchor.MiddleLeft, bold: true);
            pslot.AddComponent<LayoutElement>().preferredWidth = BarPrefixWidth;
            pfg.text = b.Prefix; pshadow.text = b.Prefix;   // static caption — no binding needed
        }

        var track = UGuiPrimitives.NewChild("Track", row.transform);
        var tle = track.AddComponent<LayoutElement>();
        if (b.FillWidth) { tle.flexibleWidth = 1f; tle.preferredWidth = 0f; }
        else { tle.preferredWidth = b.Width > 0f ? b.Width : BarTrackWidth; tle.flexibleWidth = 0f; }
        tle.preferredHeight = h;
        var trackImg = track.AddComponent<Image>();
        trackImg.color = MeterTrackBg; trackImg.raycastTarget = false;   // flat, NO sprite

        var clipGo = UGuiPrimitives.NewChild("FillClip", track.transform);
        var clipRt = clipGo.GetComponent<RectTransform>();
        clipRt.anchorMin = new Vector2(0f, 0f); clipRt.pivot = new Vector2(0f, 0.5f);
        clipRt.anchorMax = new Vector2(Mathf.Clamp01(b.Fraction01()), 1f);
        clipRt.offsetMin = Vector2.zero; clipRt.offsetMax = Vector2.zero;
        clipGo.AddComponent<RectMask2D>();

        var fillGo = UGuiPrimitives.NewChild("Fill", clipGo.transform);
        UGuiPrimitives.Stretch(fillGo);
        var fill = fillGo.AddComponent<Image>();   // flat, NO sprite, no Image.Type.Filled
        fill.color = new Color(b.Fill.R, b.Fill.G, b.Fill.B, b.Fill.A);
        fill.raycastTarget = false;

        return (track.transform, clipRt);
    }

    // Full-track overlay text (shadowed twin kept — reads better over the world than the meter's plain white).
    // MakeShadowedText's slot HLG aligns the foreground to anchor; stretched with a 5-px inset gives the
    // left/right edge padding. Returns (foreground, shadow) for the binding to update.
    private (Text Fg, Text Shadow) BuildMeterOverlay(Transform track, int size, TextAnchor anchor)
    {
        var (slot, fg, shadow) = MakeShadowedText(track, size, anchor, bold: true);
        UGuiPrimitives.Stretch(slot);
        var srt = slot.GetComponent<RectTransform>();
        srt.offsetMin = new Vector2(MeterOverlayInset, 0f);
        srt.offsetMax = new Vector2(-MeterOverlayInset, 0f);
        fg.raycastTarget = false; shadow.raycastTarget = false;
        return (fg, shadow);
    }

    // Sheen: a soft white band scrolled left→right within the clipped fill, as a child of the fill-clip so the
    // same RectMask2D clips it to the filled width. Registered as a per-frame pulse (elapsed-seconds driven).
    private void BuildSheen(RectTransform clipRt, HudToken token)
    {
        var sheenGo = UGuiPrimitives.NewChild("Sheen", clipRt.transform);
        var sheenRt = sheenGo.GetComponent<RectTransform>();
        sheenRt.anchorMin = new Vector2(0f, 0f); sheenRt.anchorMax = new Vector2(0f, 1f); sheenRt.pivot = new Vector2(0f, 0.5f);
        sheenRt.sizeDelta = new Vector2(60f, 0f); sheenRt.anchoredPosition = Vector2.zero;
        var sheen = sheenGo.AddComponent<RawImage>();
        sheen.texture = SheenTexture(); sheen.raycastTarget = false; sheen.color = Color.white;

        Action<float> sweep = elapsed => DriveSheen(clipRt, sheenRt, sheen, elapsed);
        token.Pulses.Add(sweep);
        _registerPulse?.Invoke(sweep);
    }

    // Move the sheen band across the (variable-width) clipped fill from an elapsed-seconds clock — mirrors
    // WindowBuilder.MeterRow.DriveSheen, but the time source is the renderer's accumulated tick delta (threaded
    // through the pulse arg) rather than a fresh Time.realtimeSinceStartup read.
    private static void DriveSheen(RectTransform clip, RectTransform sheen, RawImage img, float elapsed)
    {
        if (clip == null || sheen == null || img == null || !sheen.gameObject.activeInHierarchy) return;
        float w = clip.rect.width;
        if (w < 8f) { if (img.enabled) img.enabled = false; return; }
        if (!img.enabled) img.enabled = true;
        float band = Mathf.Max(50f, w * 0.55f);
        float p = (elapsed % SheenPeriod) / SheenPeriod;
        sheen.sizeDelta = new Vector2(band, 0f);
        sheen.anchoredPosition = new Vector2(-band + p * (w + band), 0f);
    }

    // Soft white horizontal gradient band (alpha peaks at the centre) — built once, shared by every sheen.
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

    // The shared sheen texture uses HideFlags.HideAndDontSave, so it is exempt from UnloadUnusedAssets AND from
    // GameObject destruction — HudRenderer.DropCanvas(_canvas) won't reclaim it. Destroy it explicitly when the
    // builder is dropped (called from HudRenderer.DropCanvas before nulling _builder), mirroring
    // WindowElementBuilder.ColorPicker.Destroy — else every theme-switch/scene-change canvas rebuild leaks a
    // 64×4 texture. Null when Sheen was never enabled (SheenTexture never called): the guard covers it.
    internal void DisposeTextures()
    {
        if (_sheenTex != null) UnityEngine.Object.Destroy(_sheenTex);
        _sheenTex = null;
    }

    // Poll-diffed Meter-bar binding: width-clips the fill via the clip rect's right anchor (NOT fillAmount — a
    // spriteless Image ignores it) and re-pulls the two overlay texts. Applied on the HUD refresh cap,
    // mirroring WindowBuilder.BarBinding; the sheen scroll is a separate per-frame pulse.
    internal sealed class MeterBarBinding
    {
        public RectTransform FillClip = null!;
        public Func<float> Fraction = null!;
        public Text? Primary; public Text? PrimaryShadow; public Func<string>? PrimaryFn;
        public Text? Secondary; public Text? SecondaryShadow; public Func<string>? SecondaryFn;
        private float _lastFrac = -1f;
        private string? _lastPrimary, _lastSecondary;

        public void Apply()
        {
            if (FillClip != null && !FillClip.gameObject.activeInHierarchy) return;
            if (FillClip != null)
            {
                var f = Mathf.Clamp01(Fraction());
                if (!Mathf.Approximately(f, _lastFrac)) { FillClip.anchorMax = new Vector2(f, 1f); _lastFrac = f; }
            }
            if (Primary != null && PrimaryFn != null)
            {
                var s = PrimaryFn();
                if (s != _lastPrimary) { Primary.text = s; if (PrimaryShadow != null) PrimaryShadow.text = s; _lastPrimary = s; }
            }
            if (Secondary != null && SecondaryFn != null)
            {
                var s = SecondaryFn();
                if (s != _lastSecondary) { Secondary.text = s; if (SecondaryShadow != null) SecondaryShadow.text = s; _lastSecondary = s; }
            }
        }
    }
}
