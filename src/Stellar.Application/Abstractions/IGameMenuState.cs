using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>Detection port for in-world game UI state. Implemented in Infrastructure by probing the game's
/// zuiroot UI layers. <see cref="UiState"/> is the un-collapsed flag view fed to
/// <c>ClientStateService.UiState</c>; <see cref="IsFullScreenMenuOpen"/> is retained as the legacy any-cover bool.</summary>
internal interface IGameMenuState
{
    /// <summary>True when any full-screen game menu / cover surface is open (the legacy collapsed signal).</summary>
    bool IsFullScreenMenuOpen { get; }

    /// <summary>The un-collapsed per-layer UI state as flags. <see cref="GameUIState.None"/> when nothing is detected.</summary>
    GameUIState UiState { get; }
}
