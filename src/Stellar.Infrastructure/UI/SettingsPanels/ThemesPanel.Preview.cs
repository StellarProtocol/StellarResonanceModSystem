using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

// The live "Preview": a small example window drawn with the theme tokens — title strip + ✕, a ToolHeader-like row
// (icon tile, "Auto-Accept", status On · 3 players · muted text), a toggle list in a panel whose scrollbar is the real
// one (so it follows the Classic Scrollbar style), two chips, a Pill dropdown, a slider, Primary / Neutral / Danger
// pills, a CLASSIC ButtonElement (follows the Classic Button style) + Caution, a "?" and a ✕ in its hover look. Every
// fill goes through the kit, so it follows the theme AND the window-opacity slider live. Its controls are playable but
// change nothing outside the preview.
internal sealed partial class ThemesPanel
{
    private const float PreviewTitleHeight = 24f;
    private const float PreviewRowHeight = 24f;
    private const float PreviewListHeight = 76f;   // < 5 rows, so the scrollbar shows
    private static readonly string[] PreviewRowKeys =
        { "themes.pv.row.party", "themes.pv.row.guild", "themes.pv.row.friend", "themes.pv.row.join", "themes.pv.row.trade" };

    private readonly bool[] _pvToggles = { true, false, true, true, false };
    private readonly bool[] _pvChips = { true, false };
    private int _pvDrop;
    private float _pvSlider = 62f;
    private string[]? _pvDropCache;
    private string? _pvDropLang;

    private HudElement BuildPreviewPanel()
        => _k.Panel("themes.preview", _k.T("themes.preview.hint"), BuildPreviewWindow());

    private HudElement BuildPreviewWindow()
    {
        var s = _k.S;
        var body = new ColumnElement(new HudElement[]
        {
            PreviewHeader(), PreviewList(), PreviewChipsRow(),
            new RowElement(new HudElement[]
            {
                new CellElement(SettingsKit.Slider(() => _pvSlider, v => _pvSlider = v, 0f, 100f), Weight: 1f),
                _k.Value(() => $"{(int)_pvSlider}%", 40f),
            }, Gap: 8f),
            new RowElement(new HudElement[]
            {
                s.SmallPill(_k.T("themes.pv.primary"), PillKind.Primary, () => { }),
                s.SmallPill(_k.T("themes.pv.neutral"), PillKind.Neutral, () => { }),
                s.SmallPill(_k.T("themes.pv.danger"), PillKind.Danger, () => { }),
            }, Gap: 6f),
            PreviewClassicRow(),
            StellarStyle.VerticalFill(),
        }, Gap: 6f) { Padding = 7 };
        // The window bubble is flexible (its body ends in a VerticalFill), so it fills the Preview panel.
        return new BubbleElement(new ColumnElement(new HudElement[] { PreviewTitle(), body }, Gap: 0.01f))
        {
            FillColorFn = () => s.Fill(s.Background()),
            OutlineColorFn = () => s.Border(),
            CornerRadius = StellarStyle.TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
    }

    private HudElement PreviewTitle()
    {
        var s = _k.S;
        return new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(8f, PreviewTitleHeight),
            new CellElement(new TextElement(_k.T("themes.pv.title"), Color: () => s.Text(), NoWrap: true, FontSize: 12)
                { Bold = true, Ellipsize = true }, Weight: 1f),
            new TextElement(() => "✕", Color: () => s.Muted(), Width: 20f, Align: TextAlign.Center, NoWrap: true),
            new SpacerElement(2f, 1f),
        }, Gap: 0.01f))
        {
            // The framework title bar is the accent at 14 % over the frame.
            FillColorFn = () => s.Fill(StellarStyle.Over(s.Accent() with { A = 0.14f }, s.Background())),
            CornerRadius = StellarStyle.TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
    }

    private HudElement PreviewHeader()
    {
        var s = _k.S;
        return new RowElement(new HudElement[]
        {
            s.IconTile("plugins", StellarStyle.CardIconSize),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(_k.T("themes.pv.tool"), Color: () => s.Text(), NoWrap: true) { Bold = true, Ellipsize = true },
                new TextElement(() => _k.Good(_text.T("common.on")) + " · "
                        + StellarStyle.Colored("<b>" + _text.TFormat("themes.pv.players", 3) + "</b>", s.Readable(s.Accent(), s.Background()))
                        + " · " + _text.T("themes.pv.muted"),
                    Color: () => s.Muted(), NoWrap: true),
            }, Gap: 1f), Weight: 1f),
        }, Gap: 8f);
    }

    private HudElement PreviewList()
    {
        var s = _k.S;
        var rows = new List<HudElement>(PreviewRowKeys.Length);
        for (var i = 0; i < PreviewRowKeys.Length; i++)
        {
            var idx = i;
            rows.Add(new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(6f, PreviewRowHeight),
                _k.LineLabel(_k.T(PreviewRowKeys[idx])),
                SettingsKit.Toggle(() => _pvToggles[idx], v => _pvToggles[idx] = v),
                new SpacerElement(4f, 1f),
            }, Gap: 6f))
            {
                FillColorFn = () => s.Fill(s.Tile()), CornerRadius = StellarStyle.TileRadius, PaddingX = 0f, PaddingY = 0f,
            });
        }
        return new BubbleElement(new ScrollElement(new ColumnElement(rows, Gap: 2f), Height: PreviewListHeight) { FixedHeight = true })
        {
            FillColorFn = () => s.Fill(s.Panel()), CornerRadius = StellarStyle.TileRadius, PaddingX = 3f, PaddingY = 3f,
        };
    }

    private HudElement PreviewChipsRow()
    {
        var s = _k.S;
        return new RowElement(new HudElement[]
        {
            s.Chip(_k.T("themes.pv.chip.party"), () => _pvChips[0], () => _pvChips[0] = !_pvChips[0], 58f, s.Accent),
            s.Chip(_k.T("themes.pv.chip.guild"), () => _pvChips[1], () => _pvChips[1] = !_pvChips[1], 58f, s.Accent),
            new SpacerElement(),
            SettingsKit.Dropdown(() => _pvDrop, () => PreviewDropOptions, i => _pvDrop = i, 108f),
        }, Gap: 6f);
    }

    private IReadOnlyList<string> PreviewDropOptions
    {
        get
        {
            if (_pvDropCache == null || _pvDropLang != _text.Language)
            {
                _pvDropLang = _text.Language;
                _pvDropCache = new[] { _text.T("themes.pv.opt.everyone"), _text.T("themes.pv.opt.friends"), _text.T("themes.pv.opt.guild") };
            }
            return _pvDropCache;
        }
    }

    // A classic ButtonElement (drawn in the chosen Classic Button style) + a Caution pill; then a static "?" and a ✕ in
    // its red hover look.
    private HudElement PreviewClassicRow()
    {
        var s = _k.S;
        HudElement Round(HudElement glyph, System.Func<ColorRgba> fill) => new CellElement(new BubbleElement(new RowElement(
            new HudElement[] { new SpacerElement(0f, StellarStyle.SmallIconSize), glyph }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => s.Fill(fill()), CornerRadius = StellarStyle.RoundRadius(StellarStyle.SmallIconSize),
            PaddingX = 0f, PaddingY = 0f, MaxWidth = StellarStyle.SmallIconSize,
        }, Width: StellarStyle.SmallIconSize);
        return new RowElement(new HudElement[]
        {
            new ButtonElement(_k.T("themes.pv.classic"), () => { }),
            s.SmallPill(_k.T("themes.pv.caution"), PillKind.Caution, () => { }),
            new SpacerElement(),
            Round(new TextElement(() => "?", Color: () => s.NeutralLabel(), Width: StellarStyle.SmallIconSize, Align: TextAlign.Center,
                NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true }, s.Neutral),
            Round(new TextElement(() => "✕", Color: () => s.RemoveHoverGlyph(s.Background()), Width: StellarStyle.SmallIconSize,
                Align: TextAlign.Center, NoWrap: true), () => s.RemoveHoverWash(s.Background())),
        }, Gap: 6f);
    }
}
