using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Toolkit service that reads the live world transform (position + facing) of an arbitrary
/// game entity by id. Backed by the game's entity manager; reads MUST happen on the main
/// thread (the framework <see cref="IFramework.Update"/> tick). Intended for replay/position
/// capture; returns <c>false</c> when the entity is not resolvable (despawned, not loaded,
/// or off-game), leaving the out-parameters at their defaults.
/// </summary>
public interface IEntityTransforms
{
    /// <summary>
    /// Attempts to read the world position and facing of the entity identified by
    /// <paramref name="id"/>. Returns <c>false</c> if the entity cannot be resolved this
    /// frame (despawned / not loaded / no game); in that case <paramref name="position"/> is
    /// <see cref="Position3D.Zero"/> and <paramref name="yawDegrees"/> is 0.
    /// </summary>
    /// <param name="id">The entity id (as seen on combat events / party roster).</param>
    /// <param name="position">World-space position on success.</param>
    /// <param name="yawDegrees">Facing in degrees [0,360) on success; 0 when facing is unavailable.</param>
    /// <returns><c>true</c> if a live transform was read; otherwise <c>false</c>.</returns>
    bool TryGetTransform(EntityId id, out Position3D position, out float yawDegrees);
}
