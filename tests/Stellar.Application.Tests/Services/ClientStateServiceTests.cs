using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>
/// Pins the 4-phase client-lifecycle model (<see cref="GamePhase.Startup"/> →
/// <see cref="GamePhase.TitleScreen"/> → <see cref="GamePhase.CharSelect"/> → <see cref="GamePhase.World"/>)
/// that <see cref="ClientStateService"/> enacts. The service is a dumb transition sink; the edge decisions
/// (which game event / probe signal drives which transition, and the Startup/TitleScreen guards) live in the
/// Host. The <c>Sim*</c> helpers below replay those Host edges so the whole model is covered here.
///
/// Empirical ground truth (live diagnostic overlay, 2026-07-29): the game's OnLogin fires when the
/// character-select screen appears (IsLoggedIn → true there, NOT at world-connect); the Unity scene name
/// does not change between title and char-select; cancelling char-select back to title fires OnLogout.
/// Startup is the boot/loading phase before the login UI exists — it latches to TitleScreen when the Host's
/// login-view probe reports login_main active.
/// </summary>
public sealed class ClientStateServiceTests
{
    // --- Host edge replays (mirror Wiring.Wire.cs / Wiring.GameLoop.cs / Wiring.ServiceTick.cs) ---

    // Login-view probe reports login_main active: latches Startup→TitleScreen exactly once (guard in service).
    private static void SimLoginViewActive(ClientStateService s) => s.NotifyLoginViewActive();

    // OnLogin: char-select appears. Guarded so only TitleScreen→CharSelect (a stray re-fire can't bounce
    // World→CharSelect, since RaisePhase itself is not phase-aware).
    private static void SimOnLogin(ClientStateService s)
    {
        s.RaiseLogin();
        if (s.Phase == GamePhase.TitleScreen)
        {
            s.RaisePhase(GamePhase.CharSelect);
        }
    }

    // OnEnterScene gate-clear while logged in: CharSelect→World (no-op if already World, e.g. zone loads).
    private static void SimEnterWorld(ClientStateService s)
    {
        s.SetWorldActive(true);
        s.RaisePhase(GamePhase.World);
    }

    // OnLogout: from either World or CharSelect back to the title screen.
    private static void SimOnLogout(ClientStateService s)
    {
        s.RaiseLogout();
        s.RaisePhase(GamePhase.TitleScreen);
    }

    // Fresh service at boot — phase is Startup, change log empty.
    private static (ClientStateService svc, List<PhaseChange> changes) Build()
    {
        var svc = new ClientStateService();
        var changes = new List<PhaseChange>();
        svc.PhaseChanged += c => changes.Add(c);
        return (svc, changes);
    }

    // Service advanced to the login screen (Startup→TitleScreen) with the change log cleared — the common
    // starting point for the OnLogin/world/logout coverage below (which asserts changes from TitleScreen on).
    private static (ClientStateService svc, List<PhaseChange> changes) BuildAtTitleScreen()
    {
        var (svc, changes) = Build();
        SimLoginViewActive(svc);
        changes.Clear();
        return (svc, changes);
    }

    [Fact]
    public void Boot_phase_is_Startup()
    {
        var (svc, _) = Build();
        Assert.Equal(GamePhase.Startup, svc.Phase);
    }

    [Fact]
    public void LoginView_active_moves_Startup_to_TitleScreen()
    {
        var (svc, changes) = Build();

        SimLoginViewActive(svc);

        Assert.Equal(GamePhase.TitleScreen, svc.Phase);
        Assert.False(svc.IsLoggedIn);
        Assert.Single(changes);
        Assert.Equal(new PhaseChange(GamePhase.Startup, GamePhase.TitleScreen), changes[0]);
    }

    [Fact]
    public void LoginView_active_is_latched_fires_once_and_never_bounces_World()
    {
        var (svc, changes) = BuildAtTitleScreen();

        // A repeat login-view signal at TitleScreen is a no-op (already latched off Startup).
        SimLoginViewActive(svc);
        Assert.Equal(GamePhase.TitleScreen, svc.Phase);
        Assert.Empty(changes);

        // Once in-world, a lingering login_main flicker must NOT bounce World→TitleScreen (guard is Startup-only).
        SimOnLogin(svc);
        SimEnterWorld(svc);
        changes.Clear();
        SimLoginViewActive(svc);
        Assert.Equal(GamePhase.World, svc.Phase);
        Assert.Empty(changes);
    }

    [Fact]
    public void OnLogin_moves_TitleScreen_to_CharSelect()
    {
        var (svc, changes) = BuildAtTitleScreen();

        SimOnLogin(svc);

        Assert.Equal(GamePhase.CharSelect, svc.Phase);
        Assert.True(svc.IsLoggedIn);
        Assert.Single(changes);
        Assert.Equal(new PhaseChange(GamePhase.TitleScreen, GamePhase.CharSelect), changes[0]);
    }

    [Fact]
    public void Full_login_sequence_walks_TitleScreen_CharSelect_World()
    {
        var (svc, changes) = BuildAtTitleScreen();

        SimOnLogin(svc);      // char-select appears
        SimEnterWorld(svc);   // player picks a character, world connects

        Assert.Equal(GamePhase.World, svc.Phase);
        Assert.True(svc.IsWorldActive);
        Assert.Equal(
            new[]
            {
                new PhaseChange(GamePhase.TitleScreen, GamePhase.CharSelect),
                new PhaseChange(GamePhase.CharSelect, GamePhase.World),
            },
            changes.ToArray());
    }

    [Fact]
    public void CharSelect_cancel_returns_to_TitleScreen()
    {
        var (svc, changes) = BuildAtTitleScreen();

        SimOnLogin(svc);    // at char-select
        SimOnLogout(svc);   // cancel back to title (game fires OnLogout)

        Assert.Equal(GamePhase.TitleScreen, svc.Phase);
        Assert.False(svc.IsLoggedIn);
        Assert.Equal(
            new[]
            {
                new PhaseChange(GamePhase.TitleScreen, GamePhase.CharSelect),
                new PhaseChange(GamePhase.CharSelect, GamePhase.TitleScreen),
            },
            changes.ToArray());
    }

    [Fact]
    public void World_logout_returns_to_TitleScreen()
    {
        var (svc, changes) = BuildAtTitleScreen();

        SimOnLogin(svc);
        SimEnterWorld(svc);
        changes.Clear();

        SimOnLogout(svc);

        Assert.Equal(GamePhase.TitleScreen, svc.Phase);
        Assert.Single(changes);
        Assert.Equal(new PhaseChange(GamePhase.World, GamePhase.TitleScreen), changes[0]);
    }

    [Fact]
    public void Stray_OnLogin_in_World_does_not_bounce_to_CharSelect()
    {
        var (svc, changes) = BuildAtTitleScreen();
        SimOnLogin(svc);
        SimEnterWorld(svc);
        changes.Clear();

        // A re-fire of the OnLogin edge while already in-world: the TitleScreen guard blocks it.
        SimOnLogin(svc);

        Assert.Equal(GamePhase.World, svc.Phase);
        Assert.Empty(changes);
    }

    [Fact]
    public void EnterWorld_is_noop_across_in_world_zone_loads()
    {
        var (svc, changes) = BuildAtTitleScreen();
        SimOnLogin(svc);
        SimEnterWorld(svc);
        changes.Clear();

        // A subsequent in-world scene load re-enters World — RaisePhase(World) is a no-op, phase steady.
        SimEnterWorld(svc);

        Assert.Equal(GamePhase.World, svc.Phase);
        Assert.Empty(changes);
    }

    [Fact]
    public void RaisePhase_to_same_phase_is_noop_and_raises_no_event()
    {
        var (svc, changes) = BuildAtTitleScreen();

        svc.RaisePhase(GamePhase.TitleScreen);   // already TitleScreen

        Assert.Equal(GamePhase.TitleScreen, svc.Phase);
        Assert.Empty(changes);
    }

    [Fact]
    public void Leaving_World_clears_UiState_to_None()
    {
        var (svc, _) = BuildAtTitleScreen();
        SimOnLogin(svc);
        SimEnterWorld(svc);
        svc.SetUiState(GameUIState.FullScreenMenu | GameUIState.GameHud);

        SimOnLogout(svc);

        Assert.Equal(GameUIState.None, svc.UiState);
    }

    [Fact]
    public void UiState_is_None_at_CharSelect()
    {
        var (svc, _) = BuildAtTitleScreen();

        SimOnLogin(svc);

        // No in-world UI at char-select — UiState never leaves None.
        Assert.Equal(GameUIState.None, svc.UiState);
    }

    // --- GameUIState.Loading: owned SOLELY by the un-gated loading probe (SetLoadingActive), composed with the
    // gated menu-state probe's bits (SetUiState) so neither stomps the other. Mirrors Host wiring in
    // Wiring.ServiceTick.cs (loading probe ticked un-gated; menu-state probe gated on IsWorldActive). ---

    [Fact]
    public void Loading_bit_is_driven_by_the_loading_probe()
    {
        var (svc, _) = BuildAtTitleScreen();
        SimOnLogin(svc);
        SimEnterWorld(svc);

        svc.SetLoadingActive(true);
        Assert.True((svc.UiState & GameUIState.Loading) != 0);

        svc.SetLoadingActive(false);
        Assert.True((svc.UiState & GameUIState.Loading) == 0);   // no stuck bit
    }

    [Fact]
    public void Loading_bit_survives_an_in_world_menu_recompute()
    {
        // The anti-stomp guarantee: the gated menu-state probe recomputing UiState (SetUiState) must never
        // clear a live Loading bit set by the un-gated loading probe.
        var (svc, _) = BuildAtTitleScreen();
        SimOnLogin(svc);
        SimEnterWorld(svc);

        svc.SetLoadingActive(true);                       // un-gated probe: loading screen up
        svc.SetUiState(GameUIState.GameHud);              // gated probe recompute (no Loading in its result)

        Assert.Equal(GameUIState.GameHud | GameUIState.Loading, svc.UiState);
    }

    [Fact]
    public void SetUiState_can_neither_set_nor_clear_the_Loading_bit()
    {
        var (svc, _) = BuildAtTitleScreen();
        SimOnLogin(svc);
        SimEnterWorld(svc);

        // A menu-state result that (erroneously) carries Loading cannot set it — the probe is the sole owner.
        svc.SetLoadingActive(false);
        svc.SetUiState(GameUIState.Loading | GameUIState.GameHud);
        Assert.Equal(GameUIState.GameHud, svc.UiState);   // Loading stripped, none live

        // ...and a menu recompute of None cannot clear a live loading screen.
        svc.SetLoadingActive(true);
        svc.SetUiState(GameUIState.None);
        Assert.Equal(GameUIState.Loading, svc.UiState);
    }

    [Fact]
    public void Loading_stays_set_across_an_in_world_zone_load()
    {
        // Real scenario: in-world zone load. IsWorldActive dips false → the gated menu-state probe is frozen
        // (no SetUiState), while the un-gated loading probe keeps reporting. Phase stays World throughout.
        var (svc, changes) = BuildAtTitleScreen();
        SimOnLogin(svc);
        SimEnterWorld(svc);
        svc.SetUiState(GameUIState.GameHud);              // stable world HUD
        changes.Clear();

        // Zone load begins: loading screen up (gated probe would be frozen — SetUiState not called).
        svc.SetLoadingActive(true);
        Assert.Equal(GameUIState.GameHud | GameUIState.Loading, svc.UiState);
        Assert.Equal(GamePhase.World, svc.Phase);         // phase steady across the load

        // Load ends: loading screen closes, gated probe resumes and recomputes.
        svc.SetLoadingActive(false);
        svc.SetUiState(GameUIState.GameHud);
        Assert.Equal(GameUIState.GameHud, svc.UiState);   // Loading OFF in stable world
        Assert.Empty(changes);                            // no phase churn
    }
}
