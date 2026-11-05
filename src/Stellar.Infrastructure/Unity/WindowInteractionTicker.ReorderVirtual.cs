using Stellar.Infrastructure.Game;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

// VirtualListElement.OnReorder — the virtual-list half of drag-reorder (.Reorder.cs owns the gesture). A uniform
// virtual list renders a small POOL of slots over Count() items, and slot k shows item First + k (VirtualListBinding).
// So the drag tracks its ITEM index, and every frame:
//  • the cell ↔ item mapping is re-derived from the binding's current First (VirtualReorderMath), so presses, the
//    insertion bar and the drop all speak item indices even while slots are recycled;
//  • the dimmed source follows its item: when the slot showing it is recycled (or it scrolls out of the pool) the old
//    slot's alpha is restored and the new one (if any) dimmed;
//  • holding the pointer within one row of the viewport's top/bottom edge auto-scrolls the content (the ScrollRect's
//    own axes stay locked for the press — we move Content.anchoredPosition directly, clamped). The binding re-windows
//    at its ~10 Hz apply; the speed cap keeps travel per apply inside the pool's margin rows.
// For a ListElement (Virtual == null) every helper is the identity, so that path is unchanged.
public sealed partial class WindowInteractionTicker
{
    // Cells to scan: every pool slot for a virtual list (ItemOfCell filters the empty ones); the live items otherwise.
    private static int CellCount(WindowBuilder.ReorderListReg reg)
        => reg.Virtual != null ? reg.Cells.Length : LiveCount(reg);

    // Item index shown by cell i (-1 = none). Identity for a ListElement.
    private static int ItemOfCell(WindowBuilder.ReorderListReg reg, int i)
        => reg.Virtual is { } v ? VirtualReorderMath.ItemOfSlot(v.First, i, LiveCount(reg)) : i;

    // Cell showing item `item` (-1 = not in the pool window). Identity for a ListElement.
    private static int CellOfItem(WindowBuilder.ReorderListReg reg, int item)
        => reg.Virtual is { } v ? VirtualReorderMath.SlotOfItem(v.First, item, reg.Cells.Length) : item;

    // Keep the dim on the slot that shows the dragged item (it moves when the window re-binds during auto-scroll).
    private void SyncVirtualSource(WindowBuilder.ReorderListReg reg)
    {
        var idx = CellOfItem(reg, _rlFrom);
        var want = idx >= 0 ? reg.Cells[idx] : null;
        if (want == _rlSrcCell) return;
        if (_rlSrcGroup != null) _rlSrcGroup.alpha = _rlSrcAlpha;
        _rlSrcGroup = null; _rlSrcCell = null;
        if (want != null) DimSource(want);
    }

    // Edge auto-scroll while dragging (pointer inside the viewport, within one row of its top/bottom edge).
    private void TickAutoScroll(WindowBuilder.ReorderListReg reg, Vector2 mp)
    {
        var v = reg.Virtual;
        var vp = v?.Viewport;
        if (v == null || vp == null || v.Content == null) return;
        if (!RectTransformUtility.RectangleContainsScreenPoint(vp, mp, null)) return;
        if (FrontWindowBlocks(mp, FindWindowRoot(reg.List))) return;   // a front window owns this point
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(vp, mp, null, out var lp)) return;
        var r = vp.rect;
        var dy = VirtualReorderMath.AutoScrollDelta(r.yMax - lp.y, r.height, v.RowHeight, Time.unscaledDeltaTime);
        if (dy == 0f) return;
        var pos = v.Content.anchoredPosition;
        var y = VirtualReorderMath.ClampScroll(pos.y + dy, v.Content.rect.height, r.height);
        if (y != pos.y) v.Content.anchoredPosition = new Vector2(pos.x, y);
    }
}
