using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Freezes what is on screen — the animation of every entity but the local player (players, NPCs, pets, mounts,
/// monsters), skill effects and particles (all of them, yours too), and (unless turned off) the drawn positions of every
/// moving entity but the local player — while any token is held. The local player is never frozen: they can walk away
/// from a frozen scene, and moving cancels their own held emote as in the game. Entities that appear while frozen are frozen as they appear. Visual and local only: the game clock and
/// the network keep running. Reference-counted; the framework releases every token on a zone change, a cutscene, the
/// game's own camera mode, a disconnect and the holder's unload — never on a free-camera release. Main thread only.
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
