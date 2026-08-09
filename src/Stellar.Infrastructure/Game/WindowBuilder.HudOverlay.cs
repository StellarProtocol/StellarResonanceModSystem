using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="WindowBuilder"/> leaves for the opt-in <see cref="SurfaceStyle.HudOverlay"/> render — the
/// borderless HUD look reproduced on the window path so a HUD-path plugin can migrate onto a window with
/// pixel-exact fidelity. The three leaf renders here are ported byte-for-byte from <c>HudElementBuilder</c>
/// (BuildText :220-239, BuildBar/BuildBarLabel :269-330, BuildPill :246-263, MakeShadowedText :406-429) — the
/// only substitutions are <see cref="HudThemeAssets"/> read off the builder's <see cref="WindowBuilder.HudAssets"/>
/// (baked/owned by <c>WindowRenderer</c>) instead of a ctor field, and the per-frame bar smoothing reproduced
/// through the window pulse hook (<c>_registerPulse</c> / <c>token.Pulses</c>) rather than the HUD's injected
/// <c>HudBarAnimator</c>. The Menu-path leaves in the sibling partials are untouched.
/// </summary>
internal sealed partial class WindowBuilder
{
    // HUD leaf geometry — verbatim from HudElementBuilder (Pill 12/4 pad + 13px; Bar 150/14, 84 numeric, 60
    // prefix, 12 label). Prefixed to avoid colliding with the window builder's own bar/meter constants.
    private const int   HudPillPadX      = 12;
    private const int   HudPillPadY      = 4;
    private const int   HudPillTextSize  = 13;
    private const float HudBarTrackWidth = 150f;
    private const float HudBarHeight     = 14f;
    private const float HudBarNumericWidth = 84f;
    private const float HudBarPrefixWidth  = 60f;
    private const int   HudBarLabelSize    = 12;

    // Active HUD text colours (white / black-0.85 fallback in the sandbox where HudAssets is unbaked/null).
    private Color HudTextColor   => HudAssets?.HudText ?? Color.white;
    private Color HudShadowColor => HudAssets?.HudTextShadow ?? new Color(0f, 0f, 0f, 0.85f);

    // Port of HudElementBuilder.BuildText (:220-239): shadowed no-wrap text sized by DynamicFontSize ?? FontSize
    // ?? (Emphasis ? 20 : 16), honouring ShadowDistance (twin offset). The twin + live font size update on the
    // refresh tick via TextBinding's HudOverlay fields (Shadow / DynamicFontSizeFn).
    private void BuildTextHud(TextElement t, Transform parent, WindowToken token)
    {
        int size = t.DynamicFontSize != null ? Math.Max(1, t.DynamicFontSize())
                 : t.FontSize > 0            ? t.FontSize
                 :                             (t.Emphasis ? 20 : 16);
        var anchor = t.Align switch
        {
            TextAlign.Center => TextAnchor.MiddleCenter,
            TextAlign.Right  => TextAnchor.MiddleRight,
            _                => TextAnchor.MiddleLeft,
        };
        var (slot, fg, shadow) = MakeShadowedTextHud(parent, size, anchor, bold: t.Emphasis, shadowOffset: t.ShadowDistance);
        HudTextReskin(token, fg, shadow);
        if (t.Width > 0f)
        {
            var le = slot.AddComponent<LayoutElement>();
            le.preferredWidth = t.Width;
            le.flexibleWidth = 0f;
        }
        token.Texts.Add(new TextBinding { C = fg, Shadow = shadow, TextFn = t.Text, ColorFn = t.Color, DynamicFontSizeFn = t.DynamicFontSize });
    }

    // Port of HudElementBuilder.BuildPill (:246-263): transparent HudPillBg 9-slice chip (ignore-layout stretched
    // bg so the HLG measures only the text) + centred shadowed text, 12/4 padding, 13px bold.
    private void BuildPillHud(PillElement p, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Pill", parent);
        var lg = go.AddComponent<HorizontalLayoutGroup>();
        lg.padding = new RectOffset(HudPillPadX, HudPillPadX, HudPillPadY, HudPillPadY);
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = false; lg.childForceExpandHeight = false;
        lg.childAlignment = TextAnchor.MiddleCenter;

        var bg = UGuiPrimitives.NewChild("Bg", go.transform);
        bg.AddComponent<LayoutElement>().ignoreLayout = true;
        UGuiPrimitives.Stretch(bg);
        var img = bg.AddComponent<Image>();
        img.sprite = HudAssets?.PillBg; img.type = Image.Type.Sliced; img.raycastTarget = false;
        token.ReskinActions.Add(() => { if (img != null) img.sprite = HudAssets?.PillBg; });

        var (_, fg, shadow) = MakeShadowedTextHud(go.transform, HudPillTextSize, TextAnchor.MiddleCenter, bold: true);
        HudTextReskin(token, fg, shadow);
        token.Texts.Add(new TextBinding { C = fg, Shadow = shadow, TextFn = p.Text, ColorFn = p.Color });
    }

    // Port of HudElementBuilder.BuildBar (Default path, :269-311): rounded 9-slice HudBarBg track + an
    // Image.Type.Filled coloured fill smoothed per-frame (RegisterBarSmoothing reproduces HudBarAnimator) + a
    // shadowed side/inside label and a shadowed fixed-width Prefix caption. Honours Height/Width/FillWidth/LabelFontSize.
    private void BuildBarHud(BarElement b, Transform parent, WindowToken token)
    {
        float h = b.Height > 0f ? b.Height : HudBarHeight;
        int ls = b.LabelFontSize > 0 ? b.LabelFontSize : HudBarLabelSize;

        var row = UGuiPrimitives.NewChild("Bar", parent);
        var lg = row.AddComponent<HorizontalLayoutGroup>();
        lg.spacing = 6f;
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = false; lg.childForceExpandHeight = false;
        lg.childAlignment = TextAnchor.MiddleLeft;

        if (b.Prefix != null)
        {
            var (pslot, pfg, pshadow) = MakeShadowedTextHud(row.transform, ls, TextAnchor.MiddleLeft, bold: true);
            HudTextReskin(token, pfg, pshadow);
            pslot.AddComponent<LayoutElement>().preferredWidth = HudBarPrefixWidth;
            pfg.text = b.Prefix; pshadow.text = b.Prefix;   // static caption — no binding needed
        }

        var track = UGuiPrimitives.NewChild("Track", row.transform);
        var tle = track.AddComponent<LayoutElement>();
        if (b.FillWidth) { tle.flexibleWidth = 1f; tle.preferredWidth = 0f; }
        else { tle.preferredWidth = b.Width > 0f ? b.Width : HudBarTrackWidth; tle.flexibleWidth = 0f; }
        tle.preferredHeight = h;
        var trackImg = track.AddComponent<Image>();
        trackImg.sprite = HudAssets?.BarBg; trackImg.type = Image.Type.Sliced; trackImg.raycastTarget = false;
        token.ReskinActions.Add(() => { if (trackImg != null) trackImg.sprite = HudAssets?.BarBg; });

        var fillGo = UGuiPrimitives.NewChild("Fill", track.transform);
        UGuiPrimitives.Stretch(fillGo);
        var fill = fillGo.AddComponent<Image>();
        fill.color = new Color(b.Fill.R, b.Fill.G, b.Fill.B, b.Fill.A);
        fill.type = Image.Type.Filled;
        fill.fillMethod = Image.FillMethod.Horizontal;
        fill.fillOrigin = 0;   // Image.OriginHorizontal.Left
        fill.fillAmount = Mathf.Clamp01(b.Fraction01());
        fill.raycastTarget = false;
        RegisterBarSmoothing(fill, b.Fraction01, token);

        Text? label = null, labelShadow = null;
        if (b.Label != null) (label, labelShadow) = BuildBarHudLabel(b, row, track, token, ls);
        token.Bars.Add(new BarBinding { Label = label, LabelShadow = labelShadow, LabelFn = b.Label });
    }

    // Port of HudElementBuilder.BuildBarLabel (:318-330): the fixed-width right-aligned side slot (default) or,
    // when LabelInside, an overlay stretched centred over the track. Returns the shadowed (fg, shadow) twin.
    private (Text Fg, Text Shadow) BuildBarHudLabel(BarElement b, GameObject row, GameObject track, WindowToken token, int ls)
    {
        if (b.LabelInside)
        {
            var (slot, fg, shadow) = MakeShadowedTextHud(track.transform, ls, TextAnchor.MiddleCenter, bold: false);
            UGuiPrimitives.Stretch(slot);
            fg.raycastTarget = false; shadow.raycastTarget = false;
            HudTextReskin(token, fg, shadow);
            return (fg, shadow);
        }
        var (sslot, sfg, sshadow) = MakeShadowedTextHud(row.transform, ls, TextAnchor.MiddleRight, bold: false);
        sslot.AddComponent<LayoutElement>().preferredWidth = HudBarNumericWidth;
        HudTextReskin(token, sfg, sshadow);
        return (sfg, sshadow);
    }

    // Theme-change reskin: re-point both twins to the rebaked HudText/HudTextShadow. A ColorFn-bound fg is
    // re-overridden by TextBinding.Apply (which Reskin() runs immediately after ReskinActions), so this default
    // is safe. Registered by each MakeShadowedTextHud caller (kept off that method to respect the 5-param gate).
    private void HudTextReskin(WindowToken token, Text fg, Text shadow)
        => token.ReskinActions.Add(() => { if (fg != null) fg.color = HudTextColor; if (shadow != null) shadow.color = HudShadowColor; });

    // Port of HudElementBuilder.MakeShadowedText (:406-429): a layout slot holding an ignore-layout shadow twin
    // (sibling 0, offset +sd,-sd) under the foreground (sibling 1, laid out → sizes the slot). Foreground stays
    // on the layout's own position so centred text stays centred (the shadow moves instead).
    private (GameObject Slot, Text Fg, Text Shadow) MakeShadowedTextHud(
        Transform parent, int fontSize, TextAnchor anchor, bool bold, int shadowOffset = 1)
    {
        var slot = UGuiPrimitives.NewChild("Text", parent);
        var lg = slot.AddComponent<HorizontalLayoutGroup>();
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = false; lg.childForceExpandHeight = false;
        lg.childAlignment = anchor;

        var shGo = UGuiPrimitives.NewChild("Shadow", slot.transform);   // sibling 0 → drawn behind the fg
        shGo.AddComponent<LayoutElement>().ignoreLayout = true;
        var shrt = shGo.GetComponent<RectTransform>();
        shrt.anchorMin = Vector2.zero; shrt.anchorMax = Vector2.one;
        float sd = shadowOffset;
        shrt.offsetMin = new Vector2(sd, -sd); shrt.offsetMax = new Vector2(sd, -sd);
        var shadow = shGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(shadow, fontSize, anchor, bold);
        shadow.color = HudShadowColor;

        var fgGo = UGuiPrimitives.NewChild("Fg", slot.transform);       // sibling 1 → on top; laid out, sizes the slot
        var fg = fgGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(fg, fontSize, anchor, bold);
        fg.color = HudTextColor;
        return (slot, fg, shadow);
    }

    // Reproduce HudBarAnimator's per-frame fillAmount smoothing on the window path (the HUD injected an animator;
    // the window renderer drives pulses off its interaction ticker). Same lerp rate (Speed=12) and rest snap
    // (SnapEpsilon=0.0005 → stop writing once at target so a static bar lets the canvas go clean). The pulse arg
    // (an elapsed/0..1 clock) is unused — Time.deltaTime is the correct per-render-frame delta here. In the
    // sandbox _registerPulse is null → the bar renders static at its build-time fillAmount (HUD parity). Registered
    // on token.Pulses so WindowRenderer.Destroy removes it from the ticker on unmount.
    private void RegisterBarSmoothing(Image fill, Func<float> fraction, WindowToken token)
    {
        const float speed = 12f, snapEpsilon = 0.0005f;
        Action<float> pulse = _ =>
        {
            if (fill == null) return;
            float t;
            try { t = Mathf.Clamp01(fraction()); } catch { return; }
            var cur = fill.fillAmount;
            if (Mathf.Abs(t - cur) <= snapEpsilon) { if (cur != t) fill.fillAmount = t; return; }
            fill.fillAmount = Mathf.Lerp(cur, t, Time.deltaTime * speed);
        };
        token.Pulses.Add(pulse);
        _registerPulse?.Invoke(pulse);
    }
}
