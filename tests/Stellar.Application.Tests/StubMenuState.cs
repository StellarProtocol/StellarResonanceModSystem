using Stellar.Application.Abstractions;

namespace Stellar.Application.Tests;

internal sealed class StubMenuState : IGameMenuState
{
    public bool IsFullScreenMenuOpen { get; set; }
}
