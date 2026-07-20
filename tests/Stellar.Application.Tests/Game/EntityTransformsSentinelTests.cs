using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Regression pin — origin: run sea/UaU5VejCA0 (Thanatos raid walk-in; docs/recon/thanatos-walkin-geo.md).
/// Pins the zero-sentinel predicate that gates the wire-position fallback in
/// <see cref="EntityTransformsService"/>. Y is the discriminator (a real play floor is Y≈100 and
/// never exactly 0); world (0,0) can be a real in-bounds coordinate on interior-origin maps, so
/// X/Z alone cannot decide. Static method — no live IL2CPP / game process needed.
/// </summary>
public sealed class EntityTransformsSentinelTests
{
    [Fact]
    public void Origin_IsSentinel()
        => Assert.True(EntityTransformsService.IsZeroSentinel(Position3D.Zero));

    [Fact]
    public void NearOriginXZ_YExactlyZero_IsSentinel()
        => Assert.True(EntityTransformsService.IsZeroSentinel(new Position3D(0.3f, 0f, -0.4f)));

    [Fact]
    public void RealFloor_Y100_NotSentinel()
        => Assert.False(EntityTransformsService.IsZeroSentinel(new Position3D(0f, 100f, 0f)));

    [Fact]
    public void TinyNonZeroY_NotSentinel()
        => Assert.False(EntityTransformsService.IsZeroSentinel(new Position3D(0f, 0.5f, 0f)));

    [Fact]
    public void RealXZ_YZero_NotSentinel()
    {
        // A real ground-level position offset from origin — |X| beyond the epsilon.
        Assert.False(EntityTransformsService.IsZeroSentinel(new Position3D(170f, 0f, -290f)));
        Assert.False(EntityTransformsService.IsZeroSentinel(new Position3D(0f, 0f, 5f)));
    }
}
