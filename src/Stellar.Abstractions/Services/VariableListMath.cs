namespace Stellar.Abstractions.Services;

/// <summary>Pure variable-height scroll-windowing math for a measured <see cref="VirtualListElement"/>
/// (BCL only, unit-tested on CI). The uniform-height sibling is <see cref="VirtualListMath"/>; this one
/// drives a list whose rows have DIFFERENT heights (wrapping chat bubbles), so the renderer maintains a
/// per-index height model and a cumulative-offset array instead of multiplying by one fixed row height.
/// Row <c>Count</c> is bounded (≤ a couple hundred), so rebuilding the whole prefix-sum each time a
/// measurement lands is cheap and keeps the model trivially correct.</summary>
public static class VariableListMath
{
    /// <summary>Rebuild the cumulative top-edge offsets. <paramref name="offsets"/> MUST have length
    /// ≥ <paramref name="count"/> + 1; on return <c>offsets[i]</c> is the distance from the content top to
    /// row <c>i</c>'s TOP edge, and <c>offsets[count]</c> is the total content height (also the return value).
    /// A row uses its measured height when <c>measured[i]</c> is true, else the shared <paramref name="estimate"/>
    /// — so an un-shown row contributes an approximate height that refines the moment it is first measured.
    /// Negative heights are floored to 0 (a not-yet-laid-out row never shrinks the content).</summary>
    public static float BuildOffsets(int count, float[] offsets, float[] heights, bool[] measured, float estimate)
    {
        if (offsets.Length < 1) return 0f;
        offsets[0] = 0f;
        if (count < 0) count = 0;
        for (var i = 0; i < count; i++)
        {
            var h = (i < measured.Length && measured[i] && i < heights.Length) ? heights[i] : estimate;
            if (h < 0f) h = 0f;
            offsets[i + 1] = offsets[i] + h;
        }
        return offsets[count];
    }

    /// <summary>First (topmost) logical row to render given the vertical scroll offset (px from the content
    /// top) and the cumulative <paramref name="offsets"/> (as filled by <see cref="BuildOffsets"/>). Returns the
    /// first row whose BOTTOM edge (<c>offsets[i+1]</c>) is past <paramref name="scrollY"/> — i.e. the first
    /// at-least-partially-visible row — then clamps the window <c>[first, first+poolSize)</c> so it never runs
    /// past the end: <c>first ≤ count − poolSize</c>, so scrolling to the bottom always renders the FINAL
    /// <paramref name="poolSize"/> rows (the newest). <c>count ≤ poolSize</c> ⇒ 0 (everything fits the pool).</summary>
    public static int FirstIndex(float scrollY, float[] offsets, int count, int poolSize)
    {
        if (count <= poolSize || count <= 0) return 0;
        var max = count - poolSize;
        if (scrollY <= 0f) return 0;
        // Linear scan: count is small (≤ ~200) so this is cheaper than the branch-mispredict of a binary search,
        // and it mirrors the straight-line clarity of the uniform VirtualListMath.
        var i = 0;
        while (i < count && offsets[i + 1] <= scrollY) i++;
        if (i > max) i = max;
        return i < 0 ? 0 : i;
    }

    /// <summary>The maximum scroll offset: how far the content can move up before its bottom aligns with the
    /// viewport bottom. 0 when the content is shorter than the viewport (nothing to scroll). Drives both the
    /// stick-to-bottom pin and the "is the user currently at the bottom?" test.</summary>
    public static float MaxScroll(float totalHeight, float viewportHeight)
        => totalHeight <= viewportHeight ? 0f : totalHeight - viewportHeight;
}
