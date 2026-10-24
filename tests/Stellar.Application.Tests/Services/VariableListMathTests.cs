using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

public class VariableListMathTests
{
    // Helper: build offsets for `count` rows, all measured at the given heights.
    private static float[] OffsetsOf(params float[] heights)
    {
        var count = heights.Length;
        var measured = new bool[count];
        for (var i = 0; i < count; i++) measured[i] = true;
        var offsets = new float[count + 1];
        VariableListMath.BuildOffsets(count, offsets, heights, measured, 0f);
        return offsets;
    }

    [Fact]
    public void BuildOffsets_is_a_cumulative_prefix_sum()
    {
        var off = OffsetsOf(10f, 20f, 30f, 40f);
        Assert.Equal(new[] { 0f, 10f, 30f, 60f, 100f }, off);
    }

    [Fact]
    public void BuildOffsets_uses_the_estimate_for_unmeasured_rows()
    {
        // rows 0 and 2 measured (10, 30); rows 1 and 3 unmeasured → use estimate 25.
        var heights = new[] { 10f, 0f, 30f, 0f };
        var measured = new[] { true, false, true, false };
        var off = new float[5];
        var total = VariableListMath.BuildOffsets(4, off, heights, measured, 25f);
        Assert.Equal(new[] { 0f, 10f, 35f, 65f, 90f }, off);
        Assert.Equal(90f, total);
    }

    [Fact]
    public void BuildOffsets_floors_negative_heights_to_zero()
    {
        var heights = new[] { 10f, -5f, 20f };
        var measured = new[] { true, true, true };
        var off = new float[4];
        VariableListMath.BuildOffsets(3, off, heights, measured, 0f);
        Assert.Equal(new[] { 0f, 10f, 10f, 30f }, off);
    }

    [Theory]
    // 5 rows of height 20 (offsets 0,20,40,60,80,100), pool 3 → max first = 2.
    [InlineData(0f, 0)]      // top
    [InlineData(19f, 0)]     // still inside row 0
    [InlineData(20f, 1)]     // row 0's bottom reached → row 1 is first partially visible
    [InlineData(45f, 2)]     // inside row 2 … but clamped to max (count-pool = 2) anyway
    [InlineData(9999f, 2)]   // bottom → last window shows rows 2,3,4
    [InlineData(-30f, 0)]    // negative (overshoot) → clamp to 0
    public void FirstIndex_finds_the_topmost_visible_row_then_clamps(float scrollY, int expected)
    {
        var off = OffsetsOf(20f, 20f, 20f, 20f, 20f);
        Assert.Equal(expected, VariableListMath.FirstIndex(scrollY, off, count: 5, poolSize: 3));
    }

    [Fact]
    public void FirstIndex_when_count_le_pool_is_zero()
    {
        var off = OffsetsOf(20f, 20f, 20f);
        Assert.Equal(0, VariableListMath.FirstIndex(9999f, off, count: 3, poolSize: 5));
    }

    // Variable heights: a tall first row pushes the windowing — bottom scroll must still reach the final rows.
    [Fact]
    public void FirstIndex_with_mixed_heights_reaches_the_last_pool_window()
    {
        // heights 100,20,20,20,20,20 → offsets 0,100,120,140,160,180,200; total 200.
        var off = OffsetsOf(100f, 20f, 20f, 20f, 20f, 20f);
        const int count = 6, pool = 3;
        var maxScroll = VariableListMath.MaxScroll(200f, 60f);   // viewport 60 → 140
        Assert.Equal(140f, maxScroll);
        var first = VariableListMath.FirstIndex(maxScroll, off, count, pool);
        Assert.Equal(3, first);               // window = rows 3,4,5 → the newest row is rendered
        Assert.Equal(count, first + pool);    // …and the window ends exactly at the list's end
    }

    [Theory]
    [InlineData(200f, 60f, 140f)]
    [InlineData(60f, 60f, 0f)]     // content exactly fits → no scroll
    [InlineData(40f, 60f, 0f)]     // content shorter than viewport → no scroll
    public void MaxScroll_is_content_minus_viewport_floored_at_zero(float total, float viewport, float expected)
        => Assert.Equal(expected, VariableListMath.MaxScroll(total, viewport));

    // StableSlots: row l lives in slot l % pool, so the window covers each slot once and a one-row shift moves only
    // the slot whose row left (here: first 10 → 11 recycles slot 10 % 4 = 2 from row 10 to row 14).
    [Fact]
    public void StableSlotRow_keeps_rows_in_their_slot_across_a_shift()
    {
        const int pool = 4;
        int[] Window(int first)
        {
            var rows = new int[pool];
            for (var k = 0; k < pool; k++) rows[k] = VariableListMath.StableSlotRow(first, k, pool);
            return rows;
        }
        Assert.Equal(new[] { 12, 13, 10, 11 }, Window(10));
        Assert.Equal(new[] { 12, 13, 14, 11 }, Window(11));
        Assert.Equal(new[] { 0, 1, 2, 3 }, Window(0));
        Assert.Equal(5, VariableListMath.StableSlotRow(5, 0, 0));   // no pool → plain first + slot
    }
}
