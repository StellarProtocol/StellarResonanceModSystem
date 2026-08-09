using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Player session state plus the client-phase and UI-state signals. Session state
/// (<see cref="IsLoggedIn"/>/<see cref="Login"/>/<see cref="Logout"/>) and client phase
/// (<see cref="Phase"/>) are distinct concepts that coexist — they correlate today but answer
/// different questions.
/// </summary>
public interface IClientState
{
    /// <summary>True when a character is fully loaded and in-world (same condition as <see cref="IPlayerIdentity.IsAvailable"/>).</summary>
    bool IsLoggedIn { get; }

    /// <summary>Identifier (currently a numeric scene id, not a friendly name) for the active scene.</summary>
    string? CurrentSceneName { get; }

    /// <summary>Fired once when the player finishes loading into the world (in-world ready).</summary>
    event Action Login;
    /// <summary>Fired once when the player disconnects or returns to character select.</summary>
    event Action Logout;

    /// <summary>Fired when the active scene changes. Argument is the new scene identifier, or <c>null</c> when no scene is active.</summary>
    event Action<string?> SceneChanged;

    /// <summary>Current client phase — read for the initial state (e.g. in a plugin ctor) or on demand.
    /// A <i>signal</i>: the framework gates nothing on it. Use for window visibility.</summary>
    GamePhase Phase { get; }

    /// <summary>Fires on each phase transition; the payload carries both the previous and next phase.</summary>
    event Action<PhaseChange> PhaseChanged;

    /// <summary>True in a stable world scene, false mid-transition (the world-connect / scene-switch handshake).
    /// Stricter than <c>Phase == World</c> — also false during in-world zone loads. This is the ONLY protective
    /// gate: every unit that touches live game state self-gates on it.</summary>
    bool IsWorldActive { get; }

    /// <summary>Informational in-world UI flags (<see cref="GameUIState.None"/> while at the title screen).
    /// The framework detects and exposes this but never gates on it; a plugin's <c>ShouldRender</c> may read it.</summary>
    GameUIState UiState { get; }
}
