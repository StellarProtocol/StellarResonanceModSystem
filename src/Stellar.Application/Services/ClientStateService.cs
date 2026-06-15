using System;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed class ClientStateService : IClientState
{
    public bool IsLoggedIn { get; private set; }
    public string? CurrentSceneName { get; private set; }

    public event Action? Login;
    public event Action? Logout;
    public event Action<string?>? SceneChanged;

    internal void RaiseLogin()
    {
        if (IsLoggedIn)
        {
            return;
        }
        IsLoggedIn = true;
        Login?.Invoke();
    }

    internal void RaiseLogout()
    {
        if (!IsLoggedIn)
        {
            return;
        }
        IsLoggedIn = false;
        Logout?.Invoke();
    }

    internal void RaiseSceneChanged(string? sceneName)
    {
        if (sceneName == CurrentSceneName)
        {
            return;
        }
        CurrentSceneName = sceneName;
        SceneChanged?.Invoke(sceneName);
    }
}
