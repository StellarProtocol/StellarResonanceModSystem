using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// WindowBuilder preview/decoration leaves — the colour Swatch (theme editor) and the Pill/Bar widgets the
/// Themes panel reuses for its live preview. Pill/Bar mirror <see cref="HudElementBuilder"/>'s versions but
/// bake from <see cref="WindowThemeAssets"/> (Capsule sprite) rather than the HUD palette, so the window
/// path stays self-contained (no HudThemeAssets dependency). Fraction/colour are poll-diffed via the token
/// bindings (no per-frame animator — the window refresh tick drives them).
/// </summary>
internal sealed partial class WindowBuilder
{
    // Solid-colour box (theme-editor swatch). Matches the mockup contract: a 3-px rounded square with a 1-px
    // dark border (a dark border layer + an inset coloured fill — one Image can't tint border + fill
    // independently). Fill colour poll-diffed via SwatchBinding so it tracks live edits.
    private void BuildSwatch(SwatchElement sw, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Swatch", parent);
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = sw.Size; le.preferredHeight = sw.Size; le.flexibleWidth = 0f;
        var border = go.AddComponent<Image>();
        border.sprite = _assets.SwatchBg; border.type = Image.Type.Sliced;
        border.color = new Color(0f, 0f, 0f, 0.4f); border.raycastTarget = false;

        var fillGo = UGuiPrimitives.NewChild("Fill", go.transform);
        var frt = fillGo.GetComponent<RectTransform>();
        frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
        frt.offsetMin = new Vector2(1f, 1f); frt.offsetMax = new Vector2(-1f, -1f);   // 1-px border inset
        var fill = fillGo.AddComponent<Image>();
        fill.sprite = _assets.SwatchBg; fill.type = Image.Type.Sliced; fill.raycastTarget = false;
        token.Swatches.Add(new SwatchBinding { Img = fill, Get = sw.Color });
    }

    // Level-pill chip: rounded sprite behind a centred label, sized to the label + padding. The bg is an
    // ignore-layout stretched child so the HorizontalLayoutGroup measures only the text (mirrors the HUD
    // BuildPill). Uses the accent button sprite as the chip background.
    private void BuildPill(PillElement p, Transform parent, WindowToken token)
    {
        if (_surface == SurfaceStyle.HudOverlay) { BuildPillHud(p, parent, token); return; }   // .HudOverlay.cs
        var go = UGuiPrimitives.NewChild("Pill", parent);
        var lg = go.AddComponent<HorizontalLayoutGroup>();
        lg.padding = new RectOffset(11, 11, 3, 3);
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = false; lg.childForceExpandHeight = false;
        lg.childAlignment = TextAnchor.MiddleCenter;
        go.AddComponent<LayoutElement>().flexibleWidth = 0f;   // chip stays content-sized in its row

        var bg = UGuiPrimitives.NewChild("Bg", go.transform);
        bg.AddComponent<LayoutElement>().ignoreLayout = true;
        UGuiPrimitives.Stretch(bg);
        var bgImg = bg.AddComponent<Image>();
        bgImg.sprite = _assets.ButtonAccentBg; bgImg.type = Image.Type.Sliced; bgImg.raycastTarget = false;
        token.ReskinActions.Add(() => { if (bgImg != null) bgImg.sprite = _assets.ButtonAccentBg; });

        var labelGo = UGuiPrimitives.NewChild("Label", go.transform);
        var label = labelGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(label, Scaled(12), TextAnchor.MiddleCenter, bold: true);
        ApplyMenuFont(label);
        label.color = _assets.MenuText;
        token.Texts.Add(new TextBinding { C = label, TextFn = p.Text, ColorFn = p.Color });
        RegisterTextReskin(token, label, 12);
    }

    // HP/Stamina-style bar: rounded track + left-anchored coloured fill + right-aligned numeric in a fixed
    // column. Fill fraction + label poll-diffed via BarBinding (no animator — the window tick refreshes).
    // Honours the BarElement geometry fields (Height/Width/FillWidth/LabelFontSize/LabelInside); BarStyle.Modern
    // branches to the CombatMeter metric-bar render (BuildBarModernWindow).
    private void BuildBar(BarElement b, Transform parent, WindowToken token)
    {
        if (b.Style == BarStyle.Modern) { BuildBarModernWindow(b, parent, token); return; }
        if (_surface == SurfaceStyle.HudOverlay) { BuildBarHud(b, parent, token); return; }   // .HudOverlay.cs (Default-style HUD bar)
        int ls = b.LabelFontSize > 0 ? b.LabelFontSize : 12;

        var row = BuildBarRow(parent);
        if (b.Prefix != null) BuildBarPrefix(b, row.transform, ls, token);

        var clipRt = BuildBarTrack(row.transform, b);
        var label = BuildBarLabel(b, row.transform, clipRt, ls, token);
        token.Bars.Add(new BarBinding { FillRect = clipRt, Fraction = b.Fraction01, Label = label, LabelFn = b.Label });
    }

    // Shared "Bar" row host (HorizontalLayoutGroup, MiddleLeft, 6-px spacing) — reused by both bar styles.
    private static GameObject BuildBarRow(Transform parent)
    {
        var row = UGuiPrimitives.NewChild("Bar", parent);
        var lg = row.AddComponent<HorizontalLayoutGroup>();
        lg.spacing = 6f;
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = false; lg.childForceExpandHeight = false;
        lg.childAlignment = TextAnchor.MiddleLeft;
        return row;
    }

    // Fixed-width left caption column (e.g. "HP") so stacked bars align. Shared by both bar styles.
    private void BuildBarPrefix(BarElement b, Transform row, int ls, WindowToken token)
    {
        var pslot = UGuiPrimitives.NewChild("Prefix", row);
        var ptxt = pslot.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(ptxt, Scaled(ls), TextAnchor.MiddleLeft, bold: true);
        ApplyMenuFont(ptxt);
        ptxt.color = _assets.MenuText; ptxt.text = b.Prefix;
        pslot.AddComponent<LayoutElement>().preferredWidth = 60f;
        RegisterTextReskin(token, ptxt, ls);
    }

    // Default-style label: either the beside-the-bar right-aligned Num slot (today's behaviour), or — when
    // LabelInside — a muted label overlaid centred ON the track (a later sibling of the fill clip → drawn on
    // top). Returns the Text for the BarBinding, or null when there is no label.
    private Text? BuildBarLabel(BarElement b, Transform row, RectTransform clipRt, int ls, WindowToken token)
    {
        if (b.Label == null) return null;
        Text txt;
        if (b.LabelInside)
        {
            var slot = UGuiPrimitives.NewChild("Num", clipRt.parent);   // overlay ON the track, over the fill
            UGuiPrimitives.Stretch(slot);
            txt = slot.AddComponent<Text>();
            UGuiPrimitives.ConfigureText(txt, Scaled(ls), TextAnchor.MiddleCenter, bold: false);
            txt.raycastTarget = false;
        }
        else
        {
            var slot = UGuiPrimitives.NewChild("Num", row);
            slot.AddComponent<LayoutElement>().preferredWidth = 84f;
            txt = slot.AddComponent<Text>();
            UGuiPrimitives.ConfigureText(txt, Scaled(ls), TextAnchor.MiddleRight, bold: false);
        }
        ApplyMenuFont(txt); txt.color = _assets.MenuMuted;
        RegisterTextReskin(token, txt, ls, muted: true);
        return txt;
    }

    // Gently-rounded (3 px) dark track (matches the PlayerHUD BarBg) + a left-anchored width-clipped fill, and
    // returns the clip RectTransform so the binding can drive its right anchor. The fill width tracks Fraction01
    // via the clip container's anchorMax.x (NOT Image.Type.Filled + fillAmount): a uGUI Image with no sprite
    // ignores fillAmount and draws a FULL quad, so the migrated Filled fill stayed full regardless of the
    // fraction. Anchor-resize needs no sprite (so no rounded-corner stretch artifact) and mirrors how the
    // MeterRow/AccentRow clip their fill width. Geometry from Height/Width/FillWidth (defaults 14/150/fixed).
    private RectTransform BuildBarTrack(Transform row, BarElement b)
    {
        var track = UGuiPrimitives.NewChild("Track", row);
        var tle = track.AddComponent<LayoutElement>();
        if (b.FillWidth) { tle.flexibleWidth = 1f; tle.preferredWidth = 0f; }
        else { tle.preferredWidth = b.Width > 0f ? b.Width : 150f; tle.flexibleWidth = 0f; }
        tle.preferredHeight = b.Height > 0f ? b.Height : 14f;
        var trackImg = track.AddComponent<Image>();
        trackImg.sprite = _assets.SwatchBg; trackImg.type = Image.Type.Sliced;
        trackImg.color = new Color(0f, 0f, 0f, 0.38f); trackImg.raycastTarget = false;

        var clipGo = UGuiPrimitives.NewChild("FillClip", track.transform);
        var clipRt = clipGo.GetComponent<RectTransform>();
        clipRt.anchorMin = new Vector2(0f, 0f); clipRt.pivot = new Vector2(0f, 0.5f);
        clipRt.anchorMax = new Vector2(Mathf.Clamp01(b.Fraction01()), 1f);
        clipRt.offsetMin = Vector2.zero; clipRt.offsetMax = Vector2.zero;

        var fillGo = UGuiPrimitives.NewChild("Fill", clipGo.transform);
        UGuiPrimitives.Stretch(fillGo);
        var fill = fillGo.AddComponent<Image>();
        fill.type = Image.Type.Simple; fill.raycastTarget = false;
        fill.color = new Color(b.Fill.R, b.Fill.G, b.Fill.B, b.Fill.A);
        return clipRt;
    }

    // Modern (BarStyle.Modern) window bar — the CombatMeter metric-bar look, REUSING the WindowBuilder.MeterRow
    // primitives (MeterTrackBg / MeterBarH / SheenTexture / DriveSheen / AddOverlayText — nothing ported from
    // HudElementBuilder.Meter.cs, no new pulse/animator/texture code). Visually mirrors the HUD BuildBarMeter
    // render. Honours Height/Width/FillWidth + Prefix (left caption); LabelInside is meaningless with dual
    // overlays (ignored, matching the HUD). Fill width driven by BarBinding; overlays by TextBinding.
    private void BuildBarModernWindow(BarElement b, Transform parent, WindowToken token)
    {
        int ls = b.LabelFontSize > 0 ? b.LabelFontSize : 10;

        var row = BuildBarRow(parent);
        if (b.Prefix != null) BuildBarPrefix(b, row.transform, ls, token);

        var (track, clipRt) = BuildModernTrack(b, row.transform);
        if (b.Sheen) BuildModernSheen(clipRt, token);
        if (b.Overlay01 != null) BuildModernOverlay(b, track, clipRt, token);
        token.Bars.Add(new BarBinding { FillRect = clipRt, Fraction = b.Fraction01 });

        if (b.Label != null)
            token.Texts.Add(new TextBinding { C = AddOverlayText(token, track, "Primary", TextAnchor.MiddleLeft, ls), TextFn = b.Label });
        if (b.SecondaryLabel != null)
            token.Texts.Add(new TextBinding { C = AddOverlayText(token, track, "Secondary", TextAnchor.MiddleRight, ls), TextFn = b.SecondaryLabel });
    }

    // Flat translucent track + width-anchored RectMask2D clip + flat role-coloured fill. Mirrors
    // WindowBuilder.MeterRow.BuildMeterBar (MeterRow.cs:322-336), honouring the BarElement geometry. Returns the
    // track transform (overlay-text parent) and the fill-clip RectTransform (the BarBinding drives its anchorMax.x).
    private (Transform Track, RectTransform ClipRt) BuildModernTrack(BarElement b, Transform row)
    {
        var track = UGuiPrimitives.NewChild("Track", row);
        var tle = track.AddComponent<LayoutElement>();
        if (b.FillWidth) { tle.flexibleWidth = 1f; tle.preferredWidth = 0f; }
        else { tle.preferredWidth = b.Width > 0f ? b.Width : 150f; tle.flexibleWidth = 0f; }
        tle.preferredHeight = b.Height > 0f ? b.Height : MeterBarH;
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
        fill.raycastTarget = false;
        fill.color = new Color(b.Fill.R, b.Fill.G, b.Fill.B, b.Fill.A);
        return (track.transform, clipRt);
    }

    // Sheen: a soft white band scrolled left→right within the clipped fill (per-frame, ticker-driven via the
    // pulse hook), REUSING the shared SheenTexture/DriveSheen. Registered exactly as MeterRow.cs:340-346.
    private void BuildModernSheen(RectTransform clipRt, WindowToken token)
    {
        var sheenGo = UGuiPrimitives.NewChild("Sheen", clipRt.transform);
        var sheenRt = sheenGo.GetComponent<RectTransform>();
        sheenRt.anchorMin = new Vector2(0f, 0f); sheenRt.anchorMax = new Vector2(0f, 1f); sheenRt.pivot = new Vector2(0f, 0.5f);
        sheenRt.sizeDelta = new Vector2(60f, 0f); sheenRt.anchoredPosition = Vector2.zero;
        var sheen = sheenGo.AddComponent<RawImage>();
        sheen.texture = SheenTexture(); sheen.raycastTarget = false; sheen.color = Color.white;
        System.Action<float> sweep = _ => DriveSheen(clipRt, sheenRt, sheen);
        token.Pulses.Add(sweep); _registerPulse?.Invoke(sweep);
    }

    // Secondary "overlay" fill on the SAME Modern track (e.g. a monster shield on an HP bar). Builds a second
    // width-clipped fill (mirrors the main FillClip) and drives it via its OWN BarBinding from Overlay01. Draw
    // order: OverlayInFront → the overlay is a later sibling than the main FillClip (created after it here) so it
    // renders OVER the main fill as a translucent band, while the label overlay texts (added afterwards) stay on
    // top. !OverlayInFront → SetAsFirstSibling puts it BEHIND the main fill so the opaque main fill covers it and
    // only the excess shows as an extension cap. Called only when b.Overlay01 != null.
    private void BuildModernOverlay(BarElement b, Transform track, RectTransform mainClip, WindowToken token)
    {
        var overlayRt = BuildOverlayClip(track, b.OverlayColor);
        overlayRt.anchorMax = new Vector2(Mathf.Clamp01(b.Overlay01!()), 1f);
        if (!b.OverlayInFront) overlayRt.SetAsFirstSibling();   // behind the main fill (extension cap)
        token.Bars.Add(new BarBinding { FillRect = overlayRt, Fraction = b.Overlay01! });
    }

    // Builds a left-anchored, width-clipped fill on the given track — identical to BuildModernTrack's main
    // FillClip (RectMask2D + a stretched spriteless Fill Image painted from ColorRgba incl. alpha). Returns the
    // clip RectTransform so a BarBinding can drive its anchorMax.x. Caller sets the initial width + draw order.
    private static RectTransform BuildOverlayClip(Transform track, ColorRgba color)
    {
        var clipGo = UGuiPrimitives.NewChild("OverlayClip", track);
        var clipRt = clipGo.GetComponent<RectTransform>();
        clipRt.anchorMin = new Vector2(0f, 0f); clipRt.pivot = new Vector2(0f, 0.5f);
        clipRt.anchorMax = new Vector2(0f, 1f);
        clipRt.offsetMin = Vector2.zero; clipRt.offsetMax = Vector2.zero;
        clipGo.AddComponent<RectMask2D>();

        var fillGo = UGuiPrimitives.NewChild("Fill", clipGo.transform);
        UGuiPrimitives.Stretch(fillGo);
        var fill = fillGo.AddComponent<Image>();
        fill.raycastTarget = false;
        fill.color = new Color(color.R, color.G, color.B, color.A);
        return clipRt;
    }
}
