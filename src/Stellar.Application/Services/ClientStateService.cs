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

    // Informational UI flags, composed from two independent sources so neither stomps the other:
    //   _menuBits    — every bit EXCEPT Loading, from the gated PandaMenuStateProbe (fresh only in-world).
    //   _loadingActive — the Loading bit alone, from the UN-gated PandaLoadingScreenProbe (fresh every phase).
    // The gated probe is frozen during a load (IsWorldActive false), so it can never own Loading; keeping the
    // Loading bit in a separate field means an in-world menu recompute (SetUiState) can't clear a live load.
    private GameUIState _menuBits;
    private bool _loadingActive;

    public GameUIState UiState => _menuBits | (_loadingActive ? GameUIState.Loading : GameUIState.None);

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

    /// <summary>Host/probe-driven: replace the non-Loading UI flags (fed by PandaMenuStateProbe each in-world
    /// tick). The Loading bit is stripped and ignored here — it is owned solely by <see cref="SetLoadingActive"/>
    /// (the un-gated loading probe), so this in-world recompute can never clear a live loading screen.</summary>
    internal void SetUiState(GameUIState state) => _menuBits = state & ~GameUIState.Loading;

    /// <summary>Host/probe-driven: sets the <see cref="GameUIState.Loading"/> bit. Fed by the UN-gated
    /// PandaLoadingScreenProbe every phase, so it is correct during a zone load / world-connect handshake when
    /// <see cref="IsWorldActive"/> is false and the gated menu-state probe is frozen.</summary>
    internal void SetLoadingActive(bool active) => _loadingActive = active;

    /// <summary>Host-driven: the game's login view (<c>login_main</c>) was detected active this tick. Promotes
    /// <see cref="GamePhase.Startup"/> → <see cref="GamePhase.TitleScreen"/> (boot) AND
    /// <see cref="GamePhase.World"/> → <see cref="GamePhase.TitleScreen"/> (post-logout) — so
    /// <see cref="GamePhase.TitleScreen"/> always means "the login screen is actually visible".
    /// <para>The <c>World</c> case is safe because <c>login_main</c> only exists in the login scene, never
    /// in-world, so this is never called during normal play — only after a logout when the login view reappears.
    /// On logout the Host deliberately leaves the phase at <c>World</c> (it does NOT raise <c>TitleScreen</c>
    /// itself); this promotion fires when the login screen is genuinely up, avoiding the early-<c>TitleScreen</c>
    /// flash. <see cref="GamePhase.CharSelect"/> is intentionally excluded — a char-select cancel is handled by
    /// the Host's direct <c>OnLogout</c> call, and <c>login_main</c> may be active at char-select, so promoting
    /// from <c>CharSelect</c> here could wrongly flip it.</para></summary>
    internal void NotifyLoginViewActive()
    {
        if (Phase == GamePhase.Startup || Phase == GamePhase.World)
        {
            RaisePhase(GamePhase.TitleScreen);
        }
    }

    /// <summary>Host-driven transition. Fires <see cref="PhaseChanged"/> only on an actual change. Any phase
    /// other than <see cref="GamePhase.World"/> clears the menu-flag portion of <see cref="UiState"/> to
    /// <see cref="GameUIState.None"/> — there is no in-world menu UI at the title or character-select screens.
    /// The Loading bit is NOT cleared here; it stays owned by the un-gated loading probe (a loading screen may
    /// legitimately be up during a transition), which drops it on the next tick once the screen closes.</summary>
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
            _menuBits = GameUIState.None;
        }
        PhaseChanged?.Invoke(new PhaseChange(prev, next));
    }
}
