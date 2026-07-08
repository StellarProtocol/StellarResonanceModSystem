using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Structural layout elements: ConditionalElement (both-subtrees-built SetActive toggle) and ListElement
// (variable-length slot pool). Extracted from WindowBuilder.cs for the file-size gate.
internal sealed partial class WindowBuilder
{
    // Column containers must force their children to the FULL window width (like the window's own
    // ColumnElement), or the width-fill chain breaks: content nested under a tab Conditional / a List wouldn't
    // inherit the window width, so Rows/Scrolls inside (e.g. the Hotkeys binding cell) got the wrong geometry
    // and overflowed the frame. AddLayout defaults childForceExpandWidth=false; this re-enables it for columns.
    private static void ExpandColumnWidth(GameObject go)
    {
        if (go.GetComponent<VerticalLayoutGroup>() is { } vlg) vlg.childForceExpandWidth = true;
    }

    // React-style conditional — both subtrees built once; CondBinding SetActive-toggles each refresh.
    // The Settings tab strip uses this (one Conditional per tab, When = isActiveTab).
    private void BuildConditional(ConditionalElement cond, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("Cond", parent);
        UGuiPrimitives.AddLayout(go, gap: 0f, columns: UGuiPrimitives.ColumnMode); ExpandColumnWidth(go);
        // Clamp horizontal flex to 0 so the Cond go does NOT expand when placed inside a Row HLG.
        // ExpandColumnWidth sets childForceExpandWidth=true on the VLG, which Unity bubbles up as
        // flexibleWidth=1f — a Row HLG (childForceExpandWidth=false) would then give all slack to this
        // one Cond go (296px in a 320px bar), making its content appear centred despite the UpperLeft/
        // MiddleLeft childAlignment. Column parents that have childForceExpandWidth=true still override
        // this via Mathf.Max(0,1)=1, so Column-contained Conditionals still fill their parent width.
        var condLe = go.AddComponent<LayoutElement>(); condLe.flexibleWidth = 0f;
        // Fill: the active branch grows to fill leftover height in a fixed-size window (the Cond grabs the slack
        // via flexibleHeight; childForceExpandHeight stretches the branch + its content to fill it).
        if (cond.Fill)
        {
            condLe.flexibleHeight = 1f;
            if (go.GetComponent<VerticalLayoutGroup>() is { } gv) gv.childForceExpandHeight = true;
        }
        var thenGo = UGuiPrimitives.NewChild("Then", go.transform);
        UGuiPrimitives.AddLayout(thenGo, gap: 0f, columns: UGuiPrimitives.ColumnMode); ExpandColumnWidth(thenGo);
        if (cond.Fill && thenGo.GetComponent<VerticalLayoutGroup>() is { } tv) tv.childForceExpandHeight = true;
        BuildElement(cond.Then, thenGo.transform, token);
        GameObject? elseGo = null;
        if (cond.Else != null)
        {
            elseGo = UGuiPrimitives.NewChild("Else", go.transform);
            UGuiPrimitives.AddLayout(elseGo, gap: 0f, columns: UGuiPrimitives.ColumnMode); ExpandColumnWidth(elseGo);
            if (cond.Fill && elseGo.GetComponent<VerticalLayoutGroup>() is { } ev) ev.childForceExpandHeight = true;
            BuildElement(cond.Else, elseGo.transform, token);
        }
        // With NO else-branch, toggle the WHOLE Cond container (not just its inner Then). Otherwise the
        // container stays an active 0-height child and the parent column still adds section-spacing around it —
        // 5 collapsed tab-Conditionals = a big empty gap above the active tab's content. Collapsing the
        // container removes it from the layout entirely. (With an else-branch the container must stay active to
        // show one of the two arms, so we keep toggling Then/Else.)
        token.Conds.Add(new CondBinding { When = cond.When, Then = elseGo == null ? go : thenGo, Else = elseGo });
    }

    // Variable-length list bounded by Slots.Count; first VisibleCount() slots shown via SetActive.
    private void BuildList(ListElement list, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("List", parent);
        UGuiPrimitives.AddLayout(go, gap: 2f, columns: list.Columns); ExpandColumnWidth(go);
        // Multi-column cell-size override (else the GridLayoutGroup uses the framework default 120×34, too narrow
        // for an icon+label+value row — StatInspector mini-HUD).
        if (list.Columns > 1 && list.CellWidth > 0f && go.GetComponent<UnityEngine.UI.GridLayoutGroup>() is { } grid)
            grid.cellSize = new Vector2(list.CellWidth, list.CellHeight > 0f ? list.CellHeight : grid.cellSize.y);
        var slots = new GameObject[list.Slots.Count];
        for (var i = 0; i < list.Slots.Count; i++)
        {
            var slot = UGuiPrimitives.NewChild("Slot", go.transform);
            UGuiPrimitives.AddLayout(slot, gap: 0f, columns: UGuiPrimitives.ColumnMode); ExpandColumnWidth(slot);
            BuildElement(list.Slots[i], slot.transform, token);
            slots[i] = slot;
        }
        token.Lists.Add(new ListBinding { Count = list.VisibleCount, Slots = slots });
    }
}
