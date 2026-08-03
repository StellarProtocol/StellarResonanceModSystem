# WindowSpec record

Immutable declaration of a plugin window's identity, initial geometry, and chrome options. Passed to [`Register`](../Stellar.Abstractions.Services/IWindowHost/Register.md) to create the window.

```csharp
public record WindowSpec : IRenderGated
```

## Public Members

| name | description |
| --- | --- |
| [Anchor](WindowSpec/Anchor.md) { get; set; } | Anchor for the initial placement of [`DefaultRect`](./WindowSpec/DefaultRect.md) on the (possibly scaled) window canvas. Defaults to TopLeft = legacy absolute top-left. Use Center etc. to center/corner-anchor without computing the UI scale yourself; DefaultRect.X/Y then act as a canvas-unit offset from the anchor. A user's saved drag still overrides this. |
| [AutoSizeWidth](WindowSpec/AutoSizeWidth.md) { get; set; } | Content-size the window WIDTH to its body instead of fixing it to [`DefaultRect`](./WindowSpec/DefaultRect.md).Width. Only safe for windows without wrapping text (e.g. the launcher's fixed-width icon tiles) — the in-world clip bug that forced fixed width was a wrapping-text problem the launcher does not have. Defaults false. |
| [BackgroundOpacity](WindowSpec/BackgroundOpacity.md) { get; set; } | Borderless windows only: poll-diffed black background opacity (0 = transparent, 1 = fully black). Applied to the root's existing click-blocker Image so the background fills the entire window rect and expands when the user resizes height — no separate child GO needed. Null = no background (default). |
| [Category](WindowSpec/Category.md) { get; set; } | Logical category that determines which group this window appears in within the layout editor. |
| [Closable](WindowSpec/Closable.md) { get; set; } | When true the chrome draws a ✕ close glyph that hides the window. Defaults false (plugin windows manage their own visibility). Independent of [`Draggable`](./WindowSpec/Draggable.md) so a window can be draggable without a close button (e.g. the Settings hub). |
| [DefaultRect](WindowSpec/DefaultRect.md) { get; set; } | Initial position and size applied on first run (before user adjustments are persisted). |
| [DismissOnOutsideClick](WindowSpec/DismissOnOutsideClick.md) { get; set; } | When true the framework auto-hides this window on Escape or a mouse press outside its rect — the click-away dismiss a cursor popup / context menu wants. The dismiss invokes the registration's `OnClose` (wire it to [`SetVisible`](../Stellar.Abstractions.Services/IWindowControl/SetVisible.md)(false)); with no OnClose the flag is inert. Handled on the per-render-frame interaction ticker, NOT the throttled framework tick, so it never misses a one-frame click/key edge (a press lasting one rendered frame would be missed by a plugin polling input from its throttled OnUpdate). |
| [Draggable](WindowSpec/Draggable.md) { get; set; } | When true the window is a movable dialog: drag-by-title-bar (the post-drag rect is committed + persisted) and excluded from the Shift+` Layout editor (it owns its own position). When false the window is positioned via the Layout editor and any title-bar drag is discarded. Defaults false. Settings windows + opt-in plugin panels (e.g. StatInspector settings) set this true. |
| [EditModeDragOnly](WindowSpec/EditModeDragOnly.md) { get; set; } | When true the chrome draws a bottom-right ↘ resize grip; dragging it changes the window size (clamped to [`MinWidth`](./WindowSpec/MinWidth.md)/[`MinHeight`](./WindowSpec/MinHeight.md) .. [`MaxWidth`](./WindowSpec/MaxWidth.md)/[`MaxHeight`](./WindowSpec/MaxHeight.md)), and the new size persists alongside the position. The window's vertical content-fit is disabled (fixed height); a `ScrollElement` in the body fills the freed space. Defaults false. The CombatMeter list uses this. |
| [Id](WindowSpec/Id.md) { get; set; } | Stable string id, unique per plugin. Used to persist position and hotkey binding. |
| [MaxHeight](WindowSpec/MaxHeight.md) { get; set; } | Maximum allowed window height in pixels when [`Resizable`](./WindowSpec/Resizable.md) is true. |
| [MaxWidth](WindowSpec/MaxWidth.md) { get; set; } | Maximum allowed window width in pixels when [`Resizable`](./WindowSpec/Resizable.md) is true. |
| [MinHeight](WindowSpec/MinHeight.md) { get; set; } | Minimum allowed window height in pixels when [`Resizable`](./WindowSpec/Resizable.md) is true. |
| [MinWidth](WindowSpec/MinWidth.md) { get; set; } | Minimum allowed window width in pixels when [`Resizable`](./WindowSpec/Resizable.md) is true. |
| [Resizable](WindowSpec/Resizable.md) { get; set; } | When true the chrome draws a resize grip; dragging it changes the window size (clamped to Min/Max bounds). |
| [ShouldRender](WindowSpec/ShouldRender.md) { get; set; } | The single source of visibility truth (`hide = !ShouldRender()`, evaluated each apply ~10 Hz). Compiler-`required`: every [`WindowSpec`](./WindowSpec.md) MUST set it or the build fails. Read whatever you want — `Phase`, `UiState`, your own state — via the plugin's captured services. Use `() => true` for always-on chrome, `() => _services.ClientState.Phase == GamePhase.World` for a gameplay window. |
| [ShowTitleBar](WindowSpec/ShowTitleBar.md) { get; set; } | GlassMenu only: draw the top title bar. Defaults true. Set false for windows that self-compose their own header inside the body (the launcher, whose header is top in Full/vertical but a LEFT strip in horizontal — a single fixed top bar can't express both). With no title bar the whole frame becomes the drag handle (if [`Draggable`](./WindowSpec/Draggable.md)), and the body must supply its own close affordance. |
| [StartVisible](WindowSpec/StartVisible.md) { get; set; } | Whether the window is visible on first run (before user toggles via hotkey). |
| [Style](WindowSpec/Style.md) { get; set; } | Visual chrome style applied to the window frame. |
| [Title](WindowSpec/Title.md) { get; set; } | Display title shown in the title bar and Settings layout editor. |
| [ZOrder](WindowSpec/ZOrder.md) { get; set; } | Explicit draw-order among Stellar windows: HIGHER draws on top. Default 0. The framework stacks windows by (ZOrder, then [`Category`](./WindowSpec/Category.md) as a tiebreak — HUD&lt;Tools&lt;Debug — then Id), so a plugin that sets this fully controls where its window sits relative to others regardless of load/mount order; one that leaves it 0 falls back to the category default. Click-away ([`DismissOnOutsideClick`](./WindowSpec/DismissOnOutsideClick.md)) popups always render above all of these. |

## See Also

* interface [IRenderGated](./IRenderGated.md)
* namespace [Stellar.Abstractions.Domain](../Stellar.Abstractions.md)

<!-- DO NOT EDIT: generated by xmldocmd for Stellar.Abstractions.dll -->
