using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Table/alignment + selectable-row primitives (Phase A of the leftover-plugin uGUI migration). Kept in a
// sibling partial so WindowBuilder.cs / .Widgets.cs stay under the file-size gate.
internal sealed partial class WindowBuilder
{
    // Width/weight column cell — the GUILayout.Width / ExpandWidth analog so a Row's children align like a
    // table. The child builds INTO a VLG host (childForceExpandWidth so a Text wraps within the column rather
    // than overflowing). Fixed Width pins preferredWidth=minWidth, flexibleWidth=0 (cannot drift with font
    // metrics — numeric columns); Weight sets flexibleWidth=Weight, minWidth=0 (grows to share leftover row
    // width — elastic label column / master-detail panes). No binding: the child owns its own.
    private void BuildCell(CellElement cell, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Cell", parent);
        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 0f; vlg.padding = new RectOffset(0, 0, 0, 0);
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.MiddleLeft;
        var le = go.AddComponent<LayoutElement>();
        if (cell.Width > 0f) { le.preferredWidth = le.minWidth = cell.Width; le.flexibleWidth = 0f; }
        else if (cell.Weight > 0f) { le.flexibleWidth = cell.Weight; le.minWidth = 0f; }
        else { le.flexibleWidth = 0f; le.minWidth = 0f; }   // natural content size
        BuildElement(cell.Child, go.transform, token);
    }

    // Selectable row: a full-width background Image (raycast target, the rounded SwatchBg chip) + a Button for
    // the click + a padded VLG host for the child subtree. Background tint is 3-state — rest transparent /
    // hover accent-wash / selected accent-fill — all derived from the menu accent so the 4 themes track. Hover
    // reuses the tile ticker hook (_registerHover); selected is poll-diffed via SelectableBinding so an
    // externally-changed selection re-tints without a rebuild. Mirrors BuildTile's clickable-cell shape.
    private void BuildSelectable(SelectableElement sel, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Selectable", parent);
        var le = go.AddComponent<LayoutElement>(); le.flexibleWidth = sel.Bare ? 0f : 1f;   // fill the column width (Bare: hug the child)
        var bg = go.AddComponent<Image>();
        // Bare: no sprite — a sliced chip reports its border as a minimum layout size, which could pad a tiny child.
        if (_assets.SwatchBg != null && !sel.Bare) { bg.sprite = _assets.SwatchBg; bg.type = Image.Type.Sliced; }
        bg.raycastTarget = true;
        var btn = go.AddComponent<Button>(); btn.targetGraphic = bg; btn.transition = Selectable.Transition.None;
        DisableKeyboardNav(btn);
        // Release the EventSystem selection after the click so a clicked row doesn't linger as
        // currentSelectedGameObject (which makes the game route WASD into chat instead of movement).
        // A release that ends a ListElement drag-reorder may still be delivered as a click (drop back on the same
        // cell inside the EventSystem's own drag threshold) — ClickSuppressed swallows it so a drag never "clicks".
        WireSelectableClick(btn, sel, go.GetComponent<RectTransform>());

        // Child host: a VLG with row padding so the tint has breathing room (4 v / 6 h); CSF sizes the row
        // height to content (same combo as BuildTile).
        var vlg = go.AddComponent<VerticalLayoutGroup>();
        // Bare: no inset at all, so the click target is exactly the child's rect and wrapping changes no size.
        vlg.padding = sel.Bare ? new RectOffset(0, 0, 0, 0) : new RectOffset(6, 6, 4, 4); vlg.spacing = sel.Bare ? 0f : 2f;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperLeft;
        var csf = go.AddComponent<ContentSizeFitter>(); csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        BuildElement(sel.Child, go.transform, token);

        // Optional "active tab" underline bar (SelectedBar) — null when not opted in; shown only while selected,
        // driven by the binding below.
        var bar = sel.SelectedBar && !sel.Bare ? AddSelectedBar(go, token) : null;

        // 3-state re-tint via a diffing binding (no-change poll writes nothing). Hover is ticker-driven (null
        // in sandbox → rest/selected only); selected is polled; reskin recomputes the accent-derived tints.
        // Bare (SelectableElement.Bare): every state paints clear — the Image stays only as the (alpha-0) raycast target.
        var binding = new SelectableBinding { Bg = bg, Bar = bar, SelectedFn = sel.Selected, Rest = SelRest(), Hover = SelHover(), On = SelOn() };
        if (sel.Bare) binding.Rest = binding.Hover = binding.On = Color.clear;
        var onHover = sel.OnHover;
        _registerHover?.Invoke(go.GetComponent<RectTransform>(), on => { binding.HoverState = on; binding.ForceRepaint(); onHover?.Invoke(on); });
        // Right-click: the ticker's RightClicks hook (right-button-down, FrontWindowBlocks-gated, the CombatMeter row
        // menu path). uGUI's Button only reacts to the LEFT button, so a right-click can never also fire OnClick.
        if (sel.OnRightClick is { } rc) RegisterRightClick?.Invoke(go.GetComponent<RectTransform>(), rc);
        token.Selectables.Add(binding);
        token.ReskinActions.Add(() => { if (!sel.Bare) { binding.Rest = SelRest(); binding.Hover = SelHover(); binding.On = SelOn(); } binding.ForceRepaint(); });
        binding.ForceRepaint();   // initial paint (also covers the sandbox static-render path)
    }

    // A ~3 px full-width accent underline overlaid on the row's BOTTOM edge for SelectableElement.SelectedBar (an
    // "active tab" indicator). ignoreLayout so the VLG/CSF never measure it → it never changes the row height;
    // raycastTarget=false so it never eats the row click. Full-alpha menu accent (the selected-FILL tint is a faint
    // accent wash; this crisp bar sits on top), re-tinted on a theme change. The binding toggles its enabled state.
    private Image AddSelectedBar(GameObject go, WindowToken token)
    {
        var barGo = UGuiPrimitives.NewChild("SelectedBar", go.transform);
        var brt = barGo.GetComponent<RectTransform>();
        brt.anchorMin = new Vector2(0f, 0f); brt.anchorMax = new Vector2(1f, 0f);   // bottom-stretch
        brt.pivot = new Vector2(0.5f, 0f); brt.sizeDelta = new Vector2(0f, 3f); brt.anchoredPosition = Vector2.zero;
        barGo.AddComponent<LayoutElement>().ignoreLayout = true;
        var bar = barGo.AddComponent<Image>();
        bar.color = new Color(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 1f);
        bar.raycastTarget = false;
        token.ReskinActions.Add(() => { if (bar != null) bar.color = new Color(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 1f); });
        return bar;
    }

    // 3-state row tints from the menu accent (so Default/Dark/Light/Crimson all track). Rest fully transparent
    // so an unselected, un-hovered list reads clean; hover ~ the titlebar 0.14; selected stronger 0.28.
    private Color SelRest()  => new(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 0f);
    private Color SelHover() => new(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 0.14f);
    private Color SelOn()    => new(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 0.28f);

    // The row's left click: OnClick, then OnClickWithRect with the live rect (read at click time, so a moved/resized
    // window reports where the row IS) — the same order as ButtonElement. ClickSuppressed gates both.
    private void WireSelectableClick(Button btn, SelectableElement sel, RectTransform? rt)
    {
        var onClick = sel.OnClick;
        var onClickWithRect = sel.OnClickWithRect;
        btn.onClick.AddListener((UnityAction)(() =>
        {
            if (ClickSuppressed?.Invoke() != true)
            {
                onClick();
                if (onClickWithRect != null && rt != null) onClickWithRect(ScreenRectOf(rt));
            }
            ClearSelectionAfterClick();
        }));
    }

    // SelectableElement.OnClickWithRect: the element's live screen rect in PIXELS, top-left origin (ButtonElement's
    // space). Every window canvas is ScreenSpaceOverlay, so world space == screen px (Y up). The two local corners go
    // through TransformPoint, which folds in every scale on the chain (canvas scaleFactor AND any scaled ancestor) —
    // NOT GetWorldCorners(new Vector3[4]): IL2CPP fills a native copy and the managed array stays all zeros.
    private static Abstractions.Domain.WindowRect ScreenRectOf(RectTransform rt)
    {
        var r = rt.rect;
        var lo = rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f));
        var hi = rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f));
        return new Abstractions.Domain.WindowRect(lo.x, Screen.height - hi.y, hi.x - lo.x, hi.y - lo.y);
    }
}
