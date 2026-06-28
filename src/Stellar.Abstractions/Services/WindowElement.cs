using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

// Interactive window elements. They derive from HudElement so they compose inside the
// existing Row/Column/Grid layout records and flow through the same element tree. Display
// state is pulled via Func (re-polled + value-diffed); interaction pushes via Action.

/// <summary>Clickable button. <paramref name="Enabled"/> null = always enabled. <paramref name="Style"/>
/// null = the chrome's active IChromeStyle default. <paramref name="Active"/> (poll-diffed) renders the
/// accent/filled look when true — used for active tab / toggle-button highlighting. <paramref name="Width"/>
/// &gt; 0 fixes the button width (it can't grow/shrink) and WRAPS its label — use for right-aligned cells
/// (e.g. a hotkey binding) that must never overflow the row regardless of font metrics. <paramref name="Icon"/>
/// (optional PNG) renders a leading icon INSIDE the button, co-centred with the label by the button's own layout
/// (so icon↔label alignment can't drift with font metrics — used by the Settings hub's iconed tabs).</summary>
public sealed record ButtonElement(
    Func<string> Label, Action OnClick, Func<bool>? Enabled = null, MenuButtonStyle? Style = null,
    Func<bool>? Active = null, float Width = 0f, Func<byte[]?>? Icon = null) : HudElement
{
    /// <summary>When non-null, invoked on click with the button's screen rect (top-left origin, same coordinate
    /// space as <see cref="IWindowControl.Rect"/>). Use to anchor a popup to the button's actual position.</summary>
    public Action<Stellar.Abstractions.Domain.WindowRect>? OnClickWithRect { get; init; }
}

/// <summary>Two-way toggle. <paramref name="Get"/> reflects external state (poll-diffed); a click calls
/// <paramref name="Set"/> with the new value.</summary>
public sealed record ToggleElement(
    Func<string> Label, Func<bool> Get, Action<bool> Set, Func<bool>? Enabled = null) : HudElement;

/// <summary>1 px themed divider (the faint row/section separator). <paramref name="Vertical"/> = a 1 px-wide
/// full-height divider for splitting columns inside a Row (else a 1 px-tall full-width line between rows).</summary>
public sealed record SeparatorElement(bool Vertical = false) : HudElement;

/// <summary>Gap inside a Row. <paramref name="Width"/> = 0 → flexible (expands to push following siblings to the
/// far edge); &gt; 0 → a fixed-width spacer (e.g. to balance a right-aligned
/// control so a centred element is TRULY centred, not centred-minus-that-control). <paramref name="Height"/>
/// &gt; 0 → a fixed-height gap (use in a Column for a little vertical margin between sections, no divider line).</summary>
public sealed record SpacerElement(float Width = 0f, float Height = 0f) : HudElement;

/// <summary>Drag slider over [<paramref name="Min"/>,<paramref name="Max"/>]. <paramref name="Get"/> reflects
/// external state (poll-diffed); a drag calls <paramref name="Set"/>.</summary>
public sealed record SliderElement(
    Func<float> Get, Action<float> Set, float Min = 0f, float Max = 1f, Func<bool>? Enabled = null) : HudElement
{
    /// <summary>Fixed track width in px; 0 → elastic (the track expands to fill its Row cell).</summary>
    public float Width { get; init; }

    /// <summary>Handle (knob) size in px; 0 → the theme default handle size.</summary>
    public float HandleSize { get; init; }
}

/// <summary>Single-line text field (wraps the proven UGuiTextInput: Enter submits without opening chat,
/// Esc/cursor escape). <paramref name="Get"/> seeds the text; Enter/blur calls <paramref name="Submit"/>.
/// <paramref name="OnChange"/> (optional) fires per-keystroke — use it for live filters that should reflow
/// as-you-type rather than on Enter.</summary>
public sealed record InputElement(Func<string> Get, Action<string> Submit, float Width = 180f,
    Action<string>? OnChange = null) : HudElement;

/// <summary>Vertical scroll viewport (fixed <paramref name="Height"/>) wrapping a child subtree + a themed
/// thin scrollbar.</summary>
public sealed record ScrollElement(HudElement Child, float Height = 200f) : HudElement;

/// <summary>HSV colour picker (SV square + hue bar + hex field). <paramref name="Get"/> reflects the slot's
/// colour; a pick calls <paramref name="Set"/>. The one hand-drawn custom widget.</summary>
public sealed record ColorPickerElement(Func<ColorRgba> Get, Action<ColorRgba> Set) : HudElement;

/// <summary>Solid-colour box (the theme-editor colour swatch). <paramref name="Color"/> is poll-diffed so
/// it tracks live edits.</summary>
public sealed record SwatchElement(Func<ColorRgba> Color, float Size = 15f) : HudElement;

/// <summary>Icon tile (launcher): a centred PNG <paramref name="Icon"/> over an optional <paramref name="Label"/>,
/// no background. Hover brightens icon+label and grows the icon ~1.18× (the native rail feel). The whole tile
/// clicks via <paramref name="OnClick"/>. When <paramref name="Pinned"/> != null, a ★/☆ toggle overlays the
/// top-right corner (its own click → <paramref name="OnTogglePin"/>). <paramref name="Label"/> null ⇒ icon-only
/// (title-bar mode/rotate toggles); a non-null Func ALWAYS builds the label cell (so a live label that is empty
/// at build time — e.g. a plugin that registers later — still shows its name once available).</summary>
public sealed record TileElement(
    Func<byte[]?> Icon, Func<string>? Label, Action OnClick,
    float Width = 88f, float IconSize = 30f,
    Func<bool>? Pinned = null, Action? OnTogglePin = null) : HudElement;

/// <summary>Animated brand logo (the launcher's Stellar sparkle): an accent-tinted <paramref name="Sparkle"/>
/// inside an accent button cell, over a soft <paramref name="Glow"/> halo whose alpha + scale PULSE. The
/// renderer drives the pulse each frame (the builder is sandbox-pure → renders static in the sandbox).
/// <paramref name="Size"/> is the sparkle size (default 22).</summary>
public sealed record BrandLogoElement(Func<byte[]?> Sparkle, Func<byte[]?> Glow, float Size = 22f) : HudElement;

/// <summary>Width-controlled column cell — fixes or weights a cell's width for aligning a
/// Row's children into a table. Wraps any <paramref name="Child"/> subtree. <paramref name="Width"/> &gt; 0 fixes
/// the cell width (cannot grow/shrink — use for numeric columns like Current / Proj / Δ so they align across the
/// header + every body row regardless of font metrics). <paramref name="Weight"/> &gt; 0 makes the cell GROW to
/// share leftover row width in that proportion (the elastic label column, or master-detail panes — Weight 1 : 2 =
/// ⅓ : ⅔). Width and Weight are mutually exclusive (Width wins if both set); both 0 = natural content size. Per-cell
/// colour stays on the Child (e.g. <see cref="TextElement.Color"/>) — the cell owns geometry only. Header and body
/// align by using the SAME cell specs on every Row. Weighted cells require the enclosing Row to be a direct child of
/// a Column / Scroll content / window body (all force-expand width — true for every layout that needs alignment).</summary>
public sealed record CellElement(HudElement Child, float Width = 0f, float Weight = 0f) : HudElement;

/// <summary>Makes any <paramref name="Child"/> subtree clickable as a whole and tints its background by interaction
/// state — the rich-row analog of <see cref="ButtonElement"/> (which only wraps a single label). For list rows that
/// are multi-line / multi-widget (a history session, a recent-lookup entry) where a per-row button would lose the
/// layout. Rest = transparent; hover = faint accent wash; <paramref name="Selected"/>() true (poll-diffed) = a
/// stronger accent fill. A click anywhere on the row fires <paramref name="OnClick"/>. Composes inside a List/Column
/// like any leaf.</summary>
public sealed record SelectableElement(HudElement Child, Action OnClick, Func<bool>? Selected = null) : HudElement;

/// <summary>Wraps a row <paramref name="Child"/> with a per-row accent backdrop drawn BEHIND it: a faint
/// <paramref name="Stripe"/>-tinted bar whose width is <paramref name="Share"/> (0..1) of the row, plus a 3-px
/// <paramref name="Stripe"/>-coloured left edge. The CombatMeter History/Skill table rows use this for the
/// role-coloured stripe + share-fraction wash the IMGUI build drew (DrawRowAccent). Both Funcs are poll-diffed.</summary>
public sealed record AccentRowElement(HudElement Child, Func<ColorRgba> Stripe, Func<float> Share) : HudElement;

/// <summary>One bespoke CombatMeter row — the borderless, role-coloured, animated meter row (HP spine + class
/// crest + name·spec·share line + role-coloured metric bar with a per-second/total overlay + self highlight +
/// offline scrim). The framework reproduces the custom visual that the IMGUI <c>MeterRowView</c> drew, so the
/// meter keeps its distinct look (it does NOT ride the generic Row/Bar primitives). <paramref name="Data"/> is
/// re-pulled on the window's capped refresh (poll-diffed) — the plugin snapshots its per-combatant state so the
/// Func never allocates. Fixed 48-px row height; width fills the row. Compose inside a <see cref="ListElement"/>.</summary>
/// <param name="Data">Per-combatant row snapshot, re-pulled on the window's capped refresh.</param>
/// <param name="OnRightClick">Optional: invoked when the row is right-clicked (CombatMeter row context menu); null = no menu.</param>
public sealed record MeterRowElement(System.Func<MeterRowData> Data, System.Action? OnRightClick = null) : HudElement;

/// <summary>
/// Wraps a grid cell so it can be dragged onto another cell. <paramref name="Key"/>
/// identifies the cell (the CombatMeter uses the flat <c>group*5+slot</c> index).
/// <paramref name="OnDrop"/> fires on the grabbed cell when it is released over a
/// different registered cell, as <c>OnDrop(fromKey, toKey)</c>.
/// <paramref name="CanDrag"/> gates the whole interaction (e.g. leader-only); when it
/// returns <c>false</c> the cell neither drags nor accepts drops. While dragging, a
/// dimmed ghost of <paramref name="Child"/> follows the cursor and the hovered target
/// highlights. Renderer-neutral — no IMGUI tokens.
/// </summary>
/// <param name="Child">The cell content (e.g. a <see cref="MeterRowElement"/>).</param>
/// <param name="Key">Stable identifier for this cell within its drag group.</param>
/// <param name="OnDrop">Callback invoked with (fromKey, toKey) on a valid drop.</param>
/// <param name="CanDrag">Optional gate; null means always draggable.</param>
public sealed record DragSlotElement(
    HudElement Child, int Key, System.Action<int, int> OnDrop, System.Func<bool>? CanDrag = null) : HudElement;

/// <summary>
/// Hosts a live render texture in a fixed-size box (e.g. the Entity Inspector's 3D portrait). The framework
/// binds the boxed <c>UnityEngine.Texture</c> returned by <paramref name="Texture"/> onto a uGUI RawImage and
/// re-pulls it each frame, so a texture created after the window builds still appears. Renderer-neutral — the
/// texture crosses the boundary as <see cref="object"/> so Abstractions names no Unity type; null = blank box.
/// </summary>
/// <param name="Texture">Supplies the boxed render texture to display (null until ready).</param>
/// <param name="Width">Box width in px.</param>
/// <param name="Height">Box height in px.</param>
/// <param name="OnDrag">Optional: called with pointer-drag deltas (dx,dy in px) while the box is dragged — e.g.
/// to orbit a 3D preview camera. Null = the box ignores drags.</param>
/// <param name="OnScroll">Optional: called with the scroll-wheel delta while the cursor is over the box — e.g.
/// to zoom a 3D preview. Null = the box ignores scrolls.</param>
/// <param name="OnPan">Optional: called with pointer-drag deltas (dx,dy in px) while the box is dragged with Shift
/// held — e.g. to pan a 3D preview camera. Null = Shift+drag falls back to <paramref name="OnDrag"/>.</param>
/// <param name="OnViewportResize">Optional: called each frame with the box's current pixel size (w,h) so a 3D
/// preview can size its render texture + projection to the (resizable) pane — fills it with no letterbox/stretch.</param>
/// <param name="TransparentBackground">When true the pane draws NO opaque backdrop behind the texture — the
/// render texture's own alpha shows whatever is behind the pane (the window chrome), so only the rendered
/// subject is visible. Default false = solid dark backdrop.</param>
public sealed record RenderTextureHostElement(System.Func<object?> Texture, int Width, int Height,
    System.Action<float, float>? OnDrag = null, System.Action<float>? OnScroll = null,
    System.Action<float, float>? OnPan = null, System.Action<int, int>? OnViewportResize = null,
    bool TransparentBackground = false) : HudElement;

/// <summary>Displays a game-asset texture handle (boxed <c>UnityEngine.Texture</c>) at a fixed pixel
/// size, optionally cropped to a UV sub-rect (for atlas icons such as profession crests). The framework
/// re-pulls <paramref name="Texture"/> (and <paramref name="Uv"/> when set) each frame, so an icon whose
/// async load finishes after the window builds still appears. Renderer-neutral — the handle crosses the
/// boundary as <see cref="object"/>. Null texture = an invisible box that keeps its layout slot. The
/// simpler sibling of <see cref="RenderTextureHostElement"/> (no drag/zoom/pan, no backdrop) and of
/// <see cref="SpriteElement"/> (which takes PNG bytes, not a live texture handle).</summary>
/// <param name="Texture">Supplies the boxed texture to display (null while loading / unavailable).</param>
/// <param name="Width">On-screen width in px.</param>
/// <param name="Height">On-screen height in px.</param>
/// <param name="Uv">Optional dynamic UV sub-rect (0..1, bottom-left origin); null = full texture.
/// Both Funcs are invoked every frame while the element is active in the hierarchy (hidden windows/tabs
/// are skipped; a late-loaded icon binds on its first visible frame) — keep them cheap
/// (cache lookups, no allocation).</param>
public sealed record GameTextureElement(Func<object?> Texture, int Width, int Height,
    Func<UvRect>? Uv = null) : HudElement;

/// <summary>Sub-rect of a packed atlas PNG — the <c>DrawTextureWithTexCoords</c> analog. <paramref name="Atlas"/> is
/// the whole packed sheet (loaded once, mipmap-smoothed); <paramref name="Uv"/> is the normalized sub-rect to show
/// (x,y,w,h in 0..1, origin BOTTOM-left per Unity texture space); <paramref name="Width"/>/<paramref name="Height"/>
/// are the on-screen pixel size. Use for icon atlases (e.g. a stat-icon sheet) where <see cref="ImageElement"/>'s
/// whole-PNG display can't pick one glyph. <paramref name="Atlas"/> is poll-friendly (re-read each build).
/// <paramref name="UvFunc"/> (optional) makes the sub-rect DYNAMIC — re-pulled on the window refresh so one
/// pooled/recycled slot can show a different atlas cell as its backing data changes (e.g. a stat-icon keyed to
/// the attribute the row currently represents). When null, <paramref name="Uv"/> is static (set once at build)
/// — use that for a fixed icon (e.g. a gear).</summary>
public sealed record SpriteElement(Func<byte[]?> Atlas, UvRect Uv, int Width, int Height, Func<UvRect>? UvFunc = null) : HudElement;

/// <summary>Scroll-windowed variable list: renders a small fixed <paramref name="Pool"/> of K rows
/// (K ≈ viewport rows + a few margin rows) over a logical list of <paramref name="Count"/>() items that may
/// be FAR larger than K. The framework sizes a scroll content spacer to Count()*RowHeight, tracks the scroll
/// offset, computes the first visible logical index, calls <paramref name="OnWindow"/>(first) BEFORE pulling
/// any slot values (so the plugin's row Funcs resolve item first+slotIndex), positions the pool rows at their
/// logical offset, and SetActive-shows only rows whose logical index &lt; Count(). Rows MUST be uniform
/// fixed-height (<paramref name="RowHeight"/>). Use for large pickers; the small <see cref="ListElement"/>
/// (eager full pool) stays for short lists. <paramref name="Height"/> is the viewport height.</summary>
public sealed record VirtualListElement(
    Func<int>  Count,
    float      RowHeight,
    IReadOnlyList<HudElement> Pool,
    Action<int> OnWindow,
    float      Height = 200f) : HudElement;

/// <summary>One cooldown/debuff tile for the CooldownBar: a fixed icon square (game-asset art via
/// <paramref name="Icon"/>/<paramref name="Uv"/>) with an <paramref name="Accent"/>-tinted outline, a foot
/// fill-bar (width = <paramref name="Fill01"/>, 0..1), an optional ★ corner badge when
/// <paramref name="IsImagine"/>, an optional ×N charge count when <paramref name="ChargeCount"/> &gt; 1, and a
/// centred <paramref name="Seconds"/> caption below — all poll-diffed each refresh. The framework binds the
/// boxed texture on the window's refresh pass (renders without the runtime ticker, unlike
/// <see cref="GameTextureElement"/>), so it appears immediately. Compose pooled tiles inside a
/// <see cref="RowElement"/> wrapped in <see cref="ConditionalElement"/>s for active-only collapse.</summary>
/// <param name="Icon">Supplies the boxed <c>UnityEngine.Texture</c> for the tile art (null = neutral box).</param>
/// <param name="Uv">The atlas UV sub-rect for the art (0..1, bottom-left origin).</param>
/// <param name="Fill01">Foot fill-bar fraction (0..1) — typically cooldown/​debuff completion.</param>
/// <param name="Seconds">Remaining-time caption shown below the icon.</param>
/// <param name="Accent">Tile accent — outline + fill + caption tint (e.g. cyan = cooldown, red = debuff).</param>
/// <param name="IsImagine">When true, a ★ corner badge marks an Imagine-lockout tile.</param>
/// <param name="ChargeCount">When &gt; 1, a ×N badge shows remaining charges.</param>
public sealed record CooldownTileElement(
    Func<object?>   Icon,
    Func<UvRect>    Uv,
    Func<float>     Fill01,
    Func<string>    Seconds,
    Func<ColorRgba> Accent,
    Func<bool>      IsImagine,
    Func<int>       ChargeCount) : HudElement;
