namespace Stellar.Application.Abstractions;

/// <summary>Detection port: is a full-screen game menu currently open? Implemented
/// in Infrastructure by probing the game's UILayerMain canvases.</summary>
internal interface IGameMenuState
{
    bool IsFullScreenMenuOpen { get; }
}
