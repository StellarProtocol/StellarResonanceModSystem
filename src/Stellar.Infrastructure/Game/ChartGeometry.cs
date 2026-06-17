using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Pure (Unity-free) chart math shared by the line-chart label placement and the renderer: the Y-max
/// "nice" rounding, the visible-window peak, and the bucket→X / value→Y pixel mappings. Isolated here so
/// it is unit-testable without a Unity player (covered by <c>Stellar.Application.Tests</c>).
/// </summary>
internal static class ChartGeometry
{
    // "Nice" rounded-up Y max for the visible window's peak (1/2/5 × 10^n ladder).
    internal static float NiceYMax(float peak)
    {
        if (peak <= 0f) return 1f;
        var mag = (float)Math.Pow(10, Math.Floor(Math.Log10(peak)));
        var norm = peak / mag;
        var step = norm <= 1f ? 1f : norm <= 2f ? 2f : norm <= 5f ? 5f : 10f;
        return step * mag;
    }

    // Clamp a raw [minBucket, maxBucket] bucket window so it always spans at least one full bucket and stays
    // inside the populated series. A sub-bucket visible range (floor==ceil) would otherwise scan an empty
    // window and report peak 0, collapsing the Y axis to the degenerate 1/0/0/0 ladder. We widen to at least
    // [minBucket, minBucket+1] then cap at seriesLen-1 so bucket 0's value is always visited.
    internal static (int Min, int Max) ClampBucketWindow(int minBucket, int maxBucket, int seriesLen)
    {
        if (minBucket < 0) minBucket = 0;
        if (maxBucket < minBucket + 1) maxBucket = minBucket + 1;
        if (seriesLen > 0 && maxBucket > seriesLen - 1) maxBucket = seriesLen - 1;
        if (maxBucket < minBucket) maxBucket = minBucket;
        return (minBucket, maxBucket);
    }

    // Peak Y across all series within [minBucket, maxBucket] inclusive.
    internal static float VisiblePeak(IReadOnlyList<ChartSeries> series, int minBucket, int maxBucket)
    {
        float peak = 0f;
        foreach (var s in series)
            for (int i = minBucket; i <= maxBucket && i < s.Values.Count; i++)
                if (i >= 0 && s.Values[i] > peak) peak = s.Values[i];
        return peak;
    }

    // Map a bucket index to an X pixel within [x0,x1] given the visible bucket window.
    internal static float BucketToX(int bucket, int minBucket, int maxBucket, float x0, float x1)
    {
        if (maxBucket <= minBucket) return x0;
        var t = (bucket - minBucket) / (float)(maxBucket - minBucket);
        return x0 + t * (x1 - x0);
    }

    // Map a Y value to a Y pixel within [y0,y1] (y0 = bottom/zero, y1 = top/yMax).
    internal static float ValueToY(float value, float yMax, float y0, float y1)
        => yMax <= 0f ? y0 : y0 + Math.Clamp(value / yMax, 0f, 1f) * (y1 - y0);

    // Navigator brush math: map a time `t` in [0,total] to an X pixel within [x0,x1]. The navigator plots
    // the FULL chart extent, so time 0 → x0 and time total → x1 (linear). total<=0 collapses to x0.
    internal static float TimeToNavX(float t, float total, float x0, float x1)
        => total <= 0f ? x0 : x0 + Math.Clamp(t / total, 0f, 1f) * (x1 - x0);

    // Inverse of TimeToNavX: a local X pixel within [x0,x1] back to a time in [0,total]. Used by the brush
    // drag handlers to convert cursor travel into a window edge. Degenerate rect (x1<=x0) returns 0.
    internal static float NavXToTime(float x, float total, float x0, float x1)
        => x1 <= x0 ? 0f : Math.Clamp((x - x0) / (x1 - x0), 0f, 1f) * total;
}
