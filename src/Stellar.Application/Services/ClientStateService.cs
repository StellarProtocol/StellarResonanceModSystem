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

    // Client phase — boot state is Startup (boot/loading, before the login UI exists). Driven by the Host:
    // Startup→TitleScreen when the login view is detected active (NotifyLoginViewActive), TitleScreen→CharSelect
    // on the game's OnLogin (char-select appears), CharSelect→World at the OnEnterScene gate-clear, and
    // →TitleScreen on OnLogout (from either World or CharSelect). It is a signal the framework gates nothing on.
    public GamePhase Phase { get; private set; } = GamePhase.Startup;
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

    /// <summary>Host-driven: the game's login view was detected active this tick. Latches
    /// <see cref="GamePhase.Startup"/> → <see cref="GamePhase.TitleScreen"/> exactly once. The guard on
    /// <see cref="GamePhase.Startup"/> makes it one-way — a later flicker of the login view (e.g. its clone
    /// still lingering after world-connect) can never bounce <see cref="GamePhase.World"/> back to the title.
    /// Runs in every phase (the detection is an un-gated UI read), so the guard is what keeps it safe.</summary>
    internal void NotifyLoginViewActive()
    {
        if (Phase == GamePhase.Startup)
        {
            RaisePhase(GamePhase.TitleScreen);
        }
    }

    /// <summary>Host-driven transition. Fires <see cref="PhaseChanged"/> only on an actual change. Any phase
    /// other than <see cref="GamePhase.World"/> clears <see cref="UiState"/> to <see cref="GameUIState.None"/>
    /// — there is no in-world UI at the title or character-select screens.</summary>
    internal void RaisePhase(GamePhase next)
    {
        var prev = Phase;
        if (prev == next)
        {
            return;
        }
        Phase = next;
        if (next != GamePhase.World)
        {
            UiState = GameUIState.None;
        }
        PhaseChanged?.Invoke(new PhaseChange(prev, next));
    }
}
