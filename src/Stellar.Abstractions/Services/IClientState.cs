using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Player session state. Mirrors lifecycle events the game itself publishes.
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
}
