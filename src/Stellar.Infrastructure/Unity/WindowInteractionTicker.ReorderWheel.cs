using Stellar.Infrastructure.Game;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

// Mouse wheel while drag-reordering (fw #162, 2.31.0). LockScroll (.Reorder.cs) switches the enclosing ScrollRect's axes
// off for the whole press so the EventSystem drag can't scroll the list under the cursor — and ScrollRect.OnScroll's
// SetContentAnchoredPosition skips a disabled axis, so the wheel went inert too: a row could only be carried past the
// visible area by parking it at the edge (8 rows/s auto-scroll). While a drag is ACTIVE we now apply the wheel ourselves,
// moving the content directly — the same path edge auto-scroll uses — with the ScrollRect's own wheel step
// (scrollSensitivity, 24 on every framework scroll view) and its exact direction (OnScroll: delta.y = −scrollDelta.y).
// The lock stays: the press-drag still can't scroll, and our move can't double-apply (the locked ScrollRect ignores the
// wheel). Ghost + insertion bar follow because TickReorder re-places both from the cell rects every frame after this;
// a virtual list re-windows at its ~10 Hz apply exactly as on a normal wheel scroll (SyncVirtualSource moves the dim).
// Works for ListElement and VirtualListElement alike (List is the scroll content for a virtual list, so _rlScroll is its
// own ScrollRect). Framework scroll views are vertical-only, so only the vertical axis is driven.
public sealed partial class WindowInteractionTicker
{
    private void TickReorderWheel(WindowBuilder.ReorderListReg reg, Vector2 mp)
    {
        var wheel = Input.mouseScrollDelta.y;
        if (wheel == 0f) return;                         // the common frame: no hit-test, no work
        var sr = _rlScroll;
        if (sr == null || !_rlScrollV) return;           // not in a scroll view, or one that never scrolled vertically
        var content = sr.content;
        var vp = sr.viewport != null ? sr.viewport : sr.GetComponent<RectTransform>();
        if (content == null || vp == null) return;
        // Same owner rule as every other ticker hit-test: the wheel belongs to this list only while the pointer is over
        // its viewport and no window in front of it owns that point.
        if (_rlZone == null || !RectTransformUtility.RectangleContainsScreenPoint(_rlZone, mp, null)) return;
        if (FrontWindowBlocks(mp, FindWindowRoot(reg.List))) return;
        var pos = content.anchoredPosition;
        var y = VirtualReorderMath.ClampScroll(pos.y + VirtualReorderMath.WheelScrollDelta(wheel, sr.scrollSensitivity),
            content.rect.height, vp.rect.height);
        if (y != pos.y) content.anchoredPosition = new Vector2(pos.x, y);
    }
}
