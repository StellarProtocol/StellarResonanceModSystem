using System;

namespace Stellar.Abstractions.Services;

/// <summary>Whether the local player is in combat, from the game's own combat-state changes (never polled).</summary>
public interface ICombatState
{
    /// <summary>True while the local player is in combat.</summary>
    bool LocalPlayerInCombat { get; }

    /// <summary>Raised (main thread) when <see cref="LocalPlayerInCombat"/> changes.</summary>
    event Action<bool>? Changed;
}
