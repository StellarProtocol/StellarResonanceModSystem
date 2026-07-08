using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>A node in a HUD's declarative element tree. Plugins compose these; the framework
/// renders them as native uGUI with one enforced chrome. Dynamic leaves carry <c>Func</c>s the
/// framework re-pulls on its capped refresh (state→view without touching the UI engine).</summary>
public abstract record HudElement;

/// <summary>Horizontal layout container: children arranged left-to-right with optional spacing.</summary>
/// <param name="Children">Child elements arranged horizontally.</param>
/// <param name="Gap">Spacing in pixels between each child.</param>
/// <param name="Justify">How children are packed horizontally; default <see cref="RowJustify.Left"/>.</param>
public sealed record RowElement(IReadOnlyList<HudElement> Children, float Gap = 0f, RowJustify Justify = RowJustify.Left) : HudElement
{
#pragma warning disable STELLAR0004   // 2-param compat overload — not a real dependency; exempt as record secondary ctor
    /// <summary>Backwards-compatible overload for plugins compiled against the pre-Justify signature.</summary>
    public RowElement(IReadOnlyList<HudElement> children, float gap)
        : this(children, gap, RowJustify.Left) { }
#pragma warning restore STELLAR0004
}

/// <summary>Vertical layout container: children stacked top-to-bottom with optional spacing.</summary>
/// <param name="Children">Child elements stacked vertically.</param>
/// <param name="Gap">Spacing in pixels between each child.</param>
public sealed record ColumnElement(IReadOnlyList<HudElement> Children, float Gap = 0f) : HudElement
{
    /// <summary>Uniform inner padding on all four sides in pixels. Adds breathing room between the column
    /// boundary and its content — use when a backdrop or explicit border sits at the column edge.</summary>
    public int Padding { get; init; }
}

/// <summary>A themed panel container: fills its area with the active theme's menu background + a 1px border and
/// lays out a single <paramref name="Child"/> inside with uniform padding. Use it to give a borderless popup
/// (e.g. a right-click context menu) a real panel look and to block click-through to whatever is behind.</summary>
/// <param name="Child">The content laid out inside the panel.</param>
/// <param name="Padding">Uniform inner padding in pixels.</param>
public sealed record PanelElement(HudElement Child, float Padding = 8f) : HudElement;

/// <summary>How children are packed horizontally within a <see cref="RowElement"/>.</summary>
public enum RowJustify
{
    /// <summary>Pack children to the left; slack space is to the right (default).</summary>
    Left,
    /// <summary>Pack children to the centre; slack split equally on both sides.</summary>
    Center,
    /// <summary>Pack children to the right; slack space is to the left.</summary>
    Right,
}

/// <summary>Horizontal text alignment within the text's cell. Default Left; Right is used for numeric table
/// columns (so magnitudes line up against the right edge of a fixed-width <see cref="CellElement"/>).</summary>
public enum TextAlign
{
    /// <summary>Align text to the left of the cell.</summary>
    Left,
    /// <summary>Centre text horizontally in the cell.</summary>
    Center,
    /// <summary>Align text to the right of the cell (use for numeric columns).</summary>
    Right
}

/// <summary>Themed text. <paramref name="Color"/> Func null (or returns null) = framework default;
/// a Func lets colour animate per-refresh (e.g. delta-flash). <paramref name="Width"/> &gt; 0 fixes the cell
/// width (the text wraps within it) — use to form aligned columns (e.g. a plugin-name column so the version
/// after it starts at a consistent x). <paramref name="Align"/> sets horizontal alignment (Right for numeric
/// columns). <paramref name="Shadow"/> draws a dark outline behind the glyphs — for chrome-less overlays (a
/// borderless HUD with no background) where light text must stay legible over arbitrary world backgrounds.
/// <paramref name="ShadowDistance"/> controls the pixel offset of the shadow (default 1). Increase for large
/// font sizes where a 1-pixel shadow is invisible. <paramref name="NoWrap"/> keeps the text on a single line
/// (any overflow spills/clips at the cell edge rather than wrapping to multiple lines) — use in a fixed-width
/// pane where a long label (e.g. a map name) must read as one row, not a 5-line block.</summary>
public sealed record TextElement(Func<string> Text, Func<ColorRgba?>? Color = null, bool Emphasis = false, float Width = 0f, TextAlign Align = TextAlign.Left, bool Shadow = false, int FontSize = 0, int ShadowDistance = 1, bool NoWrap = false) : HudElement
{
    /// <summary>Backwards-compatible overload for plugins compiled against the pre-FontSize signature.</summary>
    public TextElement(Func<string> text, Func<ColorRgba?>? color, bool emphasis, float width, TextAlign align, bool shadow)
        : this(text, color, emphasis, width, align, shadow, 0, 1) { }
#pragma warning disable STELLAR0004   // 7-param compat overload — not a real dependency; exempt as record secondary ctor
    /// <summary>Backwards-compatible overload for plugins compiled against the pre-ShadowDistance signature.</summary>
    public TextElement(Func<string> text, Func<ColorRgba?>? color, bool emphasis, float width, TextAlign align, bool shadow, int fontSize)
        : this(text, color, emphasis, width, align, shadow, fontSize, 1) { }
#pragma warning restore STELLAR0004

    /// <summary>When non-null, overrides <see cref="FontSize"/> and is re-evaluated each HUD refresh.
    /// Use with <see cref="Stellar.Abstractions.Services.IFramework.ScreenHeight"/> to scale text with
    /// screen resolution: <c>DynamicFontSize = () => services.Framework.ScreenHeight / 19</c>.</summary>
    public Func<int>? DynamicFontSize { get; init; }
}

/// <summary>Graphical fill bar (0..1). Chrome framework-themed; <paramref name="Fill"/> is the plugin's
/// semantic colour (from its colour slot). Optional right-aligned numeric <paramref name="Label"/> and
/// optional fixed-width left <paramref name="Prefix"/> caption (e.g. "HP" / "Stamina") so stacked bars
/// align in a column.</summary>
public sealed record BarElement(Func<float> Fraction01, ColorRgba Fill, Func<string>? Label = null, string? Prefix = null) : HudElement;

/// <summary>Rounded pill badge with dynamic text and optional tint colour. Suitable for short status labels (e.g. "Offline", rank numbers).</summary>
/// <param name="Text">Dynamic text displayed inside the pill; re-pulled each refresh.</param>
/// <param name="Color">Optional tint override; null (or returning null) uses the framework default pill colour.</param>
public sealed record PillElement(Func<string> Text, Func<ColorRgba?>? Color = null) : HudElement;

/// <summary>Escape hatch: plugin supplies its own PNG; framework displays it. Consistency is the
/// plugin's responsibility here (the one unenforced spot).</summary>
public sealed record ImageElement(Func<byte[]?> Png, int Width, int Height) : HudElement;

/// <summary>React-style conditional. Both subtrees are built once; the renderer SetActive-toggles them
/// each refresh from <paramref name="When"/> (no reconciliation). <paramref name="Else"/> may be null.
/// <paramref name="Fill"/> = the active branch expands to fill leftover height in a fixed-size (Resizable)
/// window (so e.g. a meter's list scroll grows with the window). Default false — no effect in content-sized
/// windows.</summary>
public sealed record ConditionalElement(Func<bool> When, HudElement Then, HudElement? Else = null, bool Fill = false) : HudElement;

/// <summary>Variable-length list, bounded by Slots.Count. All slots built once; the first
/// <paramref name="VisibleCount"/>() are SetActive-shown each refresh. <paramref name="Columns"/>&gt;1 grids them.
/// <paramref name="CellWidth"/>/<paramref name="CellHeight"/> (when &gt; 0, multi-column only) override the grid's
/// default cell size — use to widen columns past the framework default (e.g. the StatInspector mini-HUD, whose
/// icon+label+value row needs more than the default cell width).</summary>
public sealed record ListElement(Func<int> VisibleCount, IReadOnlyList<HudElement> Slots, int Columns = 1,
    float CellWidth = 0f, float CellHeight = 0f) : HudElement;
