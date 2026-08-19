using Stellar.Abstractions.Domain.DeepSlumber;

namespace Stellar.Abstractions.Services;

/// <summary>Read-only access to the local player's Deep-Slumber Psychoscope (season cultivate)
/// state — season level plus every cultivate line's areas, socketed cards, and node levels. Each
/// call reads the LIVE game containers (never a saved profile). Re-read on
/// <see cref="IInventory.SelfGearChanged"/> (which also fires on Deep-Slumber edits) or at
/// snapshot time.</summary>
public interface IDeepSlumber
{
    /// <summary>True once the live container is resolved and <see cref="GetState"/> can return data.</summary>
    bool IsAvailable { get; }

    /// <summary>The current live Deep-Slumber state, or null before the live container resolves.</summary>
    DeepSlumberState? GetState();
}
