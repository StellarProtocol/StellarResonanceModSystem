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
        _openDropdownPanel = BuildDropdownPanel(canvasRoot, anchor, opts, dd);

        var blockerRt = _openDropdownBlocker.GetComponent<RectTransform>();
        RegisterDismissable?.Invoke(blockerRt, CloseDropdown);   // full-screen root → ticker fires on Escape only
        DropdownOpenedDiag(anchor, opts.Count, _openDropdownPanel);   // .Dropdown.Diagnostics.cs (gated)
    }

    // Full-screen transparent click-catcher: absorbs every press outside the panel and closes on it. It can't
    // receive the press that opened the popup (that press already targeted the trigger), so no self-dismiss.
    private GameObject BuildDropdownBlocker(Transform canvasRoot)
    {
        var go = UGuiPrimitives.NewChild("DropdownBlocker", canvasRoot);
        UGuiPrimitives.Stretch(go);
        var img = go.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0f);   // invisible but raycastable
        img.raycastTarget = true;
        var btn = go.AddComponent<Button>(); btn.targetGraphic = img; btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener((UnityAction)CloseDropdown);
        go.transform.SetAsLastSibling();
        return go;
    }

    // Themed option list (border + opaque lifted bg, mirroring BuildPanel) sized to its items, positioned under
    // the trigger. Drawn after the blocker so it sits on top and its item buttons win the raycast.
    private GameObject BuildDropdownPanel(Transform canvasRoot, WindowRect anchor, IReadOnlyList<string> opts, DropdownElement dd)
    {
        var panel = UGuiPrimitives.NewChild("DropdownPopup", canvasRoot);
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
        for (var i = 0; i < opts.Count; i++)
        {
            var idx = i;   // capture per item for the pick closure
            BuildDropdownItem(panel.transform, opts[i], i == selected, minW, () =>
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

    // One option row: a left-aligned themed button (accent sprite if it's the current selection). Click runs
    // onPick (select + close). Static label (popup is transient) → no token binding, so it destroys cleanly.
    private void BuildDropdownItem(Transform panel, string text, bool active, float minW, Action onPick)
    {
        var go = UGuiPrimitives.NewChild("DropdownItem", panel);
        var img = go.AddComponent<Image>();
        img.sprite = active ? _assets.ButtonAccentBg : NormalButtonSprite(_assets.ButtonStyle);
        img.type = Image.Type.Sliced; img.raycastTarget = true;
        var btn = go.AddComponent<Button>(); btn.targetGraphic = img;

        var lg = go.AddComponent<HorizontalLayoutGroup>();
        lg.padding = new RectOffset(9, 9, 3, 3); lg.childAlignment = TextAnchor.MiddleLeft;
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = true; lg.childForceExpandHeight = false;
        var le = go.AddComponent<LayoutElement>(); le.minHeight = Scaled(11) + 12f; le.minWidth = minW;

        var labelGo = UGuiPrimitives.NewChild("Label", go.transform);
        var label = labelGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(label, Scaled(11), TextAnchor.MiddleLeft, bold: false);
        label.alignByGeometry = true; ApplyMenuFont(label); label.color = _assets.MenuText; label.text = text;

        btn.onClick.AddListener((UnityAction)(() => onPick()));
    }

    // Place the panel's top-left just under the trigger (open upward if it would overflow the screen bottom),
    // clamped to the right edge. Overlay canvas: RectTransform.position is screen pixels (Y-up), matching
    // FireOnClickWithRect's read. Force a layout pass first so rect width/height are known for the flip/clamp.
    private static void PositionDropdown(RectTransform prt, WindowRect anchor)
    {
        LayoutRebuilder.ForceRebuildLayoutImmediate(prt);
        var size = prt.rect.size;
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
