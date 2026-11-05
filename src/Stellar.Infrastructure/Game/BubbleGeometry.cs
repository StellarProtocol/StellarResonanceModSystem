namespace Stellar.Infrastructure.Game;

/// <summary>
/// Pure (Unity-free) corner-radius math for the rounded bubble fill. Isolated here so it is unit-testable without a
/// Unity player (covered by <c>Stellar.Application.Tests</c>).
/// </summary>
internal static class BubbleGeometry
{
    // Largest corner radius that keeps a bubble of height `side` exactly `side` tall: side/2 − 1. BubbleBg(r) is a
    // 9-slice sprite with border r+1, and a sliced Image reports its two borders as a MIN layout size (2r+2), which
    // the bubble takes over its content. r = side/2 therefore grows it 2 px (a 28 px pill drew 30, measured in-game);
    // r = side/2 − 1 makes the borders sum exactly to the side and the ends still read as round.
    internal static float MaxRoundRadius(float side) => side > 2f ? side / 2f - 1f : 0f;

    // A requested radius capped so it can't outgrow `side`. ≤ 0 (and NaN) → 0 = "keep the default chip rounding".
    internal static float CapRadius(float requested, float side)
    {
        if (!(requested > 0f)) return 0f;
        var max = MaxRoundRadius(side);
        return requested < max ? requested : max;
    }
}
