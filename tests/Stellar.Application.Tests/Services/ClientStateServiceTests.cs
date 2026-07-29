using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>
/// Pins the 3-phase client-lifecycle model (<see cref="GamePhase.TitleScreen"/> →
/// <see cref="GamePhase.CharSelect"/> → <see cref="GamePhase.World"/>) that <see cref="ClientStateService"/>
/// enacts. The service is a dumb transition sink; the edge decisions (which game event drives which
/// transition, and the TitleScreen-guard on OnLogin) live in the Host. The <c>Sim*</c> helpers below
/// replay those Host edges so the whole model is covered here.
///
/// Empirical ground truth (live diagnostic overlay, 2026-07-29): the game's OnLogin fires when the
/// character-select screen appears (IsLoggedIn → true there, NOT at world-connect); the Unity scene name
/// does not change between title and char-select; cancelling char-select back to title fires OnLogout.
/// </summary>
public sealed class ClientStateServiceTests
{
    // --- Host edge replays (mirror Wiring.Wire.cs / Wiring.GameLoop.cs) ---

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

    private static (ClientStateService svc, List<PhaseChange> changes) Build()
    {
        var svc = new ClientStateService();
        var changes = new List<PhaseChange>();
        svc.PhaseChanged += c => changes.Add(c);
        return (svc, changes);
    }

    [Fact]
    public void Boot_phase_is_TitleScreen()
    {
        var (svc, _) = Build();
        Assert.Equal(GamePhase.TitleScreen, svc.Phase);
    }

    [Fact]
    public void OnLogin_moves_TitleScreen_to_CharSelect()
    {
        var (svc, changes) = Build();

        SimOnLogin(svc);

        Assert.Equal(GamePhase.CharSelect, svc.Phase);
        Assert.True(svc.IsLoggedIn);
        Assert.Single(changes);
        Assert.Equal(new PhaseChange(GamePhase.TitleScreen, GamePhase.CharSelect), changes[0]);
    }

    [Fact]
    public void Full_login_sequence_walks_TitleScreen_CharSelect_World()
    {
        var (svc, changes) = Build();

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
        var (svc, changes) = Build();

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
        var (svc, changes) = Build();

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
        var (svc, changes) = Build();
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
        var (svc, changes) = Build();
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
        var (svc, changes) = Build();

        svc.RaisePhase(GamePhase.TitleScreen);   // already TitleScreen

        Assert.Equal(GamePhase.TitleScreen, svc.Phase);
        Assert.Empty(changes);
    }

    [Fact]
    public void Leaving_World_clears_UiState_to_None()
    {
        var (svc, _) = Build();
        SimOnLogin(svc);
        SimEnterWorld(svc);
        svc.SetUiState(GameUIState.FullScreenMenu | GameUIState.GameHud);

        SimOnLogout(svc);

        Assert.Equal(GameUIState.None, svc.UiState);
    }

    [Fact]
    public void UiState_is_None_at_CharSelect()
    {
        var (svc, _) = Build();

        SimOnLogin(svc);

        // No in-world UI at char-select — UiState never leaves None.
        Assert.Equal(GameUIState.None, svc.UiState);
    }
}
