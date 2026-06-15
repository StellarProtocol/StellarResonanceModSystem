namespace Stellar.Abstractions.Services;

/// <summary>Pure scroll-windowing math for <see cref="VirtualListElement"/> (BCL only, unit-tested on CI).
/// The renderer maps a ScrollRect's content offset to the first logical row to bind.</summary>
public static class VirtualListMath
{
    /// <summary>First logical index to render given the vertical scroll offset (px from the top of the
    /// content), the fixed row height, the total logical count, and the rendered pool size. Clamped so the
    /// window [first, first+pool) never runs past the end (or below 0).</summary>
    public static int FirstIndex(float scrollY, float rowHeight, int count, int poolSize)
    {
        if (rowHeight <= 0f || count <= poolSize) return 0;
        var raw = (int)System.Math.Floor(scrollY / rowHeight);
        var max = count - poolSize;
        if (raw < 0) return 0;
        return raw > max ? max : raw;
    }

    /// <summary>Total content height the scroll viewport must span (so the scrollbar reflects the full list).</summary>
    public static float ContentHeight(int count, float rowHeight) => count <= 0 ? 0f : count * rowHeight;
}
