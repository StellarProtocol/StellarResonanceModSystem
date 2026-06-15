# Writing a Stellar plugin

This is the developer guide for writing a plugin against the framework. It covers project setup, the plugin lifecycle, the public service surface (`IPluginServices`), and — most importantly — how you draw UI: Stellar renders **native uGUI** from a declarative element tree you describe once. There is no IMGUI/`OnGUI`/`GUI.Window` in the plugin API; the framework owns rendering, layout, theming, input gating, and persistence.

For framework-internal architecture, see [`architecture.md`](architecture.md). For the complete generated **API reference** of the plugin surface (every public interface, record, and enum), see [`api/`](api/) — start at [`IPluginServices`](api/Stellar.Abstractions.Services/IPluginServices.md).

## What a plugin is

A plugin is a single .NET 6 class library that:

- References **only** `Stellar.Abstractions` (plus the `UnityEngine.*` interop assemblies, if you touch any Unity type directly — most plugins don't need to).
- Exports exactly one public type implementing `IStellarPlugin`.
- Is discovered at runtime when its DLL is dropped into `<game_mini>/stellar/plugins/<PluginName>/`.

Plugins **never** reference `Stellar.Application`, `Stellar.Infrastructure`, BepInEx, HarmonyX, Il2CppInterop, or any `Panda.*` assembly. Those are framework internals; the framework hides them behind the abstractions.

## Project setup

Create a `net6.0` class library that references `Stellar.Abstractions`. Mirror a sample's csproj — e.g. `Stellar.PlayerHUD.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <RootNamespace>MyMod</RootNamespace>
    <AssemblyName>MyMod</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../../Stellar.Abstractions/Stellar.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

Most plugins need nothing more — the declarative UI toolkit means you usually never touch `UnityEngine` directly. Add the Unity interop `<Reference>` lines (see the sample csproj) only if you genuinely call a Unity API yourself.

## The single contract: `IStellarPlugin`

```csharp
public interface IStellarPlugin : IDisposable
{
    string Name { get; }
}
```

The framework constructs your plugin once via constructor injection of `IPluginServices`, and calls `Dispose()` on shutdown or when the user disables the plugin in **Settings → Plugins**. Everything you do — registering windows/HUDs, subscribing to events, declaring hotkeys, owning colours — happens in the constructor; everything you registered must be released in `Dispose()`.

## What's in the toolbox: `IPluginServices`

`IPluginServices` is the single object handed to your constructor. It aggregates every framework capability as a sub-service. Read whichever ones you need:

| Sub-service | What it gives you |
|---|---|
| `Log` (`IPluginLog`) | `Info` / `Warning` / `Error` / `Debug` into `BepInEx/LogOutput.log`. Tag your lines `[MyMod]`. |
| `Framework` (`IFramework`) | `Update` event (once per game tick, `float deltaTime`) + `FrameCount`. |
| `ClientState` (`IClientState`) | `IsLoggedIn`, `CurrentSceneName`, and `Login` / `Logout` / `SceneChanged` events. |
| `GameEvents` (`IGameEvents`) | Low-level escape hatch: `Subscribe(fullTypeName, handler)` returning an `IDisposable`. |
| `PlayerState` (`IPlayerState`) | Polled local-player snapshot: `IsAvailable`, `Name`, `Level`, `Profession`, `Health`/`MaxHealth`, `Stamina`/`MaxStamina`, `Position`. |
| `PlayerStats` (`IPlayerStats`) | Live character attribute snapshot (ATK, DEF, crit, etc.). |
| `Chat` (`IChat`) | `RecentMessages`, `MessageReceived` event, and `Send(target, text)`. |
| `CombatSnapshot` / `CombatLookup` / `CombatEvents` | Polled combat state, per-entity buff/skill lookups, and the real-time `CombatEventOccurred` stream. |
| `PartySnapshot` / `PartyRoster` / `PartyEvents` | Party roster + member vitals, and `MemberJoined`/`MemberLeft`/leader-change events. |
| `Inventory` (`IInventory`) | Read-only module inventory + equipped set (1 Hz polled) + `InventoryChanged` + `GetSelfGear()` (own gear instances: actual rolls / refine / perfection / enchant, refreshed on full container syncs). |
| `EntityDetail` (`IEntityDetail`) | Per-AOI-entity broadcast detail: `GetAttributes` (scalar attr map), `GetEquipment` (slot+itemId), `GetFashion` (worn cosmetics + dye colours). |
| `EntityContextMenu` (`IEntityContextMenu`) | Register items into the CombatMeter's right-click row menu. |
| `EntityPortrait` (`IEntityPortrait`) | The live 3D character portrait (show/hide/orbit/zoom/pan + render texture). |
| `ModuleEquip` (`IModuleEquip`) | Install / uninstall equipment modules via the game's own dispatcher. |
| `GameData` (`IGameData`) | Read-only lookups over the game's static tables (skills, buffs, items; `Combat.GetAttribute` is backfilled by a built-in EAttrType catalog — names, screen group, percent/flat format; `Equip` exposes gear rows, attr-lib roll ranges by lib id AND by row id, slot names). |
| `Config` (`IPluginConfig`) | Per-plugin JSON config, organised into named sections. |
| `Theme` (`ITheme`) | Active palette (`Theme.Colors.*`), semantic text helpers, and the colour registry. |
| `NamedTheme` (`INamedTheme`) | Active preset + global font scale. |
| `Hotkeys` (`IHotkeys`) | `DeclareAction(...)` to bind a keyboard shortcut to a callback. |
| `Windows` (`IWindowHost`) | Register interactive uGUI **windows** (draggable, closable, themed chrome). |
| `Hud` (`IHudHost`) | Register uGUI **HUDs** (overlay layer, draggable, position-persisted). |
| `NativeUi` (`INativeUiHost`) | Inject your own uGUI into the game's own UI anchors. |
| `Launcher` (`ILauncher`) | Register a tile in the Stellar launcher menu. |
| `GameAssets` (`IGameAssets`) | Async-load game-supplied icons by id: profession crests (atlas Sprite + UV), Battle Imagine, item (gear/cosmetic), and skill icons. Poll per frame; null until loaded. Pair with `GameTextureElement`. |

All event invocations happen on the Unity main thread. You do **not** need to marshal back to the main thread inside a handler.

## Drawing UI the uGUI way

You do **not** call any rendering API. You describe your UI as a tree of immutable `HudElement` records and hand it to the framework, which builds real uGUI objects and owns their lifecycle, layout, theming, refresh, and input gating.

The pattern has two halves:

- **Display** is pulled. Dynamic leaves carry a `Func<...>` the framework re-polls on its capped refresh (~10 Hz) and applies only when the value changed. A `TextElement(() => $"HP {hp}")` re-reads your field every refresh — you never push text into a widget.
- **Interaction** is pushed. A `ButtonElement(label, onClick)` calls your `Action` when clicked; a `ToggleElement`/`SliderElement` calls your `Set` callback.

So your plugin's job is: build the tree **once** in the constructor, keep your live state in fields, and let the Funcs surface it. Snapshot game state in your `Framework.Update` handler and let the tree read the snapshot.

### Element vocabulary

Layout containers and leaves all derive from `HudElement`:

- **Layout**: `RowElement(children, Gap)`, `ColumnElement(children, Gap)`, `CellElement(child, Width/Weight)` (table alignment), `SpacerElement(Width/Height)`, `SeparatorElement(Vertical)`, `ScrollElement(child, Height)`.
- **Display**: `TextElement(Func<string>, Color, Emphasis, Width, Align, Shadow)`, `BarElement(Func<float>, Fill, Label, Prefix)`, `PillElement(Func<string>, Color)`, `ImageElement`, `SwatchElement`, `GameTextureElement(Func<object?> texture, W, H, Func<UvRect>? uv)` (game-asset icon box: feed it an `IGameAssets.Load*Icon` poll — invisible until the async load lands; Funcs run per frame while visible, keep them cache-reads), `AccentRowElement(child, Stripe, Share)` (tinted row wash + left stripe — e.g. the inspector's Battle-Imagine rows).
- **Lists**: `ListElement(visibleCount, slots, Columns)` for short lists; `VirtualListElement(...)` for large windowed lists; `ConditionalElement(when, then, else)` for show/hide branches.
- **Interaction** (window-grade): `ButtonElement(Func<string> label, Action onClick, Enabled, Style, Active, Width, Icon)`, `ToggleElement(label, get, set)`, `SliderElement(get, set, Min, Max)`, `InputElement(get, submit, Width, OnChange)`, `SelectableElement`, `ColorPickerElement`.

Interaction elements work inside HUDs too, but interactive controls belong in **windows** (which gate game input correctly while focused). HUDs are best for read-only overlays.

### A HUD (read-only overlay)

`IHudHost.Register(HudSpec)` returns an `IHudHandle`. This is the PlayerHUD shape — a level pill, a name, two animated bars, and a position readout, all wrapped in a `ConditionalElement` so it shows "Player not loaded" before you're in-world:

```csharp
private IHudHandle _hud = null!;
private PlayerSnapshot _snap;   // your own struct, refreshed each tick

private void BuildHud()
{
    _hud = _services.Hud.Register(new HudSpec(
        Id:     "mymod.playerhud",
        Anchor: HudAnchor.FreeOverlay,
        Root:   new ConditionalElement(
            When: () => _snap.IsAvailable,
            Then: new ColumnElement(new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new PillElement(() => $"Lv {_snap.Level}"),
                    new TextElement(() => _snap.Name ?? "(unknown)"),
                }, Gap: 6f),
                new BarElement(() => Frac(_snap.Health, _snap.MaxHealth), _hpSlot.Value,
                               () => $"{_snap.Health} / {_snap.MaxHealth}", Prefix: "HP"),
                new TextElement(() => $"Pos {_snap.Position.X:0.0}, {_snap.Position.Z:0.0}"),
            }, Gap: 4f),
            Else: new TextElement(() => "Player not loaded")),
        HideUntilInWorld: true));
}

private void OnUpdate(float dt)
{
    var ps = _services.PlayerState;
    _snap = new PlayerSnapshot { IsAvailable = ps.IsAvailable, Name = ps.Name,
        Level = ps.Level, Health = ps.Health, MaxHealth = ps.MaxHealth, Position = ps.Position };
    _hud.MarkDirty();   // optional hint — the framework polls regardless
}

private static float Frac(int v, int max) => max > 0 ? (float)v / max : 0f;
```

`MarkDirty()` is an optional "apply now" hint; forgetting it never freezes the HUD because the framework polls anyway. Canonical reference: `Stellar.PlayerHUD`.

### A window (interactive)

`IWindowHost.Register(WindowRegistration)` returns an `IWindowControl`. A window has themed chrome (`WindowSpec.Style`), an optional close button, and drag/resize behaviour. Build the `WindowSpec`, give it a root element, and (optionally) leading/trailing title content + an `OnClose`:

```csharp
private IWindowControl _window = null!;

private void BuildWindow()
{
    _window = _services.Windows.Register(new WindowRegistration(
        Spec: new WindowSpec(
            Id:          "mymod.main",
            Title:       "MyMod",
            DefaultRect: new WindowRect(40f, 120f, 360f, 0f),  // Height 0 = content-sized
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        {
            Closable = true,
            Draggable = true,
            HideUntilInWorld = true,
        },
        Root: new ColumnElement(new HudElement[]
        {
            new TextElement(() => _status, Emphasis: true),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => "Greet", DoGreet),
                new ToggleElement(() => "Verbose", () => _verbose, v => _verbose = v),
            }, Gap: 6f),
            new InputElement(() => _draft, OnSubmit, Width: 240f),
        }),
        OnClose: () => _window!.SetVisible(false)));   // keep IsShown in sync with the ✕
}
```

`IWindowControl` lets you manage the live window: `SetVisible(bool)`, `IsShown`, `MarkDirty()`, `SetRect(...)` / `Rect`, and `Remove()`. Wire `OnClose` to `SetVisible(false)` (as above) so the ✕ and any hotkey/rail toggle stay agreed about visibility. Canonical reference: `Stellar.ChatTools` — a multi-section window with a scrolling log, a channel selector, an input composer, and a conditional sub-panel.

## Convenience APIs

The toolkit ships shorthands for the most common multi-step patterns. Prefer them.

### Window + hotkey toggle in one call

Registering a window and then declaring a hotkey that toggles it is the most common pairing, so `IWindowHost` has a combined overload — pass the `WindowRegistration`, a `HotkeyAction`, and your `IHotkeys` service:

```csharp
_window = _services.Windows.Register(
    new WindowRegistration(spec, root, OnClose: () => _window!.SetVisible(false)),
    new HotkeyAction(
        Id:               "mymod.toggle",
        Description:       "Toggle MyMod window",
        SuggestedDefault:  new KeyBinding(StellarKeyCode.F12)),
    _services.Hotkeys);
```

The returned `IWindowControl` manages the window; the hotkey is owned by the `IHotkeys` service for its lifetime, so you don't track a separate `IHotkeyAction` handle for it.

### Single-default colour registration

When a colour is the same across every theme preset, skip the per-preset dictionary and use the single-value overload:

```csharp
_accentSlot = _services.Theme.ColorRegistry.Register(
    "MyMod.Highlight.Fill", "Highlight", ColorRgba.FromHex(0x4CC15Cffu));
```

### Disposing a colour slot

`IColorSlot` is `IDisposable`; disposing it unregisters it from the registry. So `_slot.Dispose()` in your `Dispose()` is both the cleanup and the unregister — no separate `Unregister(key)` call needed.

### Quiet config save

`IConfigSection.Save()` persists and raises `IPluginConfig.SectionChanged`. When you're writing *because* you reacted to that event (echo suppression), call `SaveQuiet()` instead — it persists without re-raising the event.

## Theming

The active theme is exposed via `IPluginServices.Theme`.

- **Read a theme colour** when you want to match the framework's palette: `_services.Theme.Colors.Accent`, `.MenuMuted`, `.HudText`, `.TextMuted`, etc. (Base / HUD / Menu colour facets are all reachable through `Theme.Colors`.) Read these directly — do not register a slot for them.
- **Own a colour** that the user can customise in the theme editor: register it with `IColorRegistry`. The key is namespaced `Owner.Concept.Property` and must be unique. You supply a default per built-in preset (or one default for all):

```csharp
_hpSlot = _services.Theme.ColorRegistry.Register(
    "MyMod.HpBar.Fill", "HP bar", new Dictionary<ThemePreset, ColorRgba>
    {
        [ThemePreset.Default] = ColorRgba.FromHex(0x4CC15Cffu),
        [ThemePreset.Dark]    = ColorRgba.FromHex(0x52A35Effu),
        [ThemePreset.Light]   = ColorRgba.FromHex(0x46C85Effu),
        [ThemePreset.Crimson] = ColorRgba.FromHex(0xE04848ffu),
    });
```

Read the resolved colour via `_hpSlot.Value` (it honours the active preset and any user override). **Cache the slot handle, not the value** — `Value` re-resolves each read, so a `Func<ColorRgba>` like `() => _hpSlot.Value` keeps a bar correctly recoloured as the user switches themes. Dispose the slot in `Dispose()`.

## Settings (config)

Each plugin gets one JSON file split into named sections. Read on construct, write when the user changes something, subscribe to react to external edits:

```csharp
var section = _services.Config.GetSection("general");
_verbose = section.Get("verbose", false);          // typed, never throws, returns default if absent
// ... later, on a user change:
section.Set("verbose", _verbose);
section.Save();                                     // flush to disk + raise SectionChanged
```

Supported value types: primitives (`int`, `long`, `bool`, `string`, `float`, `double`), arrays of primitives, and string-keyed dictionaries of primitives. For complex objects, use multiple keys. Subscribe to `IPluginConfig.SectionChanged` (the argument is the section name) to react to settings-window writes; use `SaveQuiet()` when you write in response to it.

## Hotkeys

Declare a bindable action and a callback. The framework resolves the binding from user config, falling back to your `SuggestedDefault`:

```csharp
_toggleAction = _services.Hotkeys.DeclareAction(
    new HotkeyAction(
        Id:               "mymod.toggle",
        Description:       "Toggle MyMod",            // shown in Settings → Hotkeys
        SuggestedDefault:  new KeyBinding(StellarKeyCode.F11, ModifierKeys.Ctrl)),
    callback: () => _window.SetVisible(!_window.IsShown));
```

`DeclareAction` returns an `IHotkeyAction` (`IDisposable`) — dispose it in `Dispose()`. If the hotkey just toggles a window, prefer the combined `Windows.Register(..., HotkeyAction, IHotkeys)` overload instead.

## Lifecycle and the Dispose contract

Every plugin MUST implement `Dispose()` correctly. The **Settings → Plugins** panel lets the user disable / re-enable any plugin at runtime by calling `Dispose()` then re-constructing it. If `Dispose` leaks a subscription, the re-enabled plugin's handler fires twice on every event.

**Release everything you acquired in the constructor:**

- `-=` every event handler you `+=` (`Framework.Update`, `ClientState.Login/Logout/SceneChanged`, `Chat.MessageReceived`, `CombatEvents.CombatEventOccurred`, `Inventory.InventoryChanged`, `Config.SectionChanged`, …). **Capture handlers in fields** — inline lambdas (`X += () => ...;`) leak because `-=` can't find the same delegate instance later.
- `Remove()` every `IHudHandle` and `IWindowControl`.
- `Dispose()` every `IColorSlot` and every `IHotkeyAction`.
- `Dispose()` every token returned by `IGameEvents.Subscribe(...)` and `ILauncher.Register(...)`.

A clean `Dispose()` for the window-plus-colours pattern:

```csharp
public void Dispose()
{
    _services.Framework.Update -= OnUpdate;
    try { _services.Chat.MessageReceived -= OnMessage; } catch { /* swallow */ }
    _hpSlot.Dispose();
    _toggleAction.Dispose();
    _window.Remove();
}
```

**Disposal must not throw.** Wrap any detach that might race framework shutdown in `try { ... } catch { /* swallow */ }`. Test soft-cycle correctness yourself: toggle your plugin off and on a few times in Settings → Plugins and confirm the log stays clean and behaviour matches a fresh load.

## Threading

| Surface | Thread |
|---|---|
| `IFramework.Update` | Unity main thread |
| Element `Func`/`Action` callbacks | Unity main thread (during the framework's poll/build) |
| `IChat.MessageReceived` | Unity main thread (drained from an I/O queue once per `Update`) |
| `IClientState` / `IPartyEvents` / `ICombatEvents` | Unity main thread |
| `IGameEvents` subscriptions | Same thread the underlying game event fires on — usually main |

You can assume single-threaded handlers. If you start your own threads, marshal back to the main thread (enqueue and drain from your `Update` handler) before touching any framework or Unity state.

## Logging conventions

- **Tag every line** with your plugin name: `[MyMod]`, so the user can `grep` your output.
- **Don't spam.** Hot paths (`Update`, element Funcs, `MessageReceived`) should log at most once per state transition, not per invocation.
- **Use the right level.** `Info` for normal flow, `Warning` for recoverable problems, `Error` for failures the user needs to see, `Debug` for diagnostics.

## The no-cheating boundary

Stellar holds the same line Dalamud does — QoL, not exploitation:

- **Events are read-only.** Observe `PlayerState`, `Chat`, combat, party, inventory; do not try to mutate game state by writing back through them.
- **No packet construction.** Never assemble protobuf bytes, modify in-flight packet bodies, or send custom-built messages.
- **Game actions go through the game's own dispatcher.** `IChat.Send` and `IModuleEquip` install/uninstall are permitted because the *game's* code builds the request and runs its own validation (slot lock, type conflict, max-count). Supply the inputs; never short-circuit a lower layer to bypass a game-side check.

## Build and deploy

```bash
cd (local reference)
(local reference) build Stellar.sln -c Release
(local reference)    # the game must be closed
```

`install-stellar.sh` deploys the framework into `BepInEx/plugins/Stellar.Framework/` and each sample plugin into `stellar/plugins/<PluginName>/`. If you add a new sample, extend the `USER_PLUGINS` array near the top of the script.

Launch via Heroic (do **not** invoke Wine directly — Heroic sets `WINEDLLOVERRIDES=winhttp=n,b`, which BepInEx needs). Watch the log:

```bash
tail -f /opt/game/BlueProtocol2/drive_c/Star/StarLauncher/game/release_*/game_mini/BepInEx/LogOutput.log
```

## Reference plugins

| Plugin | Purpose | What to learn from it |
|---|---|---|
| `Stellar.DebugInfo` | Minimal scene/frame readout | Smallest scaffold; subscribing to `ClientState` events |
| `Stellar.PlayerHUD` | HP/stamina/identity HUD from `IPlayerState` | `IHudHost` + `HudElement` tree, snapshot-in-`Update` pattern, owned colour slots, hotkey toggle |
| `Stellar.ChatTools` | Chat log + composer + whisper auto-reply | `IWindowHost` multi-section window, the combined window+hotkey `Register` overload, `IChat` lifecycle, `ScrollElement`/`InputElement`/`ConditionalElement` |
| `Stellar.AutoNav` | Autonomous navigation test fixture | Advanced test-only pattern (not representative of normal plugins) |
