using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>The eight Settings drawers the hub shows (init properties: the analyzer caps constructors at 6 params).</summary>
internal sealed class SettingsHubPanels
{
    public GeneralPanel     General { get; init; } = null!;
    public PluginsPanel     Plugins { get; init; } = null!;
    public LayoutPanel      Layout  { get; init; } = null!;
    public ThemesPanel      Themes  { get; init; } = null!;
    public HotkeysPanel     Hotkeys { get; init; } = null!;
    public GameUiPanel      GameUi  { get; init; } = null!;
    public PerformancePanel Perf    { get; init; } = null!;
    public AboutPanel       About   { get; init; } = null!;
}

/// <summary>
/// The Stellar Settings hub, redesigned in the house style (approved prototype <c>framework-settings.html</c>, side-rail
/// option, 2026-11-09): a FIXED 800 × 612 GlassMenu window — Row[nav rail 172 | main], main = ToolHeader (tab icon +
/// name + the live status line from ONE builder, <see cref="Status"/>) / the tab body / the footer feedback line.
/// Lists scroll inside their panels, so switching tabs never grows or shrinks the window.
/// <para>Fixed height without a fixed-size window: the body Row carries a 1 px strut of <see cref="BodyHeight"/>, and
/// the rail and the main column each hold a <see cref="StellarStyle.VerticalFill"/> (flexibleHeight 1), so the Row
/// stretches both to the strut (a Row stretches a child whose flexibleHeight &gt; 0) and the slack lands in the
/// filler / the tab's growing list — the footer and the rail's version line sit at the bottom. Every tab body is
/// budgeted to fit <see cref="TabBodyHeight"/>; one that did not would grow the window.</para>
/// </summary>
internal sealed partial class SettingsHub
{
    public const string HelpWindowId = "stellar.settings.help";
    public const float WindowWidth = 800f;
    public const float WindowHeight = 612f;
    // 612 − 32 title bar − 2 × 12 GlassMenu body padding.
    public const float BodyHeight = WindowHeight - 32f - 24f;
    public const float RailWidth = 172f;
    private const float HeaderHeight = 40f;
    private const float FooterHeight = 22f;
    private const float MainGap = 8f;
    /// <summary>Height every tab body is laid out into (556 − header − footer − 2 gaps = 478).</summary>
    public const float TabBodyHeight = BodyHeight - HeaderHeight - FooterHeight - 2f * MainGap;
    /// <summary>Width of the main column (800 − 24 padding − 1 strut − rail − 10 gap).</summary>
    public const float MainWidth = WindowWidth - 24f - 1f - RailWidth - 10f;

    internal readonly struct TabDef
    {
        public readonly string Key, LabelKey, Icon, HintKey;
        public TabDef(string key, string labelKey, string icon, string hintKey)
        { Key = key; LabelKey = labelKey; Icon = icon; HintKey = hintKey; }
    }

    /// <summary>The tabs in rail order. <see cref="TabDef.Key"/> is the open-at-tab key
    /// (<see cref="SelectTab(string)"/>); the index is the STELLAR_SETTINGS_TAB test-hook value (0 = General …
    /// 7 = About since General was added first, 2026-11-09). Code dispatches by KEY, never by index.
    /// Every tab has its own glyph (icon set v2 "A", 2026-11-09): General = sliders, Performance = gauge; the gear stays
    /// reserved for the launcher's Settings action.</summary>
    internal static readonly TabDef[] Tabs =
    {
        new("general",     "tab.general",     "general",  "settings.foot.general"),
        new("plugins",     "tab.plugins",     "plugins",  "settings.foot.plugins"),
        new("layout",      "tab.layout",      "layout",   "settings.foot.layout"),
        new("themes",      "tab.themes",      "theme",    "settings.foot.themes"),
        new("hotkeys",     "tab.hotkeys",     "hotkeys",  "settings.foot.hotkeys"),
        new("gameui",      "tab.gameui",      "gameui",   "settings.foot.gameui"),
        new("performance", "tab.performance", "performance", "settings.foot.performance"),
        new("about",       "tab.about",       "about",    "settings.foot.about"),
    };

    private readonly SettingsKit _k;
    private readonly SettingsHubPanels _p;
    private int _tab;

    public SettingsHub(SettingsKit kit, SettingsHubPanels panels, int initialTab = 0)
    {
        _k = kit;
        _p = panels;
        _tab = Math.Clamp(initialTab, 0, Tabs.Length - 1);
        kit.GoToTab = key => SelectTab(key);
    }

    public int Tab => _tab;

    /// <summary>Selects a tab by index; closes the help popover and resets the tabs' transient state.</summary>
    public void SelectTab(int index)
    {
        index = Math.Clamp(index, 0, Tabs.Length - 1);
        if (index == _tab) return;
        _tab = index;
        _k.HelpPopover.Close();
        _k.RaiseTabChanged();
    }

    /// <summary>Selects a tab by key ("general", "plugins", "layout", "themes", "hotkeys", "gameui", "performance",
    /// "about");
    /// false (and no change) for an unknown key.</summary>
    public bool SelectTab(string key)
    {
        for (var i = 0; i < Tabs.Length; i++)
            if (string.Equals(Tabs[i].Key, key, StringComparison.OrdinalIgnoreCase)) { SelectTab(i); return true; }
        return false;
    }

    /// <summary>Per-tick work (framework tick, every phase): the help popover's first-open re-anchor and the colour
    /// editor child window's open-centred burst.</summary>
    public void Tick()
    {
        // Hub just closed: close the help popover (it would reappear with the hub) and drop transient tab state — above
        // all a running key capture, whose poll keeps listening while the window is hidden.
        var shown = HubShown();
        if (_wasShown && !shown) { _k.HelpPopover.Close(); _k.RaiseTabChanged(); }
        _wasShown = shown;
        _k.HelpPopover.Tick();
        _p.Themes.TickColoursWindow();
    }

    /// <summary>Whether the hub window is on screen (set by the wiring once the window is registered).</summary>
    public Func<bool> HubShown { get; set; } = () => true;
    private bool _wasShown;

    /// <summary>The window content: Row[1 px height strut, Row[rail, main]].</summary>
    public HudElement BuildRoot() => new RowElement(new HudElement[]
    {
        new SpacerElement(1f, BodyHeight),
        new RowElement(new HudElement[] { BuildRail(), BuildMain() }, Gap: 10f),
    }, Gap: 0.01f);

    private HudElement BuildMain()
    {
        var bodies = new List<HudElement>(Tabs.Length);
        for (var i = 0; i < Tabs.Length; i++)
        {
            var idx = i;
            // Fill: the active tab's Conditional takes the column's slack height and stretches the tab body to it, so
            // a tab's growing list panel fills down to the footer. Built lazily on the tab's first show.
            bodies.Add(new ConditionalElement(() => _tab == idx, DescribeTab(idx), Fill: true));
        }
        return new CellElement(new ColumnElement(new HudElement[]
        {
            BuildHeader(),
            new ColumnElement(bodies, Gap: 0.01f),
            _k.Feedback.Element(),
        }, Gap: MainGap), Weight: 1f);
    }

    private HudElement DescribeTab(int idx) => Tabs[idx].Key switch
    {
        "general"     => _p.General.Describe(_k),
        "plugins"     => _p.Plugins.Describe(_k),
        "layout"      => _p.Layout.Describe(_k),
        "themes"      => _p.Themes.Describe(_k),
        "hotkeys"     => _p.Hotkeys.Describe(_k),
        "gameui"      => _p.GameUi.Describe(_k),
        "performance" => _p.Perf.Describe(_k),
        _             => _p.About.Describe(_k),
    };

    // ToolHeader: 40 px accent tile with the tab's glyph (one Conditional per tab — an ImageElement loads its picture
    // once, so a changing icon is a set of pre-built tiles), bold tab name, the live status line.
    private HudElement BuildHeader()
    {
        var icons = new List<HudElement>(Tabs.Length);
        for (var i = 0; i < Tabs.Length; i++)
        {
            var idx = i;
            icons.Add(new ConditionalElement(() => _tab == idx, _k.S.IconTile(Tabs[idx].Icon, StellarStyle.ToolIconSize)));
        }
        return new RowElement(new HudElement[]
        {
            new CellElement(new ColumnElement(icons, Gap: 0.01f), Width: StellarStyle.ToolIconSize),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _k.Loc.T(Tabs[_tab].LabelKey), Color: () => _k.S.Text(), NoWrap: true, FontSize: 17)
                    { Bold = true, Ellipsize = true },
                // Rich text (coloured "on" / "failed" spans); NOT ellipsized — a cut could split a colour tag.
                new TextElement(Status, Color: () => _k.S.Muted(), NoWrap: true),
            }, Gap: 2f), Weight: 1f),
        }, Gap: 10f);
    }

    /// <summary>The idle footer hint of the current tab (prototype DESC_FOOT).</summary>
    public string FooterHint() => _k.Loc.T(Tabs[_tab].HintKey);
}
