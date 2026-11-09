using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → About: the version card, the COMMUNITY Discord link (AboutPanel.Community.cs) and "Where to find
/// things" shortcut pills that switch the hub tab. The developer
/// tools (Save HUD paths) moved to the hidden Developer tab (2026-11-09): clicking the version line 7 times in quick
/// succession turns developer mode on (<see cref="DeveloperUnlockCounter"/>), counting down in the footer from the 4th
/// click. The framework version comes from <see cref="FrameworkVersion.Value"/> so this panel and the BepInEx manifest
/// stay in lockstep.
/// </summary>
internal sealed partial class AboutPanel
{
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    private readonly ILocalization _loc;
    private readonly Func<int> _pluginCount;
    private readonly DeveloperModePrefs? _dev;
    private readonly DeveloperUnlockCounter _unlock = new();
    // Opens the Discord invite (AboutPanel.Community.cs); Application.OpenURL in the game, swappable for a test.
    internal Action<string> OpenUrl { get; set; } = OpenInBrowser;

    // theme is no longer read (the house kit carries the live colours); the parameter stays for the wiring.
    public AboutPanel(ITheme theme, ILocalization loc, Func<int>? pluginCount = null, DeveloperModePrefs? developerMode = null)
    {
        _loc = loc;
        _pluginCount = pluginCount ?? (() => 0);
        _dev = developerMode;
    }

    public HudElement Describe(SettingsKit k)
    {
        var s = k.S;
        // Bare selectable: the version line stays plain text (no pill look — it's an easter egg, not a control).
        var version = new SelectableElement(s.BoldText(() => _loc.TFormat("about.versionCard", FrameworkVersion.Value), 16),
            OnClick: () => VersionClicked(k)) { Bare = true };
        return new ColumnElement(new HudElement[]
        {
            k.Panel("about.framework", null, new RowElement(new HudElement[]
            {
                s.IconTile("about", StellarStyle.ToolIconSize),
                new CellElement(new ColumnElement(new HudElement[]
                {
                    version,
                    s.MutedLine(k.T("about.tagline.short")),
                }, Gap: 2f), Weight: 1f),
            }, Gap: 10f)),
            BuildCommunity(k),
            k.Panel("about.find", null,
                GoRow(k, "about.find.plugins", "tab.plugins", "plugins"),
                GoRow(k, "about.find.hotkeys", "tab.hotkeys", "hotkeys"),
                GoRow(k, "about.find.layout", "tab.layout", "layout")),
        }, Gap: 8f);
    }

    /// <summary>Header status: "Framework 2.30.0 · 16 plugins".</summary>
    public string Status(SettingsKit k) => _loc.TFormat("about.status", FrameworkVersion.Value, _pluginCount());

    private void VersionClicked(SettingsKit k)
    {
        if (_dev == null) return;
        if (_dev.Enabled) { _unlock.Reset(); k.Done(_loc.T("developer.fb.already")); return; }
        var left = _unlock.Click(Clock.Elapsed.TotalSeconds);
        if (left == 0)
        {
            _dev.Enabled = true;   // persists; the hub rail shows the Developer tab on its next refresh
            k.Done(_loc.T("developer.status"));
        }
        else if (left <= DeveloperUnlockCounter.ClicksNeeded - DeveloperUnlockCounter.CountdownFrom)
            k.Done(left == 1 ? _loc.T("developer.unlock.one") : _loc.TFormat("developer.unlock.more", left));
    }

    private static HudElement GoRow(SettingsKit k, string labelKey, string tabLabelKey, string tabKey)
        => SettingsKit.Row(k.Label(k.T(labelKey)),
            k.S.NeutralButton(k.T(tabLabelKey), () => k.GoToTab(tabKey), StellarStyle.PillPairWidth - 24f),
            SettingsKit.HelpGap());
}
