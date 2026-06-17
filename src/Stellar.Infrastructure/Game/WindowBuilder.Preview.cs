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
    private void BuildBar(BarElement b, Transform parent, WindowToken token)
    {
        var row = UGuiPrimitives.NewChild("Bar", parent);
        var lg = row.AddComponent<HorizontalLayoutGroup>();
        lg.spacing = 6f;
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = false; lg.childForceExpandHeight = false;
        lg.childAlignment = TextAnchor.MiddleLeft;

        if (b.Prefix != null)
        {
            var pslot = UGuiPrimitives.NewChild("Prefix", row.transform);
            var ptxt = pslot.AddComponent<Text>();
            UGuiPrimitives.ConfigureText(ptxt, Scaled(12), TextAnchor.MiddleLeft, bold: true);
            ApplyMenuFont(ptxt);
            ptxt.color = _assets.MenuText; ptxt.text = b.Prefix;
            pslot.AddComponent<LayoutElement>().preferredWidth = 60f;
            RegisterTextReskin(token, ptxt, 12);
        }

        var clipRt = BuildBarTrack(row.transform, b);

        Text? label = null;
        if (b.Label != null)
        {
            var slot = UGuiPrimitives.NewChild("Num", row.transform);
            var txt = slot.AddComponent<Text>();
            UGuiPrimitives.ConfigureText(txt, Scaled(12), TextAnchor.MiddleRight, bold: false);
            ApplyMenuFont(txt); txt.color = _assets.MenuMuted;
            slot.AddComponent<LayoutElement>().preferredWidth = 84f;
            RegisterTextReskin(token, txt, 12, muted: true);
            label = txt;
        }
        token.Bars.Add(new BarBinding { FillRect = clipRt, Fraction = b.Fraction01, Label = label, LabelFn = b.Label });
    }

    // Gently-rounded (3 px) dark track (matches the PlayerHUD BarBg) + a left-anchored width-clipped fill, and
    // returns the clip RectTransform so the binding can drive its right anchor. The fill width tracks Fraction01
    // via the clip container's anchorMax.x (NOT Image.Type.Filled + fillAmount): a uGUI Image with no sprite
    // ignores fillAmount and draws a FULL quad, so the migrated Filled fill stayed full regardless of the
    // fraction. Anchor-resize needs no sprite (so no rounded-corner stretch artifact) and mirrors how the
    // MeterRow/AccentRow clip their fill width.
    private RectTransform BuildBarTrack(Transform row, BarElement b)
    {
        var track = UGuiPrimitives.NewChild("Track", row);
        var tle = track.AddComponent<LayoutElement>();
        tle.preferredWidth = 150f; tle.preferredHeight = 14f; tle.flexibleWidth = 0f;
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
}
