using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.Application.Tests;

/// <summary>
/// Off-game safety contract tests for <see cref="IEntityTransforms"/>.
/// The real <c>EntityTransformsService</c> requires a live IL2CPP game process.
/// These tests pin the defensive contract with an off-game stub and verify that
/// the service returns false + defaults when no entity is resolvable.
/// </summary>
public class EntityTransformsServiceContractTests
{
    /// <summary>
    /// Minimal off-game stub that honours the IEntityTransforms contract without
    /// touching any game APIs — used to pin the interface shape.
    /// </summary>
    private sealed class OffGame : IEntityTransforms
    {
        public bool TryGetTransform(EntityId id, out Position3D position, out float yawDegrees)
        {
            position = Position3D.Zero;
            yawDegrees = 0f;
            return false;
        }
    }

    [Fact]
    public void OffGame_ReturnsFalse_AndDefaults()
    {
        var svc = new OffGame();
        var ok = svc.TryGetTransform(new EntityId(123), out var pos, out var yaw);
        Assert.False(ok);
        Assert.Equal(Position3D.Zero, pos);
        Assert.Equal(0f, yaw);
    }

    [Fact]
    public void OffGame_NoneEntityId_ReturnsFalse()
    {
        var svc = new OffGame();
        var ok = svc.TryGetTransform(EntityId.None, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void PositionZero_IsDefaultStruct()
    {
        // Ensure Position3D.Zero matches the default struct so callers using
        // default(Position3D) and Position3D.Zero are equivalent.
        Assert.Equal(default(Position3D), Position3D.Zero);
    }
}
