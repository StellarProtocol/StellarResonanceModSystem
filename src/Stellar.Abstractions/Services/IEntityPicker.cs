using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>Finds the character drawn under a screen point: players (including the local player) and, since 2.15.0,
/// NPCs. Main thread only.</summary>
public interface IEntityPicker
{
    /// <summary>The character nearest to (<paramref name="screenX"/>, <paramref name="screenY"/>) within a small
    /// on-screen radius, or false when none is there.</summary>
    /// <param name="screenX">Screen X in pixels, origin left.</param>
    /// <param name="screenY">Screen Y in pixels, origin top.</param>
    /// <param name="entityId">The picked entity (usable with <see cref="IEntityTransforms"/>).</param>
    bool TryPickEntity(float screenX, float screenY, out EntityId entityId);
}
