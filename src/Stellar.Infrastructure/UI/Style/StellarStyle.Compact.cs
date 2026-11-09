using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI.Style;

// Compact controls for INSIDE a 26 px list tile (the Settings hub's rows: Retry / Rename / Save / Reset pills and the
// row ✕), plus small layout helpers the fixed-size hub needs. A 28 px pill or ✕ in a 26 px tile grows the tile, so
// rows use these 22 / 24 px variants (same round shape rule R1: radius = side/2 − 1).
internal sealed partial class StellarStyle
{
    public const float SmallPillHeight = 22f;
    private const float SmallPillPadX = 10f;
    private const int SmallPillLabelSize = 12;
    public const float SmallIconSize = 24f;

    /// <summary>The theme's window border colour (Theme.MenuBorder, editable in custom themes), opaque.</summary>
    public ColorRgba Border() => _colors.MenuBorder with { A = 1f };

    /// <summary>A 22 px pill for list rows (bold-12 label). Same severities and disabled look as
    /// <see cref="PillButton"/>; <paramref name="width"/> fixes the LABEL box (R10).</summary>
    public HudElement SmallPill(Func<string> label, PillKind kind, Action onClick, float width = 0f,
        Func<bool>? enabled = null)
    {
        bool En() => enabled == null || enabled();
        var hover = new HoverFlag();
        var text = new TextElement(label, Color: () => En() ? PillLabel(kind) : DisabledLabel(Tile()),
            Width: width, Align: TextAlign.Center, NoWrap: true, FontSize: SmallPillLabelSize) { Bold = true };
        var maxW = width > 0f ? width + 2f * SmallPillPadX : 0f;
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(SmallPillPadX, SmallPillHeight),
                DropBold(text),
                new SpacerElement(SmallPillPadX, SmallPillHeight),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (En()) onClick(); })
            { Bare = true, OnHover = over => hover.On = over })
        {
            // Disabled sinks into the list TILE it sits on (rows are tiles), not the panel.
            FillColorFn = () => Fill(!En() ? DisabledFill(Tile()) : hover.On ? Hover(PillFill(kind)) : PillFill(kind)),
            CornerRadius = RoundRadius(SmallPillHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = maxW,
        };
    }

    /// <summary>Adds an on-click SCREEN-pixel rect callback (<see cref="SelectableElement.OnClickWithRect"/>, fired right
    /// after OnClick) to a pill built by <see cref="SmallPill"/> / <see cref="PillButton"/> — for anchoring a child
    /// window to the clicked control. Any other element is returned unchanged.</summary>
    public static HudElement WithClickRect(HudElement pill, Action<WindowRect> onClickWithRect)
        => pill is BubbleElement b && b.Child is SelectableElement sel
            ? b with { Child = sel with { OnClickWithRect = onClickWithRect } }
            : pill;

    /// <summary>24×24 icon-only ✕ for list rows: muted at rest, red glyph on the soft red round wash on hover (the house
    /// remove-hover). Pass <paramref name="hover"/> to own the flag (a pooled row resets it when its slot rebinds).</summary>
    public HudElement SmallCloseButton(Action onClick, Func<ColorRgba>? surface = null, HoverFlag? hover = null)
    {
        var h = hover ?? new HoverFlag();
        ColorRgba Surface() => surface?.Invoke() ?? Tile();
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, SmallIconSize),
                new TextElement(() => "✕", Color: () => h.On ? RemoveHoverGlyph(Surface()) : Muted(),
                    Width: SmallIconSize, Align: TextAlign.Center, NoWrap: true),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => h.On = over })
        {
            FillColorFn = () => h.On ? Fill(RemoveHoverWash(Surface())) : Transparent,
            CornerRadius = RoundRadius(SmallIconSize), PaddingX = 0f, PaddingY = 0f, MaxWidth = SmallIconSize,
        }, Width: SmallIconSize);
    }

    /// <summary>A rounded square of <paramref name="size"/> filled with <paramref name="fill"/> (faded with the window)
    /// around <paramref name="content"/> (a glyph PNG or a short label). The nav rail / plugin-list icon tile whose fill
    /// changes live (an <see cref="IconTile(Func{byte[]}, float)"/> is always accent).</summary>
    public HudElement FilledTile(HudElement content, Func<ColorRgba> fill, float size, float radius)
        => new CellElement(new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, size),
                content,
            }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fill(fill()), CornerRadius = radius, PaddingX = 0f, PaddingY = 0f, MaxWidth = size,
        }, Width: size);

    /// <summary>A vertical filler: takes every pixel of slack height in its Column (a Conditional with Fill reports
    /// flexibleHeight 1). Pins a footer to the bottom of a fixed-height column.</summary>
    public static HudElement VerticalFill() => new ConditionalElement(() => true, new SpacerElement(1f, 1f), Fill: true);

    /// <summary>A Column of <paramref name="items"/> (Gap 8) that sits at the TOP of a taller Row cell: a Row centres a
    /// non-flexible child vertically, so a <see cref="VerticalFill"/> follows the items — outside the gapped column, so
    /// it adds no 8 px gap of its own.</summary>
    public static HudElement TopColumn(params HudElement[] items) => new ColumnElement(new HudElement[]
    {
        new ColumnElement(items, Gap: 8f),
        VerticalFill(),
    }, Gap: 0.01f);

    /// <summary>A minimum-height box: [a 1 px wide strut of <paramref name="height"/>, the content cell]. Text that
    /// wraps to fewer lines keeps the box height, so a changing description never moves what is below it (R14). The
    /// content sits at the TOP: a Row centres a child vertically unless the child is flexible, so the content column
    /// carries a <see cref="VerticalFill"/> — which makes the box itself flexible (it may take a share of its column's
    /// slack; use it where that is harmless).</summary>
    public static HudElement MinHeight(float height, HudElement content) => new RowElement(new HudElement[]
    {
        new SpacerElement(1f, height),
        new CellElement(new ColumnElement(new HudElement[] { content, VerticalFill() }, Gap: 0.01f), Weight: 1f),
    }, Gap: 0.01f);

    /// <summary>"#RRGGBB" of <paramref name="c"/> for an inline rich-text colour tag (opaque; alpha is ignored).</summary>
    public static string Hex(ColorRgba c)
        => "#" + Byte(c.R).ToString("X2", CultureInfo.InvariantCulture) + Byte(c.G).ToString("X2", CultureInfo.InvariantCulture)
               + Byte(c.B).ToString("X2", CultureInfo.InvariantCulture);

    /// <summary><paramref name="text"/> wrapped in an inline colour tag (both text renderers honour &lt;color&gt;).</summary>
    public static string Colored(string text, ColorRgba c) => "<color=" + Hex(c) + ">" + text + "</color>";

    private static int Byte(float v) => Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
