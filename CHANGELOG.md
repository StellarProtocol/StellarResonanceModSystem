# Changelog

All notable changes to the Stellar framework are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **✍️ Writing standard (owner rule, 2026-07-25) — patch notes are for PLAYERS.**
> The launcher shows every bullet under `### Added` / `### Changed` / `### Fixed` /
> `### Removed` **verbatim to mod users** as the patch notes. Write those bullets in
> plain language: lead with what the player sees or feels ("higher FPS in dungeons",
> "no more stutter every few seconds"), one short sentence each, **no code identifiers,
> class names, or internals**. Put all technical detail — APIs, internals, measurements,
> file/type names — under a **`### Developer notes`** heading: the release pipeline
> ignores it, so it stays visible on GitHub but never reaches the launcher. The italic
> summary line under the version heading is also repo-only.

## [Unreleased]
### Added
- HUD bars can now be made taller and wider (or stretched to fill their row), given a larger label, and can show that label centred right on top of the bar — so plugins can build a big, prominent bar like a target's HP bar.
- HUD bars can also use a flatter "meter" look — a value label on each end of the bar and an optional soft moving shine — so plugins can match the Combat Meter's damage-bar style. The normal bar look is unchanged and stays the default.
### Developer notes
- `BarElement` gains five optional geometry fields (`Height`, `Width`, `FillWidth`, `LabelFontSize`, `LabelInside`) as `init`-only properties. The original four-argument positional constructor (`Fraction01`, `Fill`, `Label`, `Prefix`) is unchanged, so this is **fully backward-compatible — source AND binary**: existing compiled plugins that call `new BarElement(f, color[, label[, prefix]])` keep working with no rebuild and render identically (unset fields default to 0/false, which the renderer maps to the `BarHeight`/`BarTrackWidth`/`BarLabelSize` constants). New callers set the extras via object-initializer syntax: `new BarElement(f, color, label) { Height = 28f, FillWidth = true, LabelFontSize = 18, LabelInside = true }`. Renderer (`HudElementBuilder.BuildBar`) honours the new geometry unchanged; `LabelInside` overlays the shadowed label stretched-and-centred over the track instead of in the side slot.
- `BarElement` additionally gains three `init`-only members for an opt-in meter render — `BarStyle Style` (new enum, `Default`=0 / `Meter`), `Func<string>? SecondaryLabel`, and `bool Sheen` — leaving the positional constructor untouched, so this too is **source- AND binary-compatible**; unset `Style` defaults to `Default` and hits the pre-existing render path byte-for-byte. `BuildBar` branches to `HudElementBuilder.Meter.cs` when `Style == Meter`: a flat translucent track (`MeterTrackBg`) + a flat role-coloured anchor-clipped fill (`anchorMax.x` + `RectMask2D`, driven by a `MeterBarBinding` in `token.MeterBars`, mirroring the CombatMeter row) + dual full-width overlay labels (`Label` left / `SecondaryLabel` right). `Sheen = true` adds a procedural 64×4 texture swept per-frame through a new HUD pulse hook (`token.Pulses` → `HudBarAnimator.Step` accumulates `dt`; `HudRenderer.Destroy` removes a token's pulses by reference, and `DropCanvas` disposes the shared `_sheenTex` for `WindowRenderer` parity). Honours all geometry fields except `LabelInside` (meaningless with dual labels). Ported from `WindowBuilder.MeterRow`/`Preview`; no runtime dependency on `WindowBuilder`.

## [1.17.0] - 2026-08-05
_**1.17.0** (minor) — kill detection from the game's own logic, a run-id fix for instanced dungeons, per-class equipped-loadout capture, and a mounted-state stats-survival fix. Additive; binary-compatible with plugins built against ≤1.16.1._
### Added
- The Combat Meter can now tell exactly when an enemy dies — including bosses finished off by a cutscene or scripted move, not just a normal killing blow — so fights are timed and scored correctly.
- The logs website can now show the gear, modules, and talents you currently have equipped, for each class you play.
### Fixed
- Some dungeon runs weren't being saved to the logs website — they're recorded correctly now.
- Your character's stats and info no longer disappear while you're mounted.
### Developer notes
- Run-id: 3.7 instanced/Mistveil scenes whose scene uuid is below 2^53 were misclassified by magnitude; now classified by SceneType and early at the wire via AttrSceneBasicId (341). Fixes "No run id" (ranked runs not uploading). Commits 4683d8d, 9cb72c8.
- LIVE per-class loadout: parse the refresh chunk's LIVE row (`_StellarLiveProbe`) so a played class exposes its currently-equipped gear/modules/talents, and it is the sole source when a class has no saved plan. Commit ba74dd3 (+ the per-class gear/modules/talents series a513b29..c1a9d65).
- Player-state: rescue the local player entity when it goes dark while mounted so PlayerStats/identity survive mounting; identity served from the char record, not the world entity. Commits 1107646, 615042d, 7a40e28.
- New `Stellar.Abstractions.Domain.ActorState` enum (`Dead`=9, `Breaking`=23, `Unknown`=0 for any other/future wire value) and `CombatEvent.EntityStateChanged(TimestampMs, TargetId, ActorState)`, riding the existing `ICombatEvents` stream — no service interface gains a member, so STELLAR0005's 8-member ceiling is untouched. Field-proven after three recon rounds — see `recon/entity-state-death-signal-notes.md`. Round 1's spec'd leaf, `Panda.ZGame.EntityCtrlDead.OnEnter`, installed cleanly but stayed silent across all ten deaths in the owner's confirming run: disproven. Round 2 tried the wider `Panda.ZGame.ZStateMachine.onStateChanged`/`EnterState` hooks on top; `onStateChanged` never fired (a real negative result), but `EnterState` fired correctly for every one of the ten deaths and resolved `Dead` correctly — it was not broken, just costlier (it fires on every actor's every transition, not just the one we care about), so round 3 drops it in favour of the cheaper leaf and keeps it on record as a field-proven fallback if `ZStateDead` is ever removed/renamed. The shipped design patches exactly two sites: `Panda.ZGame.ZStateDead.OnEnter` (PROVEN — all ten deaths) and `Panda.ZGame.ZStateBreaking.OnEnter` (untested, not disproven; kept as the direct sibling of the proven hook, and as a timestamp source for an open frametime-spike investigation). Each site resolves/installs independently and degrades to "signal off" (logged) rather than throwing if a type/accessor is missing after a future game patch — see `PandaEntityStateProbe`. The site-attributed, per-scene-budgeted ungated observation line survives the cut-down unchanged. 2026-07-28 entity-state-death-signal spec; retires the HP-inference gone-timeout for `ArchiveReason.BossKill` on the plugin side (follow-up work, not in this release).

## [1.16.1] - 2026-07-25
_**1.16.1** — same code as 1.16.0, re-cut under a fresh bundle filename because a CDN cache mismatch left some 1.16.0 downloads stuck at 100%. Carries the full 1.16.0 patch notes so players updating straight from 1.15.0 see what changed._
### Fixed
- Update download no longer gets stuck at 100% for some players (a caching problem on our download server with the 1.16.0 file — this version uses a fresh file).
- Much higher FPS in dungeons and busy areas. The framework was quietly doing heavy background work several times per second — on our test machine that alone cost up to 50 FPS in a dungeon (86 to 144 after the fix). This work is now nearly free.
- The regular micro-stutter is gone. If your frametime graph showed small spikes about 5 times every second — even with no plugins installed — that was us. Fixed.
- No more short freezes every few seconds. The framework re-checked your whole inventory once per second and threw the result away, which piled up memory and caused brief frozen frames (worst on lower-end PCs). It now only does that work when your inventory actually changes.
- Smoother big fights. Network and combat data is now processed with far less memory churn, so crowded areas and boss fights cause fewer hitches.
- Zero input overhead unless you use key-blocking. The hotkey "block from game" feature used to sit on the game's keyboard input all the time; now it activates only while you actually have a blocked hotkey or are recording a new one.
### Developer notes
- Identical framework code to 1.16.0 (only FrameworkVersion + this changelog differ). The 1.16.0 manifest republish (player-facing patch notes, PR #46) rotated the bundle sha256 while CDN edges still held the previous zip (max-age 14400); launchers on those edges failed the hash check and hung at 100%. A fresh `Stellar-1.16.1.zip` filename has no cached copies anywhere, so every edge serves consistent manifest+bundle. Lesson (now in docs/release-process.md): a same-version republish requires an immediate CDN purge; prefer a patch bump instead.
- Patch-note bullets are now plain text (the launcher renders no markdown — 1.16.0's `**` showed literally).

## [1.16.0] - 2026-07-25
_**1.16.0** (minor) — the frametime-jitter release: eliminates the framework's in-dungeon FPS loss and frame-spike comb, root-caused by matched A/B on a live client (PR #44). Additive; binary-compatible with plugins built against ≤1.15.0._
### Fixed
- **Much higher FPS in dungeons and busy areas.** The framework was quietly doing heavy background work several times per second — on our test machine that alone cost up to 50 FPS in a dungeon (86 → 144 after the fix). This work is now nearly free.
- **The regular micro-stutter is gone.** If your frametime graph showed small spikes about 5 times every second — even with no plugins installed — that was us. Fixed.
- **No more short freezes every few seconds.** The framework re-checked your whole inventory once per second and threw the result away, which piled up memory and caused brief frozen frames (worst on lower-end PCs). It now only does that work when your inventory actually changes.
- **Smoother big fights.** Network and combat data is now processed with far less memory churn, so crowded areas and boss fights cause fewer hitches.
- **Zero input overhead unless you use key-blocking.** The hotkey "block from game" feature used to sit on the game's keyboard input all the time; now it activates only while you actually have a blocked hotkey or are recording a new one.
### Developer notes
_These details never appear in the launcher — full technical background lives here and in PR #44._
- Anchor probe: `PandaUGuiAdapter` ran a path-form `GameObject.Find` (full scene walk, ~30 ms/hit in dense scenes) every 200 ms while the game menu was closed; now cached `zuiroot` + relative `Transform.Find`, active-only contract preserved. A check-standards blocker now bans path-form `GameObject.Find` literals (exemptions: devkit tech-debt D-26).
- Inventory: the 1 Hz poll rebuilt the full module inventory via reflection (~1.8 MB/s garbage → 100–475 ms stop-the-world GC frames on low-spec); now generation-guarded on actual inventory syncs. Measured avgUpdAlloc 55 → 9.8 KB/tick.
- Wire tap: handler snapshot resolved before payload materialization (unsubscribed AOI/world traffic no longer copied), zero-copy reassembly drain, cached per-thread zstd decompressor with exact-size output, quiet-connection eviction + drained-buffer shrink.
- Input: `Rewired.Keyboard.GetKey*` prefixes (~8–25k calls/s at render rate) install on first block/capture and uninstall when cleared; primary-key pre-filter before modifier interop reads.
- Combat parse: AOI attr payloads slice the per-packet array (−45% alloc per combat packet), lazy cooldown list, positions cache uses an Interlocked counter instead of per-update `ConcurrentDictionary.Count`, compiled delegate replaces `MethodInfo.Invoke` for the IL2CPP span extractor.
- New PERFHUD diagnostics: `PerfProbe.HookEndRewired` + `hook:rewired=ms/calls` in the `[Perf]` line, and `BeginSeg` coverage for the previously unsegmented tick items.

## [1.15.0] - 2026-07-21
_**1.15.0** (minor) — adds per-plugin binary file storage, the substrate for CombatMeter's byte-for-byte re-upload. Additive; binary-compatible with plugins built against ≤1.14.0._
### Added
- **`IPluginServices.Data`** (`IPluginDataStore`) — per-plugin binary file storage (`Write`/`Read`/`Delete`/`List`) for data too large or opaque for `IConfigSection`. Never-throws; names are path-traversal-safe (rejects `..`, rooted paths, backslashes, `>1` separator). Each plugin's store is rooted at `<gameRoot>/stellar/plugindata/<guid>.data/` — a sibling of, and deliberately OUTSIDE, the recursive `stellar/plugins/` DLL scan path (`FrameworkPaths`, with a pinned non-nesting test), so a plugin's stored files can never be shadow-loaded as assemblies.

## [1.14.0] - 2026-07-18
_**1.14.0** (minor) — CombatMeter sync-fix surface: honest death inference, live party status transport, and dungeon flow-state. Additive; binary-compatible with plugins built against ≤1.13.0._
### Added
- **`EntityVitals.HasHpObservation`** — true only once a real current-HP value has been observed; a MaxHp-only attr delta now reads "alive, HP unknown" instead of dead. AOI appear packets now seed vitals directly (previously the first delta after appear defined them).
- **`PartyMember.FastSyncState`** — raw `TeamMemberFastSyncData.state` (field 6), previously parsed and dropped; the game client itself ignores this field, so semantics are calibrated empirically and consumers must treat unmapped values as "no signal".
- **`IDungeonState.CurrentFlowState` + `FlowStateVersion`** — the dungeon flow state machine (`EDungeonState`: Active/Ready/Playing/End/Settlement/Vote) surfaced from both the method-23 full sync and the method-24 dirty delta, with a monotonic transition counter as the poll-friendly change notification.
### Fixed
- Party fast-sync deliveries that change ONLY the member's status field now fire `MemberUpdated`.

## [1.13.0] - 2026-07-16
_**1.13.0** (minor) — adds live Trading-Center membership so exchange plugins categorize new items without a rebuild. Additive; binary-compatible with plugins built against ≤1.12.0._
### Added
- **`IExchange.GetStallSubcategoryMap()`** — item config id → Trading-Center subcategory leaf (101-104 Growth / 201-209 Life Skills / 301 Modules / 401-405 Appearance), read live at runtime from the game's `StallDetailTable`. Empty when the table isn't loaded yet; consumers fall back to their own data. Lets the Exchange Buyer plugin surface Season-3 (and future) items with no rebuild.
### Fixed
- Game config tables keyed by id in the `ZTable<int, row>` **key** (rather than a value column) — e.g. `StallDetailTable` — are now read via a key-aware path; the previous value-only table loader silently returned 0 rows for them.

## [1.12.0] - 2026-07-11
_**1.12.0** (minor) — adds the game-environment identity service for SEA/JP region-aware uploads (spec: server-region partitioning). Additive; binary-compatible with plugins built against ≤1.11.0._
### Added
- **`IGameEnvironment`** — region (`GameRegion` SEA/JP/Unknown, lowercase `RegionCode` wire form) + installed `GameVersion`, detected once at boot from install markers (`StarSEA.exe` → SEA); framework config `environment.region` overrides. Boot log prints `[Stellar] region=… version=… source=…`.

## [1.11.0] - 2026-07-10
_**1.11.0** (minor) — consolidates the portraits/replay feature line onto main (merge `ad173a0`): every addition is a new service, init-only property, or defaulted parameter under the plugins-consume-never-implement contract, binary-compatible with plugins built against ≤1.10.0. Note: the `IEntityTransforms` / `IGameDataWorld.GetMonsterByEntity` entries listed under the 1.10.0 sections below were documented there but did not ship in the 1.10.0 bundle; they ship in this release._
### Added
- **`IDungeonState`** — dungeon lifecycle + settlement service: run outcome (`DungeonOutcome`), `DungeonSettlementInfo (PassTimeSeconds, MasterModeScore, TotalScore)` with the achieved total-score carried end-to-end, defeated count, and run-identity gating for fail-outs.
- **`IEntityTransforms`** — live world transform (position + facing) reads of arbitrary entities by id (main-thread), for replay/position capture.
- **`IEntityDetail.RefreshSocialSnapshot`** — self social-data refresh (drives the game's `AsyncGetSocialData` via Lua) so plugins can re-read the local player's social snapshot (e.g. master score) after a settlement without waiting for cache expiry.
- **`SocialSnapshot.MasterScore`** and related identity fields.
- **`IGameDataWorld.GetMonsterByEntity(EntityId)`** + `MonsterInfo.MonsterType`/`IsBoss` — entity→monster-table resolution through the shared combat entity tracker.
- **Skill-phase `CombatEvent` cases** (cast phases 101–105) for forensic capture.
- **`GearInstance.BreakThroughTime`**; `PartyMember` additions; `IPluginServices` exposes the new toolkit services.

## [1.10.0] — 2026-07-03
_**1.10.0** (minor) — pure interface additions under the plugins-consume-never-implement
contract (`IEntityTransforms`, `IGameDataWorld.GetMonsterByEntity`); `MonsterInfo`'s new `MonsterType`/
`IsBoss` are init-only properties, not primary-ctor params, so the type stays binary-compatible with
plugins built against ≤1.9.1 (see the `SliderElement`/1.7.1 precedent below)._
### Added
- **`IEntityTransforms`** — toolkit service reading the live world transform (position + facing) of an
  arbitrary entity by id, for replay/position capture. `TryGetTransform` returns `false` (leaving the
  out-params at their defaults) when the entity isn't resolvable this frame; reads must happen on the
  main thread.
- **`IGameDataWorld.GetMonsterByEntity(EntityId)`** — resolves a live entity to its `MonsterInfo` table
  row via the entity's cached attr-10 (config-id) attribute, routed through a shared
  `CombatEntityTracker` so combat and game-data probes read the same cached attribute set instead of
  each maintaining their own.
- **`MonsterInfo.MonsterType` / `MonsterInfo.IsBoss`** — numeric monster classification
  (`EMonsterType`-mirrored; 0=Monster, 1=Elite, 2=Boss) and a derived boss flag, loaded from the
  `MonsterTable` row. Confirmed by recon on the Ancient Purifier run: entity attr 10 →
  `MonsterTable[33301].MonsterType == 2`.
### Fixed
- **Run-identity leak across non-instanced scenes.** `IDungeonState.CurrentRunId` (and the internal
  `DungeonRunIdGate`) now clears to 0 on entering a non-instanced (town/field) scene, instead of
  letting a previous dungeon run's id linger and get attributed to unrelated open-world activity.
### Removed
- Recon scaffolding for boss identification (the entity → monster-config-id spike diagnostic and the
  short-lived `IMonsterCatalog`/`MonsterCatalogService` probe path) — superseded by the `GetMonsterByEntity`
  + `MonsterInfo.MonsterType`/`IsBoss` resolution above and no longer needed.

## [1.10.0] - 2026-07-08
### Added
- **Window-framework support behind the CooldownBar overlay** (all additive + binary-compatible). (#32)
  - `WindowSpec.BackgroundOpacity` (`Func<float>?`) — poll-diffed black backdrop on the borderless root's
    click-blocker Image; fills the full window rect and expands on height resize.
  - `ColumnElement.Padding` (`int`) — uniform inner padding on all four sides.
  - `RowElement.Justify` + `RowJustify` enum (`Left`/`Center`/`Right`) — ships a 2-arg compat overload for
    plugins built against the old signature.
  - `BackdropElement` — stretch-fill `ignoreLayout` backdrop (place first in a column so siblings draw on top).
  - `VirtualListElement.ResetScroll` (`Func<bool>?`) — polled each refresh; true snaps the list to the top.
  - `CooldownTileElement.OnClick` (`Action?`) — whole-tile click action.
### Changed
- **Atomic game-asset icons** — `GameTextureElement` moved to `WindowBuilder.GameTexture.cs`; icon rebind is
  now a `WindowToken` binding applied in the same `Apply()` pass as the virtual list, removing the one-frame
  wrong-icon blink while scrolling. Buff icons resolve as atlas `Sprite`s instead of `Texture2D`.
- **`ConditionalElement` flex clamp** — a `Cond` node inside a row no longer sets `flexibleWidth=1` (it was
  stealing all slack, centring fixed content in a borderless bar); column parents still force-expand.
- VirtualList viewport inset 9px on the right so the scrollbar no longer overlaps content.
- Framework deploy target moved out of the csproj into `Local.props`.

## [1.9.1] - 2026-07-01
### Fixed
- **`AutoHideBehindGameMenus` windows now hide behind more full-screen game screens.**
  `PandaMenuStateProbe` was extended to detect NPC dialogue, loading screens, the dungeon-enter
  confirm popup, the line-selector panel, and story cutscenes as full-screen menu states. (#29)
  - The dungeon-enter confirm view instantiates as `team_copy_popup` on `UILayerFuncPopup` (not
    `common_matching` on `UILayerTop`), so detection now scans `UILayerFuncPopup` (`AnyChildActive`)
    and checks the `main_line_window` prefix on `UILayerMain`.
  - Removed the probe's diagnostic-logging infrastructure (`Log`, `DumpZuiroot`, `DumpActiveCanvases`);
    the layer map is documented in `GameMenuState.md` instead.

## [1.9.0] - 2026-06-30
### Added
- **`IWindowControl.BringToFront()`** — explicitly raises a window above others via a `ZFront` counter
  that sorts above category (`ZCat`), so cross-category fronting works (e.g. a HUD window surfaces above
  a Tools window). A `BringToFrontPending` flag covers the still-hidden case, so `SetVisible(true)` +
  `BringToFront()` in the same line always surfaces on top. (#27)
### Fixed
- **Interaction pass-through to covered windows.** A `FrontWindowBlocks` guard now wraps every hit-test
  in `WindowInteractionTicker` (hovers, resize grip, drag areas, scrollbar suppression, chart pan/zoom,
  chart navigator, render-host drag/zoom), so back-window elements (chart, 3D portrait, scrollbar) no
  longer receive interaction when a front window covers the pointer. (#27)
- **Dropdown item styling** now matches `SelectableElement`: rounded `SwatchBg` chip, transparent rest /
  accent@0.14 hover / accent@0.28 active selection, VLG 6/4 padding, `ContentSizeFitter`. Hover is
  ticker-driven (Unity EventSystem `ColorTint` doesn't fire reliably alongside the ticker). (#27)

## [1.8.0] - 2026-06-28
### Added
- **`DropdownElement`** — a reusable compact dropdown for a small, fixed set of mutually-exclusive choices.
  The trigger shows the current option (caption + ▾); clicking it opens a themed floating option list that
  floats **above the window's scroll clip** (parented to the canvas root, so a dropdown inside a `ScrollElement`
  is not clipped by its `RectMask2D`). Picking an option calls back with its index; the list dismisses on pick,
  outside-click (a full-screen invisible blocker), or Escape. Drop it into any window element tree like the
  other widgets.
### Changed
- The Settings → Performance per-plugin **Self-rate** control (Off / Boost / Self-managed) is now a
  `DropdownElement` instead of a click-to-cycle button.

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
