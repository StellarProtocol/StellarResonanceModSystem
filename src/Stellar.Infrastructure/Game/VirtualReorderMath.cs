using System;

namespace Stellar.Infrastructure.Game;

// Pure index/scroll math for drag-reordering (ListElement.OnReorder and VirtualListElement.OnReorder), kept free of
// UnityEngine types so it is unit-testable (Stellar.Application.Tests) — the ticker code that calls it is IL2CPP-only.
//
// Virtual lists recycle a small pool of slots: on the uniform path pool slot k shows item `first + k` (first = the
// window's first item index, VirtualListBinding.First). A drag is identified by its ITEM index, so the slot showing
// it is re-derived every frame and the drag survives the slot being recycled while auto-scrolling.
internal static class VirtualReorderMath
{
    /// <summary>Max auto-scroll speed, in rows per second (at the very edge of the viewport). Kept low enough that the
    /// list's ~10 Hz rebind keeps up: at most ~1 row of travel between rebinds, inside the pool's margin rows.</summary>
    internal const float AutoScrollRowsPerSec = 8f;

    /// <summary>Item index shown by pool slot <paramref name="slot"/>, or -1 when the slot shows nothing (window not
    /// bound yet, or past the end of the list).</summary>
    internal static int ItemOfSlot(int first, int slot, int count)
    {
        if (first < 0 || slot < 0) return -1;
        var item = first + slot;
        return item < count ? item : -1;
    }

    /// <summary>Pool slot currently showing item <paramref name="item"/>, or -1 when it is outside the bound window.</summary>
    internal static int SlotOfItem(int first, int item, int pool)
    {
        if (first < 0 || item < 0) return -1;
        var slot = item - first;
        return slot >= 0 && slot < pool ? slot : -1;
    }

    /// <summary>Remove-then-insert target for a drop at insertion slot <paramref name="insertion"/> (0..count, in
    /// ORIGINAL indexing: "before item i"), or -1 for a no-op / invalid drop (the callback must not fire).</summary>
    internal static int RemoveInsertTo(int from, int insertion, int count)
    {
        if (insertion < 0 || from < 0 || from >= count) return -1;
        var to = Math.Clamp(insertion > from ? insertion - 1 : insertion, 0, count - 1);
        return to == from ? -1 : to;
    }

    /// <summary>Auto-scroll delta (content units; negative = toward the top) for this frame while dragging.
    /// <paramref name="fromTop"/> = the pointer's distance below the viewport's top edge, in the viewport's local
    /// units; <paramref name="band"/> = the edge zone (one row). Inside the band the speed scales linearly from 0 at
    /// the band's inner edge to <see cref="AutoScrollRowsPerSec"/> rows/s at the viewport edge. Outside the viewport
    /// (a drop there cancels) or in its middle: 0. A band larger than half the viewport is clamped to half, so the
    /// two zones never overlap.</summary>
    internal static float AutoScrollDelta(float fromTop, float viewportH, float band, float dt)
    {
        if (viewportH <= 0f || band <= 0f || dt <= 0f || fromTop < 0f || fromTop > viewportH) return 0f;
        band = Math.Min(band, viewportH * 0.5f);
        var speed = AutoScrollRowsPerSec * band;   // the band is one row, so rows/s × row height = units/s
        var fromBottom = viewportH - fromTop;
        if (fromTop < band) return -speed * (1f - fromTop / band) * dt;
        if (fromBottom < band) return speed * (1f - fromBottom / band) * dt;
        return 0f;
    }

    /// <summary>Clamp a content scroll offset (0 = top) to the scrollable range.</summary>
    internal static float ClampScroll(float y, float contentH, float viewportH)
        => Math.Clamp(y, 0f, Math.Max(0f, contentH - viewportH));
}
