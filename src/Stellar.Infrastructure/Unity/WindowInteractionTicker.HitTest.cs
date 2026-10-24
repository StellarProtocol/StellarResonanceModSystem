using Stellar.Infrastructure.Game;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

// Hit-tests of the window interaction ticker (split out of WindowInteractionTicker.cs for the 500-LoC gate — a pure
// move, no behaviour change): slot drag source/target, resize grips, scrollbar + voice-cell exclusion zones, window
// drag handles, picker areas, and the z-order FrontWindowBlocks check every one of them must call.
public sealed partial class WindowInteractionTicker
{
    // SOURCE hit-test (mouse-down): the cell must be active AND pass CanDrag — so a drag only STARTS from a
    // draggable, occupied slot (CanDrag gates leader/Raid20/occupancy).
    private int HitSlot(Vector3 mp)
    {
        for (var i = 0; i < DragSlots.Count; i++)
        {
            var (cell, _, canDrag, _) = DragSlots[i];
            if (cell == null || !cell.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.RectangleContainsScreenPoint(cell, mp, null) || OutsideClip(cell, mp)) continue;
            try { if (canDrag != null && !canDrag()) continue; } catch { continue; }
            if (FrontWindowBlocks(mp, FindWindowRoot(cell))) continue;
            return i;
        }
        return -1;
    }

    // TARGET hit-test (hover + drop): any active registered cell under the cursor — NOT gated on CanDrag, so an
    // EMPTY slot is a valid drop destination (CanDrag's occupancy check only restricts the source). A drag is
    // already in progress (started from a CanDrag source), so every active cell in that grid is a legal target.
    private int HitDropTarget(Vector3 mp)
    {
        for (var i = 0; i < DragSlots.Count; i++)
        {
            var cell = DragSlots[i].Cell;
            if (cell == null || !cell.gameObject.activeInHierarchy) continue;
            if (RectTransformUtility.RectangleContainsScreenPoint(cell, mp, null) && !OutsideClip(cell, mp)) return i;
        }
        return -1;
    }

    private int HitResizeGrip(Vector3 mp)
    {
        for (var i = 0; i < DragResizers.Count; i++)
        {
            // Edit-only windows (pinned overlays) resize only while layout edit-mode is active — mirrors the
            // move gate in HitWindowHandle. Free-drag dialogs (EditOnly=false) resize any time. When gated off
            // the grip isn't hit-testable, so the press falls through to whatever's behind it.
            if (DragResizers[i].EditOnly && !LayoutEditGate.IsEditing) continue;
            // Per-plugin edit filter (same rule as the outline/select): an edit-only grip resizes only while its window passes it.
            if (DragResizers[i].EditOnly && LayoutEditGate.EditableWindowFilter != null && !LayoutEditGate.EditableWindowFilter(DragResizers[i].WindowId)) continue;
            if (DragResizers[i].Grip == null || !DragResizers[i].Grip.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.RectangleContainsScreenPoint(DragResizers[i].Grip, mp, null)) continue;
            if (FrontWindowBlocks(mp, DragResizers[i].Target)) continue;
            return i;
        }
        return -1;
    }

    // The bar is only 5px wide — inflate the exclusion zone 6px each side so a slightly-off press still
    // reaches the Scrollbar instead of starting a window drag.
    private bool PointerOnScrollbar(Vector3 mp)
    {
        for (var i = 0; i < ScrollbarRects.Count; i++)
        {
            var rt = ScrollbarRects[i];
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, mp, null, out var lp)) continue;
            var r = rt.rect;
            if (lp.x < r.xMin - 6f || lp.x > r.xMax + 6f || lp.y < r.yMin || lp.y > r.yMax) continue;
            // Only suppress the drag if the scrollbar's own window is the front window at this point —
            // a scrollbar in a back window must not block dragging the front window's titlebar.
            if (FrontWindowBlocks(mp, FindWindowRoot(rt))) continue;
            return true;
        }
        return false;
    }

    // Slot-drag exclusion (mirrors PointerOnScrollbar for the window-drag path): true when the pointer is over a
    // registered, active voice cell. Only cells a row registered while clickable are in the list, so a
    // display-only voice icon never suppresses a slot drag.
    private bool PointerOverVoiceClick(Vector3 mp)
    {
        for (var i = 0; i < VoiceClickRects.Count; i++)
        {
            var rt = VoiceClickRects[i];
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.RectangleContainsScreenPoint(rt, mp, null) || OutsideClip(rt, mp)) continue;
            if (FrontWindowBlocks(mp, FindWindowRoot(rt))) continue;
            return true;
        }
        return false;
    }

    private RectTransform? HitWindowHandle(Vector3 mp)
    {
        for (var i = 0; i < DragWindows.Count; i++)
        {
            // Edit-only windows (overlay/status chromes) drag only while layout edit-mode is active — so they
            // don't move accidentally during play. Popup dialogs (EditOnly=false) drag any time.
            if (DragWindows[i].EditOnly && !LayoutEditGate.IsEditing) continue;
            // Per-plugin edit filter (same rule as the outline/select): an edit-only window drags only while it passes it.
            if (DragWindows[i].EditOnly && LayoutEditGate.EditableWindowFilter != null && !LayoutEditGate.EditableWindowFilter(DragWindows[i].WindowId)) continue;
            if (DragWindows[i].Handle == null) continue;
            if (!RectTransformUtility.RectangleContainsScreenPoint(DragWindows[i].Handle, mp, null)) continue;
            // Don't start a window drag if a higher-Z window covers the pointer — the press belongs to that window.
            if (FrontWindowBlocks(mp, DragWindows[i].Target)) continue;
            return DragWindows[i].Target;
        }
        return null;
    }

    private int HitTest(Vector3 mp)
    {
        for (var i = 0; i < DragAreas.Count; i++)
        {
            var area = DragAreas[i].Area;
            if (area == null) continue;
            if (!PointerHitRule.AcceptsPress(alive: true, area.gameObject.activeInHierarchy,
                    RectTransformUtility.RectangleContainsScreenPoint(area, mp, null) && !OutsideClip(area, mp),
                    () => FrontWindowBlocks(mp, FindWindowRoot(area)))) continue;
            return i;
        }
        return -1;
    }

    // Returns true if any active window with a higher sibling index (= drawn in front) also covers the pointer.
    // Sibling index is set by ReorderWindows() to match ZOrder, so this is a z-order-aware block check.
    internal bool FrontWindowBlocks(Vector2 mp, RectTransform? cellWindowRoot)
    {
        if (cellWindowRoot == null) return false;
        int selfSibling = cellWindowRoot.GetSiblingIndex();
        for (var i = 0; i < WindowRoots.Count; i++)
        {
            var root = WindowRoots[i];
            if (root == null || !root.gameObject.activeInHierarchy) continue;
            if (root.GetSiblingIndex() <= selfSibling) continue;
            if (RectTransformUtility.RectangleContainsScreenPoint(root, mp, null)) return true;
        }
        return false;
    }

    // Walk up a cell's hierarchy to find its direct-canvas-child RectTransform (the window root).
    // All Stellar windows are direct children of the canvas root, so we stop one level below the canvas root.
    internal static RectTransform? FindWindowRoot(RectTransform cell)
    {
        Transform t = cell;
        while (t != null && t.parent != null && t.parent.parent != null)
            t = t.parent;
        return t as RectTransform;
    }
}
