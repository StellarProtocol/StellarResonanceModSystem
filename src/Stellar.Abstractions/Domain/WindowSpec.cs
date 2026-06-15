namespace Stellar.Abstractions.Domain;

/// <summary>Immutable declaration of a plugin window's identity, initial geometry, and chrome options.
/// Passed to <see cref="Services.IWindowHost.Register(Services.WindowRegistration)"/> to create the window.</summary>
/// <param name="Id">Stable string id, unique per plugin. Used to persist position and hotkey binding.</param>
/// <param name="Title">Display title shown in the title bar and Settings layout editor.</param>
/// <param name="DefaultRect">Initial position and size applied on first run (before user adjustments are persisted).</param>
/// <param name="Category">Logical category that determines which group this window appears in within the layout editor.</param>
/// <param name="Style">Visual chrome style applied to the window frame.</param>
public sealed record WindowSpec(string Id, string Title, WindowRect DefaultRect, WindowCategory Category, WindowPanelStyle Style)
{
    /// <summary>Whether the window is visible on first run (before user toggles via hotkey).</summary>
    public bool StartVisible { get; init; } = true;

    /// <summary>
    /// When true the window is a movable dialog: drag-by-title-bar (the post-drag rect is
    /// committed + persisted) and excluded from the Shift+` Layout editor (it owns its own
    /// position). When false the window is positioned via the Layout editor and any
    /// title-bar drag is discarded. Defaults false. Settings windows + opt-in plugin panels
    /// (e.g. StatInspector settings) set this true.
    /// </summary>
    public bool Draggable { get; init; }

    /// <summary>
    /// When true the chrome draws a ✕ close glyph that hides the window. Defaults false
    /// (plugin windows manage their own visibility). Independent of <see cref="Draggable"/>
    /// so a window can be draggable without a close button (e.g. the Settings hub).
    /// </summary>
    public bool Closable { get; init; }

    /// <summary>
    /// When true the framework stops DRAWING this window while a full-screen game
    /// menu is open (it reappears on close), so combat HUDs behave like the native
    /// HUD instead of floating over menus. Does NOT change the user's Show/Hide
    /// state — purely a draw-time suppression. Defaults false.
    /// </summary>
    public bool AutoHideBehindGameMenus { get; init; }

    /// <summary>
    /// When true the framework stops DRAWING this window until the player is logged
    /// in / in-world, so gameplay HUDs don't float over the title / character-select
    /// screens. Reappears once logged in. Purely draw-time suppression (does NOT
    /// change Show/Hide state). Defaults false — debug tools (DebugInfo, AutoNav)
    /// leave it off so they stay visible pre-login.
    /// </summary>
    public bool HideUntilInWorld { get; init; }

    /// <summary>
    /// Content-size the window WIDTH to its body instead of fixing it to <see cref="DefaultRect"/>.Width.
    /// Only safe for windows without wrapping text (e.g. the launcher's fixed-width icon tiles) — the in-world
    /// clip bug that forced fixed width was a wrapping-text problem the launcher does not have. Defaults false.
    /// </summary>
    public bool AutoSizeWidth { get; init; }

    /// <summary>
    /// When true the chrome draws a bottom-right ↘ resize grip; dragging it changes the window size (clamped to
    /// <see cref="MinWidth"/>/<see cref="MinHeight"/> .. <see cref="MaxWidth"/>/<see cref="MaxHeight"/>), and the
    /// new size persists alongside the position. The window's vertical content-fit is disabled (fixed height); a
    /// <c>ScrollElement</c> in the body fills the freed space. Defaults false. The CombatMeter list uses this.
    /// </summary>
    /// <summary>
    /// The window's drag mode, honoured by EVERY chrome (decoupled from chrome style):
    ///   false (default) → free-drag any time, like a popup dialog (Settings, DataInspector, CombatMeter History);
    ///   true            → "pinned": moves only while layout edit-mode (Shift+`) is active, and the editor draws
    ///                      its movable rect — for gameplay HUD overlays (CombatMeter meter, AutoNav) that
    ///                      shouldn't move during play.
    /// </summary>
    public bool EditModeDragOnly { get; init; }

    /// <summary>When true the chrome draws a resize grip; dragging it changes the window size (clamped to Min/Max bounds).</summary>
    public bool Resizable { get; init; }
    /// <summary>Minimum allowed window width in pixels when <see cref="Resizable"/> is true.</summary>
    public float MinWidth  { get; init; } = 160f;
    /// <summary>Minimum allowed window height in pixels when <see cref="Resizable"/> is true.</summary>
    public float MinHeight { get; init; } = 120f;
    /// <summary>Maximum allowed window width in pixels when <see cref="Resizable"/> is true.</summary>
    public float MaxWidth  { get; init; } = 1600f;
    /// <summary>Maximum allowed window height in pixels when <see cref="Resizable"/> is true.</summary>
    public float MaxHeight { get; init; } = 1200f;

    /// <summary>
    /// GlassMenu only: draw the top title bar. Defaults true. Set false for windows that self-compose their own
    /// header inside the body (the launcher, whose header is top in Full/vertical but a LEFT strip in horizontal
    /// — a single fixed top bar can't express both). With no title bar the whole frame becomes the drag handle
    /// (if <see cref="Draggable"/>), and the body must supply its own close affordance.
    /// </summary>
    public bool ShowTitleBar { get; init; } = true;
}
