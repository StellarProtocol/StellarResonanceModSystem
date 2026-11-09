using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

// The nav rail (a Panel bubble, r6, 8 / 6 padding): one 34 px row per tab [24 px icon tile, label, (red dot)], the active
// row on the accent wash with an accent tile and a bold accent label, the rest a Neutral tile + normal label with a
// Tile hover (the Developer row only while developer mode is on); the muted "Stellar <version>[ · dev]" at the
// bottom. Plus the ONE status builder the header calls.
internal sealed partial class SettingsHub
{
    private const float RailItemHeight = 34f;
    private const float RailIconSize = 24f;
    private const float RailGlyph = 15f;
    private const float RailPadX = 6f;
    private const float DotSize = 8f;

    private HudElement BuildRail()
    {
        var items = new List<HudElement>(Tabs.Length);
        for (var i = 0; i < Tabs.Length; i++)
        {
            var idx = i;
            // A hidden tab's row is wrapped in a live Conditional, so the rail updates the moment the flag flips.
            items.Add(Tabs[i].Key == DeveloperKey ? new ConditionalElement(() => IsTabVisible(idx, DeveloperOn), RailItem(i)) : RailItem(i));
        }
        return new CellElement(new BubbleElement(new ColumnElement(new HudElement[]
        {
            new ColumnElement(items, Gap: 2f),
            StellarStyle.VerticalFill(),   // pushes the version line to the bottom of the fixed-height rail
            new RowElement(new HudElement[]
            {
                new SpacerElement(RailPadX, 1f),
                new TextElement(() => RailVersion(FrameworkVersion.Value, DeveloperOn), Color: () => _k.S.Muted(), NoWrap: true),
            }, Gap: 0.01f),
        }, Gap: 0.01f))
        {
            FillColorFn = () => _k.S.Fill(_k.S.Panel()), CornerRadius = StellarStyle.TileRadius, PaddingX = 6f, PaddingY = 8f,
        }, Width: RailWidth);
    }

    // Bold can't toggle live, so active / inactive are two COMPLETE rows swapped by a Conditional wrapping the whole
    // bubble (SDL §6); both share one hover flag. The glyph tile's fill is live, the glyph PNG is the same white art.
    private HudElement RailItem(int idx)
    {
        var hover = new StellarStyle.HoverFlag();
        var s = _k.S;
        HudElement Variant(bool active)
        {
            var tile = s.FilledTile(new ImageElement(() => TabIcon(idx), (int)RailGlyph, (int)RailGlyph),
                active ? (System.Func<ColorRgba>)s.Accent : s.Neutral, RailIconSize, 6f);
            var label = new TextElement(() => _k.Loc.T(Tabs[idx].LabelKey),
                Color: active ? (System.Func<ColorRgba?>)(() => s.Readable(s.Accent(), s.AccentWash())) : () => s.Text(), NoWrap: true)
                { Bold = active, Ellipsize = true };
            var row = new List<HudElement>
            {
                new SpacerElement(RailPadX, RailItemHeight),
                tile,
                new CellElement(label, Weight: 1f),
            };
            if (Tabs[idx].Key == "plugins") row.Add(new ConditionalElement(() => _p.Plugins.AnyFailed(), FailedDot()));
            row.Add(new SpacerElement(RailPadX, 1f));
            return new BubbleElement(new SelectableElement(new RowElement(row, Gap: 8f), OnClick: () => SelectTab(idx))
                { Bare = true, OnHover = over => hover.On = over })
            {
                FillColorFn = active
                    ? (System.Func<ColorRgba?>)(() => s.Fill(s.AccentWash()))
                    : () => hover.On ? s.Fill(s.Tile()) : StellarStyle.Transparent,
                CornerRadius = StellarStyle.TileRadius, PaddingX = 0f, PaddingY = 0f,
            };
        }
        return new ConditionalElement(() => _tab == idx, Variant(true), Variant(false));
    }

    private HudElement FailedDot() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(DotSize, DotSize),
        }, Gap: 0.01f))
    {
        FillColorFn = () => _k.S.Fill(_k.S.Danger()), CornerRadius = StellarStyle.RoundRadius(DotSize),
        PaddingX = 0f, PaddingY = 0f, MaxWidth = DotSize,
    }, Width: DotSize);

    /// <summary>The live status line under the tab name — the ONE status builder (prototype <c>status()</c>), so every
    /// place that shows a tab's state reads the same text. Rich text: "on" in Success, failures in Danger.</summary>
    public string Status() => Tabs[_tab].Key switch
    {
        "general"     => _p.General.Status(_k),
        "plugins"     => _p.Plugins.Status(_k),
        "layout"      => _p.Layout.Status(_k),
        "themes"      => _p.Themes.Status(_k),
        "hotkeys"     => _p.Hotkeys.Status(_k),
        "gameui"      => _p.GameUi.Status(_k),
        "performance" => _p.Perf.Status(_k),
        "developer"   => _p.Developer.Status(_k),
        _             => _p.About.Status(_k),
    };

    // ── the hidden Developer tab (developer mode, 2026-11-09) ─────────────────────────────────────────────────────
    internal const string DeveloperKey = "developer";
    private const string IconFallback = "settings";
    // A property, not a static field: field initializers across partial files run in no guaranteed order (Tabs may be null).
    private static int AboutIndex => IndexOf("about");

    private bool DeveloperOn => _p.Developer?.IsOn == true;

    /// <summary>Whether tab <paramref name="idx"/> is in the rail: every tab except Developer, which needs developer mode.</summary>
    internal static bool IsTabVisible(int idx, bool developerMode)
        => idx >= 0 && idx < Tabs.Length && (developerMode || Tabs[idx].Key != DeveloperKey);

    /// <summary>The rail's tab keys in order for a given developer-mode state (tests pin the hidden tab's placement).</summary>
    internal static List<string> VisibleTabKeys(bool developerMode)
    {
        var keys = new List<string>(Tabs.Length);
        for (var i = 0; i < Tabs.Length; i++) if (IsTabVisible(i, developerMode)) keys.Add(Tabs[i].Key);
        return keys;
    }

    /// <summary>The rail's bottom line: "Stellar 2.30.0", plus " · dev" while developer mode is on.</summary>
    internal static string RailVersion(string version, bool developerMode)
        => developerMode ? "Stellar " + version + " · dev" : "Stellar " + version;

    // A tab glyph that isn't embedded yet (developer.png is still being drawn) falls back to the gear, never a blank tile.
    private static byte[]? TabIcon(int idx) => LauncherIcons.Get(Tabs[idx].Icon) ?? LauncherIcons.Get(IconFallback);

    private static int IndexOf(string key)
    {
        for (var i = 0; i < Tabs.Length; i++) if (Tabs[i].Key == key) return i;
        return 0;
    }
}
