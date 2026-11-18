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

    /// <summary>When true, this row's NATURAL width (the sum of its children's preferred widths + gaps) becomes a
    /// floor on the window's minimum width: the window can't be grip-resized narrower than the row needs, a saved
    /// layout narrower than that is widened on load, and the window widens once if the requirement grows later (a
    /// language / font-scale / theme change). The window never shrinks automatically. The window minimum is the
    /// largest requirement across every marked row that is currently shown, never below
    /// <see cref="Stellar.Abstractions.Domain.WindowSpec.MinWidth"/> and capped at
    /// <see cref="Stellar.Abstractions.Domain.WindowSpec.MaxWidth"/>. Use it on a row that must never overflow in
    /// any language (a tab strip, an input/action row). Only <see cref="Stellar.Abstractions.Domain.WindowSpec.Resizable"/>
    /// windows are affected — a content-sized window already sizes to its content. A
    /// <see cref="BubbleElement"/> nested inside a marked row hugs its natural width (it is not capped to its
    /// parent's width), so it can't shrink the very measurement that is meant to make room for it.</summary>
    public bool ConstrainsWindowMinWidth { get; init; }

    /// <summary>Optional — fires with <c>true</c> when the pointer enters the row's rectangle and <c>false</c> when it
    /// leaves (or the row is hidden). Ticker-driven, the same hover hook <see cref="SelectableElement.OnHover"/> uses,
    /// so a window in front that covers the pointer counts as "not over"; never fires in the static sandbox. Unlike a
    /// <see cref="SelectableElement"/> it adds NOTHING to the row — no tint, no padding, no click target — so it can
    /// reveal hover-only controls (e.g. a per-message action button) without changing the row's look or layout.
    /// Default null = unchanged. An <c>init</c> property (binary-compatible).</summary>
    public Action<bool>? OnHover { get; init; }
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

/// <summary>A content-hugging rounded "chat bubble" / card: a <see cref="FillColor"/>-tinted rounded sliced
/// background (the same rounded chip sprite <see cref="SelectableElement"/> draws) hosting a single
/// <paramref name="Child"/> with inner padding. Unlike <see cref="SelectableElement"/> — which is forced to
/// stretch the full column width — a bubble's width HUGS its child's preferred width, so a short label makes a
/// narrow bubble and a long sentence makes a wide one, capped at <see cref="MaxWidth"/> (or the available width)
/// past which a wrapping <see cref="TextElement"/> child flows to multiple lines and the bubble grows in height.
/// Built for a game-style chat UI (translucent fills + right-aligned self rows). It is a PURELY VISUAL container:
/// it does not intercept clicks (its background is not a raycast target), so to make a bubble clickable, wrap it
/// in a <see cref="SelectableElement"/> (or place a <see cref="ButtonElement"/> beside it) — the bubble renders on
/// top of the selectable's own tint.
/// <para>⚠️ Content-hug needs a layout parent that does NOT force-expand child width. A <see cref="RowElement"/>'s
/// layout respects the child's intrinsic width, so place a bubble inside a Row (right-align a self bubble with
/// <see cref="RowJustify.Right"/> + a weighted gutter). Dropped DIRECTLY into a <see cref="ColumnElement"/> (which
/// force-expands width) the bubble fills the column width instead of hugging — same gotcha as a fixed-size icon
/// (see WindowBuilder-Patterns.md).</para></summary>
/// <param name="Child">The content laid out inside the bubble (e.g. a wrapping <see cref="TextElement"/>).</param>
public sealed record BubbleElement(HudElement Child) : HudElement
{
    /// <summary>Bubble fill colour; the ALPHA is honoured so translucent tints work (the typical chat-bubble
    /// look). Null (default) = a sensible themed default (the menu accent at a soft alpha, re-tinted on a theme
    /// change so the four presets track).</summary>
    public ColorRgba? FillColor { get; init; }

    /// <summary>Hard width cap in px (canvas units). <c>0</c> (default) = clamp to the parent/available width
    /// instead of a fixed cap. When the child's natural (unwrapped) width exceeds the cap the bubble fixes at the
    /// cap and the child's wrapping text flows to multiple lines; below the cap the bubble hugs the child's width.
    /// For a right-aligned self row set an explicit cap (e.g. ~70% of the chat window) — a content-sized Row has
    /// no definite "available width" to clamp to.</summary>
    public float MaxWidth { get; init; }

    /// <summary>Corner radius in px. <c>0</c> (default) reuses the shared 3-radius rounded chip sprite (the
    /// <see cref="SelectableElement"/> chip); a positive value bakes a rounder sliced sprite at that radius (cached
    /// per radius, theme-independent). Use ~8–12 for a softer game-style bubble. Note the sliced background's minimum
    /// size is 2r+2, so for a perfectly round bubble (or capsule) of side S use r = S/2 − 1 (e.g. 13 for 28 px); a
    /// larger r makes the bubble 2 px taller than its content. The value is used as given — it is not capped.</summary>
    public float CornerRadius { get; init; }

    /// <summary>Inner horizontal padding (each side) in px between the bubble edge and its child. Default 10.</summary>
    public float PaddingX { get; init; } = 10f;

    /// <summary>Inner vertical padding (top and bottom) in px between the bubble edge and its child. Default 6.</summary>
    public float PaddingY { get; init; } = 6f;

    /// <summary>Per-refresh fill colour, mirroring <see cref="BarElement.FillColorFn"/>: evaluated every HUD
    /// refresh so the bubble can re-tint live (e.g. a chat channel tab lighting up when it becomes active)
    /// without rebuilding the element tree. Non-null wins over the static <see cref="FillColor"/>; returning
    /// null falls back to <see cref="FillColor"/> (then the themed default). Default null = unchanged: a bubble
    /// that leaves it null renders with the static fill exactly as before, no override machinery attached.
    /// Added as an init property (NOT a ctor param) so the record stays source + binary compatible with
    /// already-compiled plugins.</summary>
    public Func<ColorRgba?>? FillColorFn { get; init; }

    /// <summary>Per-refresh OUTLINE colour: a thin (1 px) ring drawn along the bubble's rounded edge, following the
    /// same <see cref="CornerRadius"/>. Evaluated every refresh; a colour shows the ring in it (alpha honoured), null
    /// hides it — so a bubble can light up a state border (e.g. "busy", "translated") without rebuilding the tree. The
    /// ring is overlaid (never measured), so toggling it never changes the bubble's size. Default null = no ring is
    /// even created: the bubble renders exactly as before. An <c>init</c> property (binary-compatible).</summary>
    public Func<ColorRgba?>? OutlineColorFn { get; init; }

    /// <summary>Thickness in px of the <see cref="OutlineColorFn"/> ring (framework 2.33.0). 0 (default) = the original
    /// 1 px ring, unchanged. A positive value draws a thicker ring INSIDE the bubble edge, at the same
    /// <see cref="CornerRadius"/> as the fill; it is snapped to 0.5 px steps and capped at the corner radius (3 px when
    /// <see cref="CornerRadius"/> is 0). Fixed at build time (the colour stays per-refresh); it never changes the
    /// bubble's size, so keep <see cref="PaddingX"/>/<see cref="PaddingY"/> larger than it if content must not sit
    /// under the ring. Has no effect without <see cref="OutlineColorFn"/>. Init property (binary-compatible).</summary>
    public float OutlineWidth { get; init; }
}

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

    /// <summary>Render in real bold weight — a designed bold FACE in every script (Latin, Thai, CJK, …),
    /// never Unity's synthetic bold. Window surfaces only (HUD-overlay text ignores the style flags).
    /// <see cref="Emphasis"/> already implies bold at header size; use this for bold at normal size.</summary>
    public bool Bold { get; init; }

    /// <summary>Slant the glyphs (synthetic italic — safe in every script). Window surfaces only.</summary>
    public bool Italic { get; init; }

    /// <summary>Draw an underline beneath the text (works in every script). Window surfaces only.</summary>
    public bool Underline { get; init; }

    /// <summary>Draw a line through the text (works in every script). Window surfaces only.</summary>
    public bool Strikethrough { get; init; }

    /// <summary>Render <c>&lt;sprite=N&gt;</c> inline emoji (the game chat's 1-based N, passed through
    /// unchanged) as actual emoji images, by hosting the text in TextMeshPro with rich text on and the GAME's own
    /// chat sprite asset — the same way the game draws chat emoji. Opt-in and additive: off (the default) nothing
    /// changes. The text then uses the framework's TMP faces (as the Bold/Italic/... styles do), and those style
    /// flags still apply. Falls back to a plain uGUI <c>Text</c> off-game (Mono sandbox) or if TMP is unavailable —
    /// worst case the literal <c>&lt;sprite=N&gt;</c> shows, so text rendering can never break; the literal tag
    /// also shows until the game's sprite asset has been found (resolved lazily once in-world). Window surfaces
    /// only; ignored on HUD-overlay and on Shadow text.</summary>
    public bool RichEmoji { get; init; }

    /// <summary>Keep the text on ONE line and, when it is wider than the space the layout gives it, cut it with a
    /// trailing "..." so it fits — re-fitted whenever the string or the laid-out width changes (e.g. the window is
    /// resized). Use it in a width-constrained slot, typically a <see cref="CellElement"/> with a <c>Weight</c> in a
    /// <see cref="RowElement"/> next to fixed-width controls: the cell's width is what the text is fitted to, and
    /// nothing spills onto the neighbours. Plain text is cut with "..." and clipped to its rect; styled
    /// (<see cref="Emphasis"/>/<see cref="Bold"/>/...) and <see cref="RichEmoji"/> text uses TextMeshPro's own ellipsis
    /// (a hard cut if the face lacks the glyph). Ignored on Shadow and HUD-overlay text. Default false = unchanged. An <c>init</c> property (binary-compatible).</summary>
    public bool Ellipsize { get; init; }

    /// <summary>Vertically centre what is actually DRAWN (the glyphs' / emoji pictures' own bounds) in the text's
    /// box, instead of the font's line box. For icon-only texts — typically a lone <see cref="RichEmoji"/>
    /// <c>&lt;sprite=N&gt;</c> used as a button or grid icon: an emoji sits on the text baseline, so with the
    /// default line-box centring it draws visibly ABOVE the middle of its box. Scales with any font size (no fixed
    /// offset). Leave it off for running text, where letters and inline emoji must share one baseline. Applies to the
    /// TextMeshPro path (styled / <see cref="RichEmoji"/> text); ignored on plain, Shadow and HUD-overlay text.
    /// Default false = unchanged. An <c>init</c> property (binary-compatible).</summary>
    public bool CenterGlyphs { get; init; }

    /// <summary>Vertically centre the font's CAPITAL height in the text's box (framework 2.33.0) — the optical centre of
    /// a one-line label in a fixed-height control (pill, chip, segment): the middle of "H" lands on the middle of the
    /// box at any font size. The default line-box centring sits the capitals a pixel or so off-centre (how far depends
    /// on the face's ascender/descender split and the size), and <see cref="CenterGlyphs"/> centres each string's OWN
    /// drawn bounds, so a label with descenders ("Party", "Playing") rises above one without ("List"). This uses the
    /// face's cap-height METRIC, not the drawn glyphs, so every label of a size shares one baseline whatever its
    /// letters. Applies to the TextMeshPro path (styled / <see cref="RichEmoji"/> text); ignored on plain, Shadow and
    /// HUD-overlay text, and when the face reports no cap height (line-box centring then, as today).
    /// <see cref="CenterGlyphs"/> wins if both are set. Default false = unchanged. An <c>init</c> property
    /// (binary-compatible).</summary>
    public bool CenterCaps { get; init; }

    /// <summary>Cap wrapped text at this many lines (framework 2.30.0). When the text wraps to more lines than this at
    /// its laid-out width, it shows exactly <c>MaxLines</c> lines and the last one ends in "…" (a cut the framework
    /// makes itself, re-fitted when the string, the font size or the width changes). The text always wraps when this
    /// is set: <see cref="NoWrap"/> and <see cref="Ellipsize"/> are ignored (<c>MaxLines = 1</c> is the one-line cut).
    /// Lines break only at spaces, after hyphens and between CJK characters — never inside a word: a single word wider
    /// than the line is cut with "…" on its own line ("CombatMet…"), not split across two lines.
    /// Give it a width-constrained slot — a fixed <see cref="Width"/> or a <see cref="CellElement"/> — since the cut
    /// fits the width the layout hands it. Works with every <see cref="Align"/>, on plain and styled
    /// (<see cref="Bold"/>/<see cref="Emphasis"/>/...) text; ignored on Shadow and HUD-overlay text. The element is
    /// only as tall as the lines it shows (a one-line string stays one line tall). 0 (the default) = unlimited,
    /// unchanged. An <c>init</c> property (binary-compatible).</summary>
    public int MaxLines { get; init; }

    /// <summary>A hard-fixed text box height in px (framework 2.30.0): the element reports exactly this as its min and
    /// preferred height, so it never grows (or shrinks) its row / column whatever its font's line box measures, and the
    /// lines are drawn vertically centred in it (a one-line string sits in the middle of a two-line slot). Content
    /// taller than the box overflows it rather than resizing it, so pair it with <see cref="MaxLines"/>. First user:
    /// the launcher tile's two-line name slot, which grew a tile 4–5 px when a name wrapped. 0 (the default) = the
    /// text's own height, unchanged. Window (Menu) text only. An <c>init</c> property (binary-compatible).</summary>
    public float Height { get; init; }
}

/// <summary>Rounded pill badge with dynamic text and optional tint colour. Suitable for short status labels (e.g. "Offline", rank numbers).</summary>
/// <param name="Text">Dynamic text displayed inside the pill; re-pulled each refresh.</param>
/// <param name="Color">Optional tint override; null (or returning null) uses the framework default pill colour.</param>
public sealed record PillElement(Func<string> Text, Func<ColorRgba?>? Color = null) : HudElement;

/// <summary>Escape hatch: plugin supplies its own PNG; framework displays it. Consistency is the
/// plugin's responsibility here (the one unenforced spot).</summary>
public sealed record ImageElement(Func<byte[]?> Png, int Width, int Height) : HudElement
{
    /// <summary>Re-pull <see cref="Png"/> on every refresh and swap the picture when the returned array changes (a
    /// REFERENCE compare, so return cached arrays — never a freshly read/allocated one per call). For an image whose
    /// picture follows live data, e.g. a pooled grid slot that shows a different item's icon after a filter or a
    /// scroll (framework 2.30.0). A transient null keeps the last picture. Default false = the picture is read ONCE
    /// when the window is built, unchanged. An <c>init</c> property (binary-compatible).</summary>
    public bool Live { get; init; }

    /// <summary>Per-refresh tint multiplied into the picture (alpha honoured), for a flat WHITE glyph PNG drawn in a
    /// theme colour (an accent star, a muted icon) or faded with its row (framework 2.30.0). Poll-diffed: only a change
    /// writes anything. Null Func (default) or a null result = untinted white, the picture's own colours, unchanged.
    /// An <c>init</c> property (binary-compatible).</summary>
    public Func<ColorRgba?>? Tint { get; init; }

    /// <summary>Draw the picture 1:1 crisp (framework 2.34.0): no mip chain + bilinear, so frame / badge art shown at
    /// its native size stays sharp instead of the default mip-smoothed icon path. Default false = unchanged.</summary>
    public bool Crisp { get; init; }
}

/// <summary>React-style conditional. Both subtrees are built once; the renderer SetActive-toggles them
/// each refresh from <paramref name="When"/> (no reconciliation). <paramref name="Else"/> may be null.
/// <paramref name="Fill"/> = the active branch expands to fill leftover height in a fixed-size (Resizable)
/// window (so e.g. a meter's list scroll grows with the window). Default false — no effect in content-sized
/// windows.</summary>
public sealed record ConditionalElement(Func<bool> When, HudElement Then, HudElement? Else = null, bool Fill = false) : HudElement
{
    /// <summary>Build the branch(es) not shown yet in the BACKGROUND after the window mounts (framework 2.30.0), a few ms
    /// per frame, and lay them out off-screen while the window is on screen — so the first show of a big branch (a
    /// settings tab) only re-activates it instead of building it in that frame. A branch shown before its background
    /// build finished is completed synchronously first (never shown half-built). Values stay live: bindings refresh on
    /// show as for any hidden branch. Ignored inside a variable-height VirtualList slot. Costs nothing once done (the
    /// built branch stays inactive while hidden). Default false = built lazily on first show, unchanged.
    /// An <c>init</c> property (binary-compatible).</summary>
    public bool Prebuild { get; init; }

    /// <summary>Diagnostics name for <see cref="Prebuild"/>: <c>[Hitch]</c> buckets <c>prebuild:&lt;name&gt;</c> and the
    /// <c>[Prebuild]</c> summary log line (HITCH/diagnostics on). Null = an index-based name.</summary>
    public string? PrebuildName { get; init; }

    /// <summary>Keep the branch ALIVE once built: when hidden it is parked (its own canvas switched off, moved out of the
    /// layout and off-screen, its bindings paused) instead of deactivated, so showing it again costs about a normal frame
    /// instead of a full re-enable + re-layout (a set of tabs switched by click). For a set of sibling Conditionals that
    /// share ONE fixed-size slot — put them alone in their own Column: the branch takes the slot's size (Fill is implied)
    /// and does not report its own size to the window. Combine with <see cref="Prebuild"/> so every branch is built and laid
    /// out in the background (and re-warmed after the window was hidden). No <see cref="ConditionalElement.Else"/>, not
    /// inside a VirtualList slot (ignored there). Not for content with InputElement.AutoFocus or
    /// RowElement.ConstrainsWindowMinWidth. Framework 2.30.0. Default false = SetActive toggling, unchanged.
    /// An <c>init</c> property (binary-compatible); its name is <c>[UiPerf] tabshow</c>'s (via <see cref="PrebuildName"/>).</summary>
    public bool KeepAlive { get; init; }

    /// <summary><see cref="KeepAlive"/> only: a cheap revision of the data the branch shows (any int that changes when
    /// that data changes — a hash of a few states, a counter). Bindings of a parked branch are paused, so data that changes
    /// while it is hidden (e.g. game HUD pieces that only exist in the world) would otherwise all be re-pulled — rows
    /// rebuilt, bubbles re-measured — in the frame it is shown again. With this, a parked branch whose revision moved is
    /// refreshed in the background (bindings on, off-screen, measures budgeted) while its window is on screen, and a
    /// re-warm after the window was hidden runs its bindings too. Polled about twice a second while parked. Framework
    /// 2.30.0. Null (default) = re-pulled on show, unchanged. An <c>init</c> property (binary-compatible).</summary>
    public Func<int>? ContentRevision { get; init; }
}

/// <summary>Variable-length list, bounded by Slots.Count. All slots built once; the first
/// <paramref name="VisibleCount"/>() are SetActive-shown each refresh. <paramref name="Columns"/>&gt;1 grids them.
/// <paramref name="CellWidth"/>/<paramref name="CellHeight"/> (when &gt; 0, multi-column only) override the grid's
/// default cell size — use to widen columns past the framework default (e.g. the StatInspector mini-HUD, whose
/// icon+label+value row needs more than the default cell width).</summary>
public sealed record ListElement(Func<int> VisibleCount, IReadOnlyList<HudElement> Slots, int Columns = 1,
    float CellWidth = 0f, float CellHeight = 0f) : HudElement
{
    /// <summary>Multi-column grid only: when true, the grid FLOWS to fill the container width instead of using the
    /// fixed <see cref="Columns"/> count — it packs as many <see cref="CellWidth"/>×<see cref="CellHeight"/> cells
    /// per row as the current width allows and wraps to new rows, re-flowing automatically when the window resizes
    /// (so the grid fills a wide/resized popover instead of leaving dead space on the right). <see cref="Columns"/>
    /// becomes advisory under this mode, and <see cref="CellWidth"/> MUST be &gt; 0 (it drives the flowed cell size).
    /// Default false = the fixed-column grid, byte-identical for every existing caller. Added as an init property
    /// (NOT a positional ctor param) so the record stays source + binary compatible with already-compiled plugins —
    /// a new ctor param would be a MissingMethodException at runtime (see WindowBuilder-Patterns.md "Binary
    /// compatibility rules").</summary>
    public bool FillWidth { get; init; }

    /// <summary>Optional — turns on drag-and-drop REORDERING of the visible items. A press on an item that moves past
    /// a small threshold (~6 screen px) starts a drag: the item dims, a ghost follows the cursor and an accent bar
    /// marks the insertion point (between grid cells, or between rows for a 1-column list). Dropping fires
    /// <c>OnReorder(from, to)</c> with REMOVE-THEN-INSERT semantics — <c>to</c> is the item's index AFTER the move,
    /// so the consumer does <c>var x = list[from]; list.RemoveAt(from); list.Insert(to, x);</c>. It never fires for a
    /// no-op drop (<c>from == to</c>). Esc, or a drop outside the list (outside its scroll viewport when it sits in a
    /// <see cref="ScrollElement"/>), cancels. A press released before the threshold is an ordinary click (the item's
    /// own <see cref="SelectableElement.OnClick"/> still fires); after a real drag the click is swallowed. The drag
    /// never moves the window or scrolls the enclosing scroll view. The grid does not reflow mid-drag — the consumer
    /// reorders its data and the list re-pulls. Default null = not reorderable. Init property, NOT a ctor param
    /// (binary compatibility).</summary>
    public Action<int, int>? OnReorder { get; init; }

    /// <summary>Optional, with <see cref="OnReorder"/>: whether the item at index <c>i</c> can be PICKED UP right now
    /// (polled on the press). False = a press on it is an ordinary click and never starts a drag; it is still a drop
    /// target. Use it to lock items (a faded placeholder) or the whole list for a while (return false for every
    /// index, e.g. while a filter makes the order ambiguous). Default null = every item can be dragged. Framework
    /// 2.30.0; init property (binary compatibility).</summary>
    public Func<int, bool>? CanReorder { get; init; }

    /// <summary>Optional spacing between items, in canvas units (both axes for a grid). Default null = the framework's
    /// 2-unit list spacing, unchanged. Framework 2.30.0; init property (binary compatibility).</summary>
    public float? Gap { get; init; }

    /// <summary>Lay the items out in ONE ROW (side by side, the list as wide as its visible items) instead of a column
    /// or a grid; <see cref="Columns"/> / <see cref="CellWidth"/> / <see cref="CellHeight"/> are ignored. With
    /// <see cref="OnReorder"/> the insertion bar sits left/right of an item. Default false = unchanged. Framework 2.30.0;
    /// init property (binary compatibility).</summary>
    public bool Horizontal { get; init; }
}
