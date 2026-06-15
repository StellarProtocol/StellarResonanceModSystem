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
        var le = go.AddComponent<LayoutElement>(); le.flexibleWidth = 1f;   // fill the column width
        var bg = go.AddComponent<Image>();
        if (_assets.SwatchBg != null) { bg.sprite = _assets.SwatchBg; bg.type = Image.Type.Sliced; }
        bg.raycastTarget = true;
        var btn = go.AddComponent<Button>(); btn.targetGraphic = bg; btn.transition = Selectable.Transition.None;
        var onClick = sel.OnClick; btn.onClick.AddListener((UnityAction)(() => onClick()));

        // Child host: a VLG with row padding so the tint has breathing room (4 v / 6 h); CSF sizes the row
        // height to content (same combo as BuildTile).
        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(6, 6, 4, 4); vlg.spacing = 2f;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperLeft;
        var csf = go.AddComponent<ContentSizeFitter>(); csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        BuildElement(sel.Child, go.transform, token);

        // 3-state re-tint via a diffing binding (no-change poll writes nothing). Hover is ticker-driven (null
        // in sandbox → rest/selected only); selected is polled; reskin recomputes the accent-derived tints.
        var binding = new SelectableBinding { Bg = bg, SelectedFn = sel.Selected, Rest = SelRest(), Hover = SelHover(), On = SelOn() };
        _registerHover?.Invoke(go.GetComponent<RectTransform>(), on => { binding.HoverState = on; binding.ForceRepaint(); });
        token.Selectables.Add(binding);
        token.ReskinActions.Add(() => { binding.Rest = SelRest(); binding.Hover = SelHover(); binding.On = SelOn(); binding.ForceRepaint(); });
        binding.ForceRepaint();   // initial paint (also covers the sandbox static-render path)
    }

    // 3-state row tints from the menu accent (so Default/Dark/Light/Crimson all track). Rest fully transparent
    // so an unselected, un-hovered list reads clean; hover ~ the titlebar 0.14; selected stronger 0.28.
    private Color SelRest()  => new(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 0f);
    private Color SelHover() => new(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 0.14f);
    private Color SelOn()    => new(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 0.28f);
}
