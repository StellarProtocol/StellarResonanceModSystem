using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Freezes what is on screen — the animation of every entity (players, NPCs, pets, mounts, monsters), skill effects and
/// particles, and (unless turned off) the drawn positions of every moving entity except the local player — while any
/// token is held. Entities that appear while frozen are frozen as they appear. Visual and local only: the game clock and
/// the network keep running. Reference-counted; the framework releases every token on a zone change, a cutscene, a
/// disconnect and the holder's unload. Main thread only.
/// </summary>
public interface ISceneFreeze
{
    /// <summary>Freezes the scene until the returned token is disposed.</summary>
    IDisposable Freeze();

    /// <summary>True while frozen.</summary>
    bool IsFrozen { get; }

    /// <summary>True while drawn positions are being held (false when turned off or over budget).</summary>
    bool HoldsPositions { get; }

    /// <summary>Raised when <see cref="IsFrozen"/> or <see cref="HoldsPositions"/> changes; the argument is <see cref="IsFrozen"/>.</summary>
    event Action<bool>? Changed;
}
