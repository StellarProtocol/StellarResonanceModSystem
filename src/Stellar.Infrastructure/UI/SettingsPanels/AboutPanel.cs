using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → About: the version card, "Where to find things" shortcut pills that switch the hub tab, and the
/// developer Diagnostics row ("Save HUD paths" — the Canvas recon walk, moved here from Game UI). The framework version
/// comes from <see cref="FrameworkVersion.Value"/> so this panel and the BepInEx manifest stay in lockstep.
/// </summary>
internal sealed class AboutPanel
{
    private readonly ILocalization _loc;
    private readonly Func<int> _pluginCount;
    private readonly Action _saveHudPaths;

    // theme is no longer read (the house kit carries the live colours); the parameter stays for the wiring.
    public AboutPanel(ITheme theme, ILocalization loc, Func<int>? pluginCount = null, Action? saveHudPaths = null)
    {
        _loc = loc;
        _pluginCount = pluginCount ?? (() => 0);
        _saveHudPaths = saveHudPaths ?? (() => { });
    }

    public HudElement Describe(SettingsKit k)
    {
        var s = k.S;
        return new ColumnElement(new HudElement[]
        {
            k.Panel("about.framework", null, new RowElement(new HudElement[]
            {
                s.IconTile("about", StellarStyle.ToolIconSize),
                new CellElement(new ColumnElement(new HudElement[]
                {
                    s.BoldText(() => _loc.TFormat("about.versionCard", FrameworkVersion.Value), 16),
                    s.MutedLine(k.T("about.tagline.short")),
                }, Gap: 2f), Weight: 1f),
            }, Gap: 10f)),
            k.Panel("about.find", null,
                GoRow(k, "about.find.plugins", "tab.plugins", "plugins"),
                GoRow(k, "about.find.hotkeys", "tab.hotkeys", "hotkeys"),
                GoRow(k, "about.find.layout", "tab.layout", "layout")),
            k.Panel("about.diagnostics", k.T("about.diagnostics.hint"),
                SettingsKit.Row(k.Label(k.T("about.recon.label")),
                    s.NeutralButton(k.T("about.recon.button"), () => { _saveHudPaths(); k.Done(_loc.T("about.recon.done")); }, 116f),
                    k.Help("about.recon", "about.recon.button", "help.about.recon", "tab.about"))),
        }, Gap: 8f);
    }

    /// <summary>Header status: "Framework 2.30.0 · 16 plugins".</summary>
    public string Status(SettingsKit k) => _loc.TFormat("about.status", FrameworkVersion.Value, _pluginCount());

    private static HudElement GoRow(SettingsKit k, string labelKey, string tabLabelKey, string tabKey)
        => SettingsKit.Row(k.Label(k.T(labelKey)),
            k.S.NeutralButton(k.T(tabLabelKey), () => k.GoToTab(tabKey), StellarStyle.PillPairWidth - 24f),
            SettingsKit.HelpGap());
}
