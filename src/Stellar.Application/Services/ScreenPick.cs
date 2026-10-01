using System;
using System.Collections.Generic;
namespace Stellar.Application.Services;

/// <summary>A character's chest projected to the screen (pixels, origin top-left; Depth = metres in front of the camera).</summary>
internal readonly record struct PickCandidate(long Id, float ScreenX, float ScreenY, float Depth);

/// <summary>Nearest-on-screen picking: cheap (one projection per character, no physics), and the radius tracks depth so
/// a far character is as clickable as its drawn body.</summary>
internal static class ScreenPick
{
    internal const float TargetRadiusMetres = 0.6f;
    internal const float MinRadiusPx = 18f;
    internal const float MaxRadiusPx = 160f;

    internal static float RadiusPx(float depth, float screenHeight, float fovDeg)
    {
        var halfHeightMetres = MathF.Tan(fovDeg * MathF.PI / 360f) * depth;
        if (halfHeightMetres <= 0f) return MinRadiusPx;
        return Math.Clamp(TargetRadiusMetres / halfHeightMetres * (screenHeight / 2f), MinRadiusPx, MaxRadiusPx);
    }

    internal static long? Nearest(IReadOnlyList<PickCandidate> candidates, float x, float y, float screenHeight, float fovDeg)
    {
        long? best = null;
        var bestPx = float.MaxValue;
        var bestDepth = float.MaxValue;
        foreach (var c in candidates)
        {
            if (c.Depth <= 0f) continue;
            var px = MathF.Sqrt((c.ScreenX - x) * (c.ScreenX - x) + (c.ScreenY - y) * (c.ScreenY - y));
            if (px > RadiusPx(c.Depth, screenHeight, fovDeg)) continue;
            var better = px < bestPx - 0.5f || (MathF.Abs(px - bestPx) <= 0.5f && c.Depth < bestDepth);
            if (!better) continue;
            best = c.Id;
            bestPx = px;
            bestDepth = c.Depth;
        }
        return best;
    }
}
