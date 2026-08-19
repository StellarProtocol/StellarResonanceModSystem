using Stellar.Abstractions.Domain.DeepSlumber;

namespace Stellar.Abstractions.Services;

/// <summary>Read-only access to the local player's Deep-Slumber Psychoscope (season cultivate)
/// state — season level plus every cultivate line's areas, socketed cards, and node levels. The
/// state is refreshed from the game's live containers via the framework's on-demand Lua bridge
/// (on login, and again on build-state changes — including Deep-Slumber edits), then cached until
/// the next refresh; it is cleared on logout so a relog never serves the previous character's data.
/// Because the refresh is on-demand rather than synchronous with the edit, <see cref="GetState"/>
/// called immediately after an edit may briefly return the state from before that edit.
/// <see cref="IInventory.SelfGearChanged"/> also fires on Deep-Slumber edits — but that event is
/// raised on the NETWORK thread and <see cref="GetState"/> reads cached state populated on the main
/// thread, so never call this from the handler: set a flag there and read from the game tick / main
/// thread, or simply read at snapshot time.</summary>
public interface IDeepSlumber
{
    /// <summary>True once the live container is resolved and <see cref="GetState"/> can return data.</summary>
    bool IsAvailable { get; }

    /// <summary>The current live Deep-Slumber state, or null before the live container resolves.</summary>
    DeepSlumberState? GetState();
}
