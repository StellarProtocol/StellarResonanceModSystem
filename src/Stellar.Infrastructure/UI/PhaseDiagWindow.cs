// DIAGNOSTIC — remove before merge.
//
// Throwaway in-game validation aid for the Game-Phases design (docs/game-phases-design.md §7).
// Renders a tiny uGUI window that displays the three new IClientState signals LIVE (the text rows
// are bound to value-funcs, so they re-pull each apply ~10 Hz): Phase, IsWorldActive, and UiState.
//
// It must show at the TITLE SCREEN so the tester can watch phase transitions and confirm the
// GameUIState cover-vs-overlay mappings, hence ShouldRender = () => true. Modelled on
// PerfOverlayWindow; registered by the Host next to the perf overlay, toggled by a hotkey.
using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI;

/// <summary>DIAGNOSTIC — remove before merge. Live readout of <see cref="IClientState.Phase"/>,
/// <see cref="IClientState.IsWorldActive"/> and <see cref="IClientState.UiState"/>.</summary>
internal sealed class PhaseDiagWindow
{
    // Single-bit flags of GameUIState, enumerated so the readout shows the exact bits set (avoids the
    // preset-mask aliasing that enum.ToString() would print, e.g. "GameHudHidden" for a bit combo) —
    // which is what the tester needs to validate the cover-vs-overlay mappings.
    private static readonly (GameUIState Flag, string Name)[] Bits =
    {
        (GameUIState.GameHud,        nameof(GameUIState.GameHud)),
        (GameUIState.FullScreenMenu, nameof(GameUIState.FullScreenMenu)),
        (GameUIState.MainMenu,       nameof(GameUIState.MainMenu)),
        (GameUIState.LineSelector,   nameof(GameUIState.LineSelector)),
        (GameUIState.Dialogue,       nameof(GameUIState.Dialogue)),
        (GameUIState.Cutscene,       nameof(GameUIState.Cutscene)),
        (GameUIState.Loading,        nameof(GameUIState.Loading)),
        (GameUIState.Matchmaking,    nameof(GameUIState.Matchmaking)),
    };

    private readonly IClientState _clientState;

    public PhaseDiagWindow(IClientState clientState) => _clientState = clientState;

    public WindowRegistration BuildRegistration()
    {
        // Distinctive id so this throwaway window is easy to spot / strip. StartVisible=false → hotkey-toggled.
        var spec = new WindowSpec("stellar.diag.phase", "Phase Diag",
            new WindowRect(40f, 275f, 340f, 0f), WindowCategory.Tools, WindowPanelStyle.GlassMenu)
        { ShouldRender = () => true, StartVisible = false, Draggable = true };

        var root = new ColumnElement(new HudElement[]
        {
            new TextElement(() => $"Phase         {_clientState.Phase}"),
            new TextElement(() => $"IsWorldActive {_clientState.IsWorldActive}"),
            new TextElement(() => $"UiState       {DescribeUiState(_clientState.UiState)}"),
        }, Gap: 4f);
        return new WindowRegistration(spec, root);
    }

    // Human-readable single-bit flag list, e.g. "GameHud|LineSelector"; "None" when no bits are set.
    private static string DescribeUiState(GameUIState state)
    {
        if (state == GameUIState.None) return nameof(GameUIState.None);
        string? s = null;
        foreach (var (flag, name) in Bits)
            if ((state & flag) != 0) s = s is null ? name : s + "|" + name;
        return s ?? ((int)state).ToString();   // fallback: unknown/appended bit set
    }
}
