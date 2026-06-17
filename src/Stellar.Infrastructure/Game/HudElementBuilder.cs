using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Pure uGUI builder for a HUD element tree — no IL2CPP / game dependencies, so it
/// can be exercised headlessly in the UI sandbox as well as in-game. Builds the
/// native uGUI hierarchy from a <see cref="HudSpec"/> onto a caller-supplied parent
/// transform, captures dynamic leaves as bindings (re-applied by <see cref="HudToken.Apply"/>),
/// and styles chrome from <see cref="HudThemeAssets"/> (rounded 9-slice pill/bar sprites
/// + shadowed HudText). The bar-fill animator hook is injected so the IL2CPP
/// <c>HudBarAnimator</c> stays in <see cref="HudRenderer"/>.
/// </summary>
internal sealed class HudElementBuilder
{
    private readonly HudThemeAssets _assets;
    private readonly Action<Image, Func<float>>? _registerBar;

    public HudElementBuilder(HudThemeAssets assets, Action<Image, Func<float>>? registerBar)
    {
        _assets = assets;
        _registerBar = registerBar;
    }

    /// <summary>
    /// Builds <paramref name="spec"/> under <paramref name="parent"/> and returns the token.
    /// The root needs BOTH a layout group (to compute a preferred size from its single child)
    /// AND a ContentSizeFitter (to resize itself to that preferred size) — without the layout
    /// group the fitter has nothing to measure and the whole tree collapses to 0×0.
    /// </summary>
    public HudToken Build(HudSpec spec, Transform parent)
    {
        var token = new HudToken();
        var root = UGuiPrimitives.NewRect(spec.Id, parent);
        token.Anchor = spec.Anchor;
        switch (spec.Anchor)
        {
            case HudAnchor.ScreenCenterX:
                // Anchor at canvas center-top; pivot at panel center-top → x=0 always centres horizontally.
                root.anchorMin = new Vector2(0.5f, 1f);
                root.anchorMax = new Vector2(0.5f, 1f);
                root.pivot     = new Vector2(0.5f, 1f);
                break;
            case HudAnchor.ScreenCenterY:
                // Anchor at canvas left-middle; pivot at panel left-middle → y=0 always centres vertically.
                root.anchorMin = new Vector2(0f, 0.5f);
                root.anchorMax = new Vector2(0f, 0.5f);
                root.pivot     = new Vector2(0f, 0.5f);
                break;
            case HudAnchor.ScreenCenter:
                // Anchor at canvas center; pivot at panel center → both axes centred at any resolution.
                root.anchorMin = new Vector2(0.5f, 0.5f);
                root.anchorMax = new Vector2(0.5f, 0.5f);
                root.pivot     = new Vector2(0.5f, 0.5f);
                break;
        }
        UGuiPrimitives.AddLayout(root.gameObject, gap: 0f, columns: UGuiPrimitives.ColumnMode);
        var fitter = root.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        token.Root = root.gameObject;
        token.Rect = root;
        BuildElement(spec.Root, root, token);
        token.Apply();   // first paint
        return token;
    }

    // ---- token + bindings (captured at build; Apply re-pulls dynamic leaves) ----

    public sealed class HudToken
    {
        public GameObject Root = null!;
        public RectTransform Rect = null!;
        internal HudAnchor Anchor;
        internal readonly List<TextBinding> Texts = new();
        internal readonly List<BarBinding> Bars = new();
        internal readonly List<CondBinding> Conds = new();
        internal readonly List<ListBinding> Lists = new();

        public void Apply()
        {
            for (var i = 0; i < Conds.Count; i++) Conds[i].Apply();
            for (var i = 0; i < Lists.Count; i++) Lists[i].Apply();
            for (var i = 0; i < Texts.Count; i++) Texts[i].Apply();
            for (var i = 0; i < Bars.Count; i++) Bars[i].Apply();
        }
    }

    internal sealed class TextBinding
    {
        public Text C = null!;
        public Text? Shadow;          // two-pass drop-shadow twin (UnityEngine.UI.Shadow is stripped from interop)
        public Func<string> TextFn = null!;
        public Func<ColorRgba?>? ColorFn;
        public Func<int>? DynamicFontSizeFn;
        private string? _lastText;
        private bool _colorInit;
        private ColorRgba? _lastColor;
        private int _lastFontSize;
        public void Apply()
        {
            if (C == null) return;
            if (DynamicFontSizeFn != null)
            {
                var fs = Math.Max(1, DynamicFontSizeFn());
                if (fs != _lastFontSize)
                {
                    C.fontSize = fs;
                    if (Shadow != null) Shadow.fontSize = fs;
                    _lastFontSize = fs;
                }
            }
            var s = TextFn();
            if (s != _lastText) { C.text = s; if (Shadow != null) Shadow.text = s; _lastText = s; }
            if (ColorFn != null)
            {
                var c = ColorFn();
                if (!_colorInit || !NullableColorEquals(c, _lastColor))
                {
                    if (c is { } v)
                    {
                        C.color = new Color(v.R, v.G, v.B, v.A);
                        if (Shadow != null) { var sc = Shadow.color; Shadow.color = new Color(sc.r, sc.g, sc.b, v.A); }
                    }
                    else C.color = Color.white;
                    _lastColor = c; _colorInit = true;
                }
            }
        }
    }

    internal sealed class BarBinding
    {
        public Text? Label;
        public Text? LabelShadow;
        public Func<string>? LabelFn;
        private string? _last;
        public void Apply()
        {
            if (Label == null || LabelFn == null) return;
            var s = LabelFn();
            if (s != _last) { Label.text = s; if (LabelShadow != null) LabelShadow.text = s; _last = s; }
        }
    }

    internal sealed class CondBinding
    {
        public Func<bool> When = null!;
        public GameObject Then = null!;
        public GameObject? Else;
        private bool _init;
        private bool _last;
        public void Apply()
        {
            var b = When();
            if (_init && b == _last) return;
            if (Then != null) Then.SetActive(b);
            if (Else != null) Else.SetActive(!b);
            _last = b; _init = true;
        }
    }

    internal sealed class ListBinding
    {
        public Func<int> Count = null!;
        public GameObject[] Slots = Array.Empty<GameObject>();
        private int _last = -1;
        public void Apply()
        {
            var n = Count();
            if (n == _last) return;
            for (var i = 0; i < Slots.Length; i++) if (Slots[i] != null) Slots[i].SetActive(i < n);
            _last = n;
        }
    }

    private static bool NullableColorEquals(ColorRgba? a, ColorRgba? b)
        => a.HasValue == b.HasValue && a.GetValueOrDefault().Equals(b.GetValueOrDefault());

    // ---- recursive builder ----

    private void BuildElement(HudElement el, Transform parent, HudToken token)
    {
        switch (el)
        {
            case RowElement r:    BuildLayout(r.Children, parent, token, r.Gap, UGuiPrimitives.RowMode); break;
            case ColumnElement c: BuildLayout(c.Children, parent, token, c.Gap, UGuiPrimitives.ColumnMode); break;
            case TextElement t:   BuildText(t, parent, token); break;
            case BarElement b:    BuildBar(b, parent, token); break;
            case PillElement p:   BuildPill(p, parent, token); break;
            case ImageElement im: BuildImage(im, parent); break;
            case ConditionalElement cond: BuildConditional(cond, parent, token); break;
            case ListElement list: BuildList(list, parent, token); break;
        }
    }

    private void BuildLayout(IReadOnlyList<HudElement> children, Transform parent, HudToken token, float gap, int columns)
    {
        var go = UGuiPrimitives.NewChild(columns == UGuiPrimitives.RowMode ? "Row" : columns > 1 ? "Grid" : "Column", parent);
        UGuiPrimitives.AddLayout(go, gap, columns);
        foreach (var child in children) BuildElement(child, go.transform, token);
    }

    private void BuildText(TextElement t, Transform parent, HudToken token)
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
        var (slot, fg, shadow) = MakeShadowedText(parent, size, anchor, bold: t.Emphasis, shadowOffset: t.ShadowDistance);
        if (t.Width > 0f)
        {
            var le = slot.AddComponent<LayoutElement>();
            le.preferredWidth = t.Width;
            le.flexibleWidth = 0f;
        }
        token.Texts.Add(new TextBinding { C = fg, Shadow = shadow, TextFn = t.Text, ColorFn = t.Color, DynamicFontSizeFn = t.DynamicFontSize });
    }

    // Level-pill chip: rounded 9-slice sprite (HudPillBg + accent border) sized to
    // its label + 12/4 padding, with centred shadowed HudText — mirrors the IMGUI
    // DrawHudPillChip. The sprite Image is an ignore-layout child stretched behind
    // the label so the HorizontalLayoutGroup measures only the text (the Image's own
    // ILayoutElement would otherwise force the chip to the sprite's native size).
    private void BuildPill(PillElement p, Transform parent, HudToken token)
    {
        var go = UGuiPrimitives.NewChild("Pill", parent);
        var lg = go.AddComponent<HorizontalLayoutGroup>();
        lg.padding = new RectOffset(PillPadX, PillPadX, PillPadY, PillPadY);
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = false; lg.childForceExpandHeight = false;
        lg.childAlignment = TextAnchor.MiddleCenter;

        var bg = UGuiPrimitives.NewChild("Bg", go.transform);
        bg.AddComponent<LayoutElement>().ignoreLayout = true;
        UGuiPrimitives.Stretch(bg);
        var img = bg.AddComponent<Image>();
        img.sprite = _assets.PillBg; img.type = Image.Type.Sliced; img.raycastTarget = false;

        var (_, fg, shadow) = MakeShadowedText(go.transform, PillTextSize, TextAnchor.MiddleCenter, bold: true);
        token.Texts.Add(new TextBinding { C = fg, Shadow = shadow, TextFn = p.Text, ColorFn = p.Color });
    }

    // HP/Stamina bar row: rounded 9-slice track (HudBarBg) + left-anchored coloured
    // fill + a right-aligned shadowed numeric in a fixed column so consecutive bars
    // line up. Fill rounding is deferred (matches the IMGUI track — the rounded track
    // is the visible win); the animator smooths fillAmount per-frame.
    private void BuildBar(BarElement b, Transform parent, HudToken token)
    {
        var row = UGuiPrimitives.NewChild("Bar", parent);
        var lg = row.AddComponent<HorizontalLayoutGroup>();
        lg.spacing = 6f;
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = false; lg.childForceExpandHeight = false;
        lg.childAlignment = TextAnchor.MiddleLeft;

        if (b.Prefix != null)
        {
            var (pslot, pfg, pshadow) = MakeShadowedText(row.transform, BarLabelSize, TextAnchor.MiddleLeft, bold: true);
            pslot.AddComponent<LayoutElement>().preferredWidth = BarPrefixWidth;
            pfg.text = b.Prefix; pshadow.text = b.Prefix;   // static caption — no binding needed
        }

        var track = UGuiPrimitives.NewChild("Track", row.transform);
        var tle = track.AddComponent<LayoutElement>();
        tle.preferredWidth = BarTrackWidth; tle.preferredHeight = BarHeight; tle.flexibleWidth = 0f;
        var trackImg = track.AddComponent<Image>();
        trackImg.sprite = _assets.BarBg; trackImg.type = Image.Type.Sliced; trackImg.raycastTarget = false;

        var fillGo = UGuiPrimitives.NewChild("Fill", track.transform);
        UGuiPrimitives.Stretch(fillGo);
        var fill = fillGo.AddComponent<Image>();
        fill.color = new Color(b.Fill.R, b.Fill.G, b.Fill.B, b.Fill.A);
        fill.type = Image.Type.Filled;
        fill.fillMethod = Image.FillMethod.Horizontal;
        fill.fillOrigin = 0;   // Image.OriginHorizontal.Left
        fill.fillAmount = Mathf.Clamp01(b.Fraction01());
        fill.raycastTarget = false;
        _registerBar?.Invoke(fill, b.Fraction01);

        Text? label = null, labelShadow = null;
        if (b.Label != null)
        {
            var (slot, fg, shadow) = MakeShadowedText(row.transform, BarLabelSize, TextAnchor.MiddleRight, bold: false);
            slot.AddComponent<LayoutElement>().preferredWidth = BarNumericWidth;
            label = fg; labelShadow = shadow;
        }
        token.Bars.Add(new BarBinding { Label = label, LabelShadow = labelShadow, LabelFn = b.Label });
    }

    // Pill chip composition (matches IMGUI HudLevelPill*): 12/4 padding, 13 px bold.
    private const int PillPadX = 12;
    private const int PillPadY = 4;
    private const int PillTextSize = 13;
    // Bar geometry. Fixed track + numeric column widths so both bars align.
    private const float BarTrackWidth = 150f;
    private const float BarHeight = 14f;
    private const float BarNumericWidth = 84f;
    private const float BarPrefixWidth = 60f;   // fixed left caption column so stacked bars align
    private const int BarLabelSize = 12;

    private void BuildImage(ImageElement im, Transform parent)
    {
        var go = UGuiPrimitives.NewChild("Image", parent);
        UGuiPrimitives.SetPreferred(go, im.Width, im.Height);
        var raw = go.AddComponent<RawImage>();
        raw.raycastTarget = false;
        var png = im.Png();
        if (png is { Length: > 0 })
        {
            // mipChain + Trilinear + Apply(updateMipmaps) — high-res PNGs downscaled to HUD sizes alias/pixelate
            // without them (the IMGUI PluginIconCache lesson). Smooth icons.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true)
            {
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            try { if (ImageConversion.LoadImage(tex, png)) { tex.Apply(updateMipmaps: true, makeNoLongerReadable: false); raw.texture = tex; } } catch { /* leave blank */ }
        }
    }

    private void BuildConditional(ConditionalElement cond, Transform parent, HudToken token)
    {
        var go = UGuiPrimitives.NewChild("Cond", parent);
        UGuiPrimitives.AddLayout(go, gap: 0f, columns: UGuiPrimitives.ColumnMode);
        var thenGo = UGuiPrimitives.NewChild("Then", go.transform);
        UGuiPrimitives.AddLayout(thenGo, gap: 0f, columns: UGuiPrimitives.ColumnMode);
        BuildElement(cond.Then, thenGo.transform, token);
        GameObject? elseGo = null;
        if (cond.Else != null)
        {
            elseGo = UGuiPrimitives.NewChild("Else", go.transform);
            UGuiPrimitives.AddLayout(elseGo, gap: 0f, columns: UGuiPrimitives.ColumnMode);
            BuildElement(cond.Else, elseGo.transform, token);
        }
        token.Conds.Add(new CondBinding { When = cond.When, Then = thenGo, Else = elseGo });
    }

    private void BuildList(ListElement list, Transform parent, HudToken token)
    {
        var go = UGuiPrimitives.NewChild("List", parent);
        UGuiPrimitives.AddLayout(go, gap: 2f, columns: list.Columns);
        var slots = new GameObject[list.Slots.Count];
        for (var i = 0; i < list.Slots.Count; i++)
        {
            var slot = UGuiPrimitives.NewChild("Slot", go.transform);
            UGuiPrimitives.AddLayout(slot, gap: 0f, columns: UGuiPrimitives.ColumnMode);
            BuildElement(list.Slots[i], slot.transform, token);
            slots[i] = slot;
        }
        token.Lists.Add(new ListBinding { Count = list.VisibleCount, Slots = slots });
    }

    // ---- shadowed text (HUD-specific: uses the theme's HudText/HudTextShadow colours) ----
    // Low-level primitives (NewRect/NewChild/AddLayout/ConfigureText/SetPreferred/Stretch +
    // UGuiPrimitives.RowMode/UGuiPrimitives.ColumnMode) live in the shared UGuiPrimitives, reused by WindowBuilder.

    // Two-pass shadowed text (UnityEngine.UI.Shadow is stripped from the game interop).
    // The slot is a layout container sized to the FOREGROUND text; the shadow is an
    // ignore-layout twin stretched behind it (sibling 0 → drawn under) offset +1,-1.
    // Keeping the foreground on the layout's own position (rather than offsetting it)
    // means centred text — e.g. the pill chip — stays truly centred; the shadow moves
    // instead. Returns (slot GO, foreground, shadow).
    private (GameObject Slot, Text Fg, Text Shadow) MakeShadowedText(Transform parent, int fontSize, TextAnchor anchor, bool bold, int shadowOffset = 1)
    {
        var slot = UGuiPrimitives.NewChild("Text", parent);
        var lg = slot.AddComponent<HorizontalLayoutGroup>();
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = false; lg.childForceExpandHeight = false;
        lg.childAlignment = anchor;   // positions the fg when the slot is widened to a fixed column

        var shGo = UGuiPrimitives.NewChild("Shadow", slot.transform);   // sibling 0 → drawn behind the fg
        shGo.AddComponent<LayoutElement>().ignoreLayout = true;
        var shrt = shGo.GetComponent<RectTransform>();
        shrt.anchorMin = Vector2.zero; shrt.anchorMax = Vector2.one;
        float sd = shadowOffset;
        shrt.offsetMin = new Vector2(sd, -sd); shrt.offsetMax = new Vector2(sd, -sd);
        var shadow = shGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(shadow, fontSize, anchor, bold);
        shadow.color = _assets.HudTextShadow;

        var fgGo = UGuiPrimitives.NewChild("Fg", slot.transform);       // sibling 1 → on top; laid out, so it sizes the slot
        var fg = fgGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(fg, fontSize, anchor, bold);
        fg.color = _assets.HudText;
        return (slot, fg, shadow);
    }
}
