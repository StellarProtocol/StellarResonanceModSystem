using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

// VirtualListElement.OnReorder / ListElement.OnReorder pure math (the ticker side is IL2CPP-only).
public sealed class VirtualReorderMathTests
{
    [Theory]
    [InlineData(10, 0, 100, 10)]    // slot 0 shows the window's first item
    [InlineData(10, 5, 100, 15)]
    [InlineData(97, 5, 100, -1)]    // past the end of the list → empty slot
    [InlineData(-1, 0, 100, -1)]    // window not bound yet
    [InlineData(0, -1, 100, -1)]
    public void ItemOfSlot_maps_pool_slot_to_item(int first, int slot, int count, int expected)
        => Assert.Equal(expected, VirtualReorderMath.ItemOfSlot(first, slot, count));

    [Theory]
    [InlineData(10, 10, 8, 0)]
    [InlineData(10, 17, 8, 7)]
    [InlineData(10, 18, 8, -1)]     // below the pool window (slot recycled away)
    [InlineData(10, 9, 8, -1)]      // above it
    [InlineData(-1, 3, 8, -1)]
    public void SlotOfItem_maps_item_back_to_its_slot(int first, int item, int pool, int expected)
        => Assert.Equal(expected, VirtualReorderMath.SlotOfItem(first, item, pool));

    [Fact]
    public void Slot_and_item_mappings_round_trip_across_window_moves()
    {
        for (var first = 0; first < 20; first++)
            for (var slot = 0; slot < 8; slot++)
            {
                var item = VirtualReorderMath.ItemOfSlot(first, slot, 100);
                Assert.Equal(slot, VirtualReorderMath.SlotOfItem(first, item, 8));
            }
    }

    [Theory]
    [InlineData(2, 0, 5, 0)]        // drop before item 0
    [InlineData(0, 5, 5, 4)]        // drop after the last item → last
    [InlineData(1, 4, 5, 3)]        // insertion past the source shifts down by one (remove-then-insert)
    [InlineData(3, 1, 5, 1)]
    [InlineData(40, 90, 100, 89)]   // far beyond the visible pool (virtual list)
    public void RemoveInsertTo_uses_remove_then_insert_indices(int from, int insertion, int count, int expected)
        => Assert.Equal(expected, VirtualReorderMath.RemoveInsertTo(from, insertion, count));

    [Theory]
    [InlineData(2, 2, 5)]           // before itself
    [InlineData(2, 3, 5)]           // after itself
    [InlineData(2, -1, 5)]          // outside the list (cancel)
    [InlineData(5, 0, 5)]           // source no longer exists
    [InlineData(-1, 0, 5)]
    public void RemoveInsertTo_is_no_op_for_same_place_or_invalid(int from, int insertion, int count)
        => Assert.Equal(-1, VirtualReorderMath.RemoveInsertTo(from, insertion, count));

    [Fact]
    public void AutoScroll_is_zero_in_the_middle_and_outside_the_viewport()
    {
        Assert.Equal(0f, VirtualReorderMath.AutoScrollDelta(100f, 280f, 28f, 0.1f));
        Assert.Equal(0f, VirtualReorderMath.AutoScrollDelta(-1f, 280f, 28f, 0.1f));
        Assert.Equal(0f, VirtualReorderMath.AutoScrollDelta(281f, 280f, 28f, 0.1f));
        Assert.Equal(0f, VirtualReorderMath.AutoScrollDelta(28f, 280f, 28f, 0.1f));   // exactly on the band's inner edge
    }

    [Fact]
    public void AutoScroll_scrolls_up_near_top_and_down_near_bottom_scaling_with_proximity()
    {
        var nearTop = VirtualReorderMath.AutoScrollDelta(14f, 280f, 28f, 0.1f);
        var atTop = VirtualReorderMath.AutoScrollDelta(0f, 280f, 28f, 0.1f);
        var nearBottom = VirtualReorderMath.AutoScrollDelta(280f - 14f, 280f, 28f, 0.1f);
        Assert.True(nearTop < 0f);
        Assert.True(atTop < nearTop);                       // faster at the edge
        Assert.True(nearBottom > 0f);
        Assert.Equal(-nearTop, nearBottom, 3);              // symmetric
        // At the edge: AutoScrollRowsPerSec rows/s × row height × dt.
        Assert.Equal(-VirtualReorderMath.AutoScrollRowsPerSec * 28f * 0.1f, atTop, 3);
    }

    [Fact]
    public void AutoScroll_band_is_capped_at_half_a_short_viewport()
    {
        // 40-unit viewport, 28 band → band clamps to 20: the middle point (20) scrolls neither way.
        Assert.Equal(0f, VirtualReorderMath.AutoScrollDelta(20f, 40f, 28f, 0.1f));
        Assert.True(VirtualReorderMath.AutoScrollDelta(5f, 40f, 28f, 0.1f) < 0f);
        Assert.True(VirtualReorderMath.AutoScrollDelta(35f, 40f, 28f, 0.1f) > 0f);
    }

    [Theory]
    [InlineData(-5f, 1000f, 280f, 0f)]
    [InlineData(500f, 1000f, 280f, 500f)]
    [InlineData(900f, 1000f, 280f, 720f)]
    [InlineData(50f, 200f, 280f, 0f)]   // content shorter than the viewport → no scroll range
    public void ClampScroll_keeps_offset_in_range(float y, float contentH, float viewportH, float expected)
        => Assert.Equal(expected, VirtualReorderMath.ClampScroll(y, contentH, viewportH));

    // fw #162: the wheel during a reorder drag must feel like the list's normal wheel — uGUI ScrollRect.OnScroll
    // negates scrollDelta.y and scales by scrollSensitivity (24 on framework scroll views): wheel-up → toward the top.
    [Theory]
    [InlineData(1f, 24f, -24f)]    // one notch up → 24 units toward the top
    [InlineData(-1f, 24f, 24f)]    // one notch down → 24 units toward the bottom
    [InlineData(-3f, 24f, 72f)]    // a fast spin delivers several notches in one frame
    [InlineData(0f, 24f, 0f)]
    public void WheelScrollDelta_matches_the_scroll_rect_wheel_step(float wheelY, float sensitivity, float expected)
        => Assert.Equal(expected, VirtualReorderMath.WheelScrollDelta(wheelY, sensitivity));

    [Fact]
    public void WheelScroll_is_clamped_at_both_ends()
    {
        // at the top, wheel-up stays at 0; at the bottom (1000 − 280 = 720), wheel-down stays at 720
        Assert.Equal(0f, VirtualReorderMath.ClampScroll(0f + VirtualReorderMath.WheelScrollDelta(1f, 24f), 1000f, 280f));
        Assert.Equal(720f, VirtualReorderMath.ClampScroll(710f + VirtualReorderMath.WheelScrollDelta(-1f, 24f), 1000f, 280f));
    }
}
