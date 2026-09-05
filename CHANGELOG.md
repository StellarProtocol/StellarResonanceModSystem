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

## [2.6.1] - 2026-09-05
_**2.6.1** (patch) — "no weapon skin" is saved as no weapon skin again. Infrastructure-only, binary-compatible with plugins built against ≤2.6.0._
### Fixed
- Saving an outfit while your weapon wears no skin now records "no skin". It used to record whichever skin the wardrobe was showing you at the time, so applying that outfit later put that skin back — even after you had changed to a different weapon, and even though you had never chosen a skin. Now an outfit saved with no weapon skin always shows your current weapon's own look, whatever weapon you are holding. Outfits you already saved keep the skin they recorded until you use Update on them.
### Developer notes
- Capture-side half of the `SkinId 0` ⇄ origin contract. The game has no "skin 0": the Weapon Skin tab builds its tiles with `isEmpty = value.Original == 1` and marks worn the tile whose `Id == weaponSkillSkinVm:GetWeaponOriginSkinId(curProfessionId)` (`fashion_weapon_skin_select_view.lua:116-120`), so picking ⊘ sends `UseProfessionSkin(pid, <that origin row id>)` and the server stores a CONCRETE `WeaponSkinTable` row whose look happens to be the weapon's native one. Measured on the owner's report: stored outfit 9 held `WeaponProfessionId=5, WeaponSkinId=7350002` = "Mirrorlight Ring", `ProfessionId 5`, `Original: 1` — and profession 5 has **four** `Original==1` rows (7350002, 7350006, 7351061, 7351062), one per weapon family, so the raw id pins the outfit to the weapon it was saved with. `PandaFashionProbe.WeaponCaptureLua` now normalises a worn skin equal to `GetWeaponOriginSkinId(cur)` back to `0`, where `cur` is `CharSerialize.professionList.curProfessionId` — exactly the id `profession_vm.GetContainerProfession` returns, which that VM method requires to match or it returns nil. Both consumers already resolved `0` → origin at USE time (`BuildWeaponSkinChunk` for apply, mirroring `AsyncUseProfessionSkin:199`; `PandaWardrobePreviewProbe.WeaponSkinSourceLua` for the 3D preview), so only the producer was wrong; `ParseWeaponSkin` and the `"<pid>:<skin>"` global format are unchanged, and the `IWardrobe.GetWornWeaponSkin` XML doc now states why `0` is the honest value. The origin lookup sits in its OWN `pcall` nested inside the weapon block's, so a nil VM, a throwing `GetVM`, a throwing `GetWeaponOriginSkinId` or an origin that resolves `0` all keep the RAW id rather than losing the capture. Verified against real Lua 5.3 with stubbed `Z.ContainerMgr.CharSerialize` + `Z.VMMgr.GetVM('weapon_skill_skin')`, 9/9 cases: worn 7350002 / origin 7350002 → `"5:0"`; worn 7350003 / origin 7350002 → `"5:7350003"`; origin call throws → `"5:7350002"`; `UseSkinId` nil → `"5:0"` (no VM call at all); plus VM-nil, GetVM-throws, origin-0, float-typed `UseSkinId` and no-current-class. Pinned by `WardrobeWeaponSkinTests.CaptureChunk_ReportsTheCurrentWeaponsOriginSkin_AsNoSkin`. Owner report 2026-09-05, after the 2.6.0 release.

## [2.6.0] - 2026-09-05
_**2.6.0** (minor) — saved outfits can now carry your weapon skin, and the outfit preview shows both head accessories. Additive, binary-compatible with plugins built against ≤2.5.0._
### Added
- Plugins can now read the weapon skin your current class is wearing and switch it for you, through the same game action as the Wardrobe's Weapon Skin tab. The Wardrobe plugin uses this to save and re-apply your weapon skin together with an outfit.
- The outfit 3D preview now shows the weapon skin a saved outfit carries, so you see the whole look before you switch. Weapon skins belong to one class, so the preview shows it when the outfit's weapon skin is for the class you are currently playing — otherwise the weapon is left as it looks now.
### Fixed
- The outfit 3D preview no longer shows two weapons at once. When a saved outfit carried a weapon skin, the preview drew that skin *and* the weapon you are currently holding; it now shows only the saved one, the way the game's own wardrobe and shop previews do. Outfits saved before weapon skins existed still preview with the weapon you are wearing.
- The outfit 3D preview now shows both head accessories at once. An outfit with two head pieces used to preview only the second one — the first appeared for a moment and was then replaced.
- No more freeze when a plugin saves its settings. Saving an outfit in the Wardrobe used to lock the game up for about a third of a second and then stutter for a few seconds afterwards — the more you had saved, the worse it got. Saving is now instant, however many outfits you keep.
- Switching to the loadout you are already wearing no longer re-equips everything. Pressing it a couple of times in a row used to freeze the game for seconds and then stutter; it now does nothing but tell you that loadout is already on.
- On-screen notices no longer hitch the game when several arrive at once. Saving a few outfits in a row, or tapping a loadout hotkey repeatedly, used to drop frames for every notice it put on screen; notices now reuse the banner that is already up, and a notice repeating word-for-word while its own copy is still visible is left alone instead of stacking.
- Loadout switches now respect the game's own three-second switch cooldown, exactly like the in-game dropdown does: a second press right after the first is refused — with the game's own "you switched too recently" message — instead of piling another full re-equip on top of the one still running.
### Developer notes
- `IWardrobe` gains `GetWornWeaponSkin()` → `WardrobeWeaponSkin(ProfessionId, SkinId)` (read from `CharSerialize.professionList.professionList[curProfessionId].UseSkinId` in the same capture chunk as the outfit) and `ApplyWeaponSkinAsync(professionId, skinId)` (drives `WorldProxy.UseProfessionSkin` exactly as `weapon_skill_skin_vm.AsyncUseProfessionSkin` does: skin 0 → the class's origin skin, `OnWeaponSkinChange` dispatch on ok; shares the single in-flight slot with `ApplyAsync`, so await the outfit before sending the skin). Weapon skins stay OUT of the outfit region map (`WardrobeRegions.All` unchanged) — they are a per-class game system. Pinned by `WardrobeWeaponSkinTests` + `WardrobeServiceTests`.
- `PandaWardrobePreviewProbe` now stamps each `SingleWearData.SlotID` with the piece's `FashionRegion` — the shape of the game's own `fashion_vm.GetFashionWearList` (`data.SlotId = region`). SlotID routes head pieces to their mount (713 → HeadWear, 718 → HeadWear2); `SlotID=0` put both on one mount so the second overwrote the first. Pinned by `WardrobePreviewChunkTests`. Discord report 2026-09-03.
- Weapon-skin preview: `WardrobeRegions.WeaponSkinPreview` (731) is a preview-only key on `IWardrobePreview.Show`'s outfit map — `IWardrobe.ApplyAsync` ignores it and it stays out of `WardrobeRegions.All` (still 14). A weapon skin can never travel in `EWearFashion` (the game's own `fashion_vm.GetFashionWearList` skips 731), so `PandaWardrobePreviewProbe` keeps 731 out of the `SingleWearData` list and instead **re-skins the social data the model is built from**: `socialData.professionData.weaponSkin = skin`, written before `Z.ModelManager:GenModelByLuaSocialData`. `SocialData.professionData` is the `{profession_id, weapon_skin}` block the server fills for `SocialDataType.SocialDataTypeWeapon` (`const_value.lua:117`), and `socialData` is a plain Lua table (`world_proxy.GetSocialData:21831` returns `pb.decode` output with camelCase keys), so the field is directly writable. The display-override attr (`m:SetLuaIntAttr((Z.ModelAttr).EModelDisplayWeaponSkinId, skin)`) is still emitted last as a secondary, but it is NOT the mechanism: all three game call sites dress the cached PLAYER model (`GetCachePlayerModel` — `fashion_system_view:831`, `shop_fashion_sub_view:300`, `competency_rating_main_view:253`), never a social-data model, and the owner's in-game test showed the preview keeping the live weapon for every outfit. **The injection alone renders TWO weapons** (owner, 2026-09-05: "it show with currently weapon using … previewer show player have 2 weapons skin rendered", with `social=set(prof=2,was=nil)` in the log): a social-data model carries two independent weapon renderers — the CMount attachment (`EModelCMountWeaponL/R`, string model paths that ride in with the social data; `Panda.ZGame.WeaponOriginData` bundles `ModelCMountWeaponL/R` beside `WeaponSkinId`/`MainModelId`) and `Panda.ZGame.WeaponModelComp`, which owns its own weapon GameObjects (`mainWeaponModel_`/`subWeaponModel_`, `getWeaponMount`/`clearModel`) and is driven by the profession+skin data. So the chunk now does what every game view that shows a weapon skin on a preview model does — clear the CMount first, then ask for the skin: `fashion_system_view.initPlayerModel:844-845` (the screen hosting the weapon-skin tab whose `SelectStyle:26` sets the override) and `shop_fashion_sub_view.initPlayerModel:319-320` (`ShowPlayerWeaponModel:275` sets it), the same `SetLuaAttr(EModelCMountWeaponL/R, "")` pair every weaponless social view uses (`investigation_clue_window_view:942/944`, `rank_main_view:503/637`). The clear is emitted **only** when the outfit carries a weapon skin and is skipped at run time when the resolved skin is `0`, so no outfit can leave the model unarmed. Skin `0` = the class's default look and resolves through `GetWeaponOriginSkinId` exactly as `AsyncUseProfessionSkin:199` does. Class-match is the caller's rule (every `WeaponSkinTable` row is `ProfessionId`-scoped; the game's own tab refuses other classes at `fashion_weapon_skin_select_view.lua:100-106`). All three weapon `pcall`s CAPTURE their result and every preview writes a one-line outcome the host logs unconditionally: `[WardrobePreview] weapon none` / `… weapon skin=<id> social=set(prof=2,was=nil) mount=cleared(L:<path>,R:<path>,skinModels:<WeaponSkinTable.WeaponModelId list>) disp=ok` / `… mount=skip(skin0)` / `… social=err:` | `mount=err:` | `disp=err:`. The mount values are read BEFORE the clear, in their own nested `pcall` so a read failure can never block the clear, and the skin's own weapon model ids sit beside them — one owner log now says which renderer held which weapon. The 2.6.0 silent `pcall` made the failure undiagnosable from the owner's log. Pinned by `WardrobePreviewChunkTests`. Owner reports 2026-09-05.
- Plugin-config save path taken off the large-object heap. Owner report 2026-09-05: every click of Wardrobe's save button (53 outfits, 57 KB config) froze a frame for 287-366 ms and then stuttered for seconds. Measured on the owner's real config, 53 saves × 3 watcher events, running the actual `PluginConfigService` + `FileConfigStore`: **1,169 KB/save and 7 full gen2 GCs → 130 KB/save and 0 GCs of any generation** (CPU 1.67 → 0.64 ms/save; gen2 pauses on this client are 100-475 ms, per the 2026-07-25 jitter investigation). Three causes, all fixed: (1) `FileConfigStore.HandleFileTouch` did `File.ReadAllText` + `Encoding.UTF8.GetBytes` + SHA256 for EVERY FileSystemWatcher event (Wine delivers 2-4 per write) *before* the self-write check — a 106 KB LOH string + 54 KB array just to recognise our own write; a new `SelfWriteLedger` now answers that from one `FileInfo` (`(Length, LastWriteTimeUtc)`) with the content hash kept as the fallback for events that race `File.WriteAllText`. (2) `PluginConfigService.SaveSection` deep-cloned the whole root (`ToJsonString` + `Parse`) only to hand it to a store that serializes it again — it now passes the live root under the existing lock, and `IConfigStore.Save` documents the serialize-synchronously / retain-nothing contract. (3) Config files are now written **compact**, not indented — the owner's nested config inflated 20 K → 55 K chars when indented, putting every save's string over the 85 KB LOH threshold; readability of the on-disk file is the deliberate trade, and reading still accepts indented files (rollback-safe, process rules § 6). Pinned by `SelfWriteLedgerTests` (11), `FileConfigStoreEchoTests` (9, incl. zero-read echo suppression and a same-length external edit that must still be detected) and `PluginConfigServiceTests.Save_HandsTheStoreTheLiveRoot_NotAFreshClonePerSave`.
- `WindowBuilder.LoadIcon` now dedups Texture2D decodes through the token's byte[]-keyed `AtlasCache` — previously only `SpriteElement` and the live tile-icon binding did, so every other icon leaf (button chips, images, brand logo, pin stars) re-decoded per widget: the Wardrobe's 20-row × 9-chip pool built ~180 textures from 8 distinct PNGs on each window open. Dedup now lives in exactly one place (`BuildSprite` and `IconBinding` dropped their own copies). Safe to share because tinting is per-graphic (`.color`) and sub-rect selection per-`RawImage` (`.uvRect`) — nothing is baked into the texture; `IconTextures` remains the single owner, so disposal is still one `Destroy` per texture.
- Loadout switch pre-dispatch gates (owner report 2026-09-05: three same-loadout presses = a 3,261 ms frametime spike, then stutter; log showed eight consecutive `[LoadoutSwitcher] Switched to Beam`). The removed fast-path comment in `PandaLoadoutProbe.CallApplyAsync` claimed the game "cheaply no-ops a switch to the already-active loadout" — REFUTED by `weapon_vm.lua:509-514`, which builds `{oldProjectId=CurPlanId, newProjectId=planId}` and fires `WorldProxy.SwitchProject` unconditionally, then (`:536-542`) stamps `SwitchRolePlanTime`, saves `currentProjectSyncData` and dispatches `OnRolePlanChange` — a full server re-equip. The game's own dropdown never allows it: `role_plan_loop_item.lua:66` ignores the click when `CurPlanId == PlanId`, `:118-121` refuses inside `Global.lua:1703 CombatStrategySswitchCd = 3` (tip 150208), `:122-125` refuses in battle (tip 150206). New pure `PandaLoadoutProbe.DecideSwitch` mirrors the first two: same plan → `LoadoutResult.Success` with nothing dispatched (`[Stellar][Loadout] switch to <id> skipped: already active`), within 3 s of our last dispatch → `Rejected` (`… refused: within the game's 3s switch cooldown`). Combat is unchanged — the server + the game's own wrapper still own that refusal. `PendingSwitch` now records whether the live plan already matched at dispatch and skips the vacuous `_currentId == TargetId` completion fallback in that case, so a switch can no longer "complete" on the first ~33 ms poll and let the caller's single-flight guard start an overlapping burst. Pinned by `PandaLoadoutProbeSwitchGateTests`. A cooldown refusal also shows the game's OWN tip rather than only a log line (the LoadoutSwitcher plugin's `Report` toasts only on `Success`): `BuildShowTipsChunk` emits `pcall(function() Z.TipsVM.ShowTips(150208) end)` — the exact dot call `role_plan_loop_item.lua:119` makes, localized by the game — queued on the probe's existing main-thread dispatch queue and drained by the same `DrainPendingDispatches` pass (no new thread hop). One tip per refused press, capped at `MaxQueuedTips` and dropped on `ClearSession`.
- The per-class gear/module resolve is now debounced on the TRAILING edge of the delta burst. The walk is whole-item-container (uuid index over every package + a gear read per slot per plan) and a re-equip burst re-arms it on every `CharSerialize` delta, so ungated it ran at the full ~30 Hz drain rate through each burst — the ~1.8 MB/s allocation class behind the 2026-07-25 A/B's 100-475 ms GC frames. A leading-edge cooldown (walk now, then wait a window) bounded the rate but still walked THROUGH the burst, twice for a ~1 s burst, both times against half-applied state. `TryResolvePerClassDetailsIfDue` now consumes `_resolvePending` once per tick into a sticky `_resolveArmed` (the arming sites live in sibling partials and a bool cannot carry "again"), restarts a quiet timer on every new arm, and walks only after `ResolveQuietTicks` (15 ≈ 0.5 s) of silence — one loadout switch = ONE walk, against the burst's final state. DEFER, NEVER DROP: the arm survives in `_resolveArmed` until the walk runs, and `ResolveMaxDeferTicks` (60 ≈ 2 s, counted on EVERY armed tick so a saturating stream cannot starve it) forces a walk during an endless stream, so "late, never stale" keeps a hard ceiling. The pure `PandaLoadoutProbe.DecideResolve` now takes `(resolveArmed, resolverAttached, hasInputs, quietTicks, deferTicks)`; the gate still wraps `TryResolvePerClassDetails` rather than living inside it, so every existing live-state / Deep-Slumber resolve pin drives the unchanged inner method. New always-on, self-limiting line `[PerClassLoadout] walk {ms}ms plans={n}`, emitted only above 33 ms (zero volume in normal play, no diagnostics restart needed to attribute a reported spike). Pinned by `PandaLoadoutProbeResolveGateTests` (10, incl. a burst replay that scores 1 walk where the leading-edge form scored 2, and a saturating-stream starvation pin).
- Notice tips no longer re-open `noticetip_pop` per tip. Owner reports 2026-09-05: (1) holding a loadout hotkey on the already-active loadout gave 4-5 frames of 100-185 ms over 3-5 s even though the framework SKIPPED every switch (no RPC) — the toast was the only game-engine touch left on that path; (2) repeated "Save current outfit" still stuttered (max 428 ms), and those toasts carry DISTINCT text so a dedupe alone could never have fixed it. Root cause: `Z.UIMgr:OpenView('noticetip_pop')` once per tip, run synchronously on the main thread inside the service tick. On an ALREADY-OPEN view that is not cheap (`ui_manager.lua:112-161`): the open list is re-ordered (`:137-145`), `Z.UICameraHelper:OpenUICamera` runs (`:151`), and `ui:Active` (`:152` → `ui_base.lua:45-59`) reaches `SetAsLastSibling` (`:53` → `ui_view_base.lua:80-85` = a transform re-parent PLUS `Z.UIMgr:UpdateDepth` over the layer, i.e. a canvas rebuild) before it ever gets to `OnRefresh` (`:55`), then `ViewStatusSwitchMgr:TrySetStateActive` (`:158`) and a global `EventMgr:Dispatch(UIOpen)` (`:160`) fire. The view already drains its own queue — `OnRefresh` dequeues one item when it has no `viewData` (`noticetip_pop_view.lua:80-86`), `showPopTip` holds three at a time and re-enqueues the overflow (`:130-133`), each item's `OnEnd` pulls the next (`:210-214`) — so `BuildPopChunk` / `BuildPopTipChunk` now enqueue and then refresh the LIVE view (`GetView` + `SetViewData(nil)` + `CallLifeCycleFunc(OnRefresh)`, the same tail `Active` would have reached), keeping `OpenView` only as the `else` fallback for when there is no active/loaded/visible view — so no tip can be stranded in the queue. Two service-side bounds on top: the drain takes ONE tip per tick (a lone tip still shows on the very next tick), and a chunk byte-identical to one still inside its own delay+duration window is dropped by the pure `NoticeTipService.DecideShow` — the same call the game's own `checkConfigRepeat` makes (`noticetip_data.lua:8-22`), which never fires for us because our tips carry `Id=0` and so have no MessageTable row. DIFFERENT content is never dropped. New always-on, self-limiting line `[NoticeTips] slow show {ms}ms`, emitted only above 33 ms. Pinned by `NoticeTipSpamTests` (10).

## [2.5.0] - 2026-09-01
_**2.5.0** (minor) — the Loadout Switcher can switch your whole Deep-Slumber (tree and all), plugins get accurate raid boss health, and raid runs record as one run. Additive, binary-compatible with plugins built against ≤2.4.1._
### Added
- Plugins can now read a boss's exact health straight from the game, and can tell when a raid boss has actually gone down even when the game never shows its health hitting zero. The Combat Meter uses this for accurate boss health in raid replays.
### Changed
- The Loadout Switcher now switches your entire Deep-Slumber when you change loadouts — its tree, not just the phantom factors. Switching to a loadout whose Deep-Slumber uses a different tree now rebuilds the tree for you instead of stopping half-done, and it no longer stutters while it applies.
### Fixed
- Raid runs are recorded as one continuous run again, instead of being split into pieces when the game's own timers reset partway through the fight.
### Developer notes
- `DeepSlumberAreaBinding` gains a non-positional `NormalNodes` init member (the tree / Anchor allocation — kept off the primary constructor so it stays binary-compatible with ≤2.4.0). `DeepSlumberReconciler` now diffs the tree and, when it differs, drives `ResetAllNodes` + `ActiveNormalNode` (whole-area reset — the game has no per-node anchor removal, owner ruling 2026-09-01) before re-socketing factors; legacy bindings with a null tree stay factor-only. Both new worldProxy RPCs were validated in-game (`code=0`, `zoneId=areaId`). New Kind-phase order: enable → reset → unsocket → activate → socket.
- The loadout live-state refresh now defers its full-container walk while a plugin Deep-Slumber apply is in flight (`PandaLoadoutProbe.DecideRefresh` gated on `PandaSeasonTalentProbe.HasPendingWrites`), collapsing the per-op `CharSerialize` burst into one refresh after the apply settles — removes the 3-5 frame hitches a tree rebuild otherwise caused. Mirrors the existing combat-defer; a manual in-game switch is unaffected.
- Native boss-HP tap: `IBossVitals` + `EntityVitalsService` read boss HP from the game's own entity, with `EDisappearType`-aware AOI eviction and an `AttrMaxHpTotal(11321)` fallback; a raid boss whose HP never reads 0 is detected as HP≈1%-then-vanish. `IRunTimer` latch-epoch gives upgrade-proof run identity so an evidence-less belt resolution cuts a segment without resetting the run's identity (raid run-split fix; spec 2026-08-26).

## [2.4.1] - 2026-08-29
_**2.4.1** (patch) — stops a repeated combat popup during long fights. No API change; binary-compatible with all existing plugins._
### Fixed
- No more repeated "Cannot perform this action during combat" popup during long fights. It could appear every few seconds in sustained combat, even with no plugins installed.
### Developer notes
- `PandaLoadoutProbe` now defers only the combat-gated `SyncProjectList` RPC (`AsyncGetRolePlanData`) while the local player is in combat, using the game's own in-combat check (`GetLuaLocalAttrInBattleShow()`/`GetLuaIsInCombat()`, fail-safe to not-in-combat), and fires exactly one refresh at combat end; the RPC-free live-state re-read keeps running in combat. Root cause: every CharSerialize merge re-armed `_refreshPending`, and the server rejects the RPC in combat (ErrStateIllegal 3202), which the game's own wrapper toasts (~every 5s as deltas drip in). Infrastructure-only (`PandaLoadoutProbe`, `PandaLoadoutProbe.Resolution`); no API change — binary-compatible with all existing plugins. +7 `PandaLoadoutProbeRefreshGateTests`. (#72)

## [2.4.0] - 2026-08-25
_**2.4.0** (minor) — plugins can now save your outfits and switch between them, with a live 3D preview. Additive, binary-compatible with plugins built against ≤2.3.0._
### Added
- Plugins can now save the outfit you're wearing and switch you back to it later, and show a live 3D preview of a saved outfit on your own character. The new Wardrobe plugin uses this for instant hotkey outfit switching.
### Developer notes
- New plugin surface: `IWardrobe` (capture the worn outfit as a region→fashionId map; apply through the game's own `WorldProxy.FashionWear`, keeping every server-side check) and `IWardrobePreview` (dress a fresh self-model with an arbitrary saved outfit via `GenModelByLuaSocialData` + `SetLuaAttr(EWearFashion, …)`, rendered through a second `PortraitModelHost`; orbit / zoom / pan). `FashionEntry` gains `DyeAreas` — parallel to `Dyes`, carrying each dye's `EFashionColorAreaType` so multi-area pieces preview on their real areas; `AttrFashionDataReader` now reads both the base (field 2) and attachment/socks (field 3) colour maps. RE: `docs/recon/wardrobe-fashion-preview.md`.

## [2.3.0] - 2026-08-25
_**2.3.0** (minor) — plugins can now apply a Deep-Slumber setup for you, and applying one is fast and self-healing. Additive, binary-compatible with plugins built against ≤2.2.0._
### Added
- Plugins can now change your Deep-Slumber Psychoscope for you — its cultivate line and its phantom factors. The Loadout Switcher uses this to re-apply the Deep-Slumber you bound to a loadout the moment you switch to it.
### Changed
- Applying a Deep-Slumber setup is much faster. A switch that moves a lot of factors used to take a few seconds; now it finishes in a fraction of that, and if the game drops one of the changes it quietly retries instead of stopping half-done.
### Developer notes
- New plugin surface: `IDeepSlumber.ApplySetupAsync(DeepSlumberSetup, CancellationToken)` returning `DeepSlumberApplyResult`, plus `DeepSlumberSetup`/`DeepSlumberAreaBinding`. The live→target diff (`DeepSlumberReconciler`) lives in Application; the write path drives the game's `season_talent` worldProxy RPCs (Approach A — raw RPC returns the bare `EErrorCode` inline) via `PandaSeasonTalentProbe`. Docs: `docs/driving-game-actions.md` § Deep-Slumber.
- Apply overlaps its server round-trips: a bounded in-flight window (5) with one-dispatch-per-tick pacing, run in Kind-phases (enable → unsocket → socket) with a barrier that preserves the scarce single-copy unsocket-before-socket invariant. Only transient (did-not-land) ops retry (initial + 2, 250 ms backoff); a positive game refusal (7555/7561/combat) is never retried.
- `LauncherEntry.TitleProvider` (`Func<string>?`): an optional live-localized tile title. The launcher renders `DisplayTitle` (`TitleProvider?.Invoke() ?? Title`) each frame, so a tile whose plugin sets `TitleProvider = () => Localization.T(key)` re-localizes immediately on a language change; `Title` remains the stable pinned-state identity. Plugins pass a captured string today, which is why their tiles were stuck in the registration-time language.

## [2.2.0] - 2026-08-24
_**2.2.0** (minor) — the live-build release. Stellar now notices the moment your setup changes and reads it live, and exposes the Deep-Slumber Psychoscope to plugins. Additive, binary-compatible with plugins built against ≤2.1.0._
### Added
- Stellar now notices the moment you change your build — swapping a piece of gear (including with the Replace button), changing modules, respeccing talents, switching a Battle Imagine, or editing your Psychoscope. Plugins that record your setup, such as the Combat Meter, can now save the exact build you fought each boss with instead of whatever you happened to have on when the run started.
- Your Psychoscope (Deep Slumber) is now available to plugins — season level, lines, socketed cards and node levels, read live from the game rather than from a saved profile.
- Plugins can now show a second value on the same on-screen bar — for example a boss's shield alongside its health, either as a see-through band laid over the health fill or as extra length past the end of it. Whether a plugin uses this is up to each plugin.
### Changed
- Less background work while you play. Stellar used to re-check your equipment, talents and dungeon death count on a timer every moment you were in the world; it now waits for the game to tell it something changed. Same information, noticeably less work per frame.
### Fixed
- Swapping a Battle Imagine now takes effect straight away. Before, Stellar kept showing the pair you had equipped when you logged in.
- Changing a piece of gear with the Replace button is picked up again — it used to go completely unnoticed.
- The dungeon death counter no longer gets stuck. If a later run reached the same number of deaths as an earlier one, it used to report none at all.
- Logging out and back in on another character no longer shows the previous character's Psychoscope.
- Fixed a case where the game sent several changes at once and everything after the first one was thrown away.
### Developer notes
- `ILoadout.LiveState` (`LiveLoadoutState`): the local player's LIVE class + talents, parsed from the live line — never a saved plan. Refreshed with the loadout data; a respec re-fires the refresh through the new dirty-delta trigger.
- `ILoadout.LiveStateChanged`: ONE game-tick event for the whole build — equipped gear/module slots, class, talent stage/nodes, the equipped Battle Imagine pair, and `IDeepSlumber.GetState()`. Raised only after a re-read actually CHANGED what the service serves (structural, order-insensitive compares; a not-yet-read surface is no-signal). Published from the RESOLVE step, not the raw slot read, so it can never fire while `GetSlots()` still describes the previous setup; an unresolvable change is held and delivered on the tick the data lands (LATE, never STALE). Raised on the game Update thread, unlike `IInventory.SelfGearChanged`. Deep-Slumber joined this event in `8e5a7b2` (owner staging run `sea/dXkw1PSyOG`: a psychoscope factor unequipped between two archives and re-equipped after — the framework re-read it correctly but told nobody, so the consumer kept one stale snapshot across two materially different builds).
- `IPluginServices.DeepSlumber` (`IDeepSlumber`) + `DeepSlumberState`/`DeepSlumberLine`/`DeepSlumberArea`: live reflection reader over `CharSerialize.SeasonCultivateLineData`/`SeasonRoleLevelData`, seeded through the tolua# bridge because the C# mirror populates lazily and is a stale latch on a fresh session. `zcontainer` maps are iterated the game's way — `__pairs` yields nil VALUES, so the reader indexes per key (owner run `O1jJepsgKC`). Session state clears on logout.
- `IInventory.SelfGearChanged` widened to the self BUILD-state signal: generic `ContainerDirtyDeltaReader.TouchesField` + semantic wrappers fire it on talent (`professionList`, field 61) and Deep-Slumber (`seasonCultivateLineData`, field 101) method-22 deltas. Still network-thread — flag there, read on the tick, or subscribe to `LiveStateChanged` instead.
- `IResonanceState.Installed` keeps its shape but changes SOURCE and id space: equipped Battle Imagines are read from the skill hotbar's aoyi slots 7/8 as aoyi SKILL ids (`IGameDataResonance.GetImagineForSkill` resolves them). `CharSerialize.resonance` (wire field 28) is never re-serialized on an in-session swap, so the old field-28 source could not see a swap at all (owner run `sea/pNhmVQvVmV`).
- Wire fix: the top-level delta scan died after the first SKIPPED field because it never consumed that field's trailing END tag — every change after the first in one packet was dropped.
- The framework's last two per-tick game reads are gone. Live-state capture is driven by the container-merge event (replacing a field allowlist + a 1 s poll), and `IDungeonState.LastDefeatedCount` now rides `WorldNtf 3 EnterScene` (`EnterSceneInfo.SceneAttrs`, the seed) + `WorldNtf 7 SyncSceneAttrs` instead of a four-layer IL2CPP reflection read of `ZWorld.GetWorldLuaAttr(348)` on every main-thread beat — the same carrier the game's own dungeon HUD watches. It also fixes a latent probe-side memo that never reset across runs (a second run reaching the same count reported 0 forever). `StubRouter.Register` is now MULTICAST with registration order preserved (WorldNtf 3 has two subscribers and the order is load-bearing).
- `BarElement.Overlay01` / `OverlayColor` / `OverlayInFront` (`BarStyle.Modern` only, #66): an optional SECOND fill fraction 0..1 on the SAME track as `Fraction01`, re-pulled each refresh through its own `BarBinding`. `OverlayInFront: true` draws it over the main fill as a translucent band (the label overlay texts still sit on top); `false` (default) `SetAsFirstSibling`s it behind the opaque main fill so only the excess shows as an extension cap (e.g. `Overlay01 = (hp+shield)/max`). A null `Overlay01` renders the bar exactly as before, and `BarStyle.Default` ignores all three. Init-props only — additive, binary-compatible.
- `tools/install-stellar.sh` gains `STELLAR_FRAMEWORK_ONLY=1` (deploy the framework set, plugin slots untouched) and `STELLAR_ONLY_PLUGINS=<slots>` (deploy only the named plugin slots, framework slot untouched).

## [2.1.0] - 2026-08-18
_**2.1.0** (minor) — Stellar's own menus now speak your language. Adds a plugin localization API; additive, binary-compatible with all existing plugins._
### Added
- Stellar's settings menus now display in **English, 日本語 (Japanese), ไทย (Thai), and Bahasa Indonesia**. Pick your language in Settings → Themes → Language, or leave it on "Follow game client" to match your game.
### Developer notes
- `IPluginServices.Localization` (`ILocalization`): a plugin-scoped UI-text localizer. Ship four embedded `Lang/{en,ja,th,id}.json` catalogs (`<EmbeddedResource Include="Lang/*.json" LogicalName="Lang.%(Filename)%(Extension)" />`) and call `Localization.T("key")` / `TFormat("key", args)`; resolution is active-language → English → the key literal (a missing key renders visibly as the key). The framework auto-discovers each plugin's catalogs at plugin-load (namespaced by plugin GUID, matched by the `Lang.<code>.json` suffix) — no registration code. `ILocalizationControl` (Settings-facing, NOT on `IPluginServices`) drives the setting `localization.language` (default `follow`); `ClientLanguageProbe` maps the game client's `LanguageType` (`en=1,ja=2,th=5,id=6`, else `en`) to a supported code, and the setting live-switches (labels re-poll, baked renderers flush via `LanguageChanged`). Catalog completeness is validated by `tools/i18n-catalog.py <repo>` (used/undefined/incomplete/orphan, `--seed`). See `docs/plugin-development.md` § Localizing your plugin. New service only — additive, binary-compatible with plugins built against ≤2.0.3.
- Section headers now stand out by **accent colour** (the theme's `MenuAccent`) instead of bold weight — crisp and clearly a header in every language. Why: under Proton/IL2CPP the overlay can't render a readable bold for Thai/CJK — a real bold FACE will NOT load (`Font.CreateDynamicFontFromOSFont` verified across a host bold family name and a distinct normal-weight family installed both in the Wine prefix and as a host `fc-list` font — none render), and Unity's synthetic bold blurs their tight loops/counters. So emphasis is carried by colour (`WindowBuilder` sets emphasis text to `MenuAccent`; `RegisterTextReskin` re-applies it on a theme change), with **weight kept CRISP** — Latin still gets real `FontStyle.Bold` (it survives synthetic bold cleanly), complex scripts (CJK/Thai) stay regular. Titles take the crisp weight but no accent (already prominent by position). `GlyphScript` picks Latin-bold vs complex-regular per string, so it tracks a live language switch.

## [2.0.3] - 2026-08-16
_**2.0.3** (minor) — readable text inputs, plus internal groundwork for the CombatMeter Discord run-card and a reconnect fix that keeps a mid-dungeon disconnect from splitting your run in two._
### Fixed
- Text boxes in Stellar windows are readable again — they show the dark themed background instead of white-on-white.
### Developer notes
- `IGameAssets.LoadByPath` — load an arbitrary game asset by its ZResLoader address (#61); backs the in-game image rendering for the CombatMeter Discord run-card.
- Mid-dungeon reconnect party recovery: `PandaTeamInfoRefreshProbe` invokes the game's own `WorldProxy.GetTeamInfo({})` via the tolua# Lua bridge when in a dungeon with `IPartySnapshot.PartyId == 0` (a reconnect drops the party id until the game lazily re-delivers it — measured arriving only after a whole run). Bounded (≤3/run, 2.5 s throttle, world-gated main-thread); the reply is decoded by the existing `PandaPartyStubProbe` → `PartyId`. Enables CombatMeter's reconnect run-split fix by making the party id available during the run instead of after it.

## [2.0.2] - 2026-08-15
### Added
- Plugins can now show more of your character's stats — this powers the new Illusion-Breaking Strength readout in CombatMeter.

## [2.0.1] - 2026-08-14
_**2.0.1** (patch) — summoned-companion (imagine) damage now resolves to the right creature in combat data. Data-resolution only; no API change, binary-compatible with all existing plugins._
### Fixed
- Damage from your summoned companions now shows up correctly in the combat meter and on the logs website. Some newer companion abilities weren't being recognised, so their damage went uncredited.
### Developer notes
- `GetImagineForSkill` gains `ImagineAoyiRule`. Newer battle imagines flag their damage skills as SlotPositionId [0]/[6] (not the aoyi 7/8 slots) with no `SkillFightLevelTable` row, so resolution rejected them and player-attributed summon damage never mapped back to the imagine (measured on jp/RXALtMH6J3: Celestial Flier 1008440, Rorola 2900840, Venobzzar 1007741, Kartgriff 111069 — all invisible while their damage sat in the run's own perActorSkills). The new rung is gated to ids with no fight-level row of their own (`baseId == 0`): it decomposes `MonsterId*100+NN` over a lazy `SkillAoyiTable` MonsterId→aoyi index (`GameDataResonance.Aoyi.cs`) plus a curated companion-arcane map (Boyce/Rorola/Fafala, table-evidenced). The gate is load-bearing — leveled player ids share the numeric namespace (140116 = Windborne Grace lv16 AND an Igoreus monster skill), and an own fight-level row wins. Negative memoisation stays pre-load-safe. Documented residuals: Igoreus/Denvel bands are ambiguous by construction; Dorothy/Lucy/Natsu have no evidenced rows and are never guessed. Also documents the `NN=00` composite probe as a sanctioned CombatMeter consumer contract (summon-entity appear-sourced imagine capture). Pinned by `ImagineAoyiRuleTests` (23 cases incl. the collision band).

## [2.0.0] - 2026-08-10
_**2.0.0** (major) — the interface overhaul: resize the whole mod UI, a redesigned move-and-resize editing mode, a hotkey to hide the on-screen displays, and the Stellar menu on the login screen. One rendering engine now draws every window and overlay. **Breaking for plugins that draw their own on-screen display (HUD)** — the old HUD API is removed and those plugins must be rebuilt against the 2.0 SDK; all other plugins stay binary-compatible. Also bundles the 1.17.0–1.18.1 player fixes for anyone updating from an older build._
### Added
- A UI Scale slider in Settings → Themes that resizes the entire mod interface from 75% up to 150% (5% steps, default 100%). Every window and on-screen display scales together, and it previews live as you drag. Useful on very high-resolution screens where the panels felt small, or just to make everything bigger or more compact to taste.
- The mod interface now sizes itself to your screen resolution, so it stays proportional on any display instead of being tied to raw pixels.
- A redesigned layout-editing mode for moving and resizing the mod's windows and on-screen displays. Turn it on with Alt+E, or with the new "Enter layout editing" button in Settings → Game UI; a bar across the top with an Exit button shows you're editing. While editing: every on-screen display is shown — even ones you normally keep hidden — so you can position them; any window you can resize shows a small square in its bottom-right corner you can drag; and dragging stays smooth at any UI scale. When you leave edit mode, windows lock in place again.
- A hotkey to hide or show all of the mod's on-screen displays at once, set to Alt+H by default (this resets each time you restart the game). There's also an optional hold-to-hide key, unbound by default, that hides them only while you hold it — handy for a quick clean screenshot.
- The mod's on-screen displays now automatically get out of the way while a game confirmation or OK pop-up is open, so they never sit on top of it.
- You can now open the Stellar menu right from the game's login screen — a Stellar button sits in the login side bar, matching the in-world one — and the mod's own windows now work at the title and login screens too, not only once you're in the world.
- Settings now show each plugin's proper name instead of its internal file name, and the Hotkeys screen groups shortcuts under the plugin that owns them, each with a short description of what it does.
### Changed
- This is a major update to how the mod draws its interface. Most plugins are unaffected, but a plugin that adds its own on-screen display may need an updated version from its author to work with 2.0.
- What this means for your existing setup: your saved window and on-screen-display positions are kept, and are remembered separately for each screen resolution. Because the interface now scales to your resolution, the windows may look a little smaller or larger than they did in the old version — most noticeable if you don't play at 2560×1440 (smaller below that, larger above). Nothing is lost; use the new UI Scale slider and the layout-editing mode to set the size and positions you want.
- You now move and resize the mod's windows only after entering layout-editing mode (Alt+E). Previously you could drag them at any time; locking them outside edit mode stops windows getting nudged by accident during play. Your positions are unchanged.
- The option that stops the game from also reacting to your mod shortcuts is now turned on by default. If you never changed this setting, the game will no longer also respond to a key that one of your mod hotkeys uses. If you had already set it yourself, your choice is kept exactly as it was.
- The default shortcut for layout-editing mode is now Alt+E (it used to be Shift+backtick). This only changes things if you never rebound it — a custom binding is kept. The block-hotkeys option is also now clearly worded: "Stop the game from also reacting to these keys."
- Plugins can now build taller, wider, or full-width bars, put a larger label centred on top of the bar, and use a flatter "meter" look with an optional soft moving shine — so a plugin can present a prominent bar like a target's HP bar or match the Combat Meter's style. Whether a plugin uses this is up to each plugin.
### Fixed
- Your own class icon (the profession crest) now shows correctly.
- Switching game accounts no longer briefly shows the previous account's character data — it's cleared when you log out.
- Your window and on-screen-display positions now survive entering a dungeon or changing zones, instead of jumping back to their defaults.
- Class, skill, buff, and item icons that occasionally loaded blank now retry and appear reliably.
- An on-screen display you've hidden now stays hidden after you restart the game.
### Developer notes
- **Breaking — the separate HUD toolkit is removed; there is now ONE UI path.** `IHudHost`/`IHudHandle`/`HudSpec`/`HudAnchor`, `HudService`(+`.Layout`), `IHudRenderer`/`HudRenderer`, `HudElementBuilder`(+`.Meter`), and `HudBarAnimator` are deleted. On-screen overlays register through the window API: `Windows.Register(new WindowRegistration(new WindowSpec(..., WindowCategory.HUD, WindowPanelStyle.Borderless){ Surface = SurfaceStyle.HudOverlay, Draggable = true, EditModeDragOnly = true, ShouldRender = ... }, root))`. The shared `HudElement` record tree is unchanged. New `enum SurfaceStyle { Menu, HudOverlay }` (default `Menu` → every existing window byte-identical); `HudOverlay` reproduces the old HUD look (always-shadowed twin text honouring `FontSize`/`DynamicFontSize`/`ShadowDistance`, the rounded 9-slice smoothed-fill bar, the transparent pill) by reusing `HudThemeAssets`, now owned by `WindowRenderer`. `IThemeHudColors` retained (Toast + the HudOverlay bake still use it). First-party PlayerHUD / RaidManager countdown / DebugInfo migrated in lockstep, pixel-identical. **Only plugins that called `IPluginServices.Hud.Register(HudSpec)` are affected**; all others are source- and binary-compatible.
- **Source-breaking rename** `BarStyle.Meter` → `BarStyle.Modern` (enum value unchanged, `Default`=0/`Modern`=1 → binary-compatible for compiled plugins). `BarElement` gains `init`-only geometry (`Height`/`Width`/`FillWidth`/`LabelFontSize`/`LabelInside`) plus the opt-in `Modern` meter style with optional `Sheen`/`SecondaryLabel`; the original positional constructor is untouched (source- and binary-compatible, unset fields default to the old render byte-for-byte). Both the HUD-overlay path and the window path (`WindowBuilder.BuildBar` / `WindowBuilder.Preview` `BuildBarModernWindow`) honour every new field.
- **UI scaling.** Window overlay canvas gets a `CanvasScaler` (ScaleWithScreenSize, reference 2560×1440, match 0.5). At the reference resolution scaleFactor = 1.0 (byte-identical to pre-2.0); other resolutions convert drag/resize/on-screen-clamp/dropdown-placement/click-rect between screen px and canvas units. The UI Scale slider sets `referenceResolution = (2560/u, 1440/u)`, persisted as `theme.uiscale`, quantised to 5% so the font atlas repacks only at boundaries; `canvas.pixelPerfect` toggles off at fractional scaleFactor to kill dragged-window jitter. `LayoutStorage.Get` reads saved rects as canvas units (scaleFactor-corrected) while the KEY lookup stays raw, so pre-2.0 per-resolution layouts are still found.
- **Game-phase model.** `GamePhase { Startup, TitleScreen, CharSelect, World }` and `[Flags] GameUIState` (+`GameHudHidden`/`AnyMenu`/`Blocking`/`Popup` presets) on `IClientState` (`Phase`/`PhaseChanged`/`IsWorldActive`/`UiState`). The framework tick now runs UI/input/hotkeys every phase (so plugin windows work at title/login); game-state work self-gates per-unit on `IsWorldActive`, enforced by new **STELLAR0006** analyzer (a `[WorldGated]` method must early-return on `!IsWorldActive`). Window visibility is plugin-owned via required `WindowSpec.ShouldRender` (`IRenderGated`), replacing `AutoHideBehindGameMenus`/`HideUntilInWorld`; the gate fails safe (null/throwing predicate → hidden + warned) so a stale pre-2.0 plugin can't NRE the tick. `Loading`/`Popup` are driven by dedicated un-gated probes (reliable across loading screens / confirm dialogs `tips_common_popup`/`tips_sys_dialog`).
- **Login-screen integration.** `NativeUiAnchor.LoginSidebar` injects the Stellar rail icon into `login_main`'s sidebar; uGUI injection is un-gated with per-anchor phase-relevance (LoginSidebar=TitleScreen, MainMenuRail/HudTopRight=World) so there is no out-of-phase probing cost. `LauncherEntry.ShouldShow` gates individual launcher tiles by phase; framework chrome hides over the loading screen.
- **New members** (all additive/binary-compatible): `GameTextureElement.CornerRadius`, `CooldownTileElement.FallbackLabel`, `IWindowHost.IsLayoutEditing`.
- **Plugin identity/hotkeys.** The framework adopts each plugin's declared `IStellarPlugin.Name` as its display name (Plugins panel, per-plugin Performance rows, Hotkeys group header); hotkeys group by owning plugin via an internal owner-tagged declaration sink, and `IHotkeyAction` gains `PluginId`+`Description`. HUD-visibility adds internal `IInputGateway.IsKeyHeld` + `HotkeyService.IsActionHeld`.
- **Interop foundation (toward v2.1), additive, framework-only-implemented:** `StellarInterop` (cached reflection floor), `ILua` (`IPluginServices.Lua`), `IHarmonyHost` (`IPluginServices.Harmony`, auto-unpatch on plugin dispose), and `IFramework`/`IFrameworkTiming` (`Post`/`Every`/`TimeNow`, skew-corrected `ICombatSnapshot.ServerNow`).
- **Icon-load resilience.** Failed icon slots retry with bounded backoff (~0.5–2s, up to 4×) instead of memoizing `Failed` for the session; a first request that beats the HybridCLR loader registration self-heals. Fixes blank profession crest / skill / buff / item icons.
- **Other fixes:** profession-crest projection falls back to `ProfessionSystemTable.Icon` and guards against table-not-ready; account/character data clears on logout (account-switch leak); mod-window and native-game-HUD positions survive scene change; a persisted layout-editor hide survives relaunch. Throwaway phase-diag overlay removed. Merged from main: Steam client region detection (1.18.1) and loadout talent-spec correctness (1.18.0).
- **Test suite: 1012 passing.**

## [1.18.1] - 2026-08-07
_**1.18.1** (patch) — Steam client region detection fix so runs from the Steam build upload again. Detection-logic only; no API change, binary-compatible with all existing plugins._
### Fixed
- If you play the game through Steam, your dungeon runs now upload to the logs website again. The mod couldn't recognise the Steam version of the game and was holding those runs back.
### Developer notes
- GameEnvironmentService now matches the running executable by name PREFIX (StarSEA → SEA, StarASIA → JP) instead of exact filename, so the Steam build's StarSEA_STEAM.exe resolves to SEA instead of Unknown — Unknown made CombatMeter's RegionKnownOrWarn withhold every upload. One row per region covers StarLauncher, Steam, and future store builds. Regression test SteamSeaExecutable_DetectsSea; the existing UnknownExecutable_DetectsUnknown still pins non-matches. The Steam install dir is flat (no release_<ver> segment) so GameVersion reads "unknown" there — cosmetic, not gated on for uploads. Commit 5f976ea.

## [1.18.0] - 2026-08-06
_**1.18.0** (minor) — one additive loadout fix on top of 1.17.0: the talent specialization shown for a saved build is now read consistently with that build's own talent nodes. Binary-compatible with plugins built against ≤1.16.1._
### Fixed
- Your saved talent build now shows the correct specialization on the logs website, instead of sometimes showing a different one than the talents you actually picked.
### Developer notes
- PandaLoadoutProbe.Resolution now sources a saved plan's talent stage from the live per-profession container (talentList[prof].talentStageCfgId) so the stage id agrees with the plan's own talent nodes — fixes a stale talentStageCfgId that mislabeled the spec (e.g. run sea/ZEEJjddKHN). Single-file change to PandaLoadoutProbe.Resolution.cs; cherry-picked from e73b99b. The exploratory NotifyShowTips (m42) win-tip tap from the same session was dropped as inert — vaults do send a real settlement message, so the actual vault fix lives plugin-side (CombatMeter 1.6.1). The SquareHandle slider knob referenced in FrameworkVersion already shipped in the 1.17.0 tree and is unchanged here.

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
