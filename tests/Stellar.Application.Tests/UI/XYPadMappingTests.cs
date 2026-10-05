using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.UI;

// XYPadElement: pointer position in the pad (normalized, origin at the BOTTOM-left, as uGUI and the window
// interaction ticker report it) <-> the value pair. Y up is positive (the aim pad's up arrow = +y).
public sealed class XYPadMappingTests
{
    [Theory]
    [InlineData(0f, 0f, -1f, -1f)]   // bottom-left
    [InlineData(1f, 1f, 1f, 1f)]     // top-right
    [InlineData(0.5f, 0.5f, 0f, 0f)] // centre
    [InlineData(0.65f, 0.45f, 0.3f, -0.1f)]
    public void Pointer_to_value_default_range(float nx, float ny, float x, float y)
    {
        var (vx, vy) = XYPadMapping.FromPointer(nx, ny, -1f, 1f);
        Assert.Equal(x, vx, 4);
        Assert.Equal(y, vy, 4);
    }

    [Fact]
    public void Up_is_positive_y()
    {
        Assert.True(XYPadMapping.FromPointer(0.5f, 0.9f, -1f, 1f).Y > 0f);
        Assert.True(XYPadMapping.FromPointer(0.5f, 0.1f, -1f, 1f).Y < 0f);
    }

    [Theory]
    [InlineData(-0.5f, 2f, -1f, 1f)]
    [InlineData(float.NaN, 0.5f, -1f, 0f)]
    public void Outside_the_pad_is_clamped(float nx, float ny, float x, float y)
    {
        var (vx, vy) = XYPadMapping.FromPointer(nx, ny, -1f, 1f);
        Assert.Equal(x, vx, 4);
        Assert.Equal(y, vy, 4);
    }

    [Fact]
    public void Custom_range()
    {
        var (x, y) = XYPadMapping.FromPointer(0.25f, 0.75f, 0f, 100f);
        Assert.Equal(25f, x, 4);
        Assert.Equal(75f, y, 4);
    }

    [Theory]
    [InlineData(0.3f, -0.1f, 0.65f, 0.45f)]
    [InlineData(5f, -5f, 1f, 0f)]           // values outside the range sit on the edge
    [InlineData(float.NaN, 0f, 0.5f, 0.5f)] // a NaN value is drawn at the centre
    public void Value_to_dot_position(float x, float y, float nx, float ny)
    {
        var (dx, dy) = XYPadMapping.ToNormalized(x, y, -1f, 1f);
        Assert.Equal(nx, dx, 4);
        Assert.Equal(ny, dy, 4);
    }

    [Fact]
    public void An_empty_range_maps_to_min_and_draws_centred()
    {
        Assert.Equal((2f, 2f), XYPadMapping.FromPointer(0.8f, 0.1f, 2f, 2f));
        Assert.Equal((0.5f, 0.5f), XYPadMapping.ToNormalized(2f, 2f, 2f, 2f));
    }

    [Fact]
    public void Round_trip()
    {
        var (nx, ny) = XYPadMapping.ToNormalized(0.3f, -0.1f, -1f, 1f);
        var (x, y) = XYPadMapping.FromPointer(nx, ny, -1f, 1f);
        Assert.Equal(0.3f, x, 4);
        Assert.Equal(-0.1f, y, 4);
    }
}
