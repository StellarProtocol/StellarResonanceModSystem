using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI.Style;

/// <summary>Inputs of <see cref="StellarStyle.ConfirmPanel"/> (a parameter object: the analyzer caps methods at 5
/// parameters). All texts live.</summary>
internal sealed record ConfirmSpec(Func<string> Question, Func<string> Consequence, Func<string> ConfirmLabel,
    Action OnConfirm, Func<string> CancelLabel, Action OnCancel)
{
    /// <summary>The panel's OUTER width — the width available where it sits (e.g. a column's panel content width). The
    /// panel never gets wider: its texts wrap inside it and the pills keep a fixed size. Required in practice: a confirm
    /// inside another panel without it hugged its unwrapped sentence and overflowed the column (in-game 2026-11-09).</summary>
    public float Width { get; init; }
}

// Layout blocks (SDL §2/§4): accent icon tiles, ToolHeader, SectionPanel (+ the separately-NAMED trailing variant),
// raised list rows, the confirm panel, and the small text helpers.
internal sealed partial class StellarStyle
{
    public const float ToolIconSize = 40f;   // ToolHeader icon tile
    public const float CardIconSize = 32f;   // hub card / launcher tile / help popover icon tile
    private const float IconGlyphShare = 0.62f;   // the white glyph fills ~62 % of the tile

    // ── icon tile ────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A rounded tile in the LIVE theme accent with a flat white glyph PNG centred on it (SDL "Icon tile",
    /// option A 2026-11-06): the art carries no colour, so the tile follows the theme. Radius ≈ 22 % of the side
    /// (9 at 40 px, 7 at 32 px). The padding IS the tile margin, so the tile is exactly <paramref name="size"/> square;
    /// the fixed Cell + inner Row stop the icon-squish (Cell/Selectable force-expand a child's width).
    /// ⚠️ A white glyph on a very light accent reads poorly (accepted).</summary>
    public HudElement IconTile(Func<byte[]?> png, float size = ToolIconSize) => IconTile(png, size, live: false);

    /// <summary>The glyph side inside a <paramref name="size"/> icon tile: ~62 % snapped DOWN to a pre-drawn icon size
    /// (icon art rule — 40 → 24, 32 → 16, 28 → 16), so a framework glyph lands on its own hinted bitmap. Even sizes
    /// keep the padding whole pixels.</summary>
    public static int IconTileGlyph(float size)
    {
        var want = MathF.Round(size * IconGlyphShare);
        var glyph = LauncherIcons.PreDrawnSizes[0];
        foreach (var s in LauncherIcons.PreDrawnSizes) if (s <= want) glyph = s;
        return glyph;
    }

    /// <summary><see cref="IconTile(Func{byte[]}, float)"/> whose image is <paramref name="live"/> (re-pulls its PNG each
    /// refresh): pass a <see cref="LauncherIcons.Glyph"/> lookup for <see cref="IconTileGlyph"/> so a canvas-scale change
    /// re-picks the pre-drawn bitmap.</summary>
    public HudElement IconTile(Func<byte[]?> png, float size, bool live)
    {
        var glyph = IconTileGlyph(size);
        var pad = (size - glyph) / 2f;
        return new CellElement(new BubbleElement(
            new RowElement(new HudElement[] { new ImageElement(png, glyph, glyph) { Live = live } }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fill(Accent()), CornerRadius = MathF.Round(size * 0.22f), PaddingX = pad, PaddingY = pad,
            MaxWidth = size,
        }, Width: size);
    }

    /// <summary><see cref="IconTile(Func{byte[]}, float)"/> for one of the framework's embedded white glyphs
    /// (<see cref="LauncherIcons"/> key, e.g. "settings", "theme", "plugins").</summary>
    public HudElement IconTile(string launcherIcon, float size = ToolIconSize)
    {
        var glyph = IconTileGlyph(size);
        return IconTile(() => LauncherIcons.Glyph(launcherIcon, glyph), size, live: true);
    }

    // ── tool header ──────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Window title block: 40 px accent icon tile + bold title over a live status line, and an optional
    /// <paramref name="trailing"/> control pinned to the right (e.g. a master on/off chip). Status colour null = muted.
    /// The text column sits in a weighted Cell, so a long title/status ellipsizes instead of pushing the trailing
    /// control out.</summary>
    public HudElement ToolHeader(Func<byte[]?> icon, Func<string> title, Func<string> status,
        Func<ColorRgba?>? statusColor = null, HudElement? trailing = null)
        => ToolHeaderWith(icon, title,
            new TextElement(status, Color: () => statusColor?.Invoke() ?? Muted(), NoWrap: true) { Ellipsize = true },
            trailing);

    /// <summary><see cref="ToolHeader"/> with a custom status element (chips, a coloured status row…).</summary>
    public HudElement ToolHeaderWith(Func<byte[]?> icon, Func<string> title, HudElement status, HudElement? trailing = null)
    {
        var row = new List<HudElement>(3)
        {
            IconTile(icon, ToolIconSize),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(title, Color: () => Text(), Emphasis: true, NoWrap: true) { Ellipsize = true },
                status,
            }, Gap: 2f), Weight: 1f),
        };
        if (trailing != null) row.Add(trailing);
        return new RowElement(row, Gap: 10f);
    }

    // ── section panel ────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A rounded panel (window background darkened, 6 px radius, 10/8 padding): bold-12 UPPERCASE muted
    /// caption + right-aligned muted hint (the hover-hint slot, R12), then the children. Directly in a Column it FILLS
    /// the width (a Bubble only hugs inside a Row). Both caption and hint null = no caption row.</summary>
    public HudElement SectionPanel(Func<string>? caption, Func<string>? hint, params HudElement[] children)
        => SectionPanelWithTrailing(caption, hint, null, children);

    /// <summary><see cref="SectionPanel"/> with a <paramref name="trailing"/> control after the hint in the caption row
    /// (e.g. a "?" or a Show/Hide pill).
    /// ⚠️ Deliberately its OWN NAME, never a SectionPanel overload: with two params-array overloads both applicable only
    /// in expanded form, C# picks the one with MORE declared parameters, so every SectionPanel(cap, hint, a, b, …) would
    /// silently bind child <c>a</c> as the trailing control (Maestro Link bug, SDL §4).</summary>
    public HudElement SectionPanelWithTrailing(Func<string>? caption, Func<string>? hint, HudElement? trailing,
        params HudElement[] children)
    {
        var items = new List<HudElement>(children.Length + 1);
        if (caption != null || hint != null || trailing != null)
        {
            var row = new List<HudElement>(4)
            {
                Caption(() => caption?.Invoke() ?? ""),
                new SpacerElement(0f),   // flexible: pushes the hint (and trailing) to the right edge
                new TextElement(() => hint?.Invoke() ?? "", Color: () => Muted(), NoWrap: true),
            };
            if (trailing != null) row.Add(trailing);
            items.Add(new RowElement(row, Gap: 6f));
        }
        items.AddRange(children);
        return new BubbleElement(new ColumnElement(items, Gap: 6f))
        {
            FillColorFn = () => Fill(Panel()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        };
    }

    // ── list rows ────────────────────────────────────────────────────────────────────────────────────────────────
    public const float ListTileHeight = 26f;
    public const float ListRowGap     = 2f;
    /// <summary>The VirtualList RowHeight for <see cref="ListSlot"/> rows.</summary>
    public const float ListRowPitch   = ListTileHeight + ListRowGap;
    public const float TilePadX       = 6f;

    /// <summary>One pooled list row = [26 px tile, 2 px spacer] (R15): a VirtualList slot STRETCHES its one child to the
    /// full RowHeight, so a bare tile in a 28 px slot fills all 28 and the rows touch.</summary>
    public static HudElement ListSlot(HudElement tile) => new ColumnElement(new HudElement[]
    {
        tile,
        new SpacerElement(0f, ListRowGap),
    }, Gap: 0.01f);

    /// <summary>Viewport height showing exactly <paramref name="rows"/> rows (the last row's gap falls outside).</summary>
    public static float ListHeight(int rows) => rows * ListRowPitch - ListRowGap;

    /// <summary>The content row of a list tile: a 26 px height strut + side padding around <paramref name="items"/>
    /// (Gap 8). Put a stretching item (a weighted Cell) in it to push trailing controls to the right edge.</summary>
    public static HudElement TileRow(params HudElement[] items) => new RowElement(new HudElement[]
    {
        new SpacerElement(TilePadX, ListTileHeight),   // left inset + the height strut (fixed: width > 0)
        new CellElement(new RowElement(items, Gap: 8f), Weight: 1f),
        new SpacerElement(TilePadX, 1f),               // right inset
    }, Gap: 0.01f);

    /// <summary>A raised list tile (Tile fill, radius 6) around <paramref name="row"/> (usually a <see cref="TileRow"/>).
    /// <paramref name="selected"/> = a slightly lighter fill; <paramref name="hovered"/> = the row's own hover (a Bare
    /// selectable inside has no wash, so the whole tile lights up as one piece); <paramref name="flashing"/> = the R13
    /// "done" flash (Success wash + 1 px Success ring); <paramref name="failed"/> = the red problem tile.</summary>
    public HudElement ListTile(HudElement row, Func<bool>? selected = null, Func<bool>? hovered = null,
        Func<bool>? flashing = null, Func<bool>? failed = null)
    {
        var tile = new BubbleElement(row)
        {
            FillColorFn = () => Fill(TileFill(selected, hovered, flashing, failed)),
            CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
        if (flashing == null) return tile;
        return tile with { OutlineColorFn = () => flashing() ? Success() : (ColorRgba?)null };
    }

    private ColorRgba TileFill(Func<bool>? selected, Func<bool>? hovered, Func<bool>? flashing, Func<bool>? failed)
    {
        if (flashing != null && flashing()) return Over(Success() with { A = WashAlpha }, Tile());
        var baseFill = failed != null && failed() ? Over(Danger() with { A = WashAlpha }, Panel()) : Tile();
        if (selected != null && selected()) return Over(Text() with { A = 0.08f }, baseFill);
        if (hovered != null && hovered()) return Over(Text() with { A = 0.05f }, baseFill);
        return baseFill;
    }

    // ── confirm panel ────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The house confirmation (SDL §2): a panel with a bold question, a muted consequence line ("…can't be
    /// undone."), then a Danger / Neutral (Cancel) pill pair of equal width.
    /// Exactly <see cref="ConfirmSpec.Width"/> wide: the bubble is capped there and both texts get the fixed content
    /// width, so they WRAP (a nested bubble otherwise hugs the text's one-line preferred width and pushes the parent
    /// column wider — Settings › Themes delete confirm, 2026-11-09). The pills sit side by side at a fixed label width
    /// (≤ <see cref="PillPairWidth"/>, smaller only if the panel is too narrow for two), never stretched by weighted
    /// cells (that grew them to ~165 px each).</summary>
    public HudElement ConfirmPanel(ConfirmSpec spec)
    {
        const float padX = 10f;   // SectionPanel PaddingX
        var inner = spec.Width > 0f ? spec.Width - 2f * padX : 0f;
        var label = inner > 0f ? MathF.Min(PillPairWidth, MathF.Floor((inner - 8f) / 2f - 2f * PillPadX)) : PillPairWidth;
        var panel = SectionPanel(null, null,
            new TextElement(spec.Question, Color: () => Text(), Width: inner) { Bold = true },
            new TextElement(spec.Consequence, Color: () => Muted(), Width: inner),
            new RowElement(new HudElement[]
            {
                DangerButton(spec.ConfirmLabel, spec.OnConfirm, label),
                NeutralButton(spec.CancelLabel, spec.OnCancel, label),
            }, Gap: 8f));
        return inner > 0f && panel is BubbleElement b ? b with { MaxWidth = spec.Width } : panel;
    }

    // ── text helpers ─────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Bold-12 UPPERCASE muted caption (section captions).</summary>
    public HudElement Caption(Func<string> text) => new TextElement(() => text().ToUpperInvariant(),
        Color: () => Muted(), NoWrap: true, FontSize: 12) { Bold = true };

    /// <summary>Bold text in the body colour (default 14 px; styled path, so FontSize applies).</summary>
    public HudElement BoldText(Func<string> text, int fontSize = 14)
        => new TextElement(text, Color: () => Text(), FontSize: fontSize) { Bold = true };

    /// <summary>A muted, wrapping body line (hints, consequences, footers). Plain text = fixed 14 px.</summary>
    public HudElement MutedLine(Func<string> text) => new TextElement(text, Color: () => Muted());

    /// <summary>One always-present line at a fixed 22 px height (a 0-width strut pins the row, so empty / bold /
    /// coloured text never changes the height — R14); the content cell stretches.</summary>
    public static HudElement FixedLine(HudElement content) => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, 22f),
        new CellElement(content, Weight: 1f),
    }, Gap: 0.01f);
}
