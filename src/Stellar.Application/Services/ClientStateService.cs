using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed class ClientStateService : IClientState
{
    public bool IsLoggedIn { get; private set; }
    public string? CurrentSceneName { get; private set; }

    public event Action? Login;
    public event Action? Logout;
    public event Action<string?>? SceneChanged;

    // Client phase — boot state is TitleScreen. Driven by the Host (RaisePhase at the OnEnterScene
    // gate-clear and at OnLogout); it is a signal the framework gates nothing on.
    public GamePhase Phase { get; private set; } = GamePhase.TitleScreen;
    public event Action<PhaseChange>? PhaseChanged;

    // The single protective gate — set by the Host at the same two spots it flips the scene-transition flag.
    public bool IsWorldActive { get; private set; }

    // Informational UI flags, fed by the menu-state probe each in-world tick. None while at the title screen.
    public GameUIState UiState { get; private set; }

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

    /// <summary>Host-driven: true in a stable world scene, false mid-transition. Flipped at the same two spots
    /// the Host flips its scene-transition flag (false in BeginSceneTransition, true at the OnEnterScene clear).</summary>
    internal void SetWorldActive(bool active) => IsWorldActive = active;

    /// <summary>Host/probe-driven: replace the informational UI flags (fed by PandaMenuStateProbe each in-world tick).</summary>
    internal void SetUiState(GameUIState state) => UiState = state;

    /// <summary>Host-driven transition. Fires <see cref="PhaseChanged"/> only on an actual change. Leaving
    /// <see cref="GamePhase.World"/> for <see cref="GamePhase.TitleScreen"/> clears <see cref="UiState"/> to
    /// <see cref="GameUIState.None"/> (there is no in-world UI at the title screen).</summary>
    internal void RaisePhase(GamePhase next)
    {
        var prev = Phase;
        if (prev == next)
        {
            return;
        }
        Phase = next;
        if (next == GamePhase.TitleScreen)
        {
            UiState = GameUIState.None;
        }
        PhaseChanged?.Invoke(new PhaseChange(prev, next));
    }
}
