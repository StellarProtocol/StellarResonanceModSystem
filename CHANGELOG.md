# Changelog

All notable changes to the Stellar framework are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.7.1] - 2026-06-28
### Fixed
- **Binary-compatibility regression from 1.7.0.** `SliderElement`'s new `Width` / `HandleSize` were added
  to the record's **primary constructor**, which changed the generated constructor signature and broke the
  old positional ctor. Plugins compiled against framework ≤1.6.0 that construct a `SliderElement` (e.g.
  `Stellar.AutoFishing`) failed to load on 1.7.0 with a `TargetInvocationException` wrapping
  `MissingMethodException`. `Width` / `HandleSize` are now **init-only properties** (the original primary
  ctor is restored), so those plugins load again.

## [1.7.0] - 2026-06-28
### Added
- **Per-plugin & dynamic update rate.** Each plugin's `IFramework.Update` now ticks at its own rate
  instead of being welded to the single global rate. In **Settings → Performance** a new "Per-plugin
  update rate" section lets the user set each plugin's rate (default *Follow global*) and grant a
  per-plugin **Self-rate** permission (Off / Boost / Self-managed).
- **`IFramework.RequestUpdateRate(int hz)` → `IUpdateRateScope`** — a permitted plugin can temporarily
  ramp its own tick rate (e.g. a market snipe's fast-retry window) and revert by disposing the scope.
  Returns an inert no-op scope unless the user granted the plugin permission. "Self-managed" exempts the
  ramp from the 10 s leak-guard for plugins that hold an elevated rate indefinitely.
- **`SliderElement.Width` / `SliderElement.HandleSize`** for compact, fixed-size sliders; `ToggleElement.Enabled`
  is now honored by the renderer; fixed-width `NoWrap` text now clips to its column.
### Changed
- The framework tick is now a single **variable-speed clock** at `max(global, all active plugin rates)`
  (clamped `[10,240]`, realized ≤ frame rate). The Lua-bridge probe drains ride the master tick so a
  ramped plugin's RPC round-trips complete proportionally faster; expensive draw/refresh work stays
  gated to the global rate. Idle behaviour (nothing ramped) is unchanged from 1.6.0.
### Fixed
- Perf overlay no longer freezes its per-window timings when the master rate exceeds the global rate.

## [1.6.0] - 2026-06-25
### Added
- **`IExchange` — player Trading-Center market access**, exposed as `IPluginServices.Market`. Plugins can
  query the live market and buy through the game's **own** trade flow — no packet construction; every
  purchase is built and validated server-side. Driven via the game's WorldProxy exchange RPCs ("Approach A",
  headless — the trade page never needs to be open).
  - Reads: `QueryListingsAsync(itemId)` (live listings, cheapest-first), `QueryCatalogAsync(category)`
    (category browse — the request's `type` is the `StallCategory` family, derived from the leaf id),
    `QueryCareListAsync(kind)` (watch list + availability), `QueryNoticeAsync(itemId)` (scheduled/pre-order
    listings).
  - Buy: `BuyAsync(itemId, quantity, price)` → `ExchangeBuyOutcome`
    (`Success` / `NoItemAvailable` / `InsufficientFunds` / `Rejected` / `Timeout`).
  - DTOs: `ExchangeListing`, `ExchangeCatalogItem`, `ExchangeCareItem`, `ExchangeNoticeListing`,
    `ExchangeItemKind`.
- **Extensible toast notifications.** `INotifications.Create()` fluent builder
  (`WithMessage` / `WithKind` / `WithDuration` / `WithIcon`) with custom-icon support — render a supplied
  texture (e.g. a game item icon) in the toast's icon slot; `null` falls back to the baked per-kind glyph.
  The existing `Notify(string, kind, seconds)` shortcut is unchanged.

## [1.5.0] - 2026-06-24
### Added
- **Team voice & dungeon ready-check, surfaced as typed party events.** The framework decodes the
  game's voice and ready-check wire traffic and exposes it through `IPartyEvents` / `IPartyRoster`,
  so plugins consume clean events instead of touching IL2CPP or Lua. (#21)
  - **Ready-check:** `WorldNtfLuaStubDispatcher` (HarmonyX postfix on `ZLuaStub.OnCallStub`) catches
    methods 70/71, which flow through the Lua stub rather than `WorldNtfStub`; `NotifyReadyCheckReader`
    decodes `NotifyAllMemberReady` (70) / `NotifyCaptainReady` (71) into
    `IPartyEvents.ReadyCheckResponded` / `ReadyCheckPhaseChanged`.
  - **Team voice:** `GrpcTeamNtf` methods 25/26 (mic mode / speaking) decoded in
    `NotifyTeamVoiceReaders`; `voice_is_open` (`TeamMemData` f7) + `mem_real_time_voice_infos`
    (`GetTeamInfoReply` f4) parsed for correct state on join/relogin (incl. the `OpenSpeaker` edge
    case the bool can't express). New `MicrophoneStatus` enum + `IPartyRoster.GetMicStatus` /
    `IsSpeaking` — additive; `PartyMember` stays binary-compatible.
  - **Meter row (`MeterRowData`):** `NameColor` (ready-check vote tint), `VoiceIcon` /
    `VoiceIconTint` / `ShowVoiceIcon` (mic icon), `RowBorder` (green while talking), `CrestTint`.
- **UI primitives for click-away popups.**
  - `WindowSpec.DismissOnOutsideClick` — Escape or press-outside invokes `OnClose` and hides the
    window; `IsShown` stays in sync.
  - `PanelElement` — themed popup container (2 px border + lifted background + padded content host).
  - Deterministic z-ordering (`ReorderWindows`): draw order follows `(ZOrder, Category, Id)`
    regardless of plugin mount order, so click-away popups always render on top.
### Fixed
- **Meter row border now draws all four edges** (previously only the top edge was visible).
- **`WindowRenderer.SetRect` clamps programmatic resizes to `MinWidth` / `MaxWidth`.** Previously
  only drag-resize was clamped, so mode switches, `RefreshPartyFocusHeight`, and prefs restore could
  silently push a window below its registered minimum.

## [1.4.2] - 2026-06-22
### Fixed
- **Non-ASCII text input (e.g. Thai) truncated in uGUI fields.** Switched from `onValidateInput`
  (per-char ASCII gate) to `onValueChanged`, so multi-byte / IME input is preserved. (#19)

## [1.4.1] - 2026-06-22
### Fixed
- **Periodic ~200 ms in-game freeze from the rail-button template lookup.** The lookup walked the
  entire UI tree on a timer; it's now scoped to the menu-panel subtree. (#17)

## [1.4.0] - 2026-06-22
### Added
- **`IWindowControl.SetVisiblePersist(bool)`** — show/hide a window AND persist the choice to the
  active layout slot (per resolution), so it survives relaunch. This is the single source of truth
  the framework reapplies on launch (the layout-editor eye-toggle writes the same slot). Plugins
  should use it for user-driven visibility toggles (hotkeys, close buttons) instead of `SetVisible`
  plus a private config key, which desyncs from the slot and loses to it on relaunch. `SetVisible`
  is now documented as session-only (non-persisting).
### Fixed
- **Native-UI edit-mode grab-boxes dropped to the bottom-left corner during loading / cutscenes.**
  The game collapses its HUD to ~1px stubs during those transitions and the edit outline followed
  them down. It now holds each element's last real-size rect (carried across the scene-change
  re-resolve) and never caches a collapsed stub.
- **Repositioned game-UI elements flung off-screen / left at the game default after a cutscene.**
  `SetRect` no longer runs on an inactive element (its world-corners are garbage mid-cutscene), caps
  any bogus translate, and re-applies the saved position once the game resets the element; the 1 Hz
  re-assert no longer force-shows elements the game is hiding for a cutscene.

## [1.3.0] - 2026-06-21
### Added
- **Native notice banners (`INoticeTips` on `IPluginServices`)** — trigger the game's own notice
  system (dungeon bars, win/fail banners, pop tips) with full control over content and audio via a
  fluent builder (`Create`/`WithContent`/`WithAudio`/`WithDuration`/`Show`). `NoticeTipService.Show()`
  is thread-safe: it enqueues the pre-built Lua chunk to a `ConcurrentQueue` drained on the
  main-thread tick, so plugins may call it from async continuations or any thread without
  marshalling boilerplate. (#15)
- **Consume hotkey keypresses (Settings → Hotkeys)** — a global toggle that blocks bound keys from
  reaching the game via Rewired while the framework still receives them through `UnityEngine.Input`.
  Blocking is modifier-aware (Ctrl+F1 bound does not also block bare F1), and all keys are suppressed
  while a rebind cell is open. Backed by `IHotkeyBlockDirectory` + `HotkeyKeyBlockPatch`. (#15)

## [1.2.0] - 2026-06-19
### Added
- **Loadout API (`ILoadout` on `IPluginServices`)** — read the player's saved in-game loadouts
  (the game's "Role Plan" system: class + gear + spec + modules) and switch to one. Drives the
  game's own `AsyncSwitchRolePlan` (the path the in-game dropdown uses), so all server-side
  validation (combat lock, etc.) is respected, never bypassed. Backed by `PandaLoadoutProbe` over
  the tolua# Lua bridge + `WorldProxy`.
- **Notification toasts (`INotifications` on `IPluginServices`)** — transient on-screen toasts any
  plugin can raise (`Notify(message, kind, seconds)`), rendered top-centre with a Pop+Scale
  animation, per-kind colour, and a countdown bar. First consumer: the LoadoutSwitcher plugin.

## [1.1.2] - 2026-06-18
### Fixed
- Launcher tile icons are now live-bound: a plugin icon that loads *after* the launcher is built
  (plugins register asynchronously, and the Full / Minimal layouts materialise their tiles at
  different times) no longer stays baked to the generic puzzle-piece fallback — it now appears in
  every launcher mode. Fixes a plugin icon showing in expanded mode but the puzzle-piece in minimal
  mode. (#11)
- Game-data name resolution: locale-gate the empty-`Name` → `NameDesign` fallback so the design-name
  fallback only kicks in where it should, instead of leaking into clients that do have a localized
  name. (#12)

## [1.1.1] - 2026-06-18
### Added
- **Plugin SDK on NuGet.org** — `Stellar.Abstractions`, `Stellar.PluginContracts`, and
  `Stellar.Plugin.InteropRefs` (the Unity/Il2Cpp/BepInEx compile-time reference stubs) are published
  via Trusted Publishing, so plugins build with just `<PackageReference>`s — no framework checkout and
  no game install. First step toward per-plugin repos (see the DevKit's DIP17 migration plan).
### Changed
- Framework runtime is unchanged from 1.1.0 (this release adds the SDK + contributor docs only).

## [1.1.0] - 2026-06-18
### Added
- `LineChartElement` — multi-series time-series line chart with labelled X/Y axes, axis titles, a
  legend, auto-scaled Y, and interactive zoom (scroll/drag + −/+/Reset buttons + range scrollbar).
  Rendered via an injected `MaskableGraphic` mesh (`ChartGraphic`). First consumer: CombatMeter
  history charts.
- Generic inter-plugin exchange: `IPluginExchange` (`Provide<T>` / `Consume<T>`) on `IPluginServices`,
  brokered purely by `Type`, plus the new **`Stellar.PluginContracts`** assembly that cooperating
  plugins reference (alongside `Stellar.Abstractions`) for shared contracts — `FrozenEntity` and
  `IFrozenEntityViewer`. (#5 Phase 2)
- HUD text enhancements on `TextElement`: `FontSize`, `DynamicFontSize`, `ShadowDistance`, and
  `NoWrap`, plus `TextAlign` (Left/Center/Right). Existing 6-argument call sites stay source- and
  binary-compatible via explicit secondary constructors.
- HUD centering anchors `HudAnchor.ScreenCenterX` / `ScreenCenterY` / `ScreenCenter` (resolution-
  independent via the RectTransform anchor system) and `HudSpec.DynamicDefaultRect`.
- `IFramework.ScreenWidth` / `ScreenHeight`, refreshed once per frame, for resolution-aware layout.
- `ICombatSpec` combat-spec lookup; `MeterRowData.SelfAccent`.
- Buff/skill `NameDesign` fallback; leveled `skill_level_ids` now resolve to their base skill names.
### Changed
- Icon-only overlay buttons are centered and equal-size.
### Fixed
- Hotkey handling, layout-editor input leak, combat-spec resolution, and theme-name sync (in-game
  hotfix batch, #2).
- Programmatic `SetRect` is clamped on-screen so windows can't be placed unreachable.
- `BarElement` fill width now tracks `Fraction01`.

## [1.0.1] - 2026-06-17
### Fixed
- Per-frame `GameObject.Find` leak that decayed FPS across dungeon re-entries.

## [1.0.0] - 2026-06-08
### Added
- In-game plugin launcher overlay
### Fixed
- Party roster for raid groups

## [0.9.0] - 2026-05-29
### Added
- Native HUD element editor (move / hide)
- PlayerHUD demonstrator
