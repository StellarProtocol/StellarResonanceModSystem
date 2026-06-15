using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound interface — produces a fresh snapshot of the local player's state
/// from whatever game-internal source is available. Implemented in
/// <c>Stellar.Infrastructure</c> by walking the live Panda hot-update objects.
/// </summary>
internal interface IPlayerStateProbe
{
    /// <summary>
    /// Attempts to sample the local player. Returns <c>true</c> with a populated
    /// <paramref name="snapshot"/> when a character is loaded and at least the
    /// minimum useful state could be read; <c>false</c> otherwise.
    /// </summary>
    bool TrySample(out PlayerStateSnapshot snapshot);
}

/// <summary>Plain DTO returned by <see cref="IPlayerStateProbe.TrySample"/>.</summary>
internal readonly struct PlayerStateSnapshot
{
    public string? Name { get; init; }
    public int Level { get; init; }
    public int Profession { get; init; }
    public int Health { get; init; }
    public int MaxHealth { get; init; }
    public int Stamina { get; init; }
    public int MaxStamina { get; init; }
    public Position3D Position { get; init; }
}
