using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Developer (approved prototype, 2026-11-09): the hidden tab that only exists while
/// <see cref="DeveloperModePrefs.Enabled"/> is on (unlocked by clicking the version on About 7 times, or
/// <c>"developerMode": true</c> in the framework config). Three panels: Game UI paths ("Save paths" — the HUD recon
/// walk that used to sit in About → Diagnostics), Performance (the Shift+End perf overlay as a toggle that reflects
/// its live state), and Developer mode ("Turn off" — clears the flag; the hub then drops the tab and goes to About).
/// </summary>
internal sealed class DeveloperPanel
{
    private readonly ILocalization _loc;
    private readonly DeveloperModePrefs _dev;
    private readonly Action _saveHudPaths;
    private readonly Func<bool> _perfShown;
    private readonly Action<bool> _setPerfShown;

    public DeveloperPanel(ILocalization loc, DeveloperModePrefs dev, Action saveHudPaths, Func<bool> perfShown,
        Action<bool> setPerfShown)
    {
        _loc = loc;
        _dev = dev;
        _saveHudPaths = saveHudPaths;
        _perfShown = perfShown;
        _setPerfShown = setPerfShown;
    }

    /// <summary>Whether the tab is available (the hub's rail + tab guard read this every refresh; a cached bool).</summary>
    public bool IsOn => _dev.Enabled;

    public HudElement Describe(SettingsKit k)
    {
        var s = k.S;
        const string section = "tab.developer";
        return new ColumnElement(new HudElement[]
        {
            k.Panel("developer.gameui", k.T("developer.gameui.hint"),
                SettingsKit.Row(k.Label(k.T("developer.recon.label")),
                    s.NeutralButton(k.T("developer.recon.button"),
                        () => { _saveHudPaths(); k.Done(_loc.T("developer.recon.done")); }, 116f),
                    k.Help("developer.recon", "developer.recon.label", "help.developer.recon", section))),
            // The overlay's own window state is the source of truth (Shift+End or its ✕ change it too), so the toggle
            // reads it live instead of keeping a copy.
            k.Panel("tab.performance", null,
                SettingsKit.Row(k.Label(k.T("developer.perf.label")),
                    SettingsKit.Toggle(_perfShown, _setPerfShown),
                    k.Help("developer.perf", "developer.perf.label", "help.developer.perf", section))),
            k.Panel("developer.mode", null,
                SettingsKit.Row(k.Label(k.T("developer.off.label")),
                    s.NeutralButton(k.T("developer.off.button"), () => TurnOff(k), 116f),
                    k.Help("developer.off", "developer.off.label", "help.developer.off", section))),
        }, Gap: 8f);
    }

    /// <summary>Header status: "Developer mode on".</summary>
    public string Status(SettingsKit k) => _loc.T("developer.status");

    // Clear the flag first: the hub's tick sees the tab is gone and moves to About (GoToTab here does it at once, so
    // the click never lands on a frame where the Developer body is shown without its tab).
    private void TurnOff(SettingsKit k)
    {
        _dev.Enabled = false;
        k.GoToTab("about");
        k.Done(_loc.T("developer.fb.off"));
    }
}
