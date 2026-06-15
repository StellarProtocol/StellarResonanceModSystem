using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

public class VirtualListMathTests
{
    [Theory]
    [InlineData(0f, 22f, 100, 10, 0)]
    [InlineData(21f, 22f, 100, 10, 0)]
    [InlineData(22f, 22f, 100, 10, 1)]
    [InlineData(55f, 22f, 100, 10, 2)]
    [InlineData(100000f, 22f, 100, 10, 90)]
    [InlineData(-30f, 22f, 100, 10, 0)]      // negative scroll (elastic overshoot) → clamp to 0
    [InlineData(50f, 0f, 100, 10, 0)]        // zero rowHeight guard → 0 (no divide)
    public void FirstIndex_clamps_to_valid_window(float scrollY, float rowH, int count, int pool, int expected)
        => Assert.Equal(expected, VirtualListMath.FirstIndex(scrollY, rowH, count, pool));

    [Fact]
    public void FirstIndex_when_count_le_pool_is_zero()
        => Assert.Equal(0, VirtualListMath.FirstIndex(9999f, 22f, 5, 10));

    [Fact]
    public void FirstIndex_when_count_equals_pool_is_zero()   // the <= boundary (off-by-one zone)
        => Assert.Equal(0, VirtualListMath.FirstIndex(9999f, 22f, 10, 10));

    [Theory]
    [InlineData(100, 22f, 2200f)]
    [InlineData(0, 22f, 0f)]
    [InlineData(-1, 22f, 0f)]                 // negative count guard → 0
    public void ContentHeight_is_count_times_rowHeight(int count, float rowH, float expected)
        => Assert.Equal(expected, VirtualListMath.ContentHeight(count, rowH));
}
