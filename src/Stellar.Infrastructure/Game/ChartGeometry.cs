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
}
