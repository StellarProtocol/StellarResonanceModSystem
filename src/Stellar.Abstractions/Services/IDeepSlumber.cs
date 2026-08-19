using Stellar.Abstractions.Domain.DeepSlumber;

namespace Stellar.Abstractions.Services;

/// <summary>Read-only access to the local player's Deep-Slumber Psychoscope (season cultivate)
/// state — season level plus every cultivate line's areas, socketed cards, and node levels. Each
/// call reads the LIVE game containers (never a saved profile).
/// <see cref="IInventory.SelfGearChanged"/> also fires on Deep-Slumber edits — but that event is
/// raised on the NETWORK thread and <see cref="GetState"/> reads live game state, so never call
/// this from the handler: set a flag there and read from the game tick / main thread, or simply
/// read at snapshot time.</summary>
public interface IDeepSlumber
{
    /// <summary>True once the live container is resolved and <see cref="GetState"/> can return data.</summary>
    bool IsAvailable { get; }

    /// <summary>The current live Deep-Slumber state, or null before the live container resolves.</summary>
    DeepSlumberState? GetState();
}
