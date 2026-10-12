namespace Stellar.Application.Services;

/// <summary>
/// The ids of the hotkey actions the framework declares for itself (straight on <see cref="HotkeyService"/>,
/// so they carry no owning PluginId). One source for the wiring that declares them, the lockout safety net
/// (<see cref="HotkeyService.RestoreSettingsHotkeyIfLocked"/>) and the Infrastructure map that relabels their
/// descriptions into the active language. These ids are persisted as config keys
/// (<c>hotkeys.bindings.&lt;id&gt;.*</c>) — NEVER change a value, or every player's saved binding is orphaned.
/// </summary>
internal static class FrameworkHotkeyIds
{
    /// <summary>Alt+E — toggles layout edit mode.</summary>
    public const string LayoutEdit = "framework.layout-edit";

    /// <summary>Alt+H — toggles every HUD-category overlay.</summary>
    public const string HudToggle = "framework.hud-toggle";

    /// <summary>Unbound by default — hides every HUD-category overlay while held.</summary>
    public const string HudHold = "framework.hud-hold";

    /// <summary>Shift+End — toggles the developer perf overlay.</summary>
    public const string PerfToggle = "framework.perf-toggle";

    /// <summary>Shift+Home — toggles the Stellar launcher / Settings.</summary>
    public const string SettingsToggle = "framework.settings-toggle";
}
