using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI;

// The Full menu: header, Find field, the tabbed tile grid panel, Settings / Edit layout pills + version, feedback line.
internal sealed partial class LauncherView
{
    // Geometry (prototype .full: 400 wide). The GlassMenu body padding is a fixed 12, so the content is 376.
    private const float FullContentWidth = 376f;
    private const int Cols = 4;
    private const int MaxRows = 8;            // tile pool: 8 × 4 = 32 plugins (the old launcher's capacity)
    private const int ReservedRows = 4;       // the grid always takes 4 rows of height and scrolls beyond
    private const float GridGap = 6f;
    // ScrollElement insets its viewport 9 px for the scrollbar: panel inner 356 − 9 = 347 ≥ 4 × 82 + 3 × 6 = 346.
    private const float TileWidth = 82f;
    private const float GridWidth = Cols * TileWidth + (Cols - 1) * GridGap;   // 346
    private const float TileHeight = 84f;
    private const float GridHeight = ReservedRows * TileHeight + (ReservedRows - 1) * GridGap;   // 354
    private const float HeaderLogo = 34f;
    private const float PillIcon = 15f;

    private HudElement BuildFull()
    {
        var body = new List<HudElement>
        {
            BuildHeader(),
            new ConditionalElement(() => _model.FindVisible, BuildFind()),
            _s.SectionPanel(null, null, BuildTabRow(), BuildGrid()),
            BuildBottomRow(),
            _feedback.Element(),
        };
        // The fixed-width cell sets the content-sized window to exactly 400 (376 + 2 × 12) in every state.
        return new CellElement(new ColumnElement(body, Gap: 8f), Width: FullContentWidth);
    }

    // ── header: logo · "Stellar" over the live status · compact-mode · ✕ ──────────────────────────────────────────
    private HudElement BuildHeader() => new RowElement(new HudElement[]
    {
        new BrandLogoElement(() => Icon("stellar"), () => Icon("stellar-glow"), Size: HeaderLogo),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Stellar", () => _s.Text(), NoWrap: true, FontSize: 17) { Bold = true },
            BuildStatusLine(),
        }, Gap: 2f), Weight: 1f),
        _s.IconButton(() => Icon("mode_list"), () => SwitchMode(LauncherMode.Minimal)),
        _s.CloseIconButton(_actions.Close, () => _s.Background()),
    }, Gap: 10f);

    // "N plugins · N favorites · N failed to load": the last part only when > 0, red, bold, opens Settings › Plugins.
    private HudElement BuildStatusLine()
    {
        var link = new SelectableElement(
            new TextElement(() => F("launcher.status.failed", _failedCount()),
                () => _s.Readable(_s.Danger(), _s.Background()), NoWrap: true, FontSize: 12) { Bold = true },
            OnClick: () => _actions.OpenSettings("plugins")) { Bare = true };
        return new RowElement(new HudElement[]
        {
            Small(() => F("launcher.status.plugins", _model.AllCount) + " · " + F("launcher.status.favorites", _model.FavoriteCount),
                () => _s.Muted(), 12),
            new ConditionalElement(() => _failedCount() > 0, new RowElement(new HudElement[]
            {
                Small(() => "·", () => _s.Muted(), 12),
                link,
            }, Gap: 4f)),
        }, Gap: 4f);
    }

    // ── Find (only above 16 plugins) ─────────────────────────────────────────────────────────────────────────────
    private HudElement BuildFind() => new InputElement(
        Get: () => _model.Query, Submit: q => _model.Query = q, Width: FullContentWidth, OnChange: q => _model.Query = q)
    {
        SingleLine = true,
        Placeholder = () => T("launcher.find.placeholder"),
    };

    // ── grid: empty states + a pool of 8 rows × 4 tiles, 4 rows tall, scrolling beyond ───────────────────────────
    private HudElement BuildGrid()
    {
        var rows = new List<HudElement>(MaxRows + 2)
        {
            new ConditionalElement(() => _model.NoMatch, NoMatchBox()),
            new ConditionalElement(() => _model.FavoritesEmpty, FavoritesEmptyBox()),
        };
        for (var r = 0; r < MaxRows; r++)
        {
            var start = r * Cols;
            var tiles = new HudElement[Cols];
            for (var c = 0; c < Cols; c++)
            {
                var i = start + c;
                tiles[c] = new ConditionalElement(() => i < _model.ShownCount, BuildGridTile(i));
            }
            rows.Add(new ConditionalElement(() => start < _model.ShownCount, new RowElement(tiles, Gap: GridGap)));
        }
        return new ScrollElement(new ColumnElement(rows, Gap: GridGap), Height: GridHeight) { FixedHeight = true };
    }

    // ── bottom: Settings · Edit layout · spacer · version ────────────────────────────────────────────────────────
    private HudElement BuildBottomRow() => new RowElement(new HudElement[]
    {
        IconPill("settings", () => T("launcher.settings"), () => _actions.OpenSettings(null)),
        IconPill("layout", () => T("launcher.edit_layout"), _actions.ToggleLayoutEdit),
        new SpacerElement(0f),   // flexible: pushes the version to the right edge
        Small(() => F("launcher.version", FrameworkVersion.Value), () => _s.Muted(), 12),
    }, Gap: 6f);

    // A Neutral 28 px pill with a 15 px glyph before its bold label (prototype .pill.neu with an icon). Built like the
    // kit's PillButton (Bubble → Bare Selectable → Row with 28 px struts; hover = the pill's own round fill, R2).
    private HudElement IconPill(string icon, Func<string> label, Action onClick)
    {
        var hover = new StellarStyle.HoverFlag();
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(StellarStyle.PillPadX - 6f, StellarStyle.PillHeight),   // + the 6 gap = 12 inset
                Glyph(() => Icon(icon), (int)PillIcon, () => _s.NeutralLabel()),
                StellarStyle.DropBold(new TextElement(label, () => _s.NeutralLabel(), NoWrap: true, FontSize: 13) { Bold = true }),
                new SpacerElement(StellarStyle.PillPadX - 6f, StellarStyle.PillHeight),
            }, Gap: 6f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => _s.Fill(hover.On ? _s.NeutralHover() : _s.Neutral()),
            CornerRadius = StellarStyle.RoundRadius(StellarStyle.PillHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = 220f,
        };
    }
}
