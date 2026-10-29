using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// DropdownElement: a trigger button (caption + ▾) that opens a themed floating option list. The popup lives on
// its OWN dedicated TOP-LEVEL (root) ScreenSpaceOverlay canvas (DropdownPopupLayer, parented to nothing) so it
// is never clipped by any window's scroll RectMask2D and sits above every window — including a window lifted
// onto its own override-sorting canvas (the layout toolbar at 32759). WHY a root canvas: root-vs-nested
// sortingOrder IS honored in this build, so a root canvas at a VALID order cleanly beats the toolbar's nested
// override canvas (32759). The old nested-under-StellarWindowCanvas attempt (1626025) and the first root-canvas
// attempt (32780/32781) both rendered BEHIND the toolbar — but the CONFIRMED cause is a short.MaxValue overflow
// (canvas sortingOrder is capped at 32767; those values wrapped negative), NOT nested-vs-nested unreliability
// (that earlier suspicion is unconfirmed — it was likely a misread of the same overflow). See the sortingOrder
// comment on DropdownPopupSortingOrder. A full-screen invisible blocker absorbs outside clicks;
// it is also registered as the dismissable "root" so the ticker's geometric outside-test never fires (the
// full-screen blocker contains every point) — leaving Escape as the only ticker-driven dismiss, while
// outside-click rides the blocker's own button. This sidesteps the classic same-click-dismisses-on-open bug:
// the opening press targeted the trigger, so the just-created blocker can't receive it. Split out of
// WindowBuilder.cs for the file-size gate. Popup interaction can't be sandbox-verified (Mono ≠ IL2CPP).
internal sealed partial class WindowBuilder
{
    // Click-away dismiss hook: (root rect, dismiss) → the ticker invokes dismiss on Escape or a mouse press
    // outside root. The dropdown registers its full-screen blocker as root, so only Escape fires here —
    // outside-click is absorbed by the blocker's own button. Null in the sandbox → the popup is static.
    internal Action<RectTransform, Action>? RegisterDismissable { get; set; }

    // The single open dropdown popup (one at a time per canvas; this builder is shared across the canvas's
    // windows). Closed before another opens and on pick / outside-click / Escape. The layer GO is the dedicated
    // root canvas that parents BOTH the blocker and the panel; destroying it tears down both (see CloseDropdown).
    private GameObject? _openDropdownLayer;
    private GameObject? _openDropdownBlocker;
    private GameObject? _openDropdownPanel;

    // GameObject names for the popup parts. Exposed so the layout editor's press guard can detect a press over an
    // OPEN dropdown by hierarchy name (walking the hit's parents) rather than by sortingOrder — the popup now
    // lives on its own root canvas (DropdownPopupLayer) which parents these two, so the ancestor-name walk in
    // LayoutEditorOverlay.IsPointerOverDropdownPopup still finds them. (Same name-match approach as the toolbar
    // guard, LayoutEditorOverlay.IsPointerOverToolbar.)
    internal const string DropdownPanelName = "DropdownPopup";
    internal const string DropdownBlockerName = "DropdownBlocker";

    // sortingOrder of the dropdown's dedicated ROOT canvas (DropdownPopupLayer). CONFIRMED CEILING: Unity canvas
    // sortingOrder is clamped to the 16-bit SHORT range in this build — short.MaxValue = 32767 is the hard ceiling;
    // any value above it wraps NEGATIVE (e.g. 32781 → 32781−65536 = −32755) and the canvas sinks below everything.
    // In-codebase precedent: UGuiInputBlocker.TopSortingOrder = 32760 is commented "just under short.MaxValue". So
    // 32766 is chosen to sit ABOVE the toolbar's override canvas (32759) and the top modal input blocker
    // (UGuiInputBlocker.TopSortingOrder = 32760), yet AT/BELOW the 32767 cap so it cannot overflow/wrap. The old
    // 32780/32781 values (and the earlier nested 1626025) all exceeded 32767 and wrapped negative — that, not any
    // nested-vs-nested unreliability, is why they rendered behind the toolbar. Root-vs-nested order IS honored
    // here (the toolbar's nested 32759 correctly beats the edit chrome's root 32758), which is why a root canvas
    // remains the structure. Blocker-below-panel is just sibling order WITHIN this one canvas. (Sorting-layer map
    // in WindowBuilder-Patterns.md.)
    private const int DropdownPopupSortingOrder = 32766;

    // Trigger: a normal themed button whose caption tracks the selected option; the click delivers the
    // trigger's screen rect (OnClickWithRect) so the popup anchors to the button's live position.
    private void BuildDropdown(DropdownElement dd, Transform parent, WindowToken token)
    {
        // Opt-in house-style look (.Dropdown.Pill.cs). Classic (the default) continues below, unchanged.
        if (dd.Style == DropdownStyle.Pill) { BuildPillDropdownTrigger(dd, parent, token); return; }

        string Caption()
        {
            var opts = dd.Options();
            var i = dd.Selected();
            var cur = opts != null && i >= 0 && i < opts.Count ? opts[i] : "";
            return cur + "  ▾";   // ▾ (same Geometric-Shapes block as the in-game-confirmed ►)
        }

        var trigger = new ButtonElement(Caption, () => { }, Width: dd.Width)
        {
            OnClickWithRect = rect => OpenDropdown(token, rect, dd),
        };
        BuildButton(trigger, parent, token);
    }

    // Build the blocker + option panel, anchored under the trigger, and arm Escape dismiss.
    private void OpenDropdown(WindowToken token, WindowRect anchor, DropdownElement dd)
    {
        CloseDropdown();
        var opts = dd.Options();
        if (opts == null || opts.Count == 0) return;
        var canvasRoot = token.Rect.transform.parent;   // the shared StellarWindowCanvas (windows are its direct children)
        if (canvasRoot == null) return;

        // Dedicated root canvas for the popup, scaled to match the shared canvas. Parent BOTH parts under it:
        // blocker FIRST (sibling 0), panel SECOND (last sibling) → within this one canvas sibling order is
        // reliable, so the panel draws above the blocker and its item buttons win the raycast over it.
        _openDropdownLayer = BuildDropdownPopupLayer(canvasRoot);
        var layer = _openDropdownLayer.transform;
        _openDropdownBlocker = BuildDropdownBlocker(layer);
        _openDropdownPanel = dd.Style == DropdownStyle.Pill
            ? BuildPillDropdownPanel(layer, anchor, opts, dd, token)   // .Dropdown.Pill.cs
            : BuildDropdownPanel(layer, anchor, opts, dd, token);

        var blockerRt = _openDropdownBlocker.GetComponent<RectTransform>();
        RegisterDismissable?.Invoke(blockerRt, CloseDropdown);   // full-screen root → ticker fires on Escape only
        DropdownOpenedDiag(anchor, opts.Count, _openDropdownPanel);   // .Dropdown.Diagnostics.cs (gated)
    }

    // The popup's dedicated TOP-LEVEL (root) ScreenSpaceOverlay canvas — parented to NOTHING (scene root,
    // DontDestroyOnLoad), NOT nested under the shared StellarWindowCanvas. WHY a root canvas: in this IL2CPP
    // build two override-sorting canvases that share one parent canvas do NOT sort reliably by sortingOrder
    // relative to each other (the same quirk that makes RaycastResult.sortingOrder report the PARENT's order,
    // not a nested child's) — so the old nested blocker/panel at 32780/32781 (1626025) still rendered BEHIND the
    // toolbar's own nested override canvas (32759). What IS reliable here is ROOT-vs-nested sorting by
    // sortingOrder: the toolbar's nested 32759 correctly beats the edit chrome's ROOT canvas at 32758 (7f11e79,
    // user-confirmed). So a dedicated root canvas above the toolbar wins deterministically.
    // - CanvasScaler is CLONED from the live shared canvas so the popup scales IDENTICALLY — otherwise a bare
    //   ScreenSpaceOverlay canvas sits at scaleFactor 1 and PositionDropdown's canvas-unit→screen-px math breaks.
    //   We also seed the Canvas.scaleFactor from the shared Canvas's CURRENT value, because a CanvasScaler only
    //   computes its scaleFactor on the next Canvas.willRenderCanvases pass (end of frame) — PositionDropdown
    //   reads it THIS frame, so the pre-seed gives the correct value before the cloned scaler's first Handle.
    // - GraphicRaycaster is MANDATORY — without it the blocker/items are un-hittable (same lesson as
    //   WindowSpec.OverrideSortingOrder). Canvas must precede the raycaster ([RequireComponent(Canvas)]).
    private static GameObject BuildDropdownPopupLayer(Transform canvasRoot)
    {
        var go = new GameObject("DropdownPopupLayer");
        go.AddComponent<RectTransform>();
        UnityEngine.Object.DontDestroyOnLoad(go);
        var c = go.AddComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay;
        c.pixelPerfect = true;
        c.sortingOrder = DropdownPopupSortingOrder;   // overrideSorting is irrelevant for a root canvas

        // Clone the shared canvas's scaler (ref WindowRenderer.EnsureCanvas: ScaleWithScreenSize, MatchWidthOrHeight,
        // matchWidthOrHeight 0.5) so the popup computes the SAME scaleFactor across resolutions.
        var srcCanvas = canvasRoot.GetComponent<Canvas>() ?? canvasRoot.GetComponentInParent<Canvas>();
        var srcScaler = canvasRoot.GetComponent<CanvasScaler>() ?? canvasRoot.GetComponentInParent<CanvasScaler>();
        var scaler = go.AddComponent<CanvasScaler>();
        if (srcScaler != null)
        {
            scaler.uiScaleMode = srcScaler.uiScaleMode;
            scaler.referenceResolution = srcScaler.referenceResolution;
            scaler.screenMatchMode = srcScaler.screenMatchMode;
            scaler.matchWidthOrHeight = srcScaler.matchWidthOrHeight;
            scaler.scaleFactor = srcScaler.scaleFactor;   // only used by ConstantPixelSize; copied for completeness
        }
        if (srcCanvas != null) c.scaleFactor = srcCanvas.scaleFactor;   // same-frame value for PositionDropdown (see above)

        go.AddComponent<GraphicRaycaster>();
        return go;
    }

    // Full-screen transparent click-catcher: absorbs every press outside the panel and closes on it. It can't
    // receive the press that opened the popup (that press already targeted the trigger), so no self-dismiss.
    private GameObject BuildDropdownBlocker(Transform layer)
    {
        // First child of the popup layer (sibling 0) → draws under the panel, which is added next. One canvas,
        // so sibling order alone decides blocker-below-panel; no per-part canvas needed.
        var go = UGuiPrimitives.NewChild(DropdownBlockerName, layer);
        UGuiPrimitives.Stretch(go);
        var img = go.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0f);   // invisible but raycastable
        img.raycastTarget = true;
        var btn = go.AddComponent<Button>(); btn.targetGraphic = img; btn.transition = Selectable.Transition.None;
        DisableKeyboardNav(btn);
        btn.onClick.AddListener((UnityAction)(() => { CloseDropdown(); ClearSelectionAfterClick(); }));
        return go;
    }

    // Themed option list (border + opaque lifted bg, mirroring BuildPanel) sized to its items, positioned under
    // the trigger. Drawn after the blocker so it sits on top and its item buttons win the raycast. When the live
    // option count exceeds dd.MaxVisibleRows the item list is capped to that many rows and wrapped in a scrollable
    // viewport (BuildScrollViewport + the SAME themed thin scrollbar BuildScroll uses); otherwise it keeps the
    // grow-to-fit ContentSizeFitter behaviour exactly.
    private GameObject BuildDropdownPanel(Transform layer, WindowRect anchor, IReadOnlyList<string> opts, DropdownElement dd, WindowToken token)
    {
        // Last child of the popup layer → draws above the blocker and its item buttons win the raycast over it.
        // Sorting + scaling + raycasting all come from the single layer canvas (BuildDropdownPopupLayer), so the
        // panel needs no canvas of its own; PositionDropdown reads the layer canvas's (cloned) scaleFactor.
        var panel = UGuiPrimitives.NewChild(DropdownPanelName, layer);
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = prt.anchorMax = new Vector2(0f, 1f);
        prt.pivot = new Vector2(0f, 1f);

        var vlg = panel.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(2, 2, 2, 2); vlg.spacing = 1f;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperLeft;
        var csf = panel.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        AddDropdownPanelChrome(panel.transform);
        var minW = Mathf.Max(anchor.Width, 80f);
        var selected = dd.Selected();

        // Where the items live: directly on the panel (grow-to-fit), or inside a fixed-height scroll viewport when
        // the list is capped. Both give each item a minW-wide SelectableElement row via BuildDropdownItem, unchanged.
        var itemParent = dd.MaxVisibleRows > 0 && opts.Count > dd.MaxVisibleRows
            ? BuildDropdownScroll(panel, minW, dd.MaxVisibleRows, token, (Scaled(11) + 8f, 1f))
            : panel.transform;

        for (var i = 0; i < opts.Count; i++)
        {
            var idx = i;   // capture per item for the pick closure
            BuildDropdownItem(itemParent, opts[i], i == selected, minW, () =>
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

    // Wrap the capped option list in a fixed-height scrollable viewport (BuildScrollViewport + the SAME themed thin
    // scrollbar BuildScroll uses) and return the content transform the items parent into. The panel's own
    // ContentSizeFitter still sizes it to minW (+ a 9px bar lane) × the capped height, so PositionDropdown's
    // overflow-flip/clamp works against the real (capped) rect. row = (H, Gap): one row's height and the inter-row
    // gap: Classic passes Scaled(11) + 8 (label + BuildDropdownItem's 4+4 padding) and 1; Pill its fixed row + gap.
    private Transform BuildDropdownScroll(GameObject panel, float minW, int cap, WindowToken token, (float H, float Gap) row)
    {
        var viewportH = cap * row.H + (cap - 1) * row.Gap;   // cap rows + the inter-row spacing between them
        var (_, content, _) = BuildScrollViewport(panel.transform, viewportH, token);
        // BuildScrollViewport's Scroll host grows elastically; pin it so the panel's ContentSizeFitter resolves a
        // definite width (minW + 9px bar lane) and the capped height instead of collapsing / absorbing slack.
        var scrollLe = content.parent.parent.GetComponent<LayoutElement>();   // content → Viewport → Scroll
        scrollLe.preferredWidth = minW + 9f; scrollLe.flexibleWidth = 0f; scrollLe.flexibleHeight = 0f;
        // Content stacks the items + sizes to their total height so the ScrollRect can scroll (mirrors BuildScroll).
        var clg = content.gameObject.AddComponent<VerticalLayoutGroup>();
        clg.spacing = row.Gap; clg.padding = new RectOffset(0, 0, 0, 0);
        clg.childControlWidth = true; clg.childControlHeight = true;
        clg.childForceExpandWidth = true; clg.childForceExpandHeight = false; clg.childAlignment = TextAnchor.UpperLeft;
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;   // width stays anchor-locked to the viewport
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return content;
    }

    // Border + inset background, copied from BuildPanel (ignoreLayout stretched Images, transient → no reskin).
    private void AddDropdownPanelChrome(Transform panel)
    {
        var border = UGuiPrimitives.NewChild("Border", panel);
        UGuiPrimitives.Stretch(border);
        border.AddComponent<LayoutElement>().ignoreLayout = true;
        var bimg = border.AddComponent<Image>(); bimg.color = PanelBorderColor(); bimg.raycastTarget = true;

        var bgGo = UGuiPrimitives.NewChild("Bg", panel);
        UGuiPrimitives.Stretch(bgGo);
        bgGo.AddComponent<LayoutElement>().ignoreLayout = true;
        var brt = bgGo.GetComponent<RectTransform>();
        brt.offsetMin = new Vector2(2f, 2f); brt.offsetMax = new Vector2(-2f, -2f);
        var bg = bgGo.AddComponent<Image>(); bg.color = PanelBgColor(); bg.raycastTarget = true;
    }

    // One option row: SelectableElement style (SwatchBg chip, transparent rest / accent hover / accent-fill for the
    // active selection). Hover is ticker-driven via _registerHover (same path as BuildSelectable) — the
    // EventSystem ColorTint path was tried first but doesn't fire reliably here since the ticker bypasses the
    // EventSystem for all other interactions. bg.color is set after AddComponent<Button> because OnEnable snaps
    // it to default-white before we can set Transition.None.
    private void BuildDropdownItem(Transform panel, string text, bool active, float minW, Action onPick)
    {
        var go = UGuiPrimitives.NewChild("DropdownItem", panel);
        var le = go.AddComponent<LayoutElement>(); le.flexibleWidth = 1f; le.minWidth = minW;
        var bg = go.AddComponent<Image>();
        if (_assets.SwatchBg != null) { bg.sprite = _assets.SwatchBg; bg.type = Image.Type.Sliced; }
        bg.raycastTarget = true;
        var btn = go.AddComponent<Button>(); btn.targetGraphic = bg; btn.transition = Selectable.Transition.None;
        DisableKeyboardNav(btn);   // list hover/selection is ticker-driven (_registerHover), NOT EventSystem focus — safe to kill nav
        var rest = active ? SelOn() : SelRest();
        bg.color = rest;   // override the white that Button.OnEnable snapped to before we set Transition.None
        btn.onClick.AddListener((UnityAction)(() => onPick()));   // onPick already calls ClearSelectionAfterClick (see BuildDropdownPanel)
        _registerHover?.Invoke(go.GetComponent<RectTransform>(), on => { if (bg != null) bg.color = on ? SelHover() : rest; });
        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(6, 6, 4, 4); vlg.spacing = 0f;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperLeft;
        go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var labelGo = UGuiPrimitives.NewChild("Label", go.transform);
        var label = labelGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(label, Scaled(11), TextAnchor.MiddleLeft, bold: false);
        label.alignByGeometry = true; ApplyMenuFont(label); label.color = _assets.MenuText; label.text = text;
    }

    // Place the panel's top-left just under the trigger (open upward if it would overflow the screen bottom),
    // clamped to the right edge. Overlay canvas: RectTransform.position is screen pixels (Y-up), matching
    // FireOnClickWithRect's read. Force a layout pass first so rect width/height are known for the flip/clamp.
    private static void PositionDropdown(RectTransform prt, WindowRect anchor)
    {
        LayoutRebuilder.ForceRebuildLayoutImmediate(prt);
        var sf = prt.GetComponentInParent<Canvas>()?.scaleFactor ?? 1f;
        if (sf <= 0f) sf = 1f;
        var size = prt.rect.size * sf;   // canvas units → screen px, to match anchor/Screen space
        var left = Mathf.Clamp(anchor.X, 0f, Mathf.Max(0f, Screen.width - size.x));
        var triggerBottom = Screen.height - anchor.Y - anchor.Height;   // screen Y-up
        var topLeftY = triggerBottom - size.y < 0f
            ? Screen.height - anchor.Y + size.y          // overflow bottom → open upward (bottom at trigger top)
            : triggerBottom;                             // normal → top at trigger bottom
        prt.position = new Vector3(left, topLeftY, 0f);
    }

    // Destroy the open popup (idempotent). Called on pick, blocker click, Escape, or before opening another.
    // Destroying the layer GO tears down the blocker + panel it parents in one shot; null every ref so repeated
    // open/close (and edit-exit) never leaks a popup layer at the scene root.
    internal void CloseDropdown()
    {
        if (_openDropdownLayer != null) { UnityEngine.Object.Destroy(_openDropdownLayer); _openDropdownLayer = null; }
        _openDropdownPanel = null;
        _openDropdownBlocker = null;
    }
}
