namespace Stellar.Infrastructure.Theme;

/// <summary>
/// Pure geometry of the window resize-grip mark (System.Math only, no Unity → unit-testable). The mark is two
/// concentric strokes that follow the window's own bottom-right corner shape, offset inward: on a rounded body they
/// are 90° arcs, on a square body they become corner brackets, and in between a rounded-L whose corner radius
/// shrinks to 0. Coordinates are measured from the window CORNER: <c>u</c> = distance from the right edge,
/// <c>v</c> = distance from the bottom edge (px, both ≥ 0). The old 3/2/1 dot triangle sat 1 px from the SQUARE
/// corner, so on a 14 px GlassMenu frame its corner dots poked outside the rounded border over the game.
/// </summary>
internal static class ResizeGripGeometry
{
    /// <summary>Stroke width (px) and the stroke centre-line offsets from the window edge (outer, inner).</summary>
    internal const float StrokeWidth = 1.4f, OuterOffset = 3.5f, InnerOffset = 7f;

    // The approved reference (r = 14): outer arc radius 9, inner 5.5, both centred 12.5 px from the edges — i.e. the
    // strokes are concentric with an arc 1.5 px inside the frame's own (centre 14). With that inset the rule
    // "stroke radius = max(r − inset − offset, 0)" reproduces both reference SVGs exactly.
    private const float ArcInset = 1.5f;

    // Square body (r = 0) bracket leg ends, from the reference: outer reaches 12 px from the corner edge, inner 8.5.
    // A rounded body's legs end where its arc ends (pure 90° arc, no straight leg) whenever that is further out.
    private const float OuterSquareLegEnd = 12f, InnerSquareLegEnd = 8.5f;

    /// <summary>Radii above this draw the cap's shape (keeps the mark a small corner affordance on a very round
    /// backdrop; the arc would otherwise grow with the radius).</summary>
    internal const int MaxRadius = 64;

    /// <summary>One stroke: centre-line <paramref name="Offset"/> from both edges, corner radius <paramref name="Rho"/>,
    /// legs ending <paramref name="LegEnd"/> from the corner edges.</summary>
    internal readonly record struct Stroke(float Offset, float Rho, float LegEnd);

    internal static Stroke StrokeFor(int bodyRadius, float offset, float squareLegEnd)
    {
        var r = System.Math.Clamp(bodyRadius, 0, MaxRadius);
        var rho = System.Math.Max(r - ArcInset - offset, 0f);
        return new Stroke(offset, rho, System.Math.Max(offset + rho, squareLegEnd));
    }

    internal static Stroke Outer(int bodyRadius) => StrokeFor(bodyRadius, OuterOffset, OuterSquareLegEnd);
    internal static Stroke Inner(int bodyRadius) => StrokeFor(bodyRadius, InnerOffset, InnerSquareLegEnd);

    /// <summary>Side (px) of the square texture that holds the whole mark, measured from the window corner: the outer
    /// leg end plus half the stroke plus the 0.5 px antialias ramp, rounded up with 1 px spare.</summary>
    internal static int TextureSize(int bodyRadius)
        => (int)System.Math.Ceiling(Outer(bodyRadius).LegEnd + StrokeWidth * 0.5f + 1.5f);

    /// <summary>Antialiased coverage [0, 1] of the mark at corner-space point (<paramref name="u"/>, <paramref name="v"/>):
    /// the max of both strokes, each a 1-px analytic ramp around the stroke edge (round caps fall out of the segment
    /// distance).</summary>
    internal static float Coverage(int bodyRadius, float u, float v)
        => System.Math.Max(StrokeCoverage(Outer(bodyRadius), u, v), StrokeCoverage(Inner(bodyRadius), u, v));

    internal static float StrokeCoverage(Stroke s, float u, float v)
    {
        var d = Distance(s, u, v);
        return System.Math.Clamp(StrokeWidth * 0.5f + 0.5f - d, 0f, 1f);
    }

    /// <summary>Distance from (u, v) to the stroke's centre line: vertical leg u = offset (v from c to the leg end),
    /// horizontal leg v = offset (u from c to the leg end), and the corner arc centred (c, c), c = offset + rho.</summary>
    internal static float Distance(Stroke s, float u, float v)
    {
        var c = s.Offset + s.Rho;
        var dv = SegDist(u - s.Offset, v, c, s.LegEnd);   // vertical leg
        var dh = SegDist(v - s.Offset, u, c, s.LegEnd);   // horizontal leg
        var best = System.Math.Min(dv, dh);
        if (u <= c && v <= c)   // the arc quadrant facing the corner (rho 0 → the bracket's corner point)
        {
            var du = u - c; var dw = v - c;
            best = System.Math.Min(best, System.Math.Abs((float)System.Math.Sqrt(du * du + dw * dw) - s.Rho));
        }
        return best;
    }

    // Distance to an axis-aligned segment: `across` = signed distance perpendicular to it, `along` clamped to [from, to].
    private static float SegDist(float across, float along, float from, float to)
    {
        var a = along < from ? from - along : along > to ? along - to : 0f;
        return (float)System.Math.Sqrt(across * across + a * a);
    }
}
