# WindowSpec record

Immutable declaration of a plugin window's identity, initial geometry, and chrome options. Passed to [`Register`](../Stellar.Abstractions.Services/IWindowHost/Register.md) to create the window.

```csharp
public record WindowSpec
```

## Public Members

| name | description |
| --- | --- |
| [WindowSpec](WindowSpec/WindowSpec.md)(…) | Immutable declaration of a plugin window's identity, initial geometry, and chrome options. Passed to [`Register`](../Stellar.Abstractions.Services/IWindowHost/Register.md) to create the window. |
| [AutoHideBehindGameMenus](WindowSpec/AutoHideBehindGameMenus.md) { get; set; } | When true the framework stops DRAWING this window while a full-screen game menu is open (it reappears on close), so combat HUDs behave like the native HUD instead of floating over menus. Does NOT change the user's Show/Hide state — purely a draw-time suppression. Defaults false. |
| [AutoSizeWidth](WindowSpec/AutoSizeWidth.md) { get; set; } | Content-size the window WIDTH to its body instead of fixing it to [`DefaultRect`](./WindowSpec/DefaultRect.md).Width. Only safe for windows without wrapping text (e.g. the launcher's fixed-width icon tiles) — the in-world clip bug that forced fixed width was a wrapping-text problem the launcher does not have. Defaults false. |
| [Category](WindowSpec/Category.md) { get; set; } | Logical category that determines which group this window appears in within the layout editor. |
| [Closable](WindowSpec/Closable.md) { get; set; } | When true the chrome draws a ✕ close glyph that hides the window. Defaults false (plugin windows manage their own visibility). Independent of [`Draggable`](./WindowSpec/Draggable.md) so a window can be draggable without a close button (e.g. the Settings hub). |
| [DefaultRect](WindowSpec/DefaultRect.md) { get; set; } | Initial position and size applied on first run (before user adjustments are persisted). |
| [Draggable](WindowSpec/Draggable.md) { get; set; } | When true the window is a movable dialog: drag-by-title-bar (the post-drag rect is committed + persisted) and excluded from the Shift+` Layout editor (it owns its own position). When false the window is positioned via the Layout editor and any title-bar drag is discarded. Defaults false. Settings windows + opt-in plugin panels (e.g. StatInspector settings) set this true. |
| [EditModeDragOnly](WindowSpec/EditModeDragOnly.md) { get; set; } | When true the chrome draws a bottom-right ↘ resize grip; dragging it changes the window size (clamped to [`MinWidth`](./WindowSpec/MinWidth.md)/[`MinHeight`](./WindowSpec/MinHeight.md) .. [`MaxWidth`](./WindowSpec/MaxWidth.md)/[`MaxHeight`](./WindowSpec/MaxHeight.md)), and the new size persists alongside the position. The window's vertical content-fit is disabled (fixed height); a `ScrollElement` in the body fills the freed space. Defaults false. The CombatMeter list uses this. |
| [HideUntilInWorld](WindowSpec/HideUntilInWorld.md) { get; set; } | When true the framework stops DRAWING this window until the player is logged in / in-world, so gameplay HUDs don't float over the title / character-select screens. Reappears once logged in. Purely draw-time suppression (does NOT change Show/Hide state). Defaults false — debug tools (DebugInfo, AutoNav) leave it off so they stay visible pre-login. |
| [Id](WindowSpec/Id.md) { get; set; } | Stable string id, unique per plugin. Used to persist position and hotkey binding. |
| [MaxHeight](WindowSpec/MaxHeight.md) { get; set; } | Maximum allowed window height in pixels when [`Resizable`](./WindowSpec/Resizable.md) is true. |
| [MaxWidth](WindowSpec/MaxWidth.md) { get; set; } | Maximum allowed window width in pixels when [`Resizable`](./WindowSpec/Resizable.md) is true. |
| [MinHeight](WindowSpec/MinHeight.md) { get; set; } | Minimum allowed window height in pixels when [`Resizable`](./WindowSpec/Resizable.md) is true. |
| [MinWidth](WindowSpec/MinWidth.md) { get; set; } | Minimum allowed window width in pixels when [`Resizable`](./WindowSpec/Resizable.md) is true. |
| [Resizable](WindowSpec/Resizable.md) { get; set; } | When true the chrome draws a resize grip; dragging it changes the window size (clamped to Min/Max bounds). |
| [ShowTitleBar](WindowSpec/ShowTitleBar.md) { get; set; } | GlassMenu only: draw the top title bar. Defaults true. Set false for windows that self-compose their own header inside the body (the launcher, whose header is top in Full/vertical but a LEFT strip in horizontal — a single fixed top bar can't express both). With no title bar the whole frame becomes the drag handle (if [`Draggable`](./WindowSpec/Draggable.md)), and the body must supply its own close affordance. |
| [StartVisible](WindowSpec/StartVisible.md) { get; set; } | Whether the window is visible on first run (before user toggles via hotkey). |
| [Style](WindowSpec/Style.md) { get; set; } | Visual chrome style applied to the window frame. |
| [Title](WindowSpec/Title.md) { get; set; } | Display title shown in the title bar and Settings layout editor. |

## See Also

* namespace [Stellar.Abstractions.Domain](../Stellar.Abstractions.md)

<!-- DO NOT EDIT: generated by xmldocmd for Stellar.Abstractions.dll -->
