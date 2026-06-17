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
    [InlineData(9.9f, 10f)]   // just under a decade → that decade
    [InlineData(50f, 50f)]    // exact 5×10^1 ladder rung → no-op
    [InlineData(500f, 500f)]  // exact 5×10^2 ladder rung → no-op
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
    public void BucketToX_non_zero_min_window_maps_by_fraction()
    {
        // window [4,8]: bucket 5 is (5-4)/(8-4) = 0.25 of the way across.
        Assert.Equal(100f, ChartGeometry.BucketToX(5, 4, 8, 0f, 400f));
        // endpoints still pin to x0/x1 even when minBucket != 0.
        Assert.Equal(0f, ChartGeometry.BucketToX(4, 4, 8, 0f, 400f));
        Assert.Equal(400f, ChartGeometry.BucketToX(8, 4, 8, 0f, 400f));
    }

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

    [Fact]
    public void NiceYMax_rounds_small_peak_up_to_a_visible_max()
    {
        // The sub-bucket fallback plots a single bucket-0 point carrying the source total (e.g. 4400),
        // which must round to a sane axis max (5000) instead of collapsing to the 1/0/0/0 ladder.
        Assert.Equal(5000f, ChartGeometry.NiceYMax(4400f));
        Assert.Equal(50f, ChartGeometry.NiceYMax(42f));
    }

    [Fact]
    public void ClampBucketWindow_widens_subbucket_range_to_visit_bucket_zero()
    {
        // floor==ceil==0 (a sub-bucket visible range) → widen to [0,1] so bucket 0 is scanned, capped at len-1.
        Assert.Equal((0, 0), ChartGeometry.ClampBucketWindow(0, 0, 1));   // only one bucket → stays at 0
        Assert.Equal((0, 1), ChartGeometry.ClampBucketWindow(0, 0, 3));   // widened to [0,1]
    }

    [Fact]
    public void ClampBucketWindow_clamps_negative_min_and_overflow_max()
    {
        Assert.Equal((0, 2), ChartGeometry.ClampBucketWindow(-5, 99, 3));   // negative min → 0, max → len-1
        // Unknown series length (0): no upper bound to clamp against, so the widened window passes through
        // (VisiblePeak tolerates over-range windows); only the min is floored to 0.
        Assert.Equal((0, 5), ChartGeometry.ClampBucketWindow(-2, 5, 0));
    }

    // Regression: the real in-game "1/0/0/0" Combat-History bug (entry 0). The team-total series for the
    // user's session was [4588, 8856] (bucketMs 1000 → BucketSeconds 1), shown over the full visible range
    // (0, 1.212) for a 1212 ms encounter. The axis read 1/0/0/0 because yMax was baked from an empty series.
    // This locks in that the geometry pipeline the axis (ChartYMax) and the line (MapSeries) share — clamp the
    // bucket window, peak it, NiceYMax it — yields 10000 for this 2-bucket / fractional-duration case, so the
    // Y axis renders 10K/7.5K/5K/2.5K/0. Mirrors WindowBuilder.ChartYMax's Floor(min/bucket)/Ceil(max/bucket).
    [Fact]
    public void Entry0_two_bucket_fractional_window_scales_axis_to_real_peak()
    {
        var series = new List<ChartSeries> { new("Team total", default, new float[] { 4588f, 8856f }, Emphasis: true) };
        const float min = 0f, max = 1.212f, bucket = 1f;
        var minBucket = (int)System.Math.Floor(min / bucket);   // 0
        var maxBucket = (int)System.Math.Ceiling(max / bucket);  // 2
        var (lo, hi) = ChartGeometry.ClampBucketWindow(minBucket, maxBucket, /*seriesLen*/ 2);
        Assert.Equal((0, 1), (lo, hi));   // capped at len-1 so both buckets are visited (not collapsed)
        var peak = ChartGeometry.VisiblePeak(series, lo, hi);
        Assert.Equal(8856f, peak);
        Assert.Equal(10000f, ChartGeometry.NiceYMax(peak));   // NOT 1 → axis reads 10K/7.5K/5K/2.5K/0
    }

    // The line and the axis must scale identically: ValueToY of the peak against NiceYMax(peak) lands at the
    // top tick row, never clipped or floored. (Pre-fix the axis used yMax 1 while the line used the live peak,
    // so the line shot past the labelled rows — the visible "rising line over a 1/0/0/0 axis" symptom.)
    [Fact]
    public void Entry0_peak_value_maps_to_top_of_plot_under_nice_max()
    {
        const float peak = 8856f;
        var yMax = ChartGeometry.NiceYMax(peak);   // 10000
        // peak is 88.56% up a 0..200px plot → ~177.1px, comfortably inside [0,200], not clamped to the top.
        Assert.Equal(177.12f, ChartGeometry.ValueToY(peak, yMax, 0f, 200f), 2);
    }
}
