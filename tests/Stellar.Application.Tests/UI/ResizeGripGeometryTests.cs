using Stellar.Infrastructure.Theme;
using Xunit;

namespace Stellar.Application.Tests.Ui;

// The resize-grip mark follows the window body's corner: arcs on the 14 px GlassMenu frame, brackets on a square body.
// The numbers are the approved reference SVGs (corner at 40,40): rounded outer `M36.5 27.5 a9 9`, inner `M33 27.5 a5.5 5.5`;
// square outer `M36.5 28 V36.5 H28`, inner `M33 31.5 V33 H31.5`.
public sealed class ResizeGripGeometryTests
{
    [Fact]
    public void Rounded_frame_gives_the_reference_arcs()
    {
        Assert.Equal(new ResizeGripGeometry.Stroke(3.5f, 9f, 12.5f), ResizeGripGeometry.Outer(14));
        Assert.Equal(new ResizeGripGeometry.Stroke(7f, 5.5f, 12.5f), ResizeGripGeometry.Inner(14));
    }

    [Fact]
    public void Square_body_gives_the_reference_brackets()
    {
        Assert.Equal(new ResizeGripGeometry.Stroke(3.5f, 0f, 12f), ResizeGripGeometry.Outer(0));
        Assert.Equal(new ResizeGripGeometry.Stroke(7f, 0f, 8.5f), ResizeGripGeometry.Inner(0));
    }

    [Fact]
    public void Intermediate_radius_is_a_rounded_L_that_never_grows_past_the_reference()
    {
        var outer = ResizeGripGeometry.Outer(8);
        Assert.Equal(3f, outer.Rho);       // 8 - 1.5 - 3.5
        Assert.Equal(12f, outer.LegEnd);   // arc ends at 6.5 → the square leg end holds
        Assert.Equal(0f, ResizeGripGeometry.Inner(8).Rho);   // inner already a bracket
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    public void Mark_stays_inside_the_body_and_fits_its_texture(int radius)
    {
        // The window corner itself is never painted, and the texture holds the whole antialiased mark.
        Assert.Equal(0f, ResizeGripGeometry.Coverage(radius, 0.5f, 0.5f));
        var n = ResizeGripGeometry.TextureSize(radius);
        for (var i = 0; i < n; i++)
        {
            Assert.Equal(0f, ResizeGripGeometry.Coverage(radius, n - 0.5f, i + 0.5f));
            Assert.Equal(0f, ResizeGripGeometry.Coverage(radius, i + 0.5f, n - 0.5f));
        }
    }

    [Fact]
    public void Rounded_mark_never_crosses_the_rounded_frame_edge()
    {
        // Every painted texel must lie inside the frame's r=14 corner arc (centre 14,14 in corner space).
        var n = ResizeGripGeometry.TextureSize(14);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            float u = x + 0.5f, v = y + 0.5f;
            if (ResizeGripGeometry.Coverage(14, u, v) <= 0f || u >= 14f || v >= 14f) continue;
            var d = System.Math.Sqrt((u - 14) * (u - 14) + (v - 14) * (v - 14));
            Assert.True(d < 14 - 1, $"texel ({u},{v}) is {d:0.00} from the arc centre");
        }
    }

    [Fact]
    public void Stroke_centre_is_fully_covered_and_ends_have_round_caps()
    {
        var outer = ResizeGripGeometry.Outer(14);
        Assert.Equal(1f, ResizeGripGeometry.StrokeCoverage(outer, 3.5f, 12.5f));   // arc start point
        Assert.Equal(0f, ResizeGripGeometry.StrokeCoverage(outer, 3.5f, 14f));     // 1.5 px past the end: cap ended
    }
}
