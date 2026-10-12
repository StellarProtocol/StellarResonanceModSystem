using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Catalog keys for the descriptions of the framework's OWN hotkey actions (<see cref="FrameworkHotkeyIds"/>).
/// Those actions are declared during wiring — before the hot-update assemblies / <c>Game.Init</c> — when the
/// client language is not safely readable (a first read that early can latch the wrong language index for the
/// whole session), so the wiring declares them with an English description and <see cref="HotkeysPanel"/>
/// relabels them here RENDER-driven: the first time a Hotkeys row is evaluated for display (only reachable
/// while the Settings window is shown on its Hotkeys tab), and again on every
/// <see cref="ILocalization.LanguageChanged"/>. Same pattern as the theme-colour labels
/// (<c>FrameworkColorRegistration.RelabelAll</c>).
/// </summary>
internal static class FrameworkHotkeyLabels
{
    /// <summary>Action id → catalog key. Internal so a test can assert it covers EXACTLY the framework's ids:
    /// an id missing here would silently stay English.</summary>
    internal static readonly IReadOnlyDictionary<string, string> DescriptionKeys = new Dictionary<string, string>
    {
        [FrameworkHotkeyIds.LayoutEdit]     = "hotkey.framework.layoutEdit",
        [FrameworkHotkeyIds.HudToggle]      = "hotkey.framework.hudToggle",
        [FrameworkHotkeyIds.HudHold]        = "hotkey.framework.hudHold",
        [FrameworkHotkeyIds.PerfToggle]     = "hotkey.framework.perfToggle",
        [FrameworkHotkeyIds.SettingsToggle] = "settings.hotkey.toggle",
    };

    /// <summary>Re-applies every framework action's description in the active language. Reads the language
    /// (via <see cref="ILocalization.T"/>) — call it ONLY from a render-driven or language-switch path, never
    /// from a per-tick one. An id not (yet) declared is a no-op inside <see cref="HotkeyService.Relabel"/>.</summary>
    public static void RelabelAll(HotkeyService hotkeys, ILocalization loc)
    {
        foreach (var (id, key) in DescriptionKeys)
            hotkeys.Relabel(id, loc.T(key));
    }
}
