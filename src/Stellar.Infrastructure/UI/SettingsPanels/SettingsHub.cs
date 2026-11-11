using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>The Settings drawers the hub shows (init properties: the analyzer caps constructors at 6 params).</summary>
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
    /// <summary>Hidden tab — only in the rail while developer mode is on.</summary>
    public DeveloperPanel   Developer { get; init; } = null!;
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
/// budgeted to fit <see cref="TabBodyHeight"/> at its PREFERRED height. The window itself is pinned too
/// (<see cref="WindowSpec.FixedHeight"/> in <see cref="SettingsHubSpec"/>): a body that runs over budget (a larger
/// font scale, a long locale) is squeezed toward its min — its list viewport gives way — and never grows the hub
/// (the Performance tab used to, 2026-11-09).</para>
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
    /// 7 = About since General was added first, 2026-11-09; 8 = the hidden Developer tab). Code dispatches by KEY, never
    /// by index. Developer stays LAST so hiding it never shifts another tab's index.
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
        new("developer",   "tab.developer",   "developer", "settings.foot.developer"),
    };

    private readonly SettingsKit _k;
    private readonly SettingsHubPanels _p;
    private int _tab;

    public SettingsHub(SettingsKit kit, SettingsHubPanels panels, int initialTab = 0)
    {
        _k = kit;
        _p = panels;
        _tab = Math.Clamp(initialTab, 0, Tabs.Length - 1);
        if (!IsTabVisible(_tab, panels.Developer?.IsOn == true)) _tab = AboutIndex;
        kit.GoToTab = key => SelectTab(key);
    }

    public int Tab => _tab;

    /// <summary>Selects a tab by index; closes the help popover and resets the tabs' transient state.</summary>
    public void SelectTab(int index)
    {
        index = Math.Clamp(index, 0, Tabs.Length - 1);
        if (!IsTabVisible(index, DeveloperOn)) return;   // the hidden Developer tab can't be selected while it's off
        if (index == _tab) return;
        _tab = index;
        _k.HelpPopover.Close();
        _k.RaiseTabChanged();
    }

    /// <summary>Selects a tab by key ("general", "plugins", "layout", "themes", "hotkeys", "gameui", "performance",
    /// "about", "developer");
    /// false (and no change) for an unknown key or the Developer tab while developer mode is off.</summary>
    public bool SelectTab(string key)
    {
        for (var i = 0; i < Tabs.Length; i++)
            if (string.Equals(Tabs[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsTabVisible(i, DeveloperOn)) return false;
                SelectTab(i);
                return true;
            }
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
        // Developer mode turned off (Turn off pill, or the config file edited) while on its tab → About.
        if (!IsTabVisible(_tab, DeveloperOn)) SelectTab(AboutIndex);
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
            // a tab's growing list panel fills down to the footer. Prebuild: every other tab is built (and, while the
            // hub is open, laid out off-screen) in the background after mount, a few ms per frame — a tab's first
            // visit cost 65-78 ms when it was built lazily on that click (measured 11-09). KeepAlive: a built tab is
            // parked (own canvas off, off-screen, bindings paused) instead of deactivated — with Prebuild alone every
            // switch still cost 45-65 ms re-enabling + re-laying-out the tab (measured 11-09). The bodies column holds
            // only these 9 Conditionals: a KeepAlive set must be alone in its slot (WindowBuilder.KeepAlive.cs).
            // ContentRevision: a parked tab whose data changes while parked is refreshed in the background (Game UI: its
            // HUD rows resolve only after world entry).
            bodies.Add(new ConditionalElement(() => _tab == idx, DescribeTab(idx), Fill: true)
                { Prebuild = true, KeepAlive = true, PrebuildName = "settings." + Tabs[idx].Key, ContentRevision = TabRevision(idx) });
        }
        return new CellElement(new ColumnElement(new HudElement[]
        {
            BuildHeader(),
            new ColumnElement(bodies, Gap: 0.01f),
            _k.Feedback.Element(),
        }, Gap: MainGap), Weight: 1f);
    }

    // A tab's ConditionalElement.ContentRevision (null = its data doesn't change while parked in a way worth a refresh).
    private Func<int>? TabRevision(int idx) => Tabs[idx].Key == "gameui" ? _p.GameUi.ContentRevision : null;

    private HudElement DescribeTab(int idx) => Tabs[idx].Key switch
    {
        "general"     => _p.General.Describe(_k),
        "plugins"     => _p.Plugins.Describe(_k),
        "layout"      => _p.Layout.Describe(_k),
        "themes"      => _p.Themes.Describe(_k),
        "hotkeys"     => _p.Hotkeys.Describe(_k),
        "gameui"      => _p.GameUi.Describe(_k),
        "performance" => _p.Perf.Describe(_k),
        "developer"   => _p.Developer.Describe(_k),
        _             => _p.About.Describe(_k),
    };

    // ToolHeader: 40 px accent tile with the tab's glyph, bold tab name, the live status line. ONE Live tile whose picture
    // follows the tab (ImageElement.Live swaps the texture when the cached glyph array changes — and re-picks the
    // pre-drawn size on a canvas-scale change: 24 units → 24 px at 1440p, 18 → 24 mipped at 1080p). It was one
    // Conditional tile per tab: each switch flipped two of them = a structural whole-window re-layout (11-09).
    private static readonly float HeaderGlyph = StellarStyle.IconTileGlyph(StellarStyle.ToolIconSize);

    private HudElement BuildHeader()
    {
        return new RowElement(new HudElement[]
        {
            _k.S.IconTile(() => TabIcon(_tab, HeaderGlyph), StellarStyle.ToolIconSize, live: true),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _k.Loc.T(Tabs[_tab].LabelKey), Color: () => _k.S.Text(), NoWrap: true, FontSize: 17)
                    { Bold = true, Ellipsize = true },
                // Rich text (coloured "on" / "failed" spans); NOT ellipsized — a cut could split a colour tag.
                new TextElement(Status, Color: () => _k.S.Muted(), NoWrap: true),
            }, Gap: 2f), Weight: 1f),
        }, Gap: 10f);
    }

    /// <summary>The idle footer hint of the current tab (prototype DESC_FOOT). The About and Layout hints name a hotkey:
    /// they show its live binding (or a no-key line once it is cleared), never a hard-coded default.</summary>
    public string FooterHint() => Tabs[_tab].Key switch
    {
        "about"  => _k.Keys.Text(FrameworkHotkeyIds.SettingsToggle, Tabs[_tab].HintKey, "settings.foot.about.unbound"),
        "layout" => _k.Keys.Text(FrameworkHotkeyIds.LayoutEdit, Tabs[_tab].HintKey, "settings.foot.layout.unbound"),
        _        => _k.Loc.T(Tabs[_tab].HintKey),
    };
}
