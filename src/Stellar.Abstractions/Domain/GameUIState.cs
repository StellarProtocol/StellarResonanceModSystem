using System;

namespace Stellar.Abstractions.Domain;

/// <summary>
/// Informational flags describing the game's in-world UI state. The framework <b>detects and exposes</b>
/// this (via <see cref="Services.IClientState.UiState"/>); it <b>never gates</b> on it. A plugin's
/// <see cref="IRenderGated.ShouldRender"/> optionally reads it (e.g. hide a gameplay HUD while a menu covers it).
///
/// <para><b>Scope:</b> describes <i>in-world</i> UI and is <see cref="None"/> while
/// <see cref="Services.IClientState.Phase"/> is <see cref="GamePhase.TitleScreen"/> — there is no in-game
/// HUD/menu at the title / login / character-select screens. Use <see cref="GamePhase"/> for "at the login
/// screen," not a <see cref="GameUIState"/> value.</para>
///
/// <para><b>Flat flags</b> (no base/overlay structure) — the game's UI layers genuinely co-occur (e.g. the
/// line selector stays open over a valid HUD: <c>GameHud | LineSelector</c>). Preset masks encode the
/// cover-vs-overlay knowledge as named values so plugins don't memorize bits. Backing <see cref="int"/> keeps
/// 32-bit headroom; new bits are append-only and non-breaking.</para>
/// </summary>
[Flags]
public enum GameUIState
{
    /// <summary>No in-world UI state (also the value while at the title / login / character-select screens).</summary>
    None           = 0,

    /// <summary>Gameplay HUD on-screen.</summary>
    GameHud        = 1 << 0,
    /// <summary>Inventory / map / character / gear / skills — covers the HUD.</summary>
    FullScreenMenu = 1 << 1,
    /// <summary>ESC functions list (main menu).</summary>
    MainMenu       = 1 << 2,
    /// <summary>SwitchLine panel — OVERLAYS the HUD (co-occurs with <see cref="GameHud"/>).</summary>
    LineSelector   = 1 << 3,
    /// <summary>NPC talk / dialogue.</summary>
    Dialogue       = 1 << 4,
    /// <summary>Story cutscene video / top overlay.</summary>
    Cutscene       = 1 << 5,
    /// <summary>Loading screen.</summary>
    Loading        = 1 << 6,
    /// <summary>Match-pop confirm (dungeon / world-boss queue).</summary>
    Matchmaking    = 1 << 7,

    // ── preset masks (provisional membership — verify cover-vs-overlay in-game) ──

    /// <summary>UIs that REPLACE the HUD (not <see cref="LineSelector"/>, which overlays it).</summary>
    GameHudHidden = FullScreenMenu | Cutscene | Loading,
    /// <summary>Any menu-like surface.</summary>
    AnyMenu       = FullScreenMenu | MainMenu | LineSelector,
    /// <summary>Surfaces that block normal gameplay input/attention.</summary>
    Blocking      = FullScreenMenu | MainMenu | Dialogue | Cutscene | Loading | Matchmaking,
}
