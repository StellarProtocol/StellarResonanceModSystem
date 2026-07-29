using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Tests;

internal sealed class StubClientState : IClientState
{
    public bool IsLoggedIn { get; set; }
    public string? CurrentSceneName { get; set; }
    public event Action? Login;
    public event Action? Logout;
    public event Action<string?>? SceneChanged;

    public GamePhase Phase { get; set; } = GamePhase.TitleScreen;
    public event Action<PhaseChange>? PhaseChanged;
    public bool IsWorldActive { get; set; } = true;   // default true so game-state service tests exercise the body
    public GameUIState UiState { get; set; }

    public void RaiseSceneChanged(string? newScene)
    {
        CurrentSceneName = newScene;
        SceneChanged?.Invoke(newScene);
    }

    public void RaiseLogin() { IsLoggedIn = true; Login?.Invoke(); }
    public void RaiseLogout() { IsLoggedIn = false; Logout?.Invoke(); }

    public void RaisePhase(GamePhase next)
    {
        var prev = Phase;
        Phase = next;
        PhaseChanged?.Invoke(new PhaseChange(prev, next));
    }
}
