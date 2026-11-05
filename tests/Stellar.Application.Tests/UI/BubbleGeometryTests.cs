using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

public sealed class BubbleGeometryTests
{
    // The sliced fill's min size is 2r+2, so the round radius for side S is S/2 − 1 (28 px pill → 13).
    [Theory]
    [InlineData(28f, 13f)]
    [InlineData(24f, 11f)]
    [InlineData(2f, 0f)]      // too small to round: no radius
    [InlineData(0f, 0f)]
    public void MaxRoundRadius_is_half_side_minus_one(float side, float expected)
        => Assert.Equal(expected, BubbleGeometry.MaxRoundRadius(side));

    [Theory]
    [InlineData(0f, 0f)]      // 0 = default chip rounding, unchanged
    [InlineData(-3f, 0f)]
    [InlineData(float.NaN, 0f)]
    [InlineData(8f, 8f)]      // below the cap: used as given
    [InlineData(13f, 13f)]
    [InlineData(14f, 13f)]    // the old "half the height" advice still gives an exact 28 px capsule
    [InlineData(100f, 13f)]
    public void CapRadius_clamps_to_round_radius_of_28px_pill(float requested, float expected)
        => Assert.Equal(expected, BubbleGeometry.CapRadius(requested, 28f));
}
