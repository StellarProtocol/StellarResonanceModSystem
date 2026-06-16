using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

public sealed class ChartGeometryTests
{
    [Fact]
    public void NiceYMax_rounds_up_on_125_ladder()
    {
        Assert.Equal(2f, ChartGeometry.NiceYMax(1.3f));
        Assert.Equal(5f, ChartGeometry.NiceYMax(4.1f));
        Assert.Equal(20000f, ChartGeometry.NiceYMax(18000f));
    }

    [Theory]
    [InlineData(0f, 1f)]      // non-positive peak floors to 1
    [InlineData(-5f, 1f)]
    [InlineData(1f, 1f)]      // exact ladder values stay put
    [InlineData(2f, 2f)]
    [InlineData(5f, 5f)]
    [InlineData(10f, 10f)]
    [InlineData(6f, 10f)]     // above 5 → next decade
    [InlineData(640f, 1000f)]
    public void NiceYMax_handles_edges(float peak, float expected)
        => Assert.Equal(expected, ChartGeometry.NiceYMax(peak));

    [Fact]
    public void ValueToY_clamps_and_scales()
    {
        Assert.Equal(0f, ChartGeometry.ValueToY(0f, 100f, 0f, 200f));
        Assert.Equal(200f, ChartGeometry.ValueToY(100f, 100f, 0f, 200f));
        Assert.Equal(200f, ChartGeometry.ValueToY(150f, 100f, 0f, 200f)); // clamped
        Assert.Equal(100f, ChartGeometry.ValueToY(50f, 100f, 0f, 200f));  // midpoint
    }

    [Fact]
    public void ValueToY_zero_yMax_floors_to_bottom()
        => Assert.Equal(10f, ChartGeometry.ValueToY(50f, 0f, 10f, 200f));

    [Fact]
    public void BucketToX_maps_endpoints_and_midpoint()
    {
        Assert.Equal(0f, ChartGeometry.BucketToX(0, 0, 4, 0f, 400f));
        Assert.Equal(400f, ChartGeometry.BucketToX(4, 0, 4, 0f, 400f));
        Assert.Equal(200f, ChartGeometry.BucketToX(2, 0, 4, 0f, 400f));
    }

    [Fact]
    public void BucketToX_degenerate_window_pins_to_x0()
        => Assert.Equal(50f, ChartGeometry.BucketToX(3, 3, 3, 50f, 400f));

    [Fact]
    public void VisiblePeak_takes_max_across_series_within_window()
    {
        var series = new List<ChartSeries>
        {
            new("A", default, new float[] { 10f, 40f, 30f, 60f, 50f }),
            new("B", default, new float[] { 80f, 20f, 15f, 30f, 25f }),
        };
        // window [1,3] excludes B's 80 at index 0 → peak is A's 60.
        Assert.Equal(60f, ChartGeometry.VisiblePeak(series, 1, 3));
        // full window includes the 80.
        Assert.Equal(80f, ChartGeometry.VisiblePeak(series, 0, 4));
    }

    [Fact]
    public void VisiblePeak_clamps_window_to_series_bounds()
    {
        var series = new List<ChartSeries> { new("A", default, new float[] { 5f, 9f, 7f }) };
        // maxBucket past the end and minBucket negative are tolerated.
        Assert.Equal(9f, ChartGeometry.VisiblePeak(series, -2, 99));
    }
}
