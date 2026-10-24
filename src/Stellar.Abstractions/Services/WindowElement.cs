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

    /// <summary>
    /// Opt in to a knob that is exactly <see cref="HandleSize"/> square, instead of one stretched to the
    /// height of its row.
    /// <para>Unity's <c>Slider.UpdateVisuals</c> drives the handle's anchors every frame — full stretch,
    /// with only the AXIS component pinned to the value — so on the cross axis <c>HandleSize</c> acts as an
    /// ADDITION to the row height, not as the knob's height. A 13px handle in a 16px row therefore draws a
    /// 13×29 capsule (measured 2026-07-30; predicted and confirmed at <c>HandleSize=7</c> → 7×29−6=7×23).
    /// </para>
    /// <para>That stretched look is what every existing slider already renders, so it stays the DEFAULT:
    /// correcting it globally would silently restyle every plugin's sliders at once. New UI that wants a
    /// round knob opts in here. Drag behaviour is identical either way — the value is mapped from the
    /// container's WIDTH, which this does not touch.</para>
    /// </summary>
    public bool SquareHandle { get; init; }
}

/// <summary>
/// Two-axis pad: a square with a grid and a centre crosshair, and a dot at the current value. A click or a drag
/// anywhere on the pad moves the dot to the pointer and calls <paramref name="Set"/> with the pointer's value, clamped to
/// [<paramref name="Min"/>,<paramref name="Max"/>] on both axes. <c>X</c> grows to the right and <c>Y</c> grows UP
/// (math convention: the top edge is <paramref name="Max"/>), so an "up" arrow beside the pad means +Y.
/// <para>Like <see cref="SliderElement"/>, <paramref name="Set"/> is called on every drag frame (not only on release)
/// and there is no separate release callback: persist from <paramref name="Set"/> (cheaply, e.g. a config write that
/// coalesces) exactly as you would for a slider. <paramref name="Get"/> reflects external state (poll-diffed), so the
/// dot follows changes made elsewhere — arrow buttons, a reset. <paramref name="Enabled"/> null = always enabled;
/// while it returns false the pad is dimmed and ignores the pointer.</para>
/// </summary>
/// <param name="Get">Current value (X, Y).</param>
/// <param name="Set">Called with the new value (X, Y) on press and on every drag frame.</param>
/// <param name="Min">Lowest value on both axes (left / bottom edge).</param>
/// <param name="Max">Highest value on both axes (right / top edge).</param>
/// <param name="Enabled">Whether the pad takes input (poll-diffed); null = always.</param>
public sealed record XYPadElement(
    Func<(float X, float Y)> Get, Action<float, float> Set, float Min = -1f, float Max = 1f, Func<bool>? Enabled = null)
    : HudElement
{
    /// <summary>Side of the square pad in px; 0 → fill the width of its cell (the pad stays square).</summary>
    public float Size { get; init; }

    /// <summary>Grid divisions per axis (8 → 7 faint lines each way); 0 or 1 → no grid. The centre crosshair is always
    /// drawn.</summary>
    public int GridLines { get; init; } = 8;
}

/// <summary>Single-line text field (wraps the proven UGuiTextInput: Enter submits without opening chat,
/// Esc/cursor escape). <paramref name="Get"/> seeds the text; Enter/blur calls <paramref name="Submit"/>.
/// <paramref name="OnChange"/> (optional) fires per-keystroke — use it for live filters that should reflow
/// as-you-type rather than on Enter.</summary>
public sealed record InputElement(Func<string> Get, Action<string> Submit, float Width = 180f,
    Action<string>? OnChange = null) : HudElement
{
    /// <summary>Opt-in: render on a single visible line that never grows vertically. Long/pasted text
    /// overflows (scrolls) HORIZONTALLY within a fixed-height box instead of wrapping to more lines and
    /// growing the field — which, in an auto-height window, grows the whole window. Pasted newlines are
    /// stripped (the text stays one line). Unlike the default mode, Enter submits AND ends editing (the
    /// field drops focus; click it to type again) — the game's chat still never opens. Default
    /// <c>false</c> keeps the existing behaviour (the field wraps and grows), so every current
    /// InputElement is byte-for-byte unchanged. Added as an <c>init</c> property (NOT a primary-ctor
    /// param) so the record constructor stays source AND binary compatible with already-compiled plugins
    /// — see PerPluginServices-Decoration.md §3 / WindowBuilder-Patterns.md "Binary compatibility rules".</summary>
    public bool SingleLine { get; init; }

    /// <summary>Opt-in: when the window hosting this field is OPENED/toggled visible by the user, the framework
    /// gives this field keyboard focus (uGUI ActivateInputField) so the user can type immediately without
    /// clicking it first. Focus fires on the user-initiated show transition (hotkey toggle, SetVisible(true),
    /// close-button reopen) — NOT on a ShouldRender re-mount (e.g. zoning back into the world with the window
    /// already toggled on), so chat never steals movement keys on a world load. Only fires when the field's
    /// GameObject is active in the hierarchy (a field inside a collapsed ConditionalElement is skipped). If more
    /// than one field in a window sets it, the first one built wins. Default false = unchanged. Added as an init
    /// property (NOT a ctor param) so the record stays source + binary compatible with already-compiled plugins.</summary>
    public bool AutoFocus { get; init; }

    /// <summary>Opt-in: draw inline chat emoji tags (<c>&lt;sprite=N&gt;</c>, N = 1..63) in the field as the game's
    /// emoji PICTURES — like the game's own chat input box — instead of the literal tag text. The tag stays in the
    /// text unchanged (Get / OnChange / Submit all see <c>&lt;sprite=N&gt;</c>), the caret steps over a picture as
    /// one character, and one Backspace/Delete removes the whole tag. When <see cref="Get"/> changes externally
    /// (e.g. a picker appended a tag) the field shows it and the caret moves to the END. Other behaviour (Enter
    /// submit without opening chat, Esc, keyboard gate, <see cref="SingleLine"/>, <see cref="AutoFocus"/>) is the
    /// same; one difference: focusing does NOT select all, so a click places the caret instead of arming a
    /// replace-everything. Same per-string font limitation as <see cref="TextElement.RichEmoji"/> (one face per
    /// string: mixed Thai + CJK shows boxes for one script). Window surfaces only; falls back to the plain field
    /// when the rich field can't be built. Default false = unchanged. Init property (NOT a ctor param) for binary
    /// compatibility.</summary>
    public bool RichEmoji { get; init; }

    /// <summary>Opt-in: focus this field ON DEMAND, at any time (not just on window open like <see cref="AutoFocus"/>).
    /// The framework polls the returned number; whenever it CHANGES (the first value observed when the window is
    /// built never counts), the field takes keyboard focus the same way a click on it would, with the caret at the
    /// END of the text and nothing selected — so the user can keep typing. Typical use: an emoji/insert picker
    /// whose click would otherwise leave the field unfocused — keep an int, increment it in the click handler, and
    /// bind <c>FocusRequest = () =&gt; _seq</c>. While focused the game's keyboard is suppressed as for any focused
    /// field; Esc/click-away release it. A request on a field that isn't visible (collapsed ConditionalElement,
    /// hidden window) is dropped after a short wait rather than firing later. Default null = unchanged. Init
    /// property (NOT a ctor param) for binary compatibility.</summary>
    public Func<int>? FocusRequest { get; init; }

    /// <summary>Opt-in hint text shown dimmed inside the field while it is EMPTY (e.g. "Message Guild…"), in both the
    /// plain and the <see cref="RichEmoji"/> field. Polled with the window refresh and diffed, so it can follow live
    /// state; return a cached string (it is read every refresh). Default null = no hint, unchanged. Init property
    /// (NOT a ctor param) for binary compatibility.</summary>
    public Func<string>? Placeholder { get; init; }
}

/// <summary>Multi-line editable text box (wraps UGuiTextInput in multi-line mode). Enter inserts a REAL newline
/// and KEEPS focus — it does NOT submit; give the plugin its own submit button that reads the current buffer via
/// <paramref name="OnChange"/>. The box is a FIXED height (<see cref="Lines"/> visible lines) that NEVER grows the
/// window: text wraps within the width and overflows downward, clipped to the box and scrolled vertically to
/// follow the caret. Esc defocuses and the cursor stays free, same as <see cref="InputElement"/>.
/// <paramref name="Get"/> seeds the text; <paramref name="OnChange"/> fires per edit with the FULL buffer
/// (newlines included). Set <see cref="ReadOnly"/> for a select/copy-only display box (e.g. a generated code the
/// user copies out). Add any later options as <c>init</c> properties, never new positional parameters (see
/// WindowBuilder-Patterns.md "Binary compatibility rules").</summary>
/// <param name="Get">Supplies the initial text (and re-seeds on external change, diffed like InputElement). It keeps
/// driving the content when <see cref="ReadOnly"/>: change what it returns to replace the shown text.</param>
/// <param name="OnChange">Invoked per edit with the full buffer (newlines kept) — the plugin's submit button reads this.
/// With <see cref="ReadOnly"/> the user can't edit, so it never fires from user input (a <paramref name="Get"/>
/// re-seed may still echo through it); it stays required for API stability — pass a no-op.</param>
/// <param name="Width">Fixed box width in px.</param>
public sealed record TextAreaElement(Func<string> Get, Action<string> OnChange, float Width = 260f) : HudElement
{
    /// <summary>Number of visible text lines → the box's FIXED height (it never grows past this; overflow
    /// scrolls). Default 4. An init property so future additions stay binary-compatible.</summary>
    public int Lines { get; init; } = 4;

    /// <summary>Read-only display (uGUI <c>InputField.readOnly</c>): the user can still focus the box, place the
    /// caret, select with mouse or keyboard (Shift+arrows, Ctrl+A) and copy (Ctrl+C), but typing, paste, cut and
    /// delete are blocked. Scrollbar, wheel and caret-follow keep working; focus suppresses the game's keys as
    /// usual, so Ctrl+C never reaches a game binding. The box bg is drawn slightly more translucent as a subtle
    /// "not editable" hint. Default false = the normal editable box, unchanged. An init property (binary-compatible).</summary>
    public bool ReadOnly { get; init; }
}

/// <summary>Compact dropdown for a small, fixed set of mutually-exclusive choices (e.g. a mode selector) — a
/// reusable replacement for a click-to-cycle button. The trigger shows the current option (caption + ▾); a
/// click opens a themed floating option list that floats above the window's scroll clip. Picking an option
/// calls <paramref name="OnSelect"/> with its index; the list dismisses on pick, outside-click, or Esc.</summary>
/// <param name="Selected">Current selected index (poll-diffed); out of range → empty caption.</param>
/// <param name="Options">Option captions in order; the index passed to <paramref name="OnSelect"/> indexes this list.</param>
/// <param name="OnSelect">Invoked with the picked option's index.</param>
/// <param name="Width">Fixed trigger width in px; 0 → sized to the current caption like a normal button.</param>
public sealed record DropdownElement(
    Func<int> Selected, Func<IReadOnlyList<string>> Options, Action<int> OnSelect, float Width = 0f) : HudElement
{
    /// <summary>Maximum option rows shown before the floating list becomes vertically scrollable: when the live
    /// option count exceeds this, the popup height is capped at about this many rows and a themed thin scrollbar
    /// (the same one <see cref="ScrollElement"/> uses) appears. <c>0</c> (default) = unlimited — the list grows to
    /// fit every option exactly as before, so existing dropdowns are unchanged. An <c>init</c> property (NOT a
    /// primary-ctor parameter) so the record stays source AND binary compatible with already-compiled plugins —
    /// see WindowBuilder-Patterns.md "Binary compatibility rules for HudElement records".</summary>
    public int MaxVisibleRows { get; init; }
}

/// <summary>Vertical scroll viewport wrapping a child subtree + a themed thin scrollbar. By default the viewport
/// uses <paramref name="Height"/> only as a PREFERRED baseline and still GROWS to absorb slack height in a
/// resizable window (<c>flexibleHeight 1</c>); set <see cref="FixedHeight"/> to pin it instead.</summary>
public sealed record ScrollElement(HudElement Child, float Height = 200f) : HudElement
{
    /// <summary>When true, the viewport is pinned to <see cref="Height"/> (min==preferred, flexibleHeight 0) so it
    /// does NOT absorb slack height in a resizable window. Default false preserves the existing behaviour
    /// (flexibleHeight 1 → the scroll grows to fill leftover vertical space). An <c>init</c> property (NOT a
    /// primary-ctor parameter) so the record stays source AND binary compatible with already-compiled plugins —
    /// see WindowBuilder-Patterns.md "Binary compatibility rules for HudElement records".</summary>
    public bool FixedHeight { get; init; }
}

/// <summary>HSV colour picker (SV square + hue bar + hex field). <paramref name="Get"/> reflects the slot's
/// colour; a pick calls <paramref name="Set"/>. The one hand-drawn custom widget.</summary>
public sealed record ColorPickerElement(Func<ColorRgba> Get, Action<ColorRgba> Set) : HudElement
{
    /// <summary>Draw the Opacity (alpha) slider. Default true. Set false for a colour that has no alpha (e.g. a light's
    /// colour) — the picker then shows only the SV square, hue bar and hex field. An init property (not a constructor
    /// parameter) so plugins built against the two-argument constructor keep loading.</summary>
    public bool ShowAlpha { get; init; } = true;
}

/// <summary>Solid-colour box (the theme-editor colour swatch). <paramref name="Color"/> is poll-diffed so
/// it tracks live edits.</summary>
public sealed record SwatchElement(Func<ColorRgba> Color, float Size = 15f) : HudElement;

/// <summary>Full-size black backdrop behind all content — an ignoreLayout Image that stretches to cover its
/// parent GO. Place as the FIRST child of a ColumnElement so all siblings render on top. <paramref name="Opacity"/>
/// (0..1) is poll-diffed each Apply tick so a settings slider updates it in real time.</summary>
public sealed record BackdropElement(Func<float> Opacity) : HudElement;

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
public sealed record SelectableElement(HudElement Child, Action OnClick, Func<bool>? Selected = null) : HudElement
{
    /// <summary>Optional — fires with <c>true</c> when the pointer enters the row and <c>false</c> when it leaves
    /// (ticker-driven; never fires in the static sandbox). Use for hover-preview panes. Added as an init property,
    /// NOT a ctor param, so the record's primary constructor stays binary-compatible with existing plugins.</summary>
    public Action<bool>? OnHover { get; init; }

    /// <summary>Opt in to a thin accent underline bar drawn across the row's BOTTOM edge while
    /// <see cref="Selected"/>() is true — on TOP of the selected-fill tint — to mark an "active tab" in a tab
    /// strip (a soft accent pill + an underline indicator). The bar is ~3 px tall, full-width, in the theme menu
    /// accent, and is OVERLAID (ignores layout) so it never changes the row's measured size. Default <c>false</c>:
    /// every other <see cref="SelectableElement"/> (ordinary list rows, grid cells with no <see cref="Selected"/>
    /// state) is drawn exactly as before. Added as an init property, NOT a ctor param, so the record's primary
    /// constructor stays binary-compatible with existing plugins.</summary>
    public bool SelectedBar { get; init; }

    /// <summary>Optional — fires on RIGHT-button-down over the row (ticker-driven, the same hook as
    /// <see cref="MeterRowElement.OnRightClick"/>; skipped when a window in front covers the pointer). Use to open a
    /// context menu. A right-click never fires <see cref="OnClick"/>, and the left-click path is unchanged. Init
    /// property, NOT a ctor param, so the record's primary constructor stays binary-compatible.</summary>
    public Action? OnRightClick { get; init; }

    /// <summary>Opt in to a click target with NO visuals of its own: no background tint (rest, hover and selected all
    /// stay fully transparent), no padding, and the element HUGS its child instead of filling the row — so wrapping
    /// a text in it changes neither the layout nor the look, and only the child's own rect is clickable. Click,
    /// <see cref="OnHover"/>, <see cref="OnRightClick"/>, FrontWindowBlocks and the drag-reorder click swallow work as
    /// usual; <see cref="SelectedBar"/> is ignored. Use for a link-styled label whose hover look the plugin draws
    /// itself (e.g. a clickable name). Default <c>false</c> = the normal tinted, padded row. Init property, NOT a ctor
    /// param (binary-compatible).</summary>
    public bool Bare { get; init; }
}

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
    Func<UvRect>? Uv = null) : HudElement
{
    /// <summary>Corner radius in px for the displayed box. 0 = square (default). Rounds via a stencil Mask.</summary>
    public int CornerRadius { get; init; }
    /// <summary>When true the icon STRETCHES to fill the whole Width×Height box (no aspect-fit letterboxing) —
    /// use it to render an icon exactly like a stretched RawImage cell elsewhere (e.g. the meter's debuff tiles).
    /// Default false = aspect-preserving letterbox.</summary>
    public bool Fill { get; init; }
}

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
    Func<int>       ChargeCount) : HudElement
{
    /// <summary>When non-null, clicking anywhere on the tile calls this action.</summary>
    public Action? OnClick { get; init; }

    /// <summary>Drawn centered on the tile only when Icon() is null (e.g. a 2-letter abbreviation). Null/empty = nothing.</summary>
    public Func<string>? FallbackLabel { get; init; }
}
