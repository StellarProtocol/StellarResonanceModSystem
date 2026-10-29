using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// DropdownElement.Style == Pill: the Stellar Design Language look (Stellar-Design-Language.md §3/§4) for the same
// dropdown behaviour. Only the two visuals differ from Classic: the trigger and the option panel. Opening, the root
// popup canvas, the blocker, Escape/outside-click dismiss, PositionDropdown and the MaxVisibleRows scroll are the
// shared Classic code (.Dropdown.cs), so the two styles can't drift behaviourally. Classic never reaches this file.
//
// Colours are the house-style tokens, computed from the LIVE theme (_assets.Menu*) on every call — the trigger's
// funcs are polled each refresh (BubbleBinding / TextBinding), so a theme switch repaints it with no rebuild; the
// popup is transient and reads them once per open.
internal sealed partial class WindowBuilder
{
    private const float PillDdHeight     = 28f;   // trigger height (the house PillButton height)
    private const float PillDdPadX       = 12f;   // trigger inset each side of the caption / chevron
    private const float PillDdChevronGap = 6f;    // caption → ▾
    private const float PillDdChevronW   = 8f;    // ▾ image size (canvas units) — ChatTools' composer chevron size
    private const float PillDdChevronH   = 5f;
    private const int   PillDdFontSize   = 13;    // bold 13 stays under the 28 px strut (house PillButton rule)
    private const int   PillDdPanelRadius = 8;
    private const int   PillDdPanelPad   = 4;
    private const int   PillDdItemRadius = 5;
    private const float PillDdItemHeight = 26f;
    private const float PillDdItemGap    = 2f;

    // Baked once: a white anti-aliased down-pointing chevron, tinted MenuMuted at the use-site (see PillChevronSprite).
    private static Sprite? _pillChevronSprite;

    // Trigger: Bubble{FillColorFn} → Selectable{Bare} → Row[strut, bold caption, ▾, strut] — the house PillButton
    // composed from the framework's own elements, so the rounded fill, polled caption, live colours, hover and click
    // all reuse the validated Bubble/Selectable/Text paths. Bare: no inset and no default hover wash (the hover is the
    // fill change below), so the 28 px strut IS the height. The click delivers the Selectable's screen rect (the whole
    // pill — Bare has no padding) so the popup anchors exactly under it, like Classic's OnClickWithRect.
    private void BuildPillDropdownTrigger(DropdownElement dd, Transform parent, WindowToken token)
    {
        var hovered = false;
        ColorRgba Fill() => hovered ? Over(WithA(_assets.MenuText, 0.08f), PillNeutral()) : PillNeutral();

        string Caption()
        {
            var opts = dd.Options();
            var i = dd.Selected();
            return opts != null && i >= 0 && i < opts.Count ? opts[i] : "";
        }

        // The caption + ▾ GROUP is centred in the pill (house PillButton: Row(Center)). The caption hugs its text.
        // The ▾ is an IMAGE, not a glyph: the TMP bold face has no "▾" — in-game a TMP "▾" reported a ZERO preferred
        // size (first 0×0, then 10×0 inside a Width-10 Cell, whose VLG gives a child its PREFERRED height) and drew
        // nothing; a plain Text "▾" falls back to a taller OS face and grows the row (ChatTools hit both → 8×5 image).
        // The slot is a fixed 8×5 Spacer placeholder; AttachPillChevron hangs the tinted image on it after the build.
        // 0.01 gaps = a real zero gap (Gap 0 means the default 8).
        var group = new RowElement(new HudElement[]
        {
            new TextElement(Caption, Color: () => EnsureContrast(ToRgba(_assets.MenuText), Fill(), 4.5f),
                NoWrap: true, FontSize: PillDdFontSize) { Bold = true, Ellipsize = dd.Width > 0f },
            new SpacerElement(PillDdChevronGap, 0f),
            new SpacerElement(PillDdChevronW, PillDdChevronH),   // ▾ slot (index 2) — see AttachPillChevron
        }, Gap: 0.01f, Justify: RowJustify.Center);
        // Width 0 → the pill hugs the group; Width > 0 → a fixed inner Cell makes the pill exactly Width, the group centred
        // in it (a caption longer than the room shrinks toward its min width 0 and is ellipsized).
        var inner = dd.Width > 0f ? Mathf.Max(0f, dd.Width - 2f * PillDdPadX) : 0f;
        var row = new RowElement(new HudElement[]
        {
            new SpacerElement(PillDdPadX, PillDdHeight),
            inner > 0f ? new CellElement(group, Width: inner) : group,
            new SpacerElement(PillDdPadX, PillDdHeight),
        }, Gap: 0.01f, Justify: RowJustify.Center);

        var pill = new BubbleElement(new SelectableElement(row, OnClick: () => { })
        {
            Bare = true,
            OnHover = on => hovered = on,
            OnClickWithRect = rect =>
            {
                if (SuppressClickInEditMode(token)) return;   // Classic gets this from BuildButton; Selectable doesn't
                OpenDropdown(token, rect, dd);
            },
        })
        {
            FillColorFn = () => Fill(), PaddingX = 0f, PaddingY = 0f,
        };
        BuildElement(pill, parent, token);
        AttachPillChevron(parent.GetChild(parent.childCount - 1), inner > 0f, token);
    }

    // Find the ▾ placeholder in the tree just built and hang the chevron image on it. The path is fixed by the
    // composition above: Bubble → [0] Selectable → [0] Row → [1] (Cell → [0])? group Row → [2] slot Spacer (Bubble adds
    // its content as child 0 and no outline here; a Bare Selectable adds no bar). Guarded: a mismatch just omits the ▾.
    private void AttachPillChevron(Transform bubble, bool hasInnerCell, WindowToken token)
    {
        static Transform? Child(Transform? t, int i) => t != null && t.childCount > i ? t.GetChild(i) : null;
        var group = Child(Child(Child(bubble, 0), 0), 1);
        if (hasInnerCell) group = Child(group, 0);
        var slot = Child(group, 2);
        if (slot == null) return;
        var go = UGuiPrimitives.NewChild("Chevron", slot);
        UGuiPrimitives.Stretch(go);
        go.AddComponent<LayoutElement>().ignoreLayout = true;
        var img = go.AddComponent<Image>();
        img.sprite = PillChevronSprite(); img.raycastTarget = false;
        img.color = _assets.MenuMuted;
        token.ReskinActions.Add(() => { if (img != null) img.color = _assets.MenuMuted; });   // live theme switch
    }

    // 4× supersampled triangle (top edge full width → point at the bottom), white with AA coverage in alpha, bilinear.
    private static Sprite PillChevronSprite()
    {
        if (_pillChevronSprite != null) return _pillChevronSprite;
        const int W = 32, H = 20, S = 4;
        var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var hit = 0;
                for (var sy = 0; sy < S; sy++)
                    for (var sx = 0; sx < S; sx++)
                    {
                        var fx = x + (sx + 0.5f) / S;
                        var depth = H - (y + (sy + 0.5f) / S);        // 0 at the top edge (texture y is bottom-up)
                        var half = W * 0.5f * (1f - depth / H);       // half-width shrinks linearly to the point
                        if (Mathf.Abs(fx - W * 0.5f) <= half) hit++;
                    }
                t.SetPixel(x, y, new Color(1f, 1f, 1f, hit / (float)(S * S)));
            }
        t.Apply();
        _pillChevronSprite = Sprite.Create(t, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f));
        return _pillChevronSprite;
    }

    // Option panel: rounded Panel-coloured card with a 1 px Neutral ring (BubbleBg / BubbleOutline sprites at the same
    // radius — the BubbleElement mechanism), 4 px padding, fixed-height rounded rows. Same skeleton as the Classic
    // BuildDropdownPanel (anchors, VLG + ContentSizeFitter, MaxVisibleRows scroll, PositionDropdown) so the flip/clamp
    // and the capped scroll behave identically.
    private GameObject BuildPillDropdownPanel(Transform layer, WindowRect anchor, IReadOnlyList<string> opts, DropdownElement dd, WindowToken token)
    {
        var panel = UGuiPrimitives.NewChild(DropdownPanelName, layer);   // same name → the layout editor's press guard
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = prt.anchorMax = new Vector2(0f, 1f);
        prt.pivot = new Vector2(0f, 1f);

        var vlg = panel.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(PillDdPanelPad, PillDdPanelPad, PillDdPanelPad, PillDdPanelPad); vlg.spacing = PillDdItemGap;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperLeft;
        var csf = panel.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        AddPillPanelChrome(panel.transform);
        // Panel outer width ≈ the trigger's (minus the 2×4 padding), never narrower than Classic's 80 floor.
        var minW = Mathf.Max(anchor.Width - 2f * PillDdPanelPad, 80f);
        var selected = dd.Selected();
        var itemParent = dd.MaxVisibleRows > 0 && opts.Count > dd.MaxVisibleRows
            ? BuildDropdownScroll(panel, minW, dd.MaxVisibleRows, token, (PillDdItemHeight, PillDdItemGap))
            : panel.transform;

        for (var i = 0; i < opts.Count; i++)
        {
            var idx = i;   // capture per item for the pick closure
            BuildPillDropdownItem(itemParent, opts[i], i == selected, minW, () =>
            {
                CloseDropdown();
                try { dd.OnSelect(idx); }
                catch (Exception ex) { DropdownPickThrewDiag(ex); }   // never let a plugin callback break the overlay
                ClearSelectionAfterClick();
            });
        }

        panel.transform.SetAsLastSibling();
        PositionDropdown(prt, anchor);
        return panel;
    }

    // Rounded fill + 1 px ring, both stretched + ignoreLayout (never measured). The fill stays raycastable (as Classic's
    // chrome) so a press on the panel padding doesn't fall through to the blocker and close the list.
    private void AddPillPanelChrome(Transform panel)
    {
        var bgGo = UGuiPrimitives.NewChild("Bg", panel);
        UGuiPrimitives.Stretch(bgGo);
        bgGo.AddComponent<LayoutElement>().ignoreLayout = true;
        var bg = bgGo.AddComponent<Image>();
        if (_assets.BubbleBg(PillDdPanelRadius) is { } fill) { bg.sprite = fill; bg.type = Image.Type.Sliced; }
        bg.color = ToColor(PillPanel()); bg.raycastTarget = true;

        var ringGo = UGuiPrimitives.NewChild("Edge", panel);
        UGuiPrimitives.Stretch(ringGo);
        ringGo.AddComponent<LayoutElement>().ignoreLayout = true;
        var ring = ringGo.AddComponent<Image>();
        if (_assets.BubbleOutline(PillDdPanelRadius) is { } edge) { ring.sprite = edge; ring.type = Image.Type.Sliced; }
        ring.color = ToColor(PillNeutral()); ring.raycastTarget = false;
    }

    // One option row: a rounded chip, transparent at rest (selected = accent wash + bold), MenuText@0.08 hover wash.
    // Hover is ticker-driven via _registerHover (as Classic's BuildDropdownItem — the EventSystem ColorTint path is
    // unreliable here). bg.color is set AFTER AddComponent<Button>: Button.OnEnable snaps targetGraphic to white.
    private void BuildPillDropdownItem(Transform parent, string text, bool active, float minW, Action onPick)
    {
        var go = UGuiPrimitives.NewChild("DropdownItem", parent);
        var le = go.AddComponent<LayoutElement>();
        le.flexibleWidth = 1f; le.minWidth = minW;
        le.minHeight = le.preferredHeight = PillDdItemHeight; le.flexibleHeight = 0f;   // fixed row → exact scroll cap
        var bg = go.AddComponent<Image>();
        if (_assets.BubbleBg(PillDdItemRadius) is { } chip) { bg.sprite = chip; bg.type = Image.Type.Sliced; }
        bg.raycastTarget = true;
        var btn = go.AddComponent<Button>(); btn.targetGraphic = bg; btn.transition = Selectable.Transition.None;
        DisableKeyboardNav(btn);
        var rest = active ? WithAlpha(_assets.MenuAccent, 0.28f) : Color.clear;
        var hover = active ? WithAlpha(_assets.MenuAccent, 0.36f) : WithAlpha(_assets.MenuText, 0.08f);
        bg.color = rest;   // after AddComponent<Button> (OnEnable white-snap)
        btn.onClick.AddListener((UnityAction)(() => onPick()));   // onPick calls ClearSelectionAfterClick
        _registerHover?.Invoke(go.GetComponent<RectTransform>(), on => { if (bg != null) bg.color = on ? hover : rest; });

        // Label host: no ContentSizeFitter — the fixed LayoutElement height above owns the row (a CSF inside the
        // panel's height-controlling VLG is the per-frame-rebuild anti-pattern). MiddleLeft centres the label vertically.
        var hlg = go.AddComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(8, 8, 0, 0); hlg.spacing = 0f;
        hlg.childControlWidth = true; hlg.childControlHeight = true;
        hlg.childForceExpandWidth = true; hlg.childForceExpandHeight = false;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        BuildPillItemLabel(go.transform, text, active);
    }

    // Item label: the TMP styled face (regular, or bold for the selected row) so both weights share one face; falls
    // back to the legacy Menu-font Text (bold via the per-script emphasis style) when TMP is unavailable.
    private void BuildPillItemLabel(Transform parent, string text, bool bold)
    {
        var size = Scaled(PillDdFontSize);
        var h = StyledTextFactory.CreateBold?.Invoke(parent,
            new StyledTextSpec(text, size, _assets.MenuText, wrap: false) { Bold = bold });
        if (h?.Go != null) return;
        var labelGo = UGuiPrimitives.NewChild("Label", parent);
        var label = labelGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(label, size, TextAnchor.MiddleLeft, bold: false);
        label.alignByGeometry = true; ApplyMenuFont(label); label.color = _assets.MenuText; label.text = text;
        label.fontStyle = UGuiPrimitives.EmphasisStyle(bold, text);
    }

    // ── house-style colour tokens (Stellar-Design-Language.md §3), from the live theme ──────────────────────────────
    // Same math as the plugins' Plugin.Style.cs (Over / Mix / EnsureContrast); pure struct math, no allocation.
    private ColorRgba PillPanel() => Over(new ColorRgba(0f, 0f, 0f, 0.20f), ToRgba(_assets.MenuBackground));
    private ColorRgba PillNeutral() => Over(WithA(_assets.MenuMuted, 0.22f), PillPanel());

    private static ColorRgba ToRgba(Color c) => new(c.r, c.g, c.b, c.a);
    private static Color ToColor(ColorRgba c) => new(c.R, c.G, c.B, c.A);
    private static ColorRgba WithA(Color c, float a) => new(c.r, c.g, c.b, a);
    private static Color WithAlpha(Color c, float a) => new(c.r, c.g, c.b, a);

    // a → b by t, opaque.
    private static ColorRgba Mix(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    // Alpha-composite fg (its A = coverage) over an opaque bg.
    private static ColorRgba Over(ColorRgba fg, ColorRgba bg) => Mix(bg, fg with { A = 1f }, fg.A);

    // Nudge fg toward white (dark bg) or black (light bg) in 15% steps until it reaches minRatio against bg (WCAG).
    private static ColorRgba EnsureContrast(ColorRgba fg, ColorRgba bg, float minRatio)
    {
        var lb = Luminance(bg);
        var target = lb < 0.179f ? new ColorRgba(1f, 1f, 1f, 1f) : new ColorRgba(0f, 0f, 0f, 1f);   // equal-contrast point
        for (var i = 0; i < 12 && Contrast(Luminance(fg), lb) < minRatio; i++) fg = Mix(fg, target, 0.15f);
        return fg with { A = 1f };
    }

    private static float Contrast(float la, float lb) => (Mathf.Max(la, lb) + 0.05f) / (Mathf.Min(la, lb) + 0.05f);
    private static float Luminance(ColorRgba c) => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
    private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
}
