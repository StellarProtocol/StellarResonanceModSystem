using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>WindowBuilder widgets — interactive leaves (Button, Toggle). Raycast ON; events push through
/// the element's Action. Slider/Input/Scroll/ColorPicker arrive in Plan 3. Spacing per the Measurement
/// contract (button radius 6 via the sliced sprite, padding 3 v / 11 h; toggle capsule 30×15, knob 11).</summary>
internal sealed partial class WindowBuilder
{
    // Release the EventSystem selection after a window button/toggle click so no Stellar selectable
    // lingers as currentSelectedGameObject (which makes the game route keyboard into its chat input).
    // Mirrors PandaUGuiAdapter.ClearRailSelection; safe no-op when no EventSystem is present.
    private static void ClearSelectionAfterClick()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es != null) es.SetSelectedGameObject(null);
    }

    // Normal (non-active) button sprite for a style: Outline = faint outline, Filled = accent, Glass = frosted.
    private Sprite? NormalButtonSprite(MenuButtonStyle style) => style switch
    {
        MenuButtonStyle.Filled => _assets.ButtonAccentBg,
        MenuButtonStyle.Glass  => _assets.ButtonGlassBg,
        _                      => _assets.ButtonBg,
    };

    // Re-skin: re-pick the normal sprite from the (possibly changed) global Button style + accent, then
    // re-apply to the image — so changing Button style / theme updates buttons in place (no rebuild).
    private void RegisterButtonReskin(WindowToken token, ButtonBinding binding, MenuButtonStyle? pinned)
        => token.ReskinActions.Add(() =>
        {
            binding.Normal = NormalButtonSprite(pinned ?? _assets.ButtonStyle);
            binding.Accent = _assets.ButtonAccentBg;
            binding.Resprite();
        });

    // True when a window control click should be ignored: layout-edit mode is active (the press is a
    // drag-to-reposition) and this window isn't the edit toolbar. Releases the EventSystem selection so the
    // suppressed click doesn't leave a control selected. Fixes clicks leaking into plugin windows in edit mode.
    private bool SuppressClickInEditMode(WindowToken token)
    {
        if (!Stellar.Infrastructure.Unity.LayoutEditGate.IsEditing || token.EditModeInteractive) return false;
        ClearSelectionAfterClick();
        return true;
    }

    // Button: sliced rounded chip (radius baked into the sprite) + centred label. Sized to label+padding
    // via its own HorizontalLayoutGroup; the parent Row reads its preferred size (childControlWidth).
    private void BuildButton(ButtonElement b, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Button", parent);
        var img = go.AddComponent<Image>();
        // Effective style: the element's pinned style, else the user's global IChromeStyle.ButtonStyle.
        var normalSprite = NormalButtonSprite(b.Style ?? _assets.ButtonStyle);
        img.sprite = (b.Active?.Invoke() ?? false) ? _assets.ButtonAccentBg : normalSprite;
        img.type = Image.Type.Sliced; img.raycastTarget = true;
        var btn = go.AddComponent<Button>(); btn.targetGraphic = img;

        // Icon-only button (icon + empty label, e.g. the meter row's inspect magnifier): the icon must be the
        // SOLE centred child — so symmetric horizontal padding + no inter-child spacing (otherwise the trailing
        // empty-label cell + asymmetric padding push the icon left-of-centre), and a larger glyph.
        bool iconOnly = b.Icon != null && string.IsNullOrEmpty(b.Label());
        ConfigureButtonLayout(go, b, iconOnly);

        // Optional leading icon INSIDE the button → co-centred with the label by this HLG (MiddleCenter), so the
        // icon↔label vertical alignment can't drift with the OS font line-box (the iconed-tab alignment bug).
        // Icon-only buttons size the icon to Scaled(14): kept strictly UNDER the minHeight floor (Scaled(11)+12)
        // at any UI scale, so an icon-only button and a glyph button (whose label also sits under that floor) end
        // up the SAME height — a matched pair (e.g. the meter row's inspect magnifier next to the drill ►).
        if (b.Icon != null) BuildButtonIcon(b.Icon(), go.transform, token, iconOnly ? Scaled(14) : 16f);

        AddButtonSizing(go, b.Width);

        var label = BuildButtonLabel(go.transform, b.Width > 0f);

        var onClick = b.OnClick;
        var onClickWithRect = b.OnClickWithRect;
        btn.onClick.AddListener((UnityAction)(() =>
        {
            if (SuppressClickInEditMode(token)) return;
            onClick();
            if (onClickWithRect != null) FireOnClickWithRect(go, onClickWithRect);
            ClearSelectionAfterClick();
        }));
        var binding = new ButtonBinding
        {
            B = btn, Label = label, LabelFn = b.Label, EnabledFn = b.Enabled,
            Img = img, Normal = normalSprite, Accent = _assets.ButtonAccentBg, ActiveFn = b.Active,
        };
        token.Buttons.Add(binding);
        RegisterTextReskin(token, label, 11);
        RegisterButtonReskin(token, binding, b.Style);
    }

    // Delivers the button's screen rect to OnClickWithRect. Overlay canvas: world space == screen space, Y up.
    // Uses rt.position (pivot center in screen coords) + rt.rect (local extents) to avoid passing a managed
    // Vector3[] to GetWorldCorners, which doesn't write through to managed arrays in IL2CPP.
    private static void FireOnClickWithRect(GameObject go, System.Action<Abstractions.Domain.WindowRect> cb)
    {
        var rt = go.GetComponent<RectTransform>();
        if (rt == null) return;
        var pos = rt.position;
        var r   = rt.rect;
        cb(new Abstractions.Domain.WindowRect(
            pos.x + r.xMin, Screen.height - pos.y - r.yMax, r.width, r.height));
    }

    // LayoutElement for minHeight floor + optional fixed-width pin.
    private void AddButtonSizing(GameObject go, float width)
    {
        var ble = go.AddComponent<LayoutElement>();
        ble.minHeight = Scaled(11) + 12f;
        if (width > 0f) { ble.preferredWidth = width; ble.minWidth = width; ble.flexibleWidth = 0f; }
    }

    // HLG for a ButtonElement: compact, MiddleCenter, SYMMETRIC vertical padding so the label centres on glyph
    // geometry (alignByGeometry). Shared by every ButtonElement (meter == chat == menus). Icon-only buttons use
    // symmetric horizontal padding + zero spacing so the lone icon truly centres.
    private static void ConfigureButtonLayout(GameObject go, ButtonElement b, bool iconOnly)
    {
        var lg = go.AddComponent<HorizontalLayoutGroup>();
        lg.padding = iconOnly ? new RectOffset(5, 5, 3, 3) : new RectOffset(b.Icon != null ? 7 : 9, 9, 3, 3);
        lg.spacing = iconOnly ? 0f : 5f;
        lg.childAlignment = TextAnchor.MiddleCenter;
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = false; lg.childForceExpandHeight = false;
    }

    // Button label cell — centred on glyph GEOMETRY (not the OS font line box, which sits the ink high; without
    // this a single-glyph button like the −/+ stepper renders its label near the top, looking off-centre + tall).
    private Text BuildButtonLabel(Transform parent, bool fixedWidth)
    {
        var labelGo = UGuiPrimitives.NewChild("Label", parent);
        var label = labelGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(label, Scaled(11), TextAnchor.MiddleCenter, bold: false);
        label.alignByGeometry = true;
        ApplyMenuFont(label);   // OS dynamic font — builtin Arial is absent from player builds (clip-bug fix)
        if (fixedWidth) label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.color = _assets.MenuText;
        return label;
    }

    // Leading button icon (16px, theme-tinted, smooth via LoadIcon's mipmaps). Sits before the label in the
    // button's HLG → centred with it. (LoadIcon lives in WindowBuilder.Tiles.cs — same partial class.)
    private void BuildButtonIcon(byte[]? png, Transform parent, WindowToken token, float size = 16f)
    {
        var go = UGuiPrimitives.NewChild("BtnIcon", parent);
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = le.minWidth = size; le.preferredHeight = le.minHeight = size;
        // The button content box is shifted UP 1 px (padding top 2 / bottom 4, to optically centre the OS-font
        // text ink). A geometrically-centred icon then sits ~1 px high. Nudge the icon DOWN to sit on the text
        // ink line (the image is a stretched child so the HLG still lays out the 16×16 slot).
        var imgGo = UGuiPrimitives.NewChild("Img", go.transform);
        var rt = imgGo.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(0f, -1.5f); rt.offsetMax = new Vector2(0f, -1.5f);
        var raw = imgGo.AddComponent<RawImage>(); raw.raycastTarget = false; raw.color = _assets.MenuText;
        var tex = LoadIcon(png, token); if (tex != null) raw.texture = tex;
        token.ReskinActions.Add(() => { if (raw != null) raw.color = _assets.MenuText; });
    }

    // Toggle: a 30×15 capsule (track tinted green ON / grey OFF) with an 11 px knob that slides
    // right (ON) / left (OFF). Click flips via Set(!Get()). The capsule has no text label — settings
    // rows supply the label as a sibling Text (matching the IMGUI GUILayout.Toggle(" ") pattern).
    private void BuildToggle(ToggleElement t, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Toggle", parent);
        var le = go.AddComponent<LayoutElement>(); le.preferredWidth = 30f; le.preferredHeight = 15f; le.flexibleWidth = 0f;
        var track = go.AddComponent<Image>(); track.sprite = _assets.Capsule; track.type = Image.Type.Sliced; track.raycastTarget = true;
        var btn = go.AddComponent<Button>(); btn.targetGraphic = track;

        var knobGo = UGuiPrimitives.NewChild("Knob", go.transform);
        var krt = knobGo.GetComponent<RectTransform>();
        krt.sizeDelta = new Vector2(11f, 11f);
        krt.anchorMin = krt.anchorMax = krt.pivot = new Vector2(1f, 0.5f);
        krt.anchoredPosition = new Vector2(-2f, 0f);
        var knob = knobGo.AddComponent<Image>();
        knob.sprite = _assets.Capsule; knob.type = Image.Type.Sliced;
        knob.color = new Color(0.92f, 1f, 0.95f, 1f); knob.raycastTarget = false;

        var get = t.Get; var set = t.Set;
        // Same selection release as buttons — a clicked toggle left selected mis-routes keyboard to chat.
        btn.onClick.AddListener((UnityAction)(() => { if (SuppressClickInEditMode(token)) return; set(!get()); ClearSelectionAfterClick(); }));
        token.Toggles.Add(new ToggleBinding
        {
            Track = track, Knob = krt, Get = t.Get,
            On = new Color(0.18f, 0.49f, 0.32f, 1f),
            Off = new Color(0.29f, 0.32f, 0.36f, 1f),
        });
    }

    // Slider: track (muted capsule) + accent fill + light knob. value-diffed via SliderBinding.
    private void BuildSlider(SliderElement s, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Slider", parent);
        var le = go.AddComponent<LayoutElement>(); le.preferredWidth = 160f; le.preferredHeight = 16f; le.flexibleWidth = 1f;
        var slider = go.AddComponent<Slider>();
        slider.minValue = s.Min; slider.maxValue = s.Max; slider.direction = Slider.Direction.LeftToRight;

        var track = UGuiPrimitives.NewChild("Track", go.transform);
        var trt = track.GetComponent<RectTransform>();
        trt.anchorMin = new Vector2(0f, 0.5f); trt.anchorMax = new Vector2(1f, 0.5f); trt.sizeDelta = new Vector2(0f, 4f); trt.anchoredPosition = Vector2.zero;
        var timg = track.AddComponent<Image>(); timg.sprite = _assets.Capsule; timg.type = Image.Type.Sliced; timg.color = new Color(1f, 1f, 1f, 0.10f); timg.raycastTarget = false;

        // Fill area spans the FULL track width (no handle inset) so the accent fill starts exactly at the
        // track's left edge — no grey sliver before it.
        var fillArea = UGuiPrimitives.NewChild("FillArea", go.transform);
        var fart = fillArea.GetComponent<RectTransform>();
        fart.anchorMin = new Vector2(0f, 0.5f); fart.anchorMax = new Vector2(1f, 0.5f); fart.sizeDelta = new Vector2(0f, 4f); fart.anchoredPosition = Vector2.zero;
        var fillGo = UGuiPrimitives.NewChild("Fill", fillArea.transform);
        var fillrt = fillGo.GetComponent<RectTransform>(); fillrt.anchorMin = Vector2.zero; fillrt.anchorMax = new Vector2(0f, 1f); fillrt.sizeDelta = Vector2.zero;
        var fimg = fillGo.AddComponent<Image>(); fimg.sprite = _assets.Capsule; fimg.type = Image.Type.Sliced; fimg.color = _assets.MenuAccent; fimg.raycastTarget = false;
        token.ReskinActions.Add(() => { if (fimg != null) fimg.color = _assets.MenuAccent; });   // accent follows theme

        var handleArea = UGuiPrimitives.NewChild("HandleArea", go.transform);
        var hart = handleArea.GetComponent<RectTransform>(); hart.anchorMin = Vector2.zero; hart.anchorMax = Vector2.one; hart.sizeDelta = new Vector2(-13f, 0f); hart.anchoredPosition = Vector2.zero;
        var handleGo = UGuiPrimitives.NewChild("Handle", handleArea.transform);
        var hrt = handleGo.GetComponent<RectTransform>(); hrt.sizeDelta = new Vector2(13f, 13f);
        var himg = handleGo.AddComponent<Image>(); himg.sprite = _assets.Capsule; himg.type = Image.Type.Sliced; himg.color = new Color(0.81f, 0.88f, 0.95f, 1f);

        slider.fillRect = fillrt; slider.handleRect = hrt; slider.targetGraphic = himg;
        slider.SetValueWithoutNotify(s.Get());
        var set = s.Set;
        slider.onValueChanged.AddListener((UnityAction<float>)(v => set(v)));
        token.Sliders.Add(new SliderBinding { S = slider, Get = s.Get, EnabledFn = s.Enabled });
    }

    // Input: the proven UGuiTextInput (Enter-no-chat / Esc / cursor). Seeded from Get(); registered for
    // the renderer's per-frame field tick (null in the sandbox) + the token's focus query.
    private void BuildInput(InputElement inp, Transform parent, WindowToken token)
    {
        var submit = inp.Submit;
        var onChange = inp.OnChange;
        var field = new UGuiTextInput(onSubmit: s => submit(s), onChange: onChange != null ? s => onChange(s) : (System.Action<string>?)null);
        var go = field.Build(parent);
        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        le.preferredWidth = inp.Width; le.flexibleWidth = 0f;
        field.SetFont(_assets.MenuFont);
        field.SetText(inp.Get());
        // Theme the field to the chrome: dark rounded inset + light text + left padding (not the spike's
        // white box). Text is fixed LIGHT (not MenuText) because the field bg is always dark — in the Light
        // theme MenuText is dark, which would vanish on the dark field. Capsule sprite gives the rounding.
        field.ApplyStyle(_assets.Capsule, new Color(0.05f, 0.07f, 0.10f, 0.95f), new Color(0.92f, 0.94f, 0.97f, 1f), 8f);
        _registerField?.Invoke(field);
        token.Fields.Add(field);
        token.FieldSyncs.Add(new FieldBinding { Field = field, Get = inp.Get, Last = inp.Get() });
    }

    // Scroll: vertical ScrollRect with a masked viewport + content-sized child + thin accent thumb.
    private void BuildScroll(ScrollElement sc, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Scroll", parent);
        // flexibleHeight=1 so the scroll absorbs slack height in a fixed-size (Resizable) window; in a
        // content-height-fit window there's no slack, so it stays at preferredHeight (no behaviour change).
        var le = go.AddComponent<LayoutElement>(); le.preferredHeight = sc.Height; le.flexibleWidth = 1f; le.flexibleHeight = 1f;
        var sr = go.AddComponent<ScrollRect>(); sr.horizontal = false; sr.vertical = true; sr.movementType = ScrollRect.MovementType.Clamped;

        var viewport = UGuiPrimitives.NewChild("Viewport", go.transform); UGuiPrimitives.Stretch(viewport);
        // Inset the viewport clear of the right-edge scrollbar (5px bar + 4px gap) — the bar used to
        // OVERLAY the content, sitting on top of right-aligned text (user-flagged in-world 2026-06-13).
        viewport.GetComponent<RectTransform>().offsetMax = new Vector2(-9f, 0f);
        viewport.AddComponent<RectMask2D>();
        // Transparent raycast target on the viewport so the mouse WHEEL has something to hit over the scroll area
        // — the EventSystem routes OnScroll up from the hit graphic to the ScrollRect. Without it, body text has
        // raycastTarget=false (ConfigureText) so the wheel found no target and the list wouldn't scroll.
        var catcher = viewport.AddComponent<Image>(); catcher.color = new Color(0f, 0f, 0f, 0f); catcher.raycastTarget = true;
        sr.scrollSensitivity = 24f;
        var content = UGuiPrimitives.NewChild("Content", viewport.transform);
        var crt = content.GetComponent<RectTransform>();
        // Pivot top-LEFT (not centred): with the horizontal stretch anchors below the pivot is layout-neutral,
        // but top-left guarantees any residual overflow extends rightward only (never off the left edge) —
        // defensive against the Game-UI "left clip" symptom.
        crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(1f, 1f); crt.pivot = new Vector2(0f, 1f); crt.anchoredPosition = Vector2.zero;
        // MUST zero sizeDelta.x: with stretch-x anchors the content width = viewport.width + sizeDelta.x, and a
        // fresh RectTransform's leftover sizeDelta.x (~100) made the content WIDER than the masked viewport, so
        // right-aligned content (the Hotkeys binding cell) overflowed past the RectMask2D edge → clipped. The
        // ContentSizeFitter is vertical-only, so it never corrects x. (This was the real "clip" bug — measured.)
        crt.sizeDelta = Vector2.zero;
        var clg = content.AddComponent<VerticalLayoutGroup>();
        clg.spacing = RowGap; clg.childControlWidth = true; clg.childControlHeight = true;
        clg.childForceExpandWidth = true; clg.childForceExpandHeight = false; clg.childAlignment = TextAnchor.UpperLeft;
        var fitter = content.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;   // width stays anchor-locked to the viewport
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        sr.viewport = viewport.GetComponent<RectTransform>(); sr.content = crt;
        BuildElement(sc.Child, content.transform, token);
        BuildScrollbar(sr, go, token);
    }

    // Visible themed vertical scrollbar — activates IChromeStyle.ScrollbarStyle (previously a no-op). A thin
    // (5 px) bar overlaying the right edge; AutoHide so it vanishes when the content fits. ThumbOnly = accent
    // thumb, no track; ThinTrack = faint MenuBorder track + muted thumb. Re-tinted on a theme change.
    private void BuildScrollbar(ScrollRect sr, GameObject scroll, WindowToken token)
    {
        var thinTrack = (_assets.ScrollbarStyleProvider?.Invoke() ?? MenuScrollbarStyle.ThumbOnly) == MenuScrollbarStyle.ThinTrack;

        var sbGo = UGuiPrimitives.NewChild("Scrollbar", scroll.transform);
        var sbrt = sbGo.GetComponent<RectTransform>();
        sbrt.anchorMin = new Vector2(1f, 0f); sbrt.anchorMax = new Vector2(1f, 1f); sbrt.pivot = new Vector2(1f, 1f);
        sbrt.sizeDelta = new Vector2(5f, 0f); sbrt.anchoredPosition = Vector2.zero;

        Image? track = null;
        if (thinTrack)
        {
            track = sbGo.AddComponent<Image>();
            if (_assets.Capsule != null) { track.sprite = _assets.Capsule; track.type = Image.Type.Sliced; }
            track.color = _assets.MenuBorder;
        }

        var scrollbar = sbGo.AddComponent<Scrollbar>(); scrollbar.direction = Scrollbar.Direction.BottomToTop;
        var handle = UGuiPrimitives.NewChild("Handle", sbGo.transform);
        var hrt = handle.GetComponent<RectTransform>();
        hrt.anchorMin = Vector2.zero; hrt.anchorMax = Vector2.one; hrt.sizeDelta = Vector2.zero; hrt.anchoredPosition = Vector2.zero;
        var thumb = handle.AddComponent<Image>();
        if (_assets.Capsule != null) { thumb.sprite = _assets.Capsule; thumb.type = Image.Type.Sliced; }
        thumb.color = ThumbColor(thinTrack);
        scrollbar.targetGraphic = thumb; scrollbar.handleRect = hrt; scrollbar.transition = Selectable.Transition.None;

        sr.verticalScrollbar = scrollbar;
        sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        // Drag-exclusion zone: the manual window-drag ticker must yield here, or pressing the bar on a
        // whole-frame-draggable window (Party chrome) moved the window instead of scrolling.
        RegisterScrollbar?.Invoke(sbrt);

        token.ReskinActions.Add(() =>
        {
            var tt = (_assets.ScrollbarStyleProvider?.Invoke() ?? MenuScrollbarStyle.ThumbOnly) == MenuScrollbarStyle.ThinTrack;
            if (thumb != null) thumb.color = ThumbColor(tt);
            if (track != null) track.color = _assets.MenuBorder;
        });
    }

    private Color ThumbColor(bool thinTrack)
        => thinTrack ? _assets.MenuMuted : new Color(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 0.75f);
}
