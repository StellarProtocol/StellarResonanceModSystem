using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// DropdownElement: a trigger button (caption + ▾) that opens a themed floating option list. The popup is
// parented to the CANVAS ROOT (an ancestor of every window's scroll RectMask2D, same canvas) so it is never
// clipped, and drawn last so it sits above all windows. A full-screen invisible blocker absorbs outside
// clicks; it is also registered as the dismissable "root" so the ticker's geometric outside-test never fires
// (the full-screen blocker contains every point) — leaving Escape as the only ticker-driven dismiss, while
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
    // windows). Closed before another opens and on pick / outside-click / Escape.
    private GameObject? _openDropdownBlocker;
    private GameObject? _openDropdownPanel;

    // Canvas-root GameObject names for the popup parts. Exposed so the layout editor's press guard can detect a
    // press over an OPEN dropdown by hierarchy name (its override-sorting canvas's order isn't reported by
    // RaycastResult — same lesson as the toolbar guard, see LayoutEditorOverlay.IsPointerOverToolbar).
    internal const string DropdownPanelName = "DropdownPopup";
    internal const string DropdownBlockerName = "DropdownBlocker";

    // The popup draws on its OWN nested override-sorting canvases so it renders ABOVE every window — including a
    // window itself lifted onto an override-sorting canvas (the layout toolbar at 32759) and the top modal input
    // blocker (UGuiInputBlocker.TopSortingOrder = 32760). Without this the popup, parented to the shared base
    // window canvas (32755) and relying only on sibling SetAsLastSibling order, is OCCLUDED by the higher toolbar
    // canvas where they overlap (the list opens right under the top-anchored bar). The panel sits one order ABOVE
    // the blocker so the panel's item buttons win the raycast over the full-screen blocker; both stay well under
    // the click-away popup band (1_000_000). (Sorting-layer map in WindowBuilder-Patterns.md.)
    private const int DropdownBlockerSortingOrder = 32780;
    private const int DropdownPanelSortingOrder = 32781;

    // Trigger: a normal themed button whose caption tracks the selected option; the click delivers the
    // trigger's screen rect (OnClickWithRect) so the popup anchors to the button's live position.
    private void BuildDropdown(DropdownElement dd, Transform parent, WindowToken token)
    {
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
        var canvasRoot = token.Rect.transform.parent;   // windows are direct children of the canvas root
        if (canvasRoot == null) return;

        _openDropdownBlocker = BuildDropdownBlocker(canvasRoot);
        _openDropdownPanel = BuildDropdownPanel(canvasRoot, anchor, opts, dd, token);

        var blockerRt = _openDropdownBlocker.GetComponent<RectTransform>();
        RegisterDismissable?.Invoke(blockerRt, CloseDropdown);   // full-screen root → ticker fires on Escape only
        DropdownOpenedDiag(anchor, opts.Count, _openDropdownPanel);   // .Dropdown.Diagnostics.cs (gated)
    }

    // Lift a transient popup GameObject onto its OWN nested override-sorting canvas so it renders above all
    // windows. Mirrors WindowRenderer.Mount's per-window override-sorting block: the GraphicRaycaster is
    // MANDATORY — the parent canvas's raycaster skips a child canvas, so without it the blocker/items become
    // un-hittable (same lesson as WindowSpec.OverrideSortingOrder). Canvas must precede the raycaster
    // ([RequireComponent(typeof(Canvas))] on GraphicRaycaster).
    private static void ElevatePopupCanvas(GameObject go, int sortingOrder)
    {
        var c = go.AddComponent<Canvas>();
        c.overrideSorting = true;
        c.sortingOrder = sortingOrder;
        go.AddComponent<GraphicRaycaster>();
    }

    // Full-screen transparent click-catcher: absorbs every press outside the panel and closes on it. It can't
    // receive the press that opened the popup (that press already targeted the trigger), so no self-dismiss.
    private GameObject BuildDropdownBlocker(Transform canvasRoot)
    {
        var go = UGuiPrimitives.NewChild(DropdownBlockerName, canvasRoot);
        UGuiPrimitives.Stretch(go);
        var img = go.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0f);   // invisible but raycastable
        img.raycastTarget = true;
        var btn = go.AddComponent<Button>(); btn.targetGraphic = img; btn.transition = Selectable.Transition.None;
        DisableKeyboardNav(btn);
        btn.onClick.AddListener((UnityAction)(() => { CloseDropdown(); ClearSelectionAfterClick(); }));
        // Own override-sorting canvas just BELOW the panel so the blocker absorbs every outside press but the
        // panel's item buttons still win the raycast over it. SetAsLastSibling is now moot (order comes from the
        // canvas, not sibling index) but kept harmless.
        ElevatePopupCanvas(go, DropdownBlockerSortingOrder);
        go.transform.SetAsLastSibling();
        return go;
    }

    // Themed option list (border + opaque lifted bg, mirroring BuildPanel) sized to its items, positioned under
    // the trigger. Drawn after the blocker so it sits on top and its item buttons win the raycast. When the live
    // option count exceeds dd.MaxVisibleRows the item list is capped to that many rows and wrapped in a scrollable
    // viewport (BuildScrollViewport + the SAME themed thin scrollbar BuildScroll uses); otherwise it keeps the
    // grow-to-fit ContentSizeFitter behaviour exactly.
    private GameObject BuildDropdownPanel(Transform canvasRoot, WindowRect anchor, IReadOnlyList<string> opts, DropdownElement dd, WindowToken token)
    {
        var panel = UGuiPrimitives.NewChild(DropdownPanelName, canvasRoot);
        var prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = prt.anchorMax = new Vector2(0f, 1f);
        prt.pivot = new Vector2(0f, 1f);
        // Own override-sorting canvas (+ raycaster) so the whole option list draws and hit-tests ABOVE every
        // window, including the layout toolbar's own override canvas (32759). A nested Canvas has no CanvasScaler,
        // so it inherits the root canvas's scaleFactor — PositionDropdown's scaleFactor/flip/clamp math is
        // unchanged. Added before the items + PositionDropdown; neither reads the canvas for layout.
        ElevatePopupCanvas(panel, DropdownPanelSortingOrder);

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
            ? BuildDropdownScroll(panel, minW, dd.MaxVisibleRows, token)
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
    // overflow-flip/clamp works against the real (capped) rect.
    private Transform BuildDropdownScroll(GameObject panel, float minW, int cap, WindowToken token)
    {
        var itemH = Scaled(11) + 8f;                    // label + item VLG top/bottom padding (4+4, BuildDropdownItem)
        var viewportH = cap * itemH + (cap - 1) * 1f;   // cap rows + the 1px inter-row spacing between them
        var (_, content, _) = BuildScrollViewport(panel.transform, viewportH, token);
        // BuildScrollViewport's Scroll host grows elastically; pin it so the panel's ContentSizeFitter resolves a
        // definite width (minW + 9px bar lane) and the capped height instead of collapsing / absorbing slack.
        var scrollLe = content.parent.parent.GetComponent<LayoutElement>();   // content → Viewport → Scroll
        scrollLe.preferredWidth = minW + 9f; scrollLe.flexibleWidth = 0f; scrollLe.flexibleHeight = 0f;
        // Content stacks the items + sizes to their total height so the ScrollRect can scroll (mirrors BuildScroll).
        var clg = content.gameObject.AddComponent<VerticalLayoutGroup>();
        clg.spacing = 1f; clg.padding = new RectOffset(0, 0, 0, 0);
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
    private void CloseDropdown()
    {
        if (_openDropdownPanel != null) { UnityEngine.Object.Destroy(_openDropdownPanel); _openDropdownPanel = null; }
        if (_openDropdownBlocker != null) { UnityEngine.Object.Destroy(_openDropdownBlocker); _openDropdownBlocker = null; }
    }
}
