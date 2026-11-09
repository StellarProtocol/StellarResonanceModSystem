using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI;

// The grid panel's tab row (Favorites / All plugins chips with count badges + the "Star = favorite" hint) and the two
// empty states (no favorites yet, no Find match).
internal sealed partial class LauncherView
{
    private const float TabHeight = 24f;       // prototype .tchip
    private const float TabPadX = 10f;
    private const float TabCap = 200f;         // explicit MaxWidth: a cap-0 Bubble in a Conditional caps itself (WB-Patterns)
    private const float BadgeHeight = 16f;     // prototype .tchip .n
    private const float BadgeWidth = 24f;      // fixed (fits "32") so a count change never moves the next chip (R10)
    private const int TabFont = 12;

    private HudElement BuildTabRow() => new RowElement(new HudElement[]
    {
        TabChip(LauncherTab.Favorites, () => T("launcher.tab.favorites"), () => _model.FavoriteCount, star: true),
        TabChip(LauncherTab.All, () => T("launcher.tab.all"), () => _model.AllCount, star: false),
        new SpacerElement(0f),   // flexible: pushes the hint to the right edge
        Small(() => T("launcher.tab.hint"), () => _s.Muted(), TabFont),
    }, Gap: 4f);

    // One tab chip. Bold can't toggle live, so on/off are two COMPLETE chips in one Conditional sharing a hover flag
    // (SDL §6). On = accent wash + bold accent label; off = muted label, hover = the tile fill + text colour.
    private HudElement TabChip(LauncherTab tab, Func<string> label, Func<int> count, bool star)
    {
        var hover = new StellarStyle.HoverFlag();
        bool On() => _model.Tab == tab;
        ColorRgba OnLabel() => StellarStyle.EnsureContrast(_s.Accent(), _s.AccentWash(), StellarStyle.SolidMinContrast);
        ColorRgba OffLabel() => hover.On ? _s.Text() : _s.Muted();

        var onChip = TabShell(
            TabContent(new TextElement(label, () => OnLabel(), NoWrap: true, FontSize: TabFont) { Bold = true },
                star ? OnLabel : null, count),
            () => _s.AccentWash(), tab, hover);
        var offChip = TabShell(
            TabContent(Small(label, () => OffLabel(), TabFont), star ? OffLabel : null, count),
            () => hover.On ? _s.Tile() : StellarStyle.Transparent, tab, hover);
        return new ConditionalElement(On, onChip, offChip);
    }

    // Row[strut, (★), label, badge, strut] — the 24 px struts make the chip exactly 24 tall (R4); strut 5 + gap 5 = 10.
    private HudElement TabContent(HudElement label, Func<ColorRgba>? starTint, Func<int> count)
    {
        var items = new System.Collections.Generic.List<HudElement>(5) { new SpacerElement(TabPadX - 5f, TabHeight) };
        if (starTint != null) items.Add(Glyph("star", 12, starTint));
        items.Add(label);
        items.Add(CountBadge(count));
        items.Add(new SpacerElement(TabPadX - 5f, TabHeight));
        return new RowElement(items, Gap: 5f);
    }

    // Bubble{r11} → Bare Selectable → content; the hover is the chip's own round fill (R2).
    private HudElement TabShell(HudElement content, Func<ColorRgba> fill, LauncherTab tab, StellarStyle.HoverFlag hover)
        => new BubbleElement(new SelectableElement(content, OnClick: () => _model.SelectTab(tab))
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => { var f = fill(); return f.A <= 0f ? StellarStyle.Transparent : _s.Fill(f); },
            CornerRadius = StellarStyle.RoundRadius(TabHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = TabCap,
        };

    // The small rounded count pill inside a tab chip (ChipOff fill, bold 11 text colour).
    private HudElement CountBadge(Func<int> count) => new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, BadgeHeight),
            new TextElement(() => count().ToString(), () => _s.Text(), Width: BadgeWidth, Align: TextAlign.Center,
                NoWrap: true, FontSize: 11) { Bold = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => _s.Fill(_s.ChipOff()), CornerRadius = StellarStyle.RoundRadius(BadgeHeight),
        PaddingX = 0f, PaddingY = 0f, MaxWidth = BadgeWidth,
    };

    // ── empty states (prototype .empty: a muted box 84 tall; the favorites one 120) ──────────────────────────────
    // ⚠️ The prototype's border is DASHED; the toolkit's Bubble outline is a solid 1 px ring, so this draws solid.
    private HudElement EmptyBox(float height, HudElement content) => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(1f, height),   // height strut (width > 0: a 0-wide spacer in a Row is stretchy)
            new CellElement(content, Weight: 1f),
        }, Gap: 0.01f))
    {
        FillColorFn = () => StellarStyle.Transparent,
        OutlineColorFn = () => _s.Fill(_s.ChipOff()),
        CornerRadius = StellarStyle.TileRadius, PaddingX = 10f, PaddingY = 0f, MaxWidth = GridWidth,
    }, Width: GridWidth);

    private HudElement NoMatchBox() => EmptyBox(TileHeight,
        Small(() => F("launcher.find.none", _model.Query.Trim()), () => _s.Muted(), TabFont, TextAlign.Center, maxLines: 2));

    // "No favorites yet. / Star a plugin in [All plugins] to add it here / and to the compact bar." — the link is a
    // Bare selectable in the accent colour that switches to the All plugins tab.
    private HudElement FavoritesEmptyBox()
    {
        string Before() => SplitPart("launcher.favorites.empty.link", 0);
        string After() => SplitPart("launcher.favorites.empty.link", 1);
        var link = new SelectableElement(
            new TextElement(() => T("launcher.tab.all"),
                () => StellarStyle.EnsureContrast(_s.Accent(), _s.Panel(), StellarStyle.SolidMinContrast),
                NoWrap: true, FontSize: TabFont) { Bold = true },
            OnClick: () => _model.SelectTab(LauncherTab.All)) { Bare = true };
        return EmptyBox(120f, new ColumnElement(new HudElement[]
        {
            Centered(Small(() => T("launcher.favorites.empty.title"), () => _s.Muted(), TabFont)),
            new RowElement(new HudElement[]
            {
                Small(Before, () => _s.Muted(), TabFont),   // no Conditional inside a Bubble (WB-Patterns: re-measure churn)
                link,
                Small(After, () => _s.Muted(), TabFont),
            }, Gap: 4f, Justify: RowJustify.Center),
            Centered(Small(() => T("launcher.favorites.empty.tail"), () => _s.Muted(), TabFont)),
        }, Gap: 2f));
    }

    // Part 0 (before "{0}") or 1 (after it) of a link template, trimmed (the row gap is the space).
    private string SplitPart(string key, int part)
    {
        var s = T(key);
        var at = s.IndexOf("{0}", StringComparison.Ordinal);
        if (at < 0) return part == 0 ? s.Trim() : "";
        return (part == 0 ? s.Substring(0, at) : s.Substring(at + 3)).Trim();
    }

    private static HudElement Centered(HudElement e) => new RowElement(new[] { e }, Gap: 0.01f, Justify: RowJustify.Center);
}
