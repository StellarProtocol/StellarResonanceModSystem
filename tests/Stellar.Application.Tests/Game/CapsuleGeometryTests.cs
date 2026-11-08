using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Regression pin — <c>SliderElement.SquareHandle</c> knobs larger than 14 px drew as rounded SQUARES (Dungeon Hub,
/// HandleSize 22): the capsule sprite's 9-slice corners are a fixed 7 texels. The multiplier must scale each corner
/// to exactly half the knob side (border units = 7 / multiplier), so the two arcs meet = a circle at any size.
/// </summary>
public class CapsuleGeometryTests
{
    [Theory]
    [InlineData(7f)]
    [InlineData(13f)]
    [InlineData(14f)]
    [InlineData(22f)]
    [InlineData(40f)]
    public void Corner_is_half_the_side(float side)
    {
        var m = CapsuleGeometry.CircleMultiplier(side);
        Assert.Equal(side / 2f, CapsuleGeometry.Radius / m, 3);
    }

    [Fact]
    public void Fourteen_px_is_the_native_size() => Assert.Equal(1f, CapsuleGeometry.CircleMultiplier(14f), 5);

    [Fact]
    public void Non_positive_side_falls_back_to_one() => Assert.Equal(1f, CapsuleGeometry.CircleMultiplier(0f));
}
