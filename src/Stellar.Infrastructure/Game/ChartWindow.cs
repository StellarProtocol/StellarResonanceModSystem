using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Pure (Unity-free) visible-window math for the line chart's zoom/pan controls — shared by the scroll/drag
/// gesture handler, the −/+/Reset button bar, and the range scrollbar so every input path computes the same
/// clamped window. The window is a (min,max) span in SECONDS, always kept inside [0, total] with a minimum
/// span of <see cref="MinSpan"/> (two buckets). Isolated here so it is unit-testable without a Unity player
/// (covered by <c>Stellar.Application.Tests</c>); the renderer reads/writes it via the plugin-owned
/// <see cref="LineChartElement.VisibleRange"/> / <see cref="LineChartElement.SetVisibleRange"/>.
/// </summary>
internal static class ChartWindow
{
    // Total time the chart spans: the longest series' bucket count × bucket-seconds. Defines the pan/zoom
    // bounds ([0, total]) so a gesture can never scroll past the recorded data. Floors to one bucket.
    internal static float TotalSeconds(IReadOnlyList<ChartSeries> series, float bucketSeconds)
    {
        var bucket = bucketSeconds > 0f ? bucketSeconds : 1f;
        var maxCount = 0;
        for (var i = 0; i < series.Count; i++) if (series[i].Values.Count > maxCount) maxCount = series[i].Values.Count;
        return Math.Max(maxCount, 1) * bucket;
    }

    // Minimum visible span: two buckets (so the user can always see at least a segment, never collapse to a
    // point). Clamped to total when the whole chart is shorter than two buckets.
    internal static float MinSpan(float bucketSeconds, float total)
    {
        var bucket = bucketSeconds > 0f ? bucketSeconds : 1f;
        return Math.Min(2f * bucket, total);
    }

    // Clamp an arbitrary (min,max) request into [0,total] honouring the minimum span. Order-normalises a
    // flipped pair, grows a too-small span outward (centred), then shifts a span that overhangs an edge back
    // inside the bounds (preserving its width) rather than squashing it.
    internal static (float Min, float Max) Clamp((float Min, float Max) w, float total, float minSpan)
    {
        var (min, max) = w.Min <= w.Max ? (w.Min, w.Max) : (w.Max, w.Min);
        var span = Math.Min(Math.Max(max - min, minSpan), total);
        var centre = (min + max) * 0.5f;
        min = centre - span * 0.5f;
        if (min < 0f) min = 0f;
        if (min + span > total) min = total - span;
        if (min < 0f) min = 0f;
        return (min, min + span);
    }

    // Zoom around a fixed time anchor (the cursor's time, for scroll-zoom): scale the span by `factor`
    // (<1 = zoom in, >1 = zoom out) while keeping `anchor`'s on-screen fraction constant, then clamp.
    internal static (float Min, float Max) ZoomAround((float Min, float Max) w, float anchor, float factor,
        float total, float minSpan)
    {
        var span = w.Max - w.Min;
        var t = span > 0f ? (anchor - w.Min) / span : 0.5f;
        var newSpan = Math.Min(Math.Max(span * factor, minSpan), total);
        var min = anchor - t * newSpan;
        return Clamp((min, min + newSpan), total, minSpan);
    }

    // Zoom around the window centre (the −/+ buttons): a ZoomAround anchored at the current midpoint.
    internal static (float Min, float Max) ZoomCentre((float Min, float Max) w, float factor, float total, float minSpan)
        => ZoomAround(w, (w.Min + w.Max) * 0.5f, factor, total, minSpan);

    // Pan by a time delta (drag / scrollbar): translate both edges, then clamp back inside the bounds.
    internal static (float Min, float Max) Pan((float Min, float Max) w, float deltaSeconds, float total, float minSpan)
        => Clamp((w.Min + deltaSeconds, w.Max + deltaSeconds), total, minSpan);

    // The full chart window — the Reset button's target.
    internal static (float Min, float Max) Full(float total) => (0f, total);
}
