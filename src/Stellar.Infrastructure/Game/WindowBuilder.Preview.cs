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

        // Gently-rounded (3 px) dark track (matches the PlayerHUD BarBg) + a sprite-less flat Filled fill
        // (a rounded sprite under Type.Filled stretched its corners → the grey "left bar" sliver).
        var track = UGuiPrimitives.NewChild("Track", row.transform);
        var tle = track.AddComponent<LayoutElement>();
        tle.preferredWidth = 150f; tle.preferredHeight = 14f; tle.flexibleWidth = 0f;
        var trackImg = track.AddComponent<Image>();
        trackImg.sprite = _assets.SwatchBg; trackImg.type = Image.Type.Sliced;
        trackImg.color = new Color(0f, 0f, 0f, 0.38f); trackImg.raycastTarget = false;

        var fillGo = UGuiPrimitives.NewChild("Fill", track.transform);
        UGuiPrimitives.Stretch(fillGo);
        var fill = fillGo.AddComponent<Image>();
        fill.type = Image.Type.Filled; fill.fillMethod = Image.FillMethod.Horizontal; fill.fillOrigin = 0;
        fill.color = new Color(b.Fill.R, b.Fill.G, b.Fill.B, b.Fill.A);
        fill.fillAmount = Mathf.Clamp01(b.Fraction01()); fill.raycastTarget = false;

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
        token.Bars.Add(new BarBinding { Fill = fill, Fraction = b.Fraction01, Label = label, LabelFn = b.Label });
    }
}
