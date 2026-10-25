using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Structural layout elements: ConditionalElement (SetActive toggle; each branch built on first show) and ListElement
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
        // Branch CONTENT is built lazily — the first time its branch becomes active (CondBinding.Apply), not at
        // mount. A never-shown branch costs nothing: the CombatMeter main window eagerly built its list view AND
        // both party layouts (49 meter rows, ~5k GameObjects via IL2CPP) to show one of them — a 350-476 ms mount
        // (owner report 2026-09-30, [Hitch] measured). The active branch still builds inside the first paint.
        var gate = _condGate;   // enclosing variable-list slot (null = none); a lazy branch build restores it too
        var scope = CaptureScope();
        var then = cond.Then;
        Action buildThen = () => BuildDeferred(then, thenGo.transform, token, scope);
        Action? buildElse = null;
        GameObject? elseGo = null;
        if (cond.Else != null)
        {
            elseGo = UGuiPrimitives.NewChild("Else", go.transform);
            UGuiPrimitives.AddLayout(elseGo, gap: 0f, columns: UGuiPrimitives.ColumnMode); ExpandColumnWidth(elseGo);
            if (cond.Fill && elseGo.GetComponent<VerticalLayoutGroup>() is { } ev) ev.childForceExpandHeight = true;
            var elseEl = cond.Else; var elseT = elseGo.transform;
            buildElse = () => BuildDeferred(elseEl, elseT, token, scope);
        }
        // With NO else-branch, toggle the WHOLE Cond container (not just its inner Then). Otherwise the
        // container stays an active 0-height child and the parent column still adds section-spacing around it —
        // 5 collapsed tab-Conditionals = a big empty gap above the active tab's content. Collapsing the
        // container removes it from the layout entirely. (With an else-branch the container must stay active to
        // show one of the two arms, so we keep toggling Then/Else.)
        token.Conds.Add(new CondBinding
            { When = cond.When, Then = elseGo == null ? go : thenGo, Else = elseGo, BuildThen = buildThen, BuildElse = buildElse, Gate = gate });
    }

    // A deferred branch build runs later, from the window's Apply — restore the per-window surface the shared
    // builder had when this window was built, the enclosing variable-list slot gate (so a Conditional nested in
    // the lazily-built branch is gated on the same slot), and the min-width-row depth (uncapped bubbles).
    // The enclosing bubble (if any) claims the branch's new content bindings, so their later changes re-measure it.
    private void BuildDeferred(HudElement el, Transform parent, WindowToken token, BuildScope scope)
    {
        var prev = CaptureScope();
        RestoreScope(scope);
        var marks = new BindingMarks(token);
        try { BuildElement(el, parent, token); }
        finally { RestoreScope(prev); }
        if (scope.BubbleOwner is { } owner) { TagBubbleOwner(token, marks, owner); owner.MarkDirty(); }
    }

    // The builder state a lazily-built branch must see exactly as it was when its window was built.
    private readonly struct BuildScope
    {
        public readonly SurfaceStyle Surface; public readonly GameObject? Gate; public readonly int WidthRowDepth;
        public readonly BubbleBinding? BubbleOwner;   // innermost enclosing BubbleElement (.BubbleOwner.cs)
        public BuildScope(SurfaceStyle surface, GameObject? gate, int widthRowDepth, BubbleBinding? bubbleOwner)
        { Surface = surface; Gate = gate; WidthRowDepth = widthRowDepth; BubbleOwner = bubbleOwner; }
    }
    private BuildScope CaptureScope() => new(_surface, _condGate, _widthRowDepth, _bubbleOwner);
    private void RestoreScope(BuildScope s)
    { _surface = s.Surface; _condGate = s.Gate; _widthRowDepth = s.WidthRowDepth; _bubbleOwner = s.BubbleOwner; }

    // RowElement: an HLG row (Gap 0 = the default RowGap), optionally justified. A ConstrainsWindowMinWidth row is
    // also recorded on the token so the renderer can derive the window's minimum width from its natural width
    // (WindowToken.MeasureMinWidth / WindowRenderer.ContentMinWidth.cs). Its natural width is the HLG's preferred
    // width — but a BubbleElement inside caps itself at its PARENT's laid-out width, so in a squeezed row each bubble
    // would pin itself narrower, lowering the row's preferred width to ≈ the squeezed width: the measurement would
    // ratchet down with the window and never ask for more room. So bubbles built under a marked row (depth > 0,
    // carried into lazy Conditional branches by BuildDeferred) hug their natural width uncapped (BubbleBinding.NoCap).
    private void BuildRow(RowElement r, Transform parent, WindowToken token)
    {
        if (r.ConstrainsWindowMinWidth) _widthRowDepth++;
        try { BuildLayout(r.Children, parent, token, r.Gap == 0f ? RowGap : r.Gap, UGuiPrimitives.RowMode); }
        finally { if (r.ConstrainsWindowMinWidth) _widthRowDepth--; }
        var row = parent.GetChild(parent.childCount - 1);   // BuildLayout appends the row as the last child
        if (r.ConstrainsWindowMinWidth && row is RectTransform rowRt) token.WidthRows.Add(rowRt);
        // RowElement.OnHover: the plain row rect joins the ticker's hover list (RectangleContainsScreenPoint +
        // FrontWindowBlocks, TickHoversAndPulses) — no Image/raycast/tint is added, so the row looks and lays out as before.
        if (r.OnHover is { } onHover && row is RectTransform hoverRt) _registerHover?.Invoke(hoverRt, onHover);
        if (r.Justify != RowJustify.Left && row.GetComponent<HorizontalLayoutGroup>() is { } hlg)
            hlg.childAlignment = r.Justify == RowJustify.Center ? TextAnchor.MiddleCenter : TextAnchor.MiddleRight;
    }

    // Variable-length list bounded by Slots.Count; first VisibleCount() slots shown via SetActive.
    private void BuildList(ListElement list, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("List", parent);
        UGuiPrimitives.AddLayout(go, gap: 2f, columns: list.Columns); ExpandColumnWidth(go);
        // Multi-column cell-size override (else the GridLayoutGroup uses the framework default 120×34, too narrow
        // for an icon+label+value row — StatInspector mini-HUD).
        if (list.Columns > 1 && list.CellWidth > 0f && go.GetComponent<UnityEngine.UI.GridLayoutGroup>() is { } grid)
        {
            grid.cellSize = new Vector2(list.CellWidth, list.CellHeight > 0f ? list.CellHeight : grid.cellSize.y);
            // FillWidth: flow to fill the container width instead of a fixed column count. Flexible packs as many
            // fixed-cellSize cells per row as the current width allows and wraps to new rows, re-flowing when the
            // window resizes (the fixed-column path above is untouched when FillWidth is false). Keep the fixed
            // cellSize + spacing; UpperLeft so cells pack from the left edge (no centred dead space). Content height
            // still grows with the flowed row count (Flexible reports preferredHeight), so the surrounding Scroll's
            // vertical ContentSizeFitter drives the viewport scroll exactly as before.
            if (list.FillWidth)
            {
                grid.constraint = UnityEngine.UI.GridLayoutGroup.Constraint.Flexible;
                grid.childAlignment = TextAnchor.UpperLeft;
            }
        }
        var slots = new GameObject[list.Slots.Count];
        for (var i = 0; i < list.Slots.Count; i++)
        {
            var slot = UGuiPrimitives.NewChild("Slot", go.transform);
            UGuiPrimitives.AddLayout(slot, gap: 0f, columns: UGuiPrimitives.ColumnMode); ExpandColumnWidth(slot);
            BuildElement(list.Slots[i], slot.transform, token);
            slots[i] = slot;
        }
        token.Lists.Add(new ListBinding { Count = list.VisibleCount, Slots = slots });
        if (list.OnReorder != null && RegisterReorderList != null)
        {
            var cells = new RectTransform[slots.Length];
            for (var i = 0; i < slots.Length; i++) cells[i] = slots[i].GetComponent<RectTransform>();
            RegisterReorderList(new ReorderListReg
            {
                List = go.GetComponent<RectTransform>(), Cells = cells, Count = list.VisibleCount,
                Grid = list.Columns > 1, OnReorder = list.OnReorder, Accent = _assets.MenuAccent,
            });
        }
    }

    // ListElement.OnReorder registration payload (the ticker keeps these; see WindowInteractionTicker.Reorder.cs).
    // Cells are the list's Slot rects in index order — the first Count() of them are the live items.
    internal sealed class ReorderListReg
    {
        public RectTransform List = null!;
        public RectTransform[] Cells = null!;
        public Func<int> Count = null!;
        public bool Grid;   // multi-column → insertion is left/right of a cell; single column → above/below a row
        public Action<int, int> OnReorder = null!;
        public Color Accent;   // insertion-bar colour (the menu accent at build time)
    }
}
