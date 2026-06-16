using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

public sealed class ChartWindowTests
{
    [Fact]
    public void TotalSeconds_uses_longest_series_times_bucket()
    {
        var series = new List<ChartSeries>
        {
            new("A", default, new float[] { 1f, 2f, 3f }),
            new("B", default, new float[] { 1f, 2f, 3f, 4f, 5f }),   // longest = 5 buckets
        };
        Assert.Equal(10f, ChartWindow.TotalSeconds(series, 2f));   // 5 × 2
    }

    [Fact]
    public void TotalSeconds_floors_to_one_bucket_when_empty()
        => Assert.Equal(1f, ChartWindow.TotalSeconds(new List<ChartSeries>(), 1f));

    [Fact]
    public void MinSpan_is_two_buckets_or_total_when_shorter()
    {
        Assert.Equal(2f, ChartWindow.MinSpan(1f, 64f));    // 2 × 1s
        Assert.Equal(3f, ChartWindow.MinSpan(5f, 3f));     // total shorter than two buckets → total
    }

    [Fact]
    public void Clamp_keeps_window_inside_bounds()
    {
        Assert.Equal((0f, 10f), ChartWindow.Clamp((-5f, 5f), 64f, 2f));        // left overhang shifts in
        Assert.Equal((54f, 64f), ChartWindow.Clamp((59f, 69f), 64f, 2f));      // right overhang shifts in
        Assert.Equal((20f, 36f), ChartWindow.Clamp((20f, 36f), 64f, 2f));      // already inside → unchanged
    }

    [Fact]
    public void Clamp_normalises_flipped_pair()
        => Assert.Equal((10f, 20f), ChartWindow.Clamp((20f, 10f), 64f, 2f));

    [Fact]
    public void Clamp_grows_too_small_span_to_min_centred()
    {
        // request span 1 (< minSpan 2) around centre 30 → grows to [29,31].
        Assert.Equal((29f, 31f), ChartWindow.Clamp((29.5f, 30.5f), 64f, 2f));
    }

    [Fact]
    public void Clamp_caps_span_at_total()
        => Assert.Equal((0f, 64f), ChartWindow.Clamp((-10f, 100f), 64f, 2f));

    [Fact]
    public void ZoomAround_keeps_anchor_fraction_constant_on_zoom_in()
    {
        // window [0,40], anchor 10 (25% from left), zoom in ×0.5 → span 20, anchor still 25% → [5,25].
        var w = ChartWindow.ZoomAround((0f, 40f), 10f, 0.5f, 64f, 2f);
        Assert.Equal((5f, 25f), w);
    }

    [Fact]
    public void ZoomAround_respects_min_span()
    {
        // a hard zoom-in (×0.01) around 30 can't go below the 2s min span.
        var w = ChartWindow.ZoomAround((20f, 40f), 30f, 0.01f, 64f, 2f);
        Assert.Equal(2f, w.Max - w.Min, 3);
        Assert.Equal(30f, (w.Min + w.Max) * 0.5f, 3);
    }

    [Fact]
    public void ZoomCentre_zooms_out_about_midpoint()
    {
        // window [20,40] (centre 30), zoom out ×2 → span 40 → [10,50].
        Assert.Equal((10f, 50f), ChartWindow.ZoomCentre((20f, 40f), 2f, 64f, 2f));
    }

    [Fact]
    public void ZoomCentre_out_clamps_to_total_when_overflowing()
    {
        // window [0,40] (centre 20), zoom out ×4 → span 160 capped to 64 → full [0,64].
        Assert.Equal((0f, 64f), ChartWindow.ZoomCentre((0f, 40f), 4f, 64f, 2f));
    }

    [Fact]
    public void Pan_translates_then_clamps()
    {
        Assert.Equal((20f, 30f), ChartWindow.Pan((10f, 20f), 10f, 64f, 2f));     // free pan right
        Assert.Equal((0f, 10f), ChartWindow.Pan((10f, 20f), -50f, 64f, 2f));     // pan past left edge → pinned
        Assert.Equal((54f, 64f), ChartWindow.Pan((50f, 60f), 50f, 64f, 2f));     // pan past right edge → pinned
    }

    [Fact]
    public void Full_is_zero_to_total()
        => Assert.Equal((0f, 64f), ChartWindow.Full(64f));
}
