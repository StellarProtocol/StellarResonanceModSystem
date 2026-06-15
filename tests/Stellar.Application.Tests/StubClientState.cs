using System;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Tests;

internal sealed class StubClientState : IClientState
{
    public bool IsLoggedIn { get; set; }
    public string? CurrentSceneName { get; set; }
    public event Action? Login;
    public event Action? Logout;
    public event Action<string?>? SceneChanged;

    public void RaiseSceneChanged(string? newScene)
    {
        CurrentSceneName = newScene;
        SceneChanged?.Invoke(newScene);
    }

    public void RaiseLogin() { IsLoggedIn = true; Login?.Invoke(); }
    public void RaiseLogout() { IsLoggedIn = false; Logout?.Invoke(); }
}
