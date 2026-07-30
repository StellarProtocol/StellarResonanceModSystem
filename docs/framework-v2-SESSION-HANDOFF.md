# Handoff — Framework v2 + Game Phases + full plugin migration + AccountSwitcher

_End of a long session, 2026-07-30. Everything below is built, deployed to the local game, and committed
(except the two non-git plugins + the AccountSwitcher, which aren't repos). NOTHING is pushed or merged._

Game: `E:\BPSR\StarLauncher\game\release_3.7\game_mini`. Deploy = copy DLLs into
`…\BepInEx\plugins\Stellar.Framework\` (framework) or `…\stellar\plugins\<name>\` (plugins); **game must be
closed** or the DLL is locked. Framework build: `dotnet build src/Stellar.sln -c Release
-p:GameInterop="<game>/BepInEx/interop" -p:BepInExCore="<game>/BepInEx/core"`.

---

## 1. Framework — branch `enhance/game-phases` (off `origin/main` ab1e17b, was framework 1.16.1)

**Status: implemented, in-game validated, deployed. 954 tests pass. Not merged.** Key commits (newest first):
- `889a4f9` — **logout flash fix**: `World→TitleScreen` no longer fires on raw `OnLogout`. `OnLogout` from
  `World` DEFERS (phase stays `World`, `IsWorldActive` false + `Loading` true → all plugins gated off); the
  login-view probe promotes `World→TitleScreen` when `login_main` appears (guard widened to
  `Phase==Startup||World`). `CharSelect` cancel keeps its direct `OnLogout→TitleScreen`. So **`TitleScreen`
  now ALWAYS means "login screen actually visible."** ⚠️ Flagged edge: a `World→CharSelect` path that skips
  the login screen would stick at `World` — not observed in practice; fix = extend `OnLogin` guard to accept `World`.
- `bd15f7f` — login-sidebar Stellar icon **styling** (colored glowing star via `AddGlowingRailIcon`, glow
  ticks UN-gated so it animates at title, dark circle stretch-fills `btn_setting`'s rect).
- `5fd15a2` — **per-anchor phase gating** on uGUI injection: `IsAnchorRelevant(anchor,phase)` in
  `UGuiInjectionService` — `LoginSidebar→TitleScreen`, `MainMenuRail`/`HudTopRight→World`; skips the
  `GameObject.Find` out-of-phase.
- `579ea15` — **un-gated the uGUI injection service** (was inside the `IsWorldActive`-gated `_framework.Tick`
  → froze at title). Moved `uguiInjection.Tick`/`TickGlow` to the un-gated `RunGlobalRateWork` (next to the
  login-view/loading probes). Also: `login_main` matched by **name-CONTAINS** (not exact `Transform.Find`).
- `fd8d47f` — **`NativeUiAnchor.LoginSidebar`** (login-screen Stellar icon → opens launcher menu);
  `UGuiAnchorAllowlist` parent `zuiroot/UILayerMain/login_main(Clone)`, template `btn_setting`, container =
  `btn_setting.parent` (the `layout` VerticalLayoutGroup). `PandaUGuiAdapter.LoginButton.cs`.
- `775066c` — **`LauncherEntry.ShouldShow` (`Func<bool>?`, null=every phase)** + render filter in
  `LauncherView.cs` (live ~10 Hz). Framework chrome (launcher/Settings/perf overlay/layout toolbar) →
  `ShouldRender = () => (UiState & Loading)==0` (hide on loading); phase-diag overlay left `() => true`.
- `44bd232` — **un-gated `PandaLoadingScreenProbe`** so `GameUIState.Loading` fires during loads (the
  menu-state probe that used to set it is `IsWorldActive`-gated → frozen during a load). Two-field compose
  in `ClientStateService` (`_menuBits` from the gated probe stripped of Loading, `_loadingActive` from the
  un-gated probe). Detects `loading_window` under `UILayerSystemTip`.
- `a12096e` — **`Startup` phase** (`GamePhase{Startup,TitleScreen,CharSelect,World}`); boot=Startup;
  latched `Startup→TitleScreen` when `PandaLoginViewProbe` detects `login_main` (un-gated tick).
- earlier: `9984fd3` CharSelect (OnLogin→CharSelect); `ee55458` fail-safe `ShouldRender` gate hardening;
  `d2c9dbc` throwaway phase-diag overlay (**Shift+PageUp**, `stellar.diag.phase`, remove-before-merge);
  `09923d4`→`f77b98b` initial Game-Phases impl (see `docs/game-phases-design.md` §5/§10 — the spec).

**Core model:** `GamePhase{Startup,TitleScreen,CharSelect,World}` (signal, gates nothing) + `IsWorldActive`
(game-state gate, self-gated per-unit) + `GameUIState` flags + windows/HUDs gate via **required**
`ShouldRender`. Tick is a dumb dispatcher (runs every phase); game-state units self-gate on `IsWorldActive`;
draw/UI/input run always. Un-gated probes (above the `IsWorldActive` gate in `RunGlobalRateWork`):
login-view, loading-screen, uGUI injection. See `docs/game-phases-design.md`.

---

## 2. Plugins — ALL 18 on branch `feature/framework-v2`, SDK 2.0.0, deployed

Each plugin repo: cut `feature/framework-v2` from its `main`, ported to SDK **2.0.0** (add `ShouldRender`,
drop removed `AutoHideBehindGameMenus`/`HideUntilInWorld`/`Phases`, bump `<Version>` to 2.0.0), added a
loading guard, HUD windows use `Blocking|AnyMenu`, and (if it has a launcher tile) `ShouldShow = ()=>Phase==World`.
Committed on the branch, deployed. **Not pushed.**

**⚠️ NOT git repos (ported + deployed but UNCOMMITTED — edits staged on disk):** `StellarMaestroPlugin`,
`StellarPositionPlugin`, `StellarAccountSwitcherPlugin`. Commit from real clones before release.
**⚠️ `StellarMahiruUtilityPlugin`** has a synthetic local `git init` history (baseline `4c2785e`) — reconcile
with its real remote before pushing. **⚠️ `StellarExperimentPlugin`** on `master` (no `main`); commits made
WIP-free via index surgery — heavy unrelated WIP left untouched in the tree.

**11 have launcher tiles → `ShouldShow=World`:** AutoFishing, CustomProfleImage, ExchangeBuyer,
ModuleOptimizer, RaidManager, Position, MinimalNameplate, MahiruUtility, Maestro, Experiment, AccountSwitcher.
**7 have NO tile** (open via hotkey/auto-show/HUD; no change needed): ChatTools, CombatMeter, CooldownBar,
EntityInspector, LoadoutSwitcher, PlayerHUD, StatInspector.

**ExchangeBuyer** is special: multi-project, ILRepack-merges `Stellar.Licensing`+BouncyCastle; keep its
`RequiredMemberPolyfill.cs` (its Engine DECLARES `required` members). Deploy target runs `AfterTargets="RepackLicensing"`.

**Every repo's `nuget.config`** points at the local feed `..\local-nuget-feed\` (absolute path) — **swap for
the published 2.0.0 packages before CI/merge.** The `2.0.0` local packages were re-packed several times (last
included `LauncherEntry.ShouldShow`, but NOT the `LoginSidebar` enum — plugins don't need it).

---

## 3. AccountSwitcher (`Mod\StellarAccountSwitcherPlugin`) — NOT a git repo

HaoPlay multi-account switcher. Deployed, working. Features built this session:
- **Login-screen window** (auto-shows, `Phase==TitleScreen && !Loading`) + **in-world manage window**
  (`Phase==World && !Loading`, opened via launcher tile) + **edit-nickname dialog** + **SDK-add status modal**.
  All centered-on-open, ~640 wide.
- **Nicknames**: per-account `Label` (already persisted in the DPAPI `.dat`), editable via ✎ → dedicated
  window; rows show single line (nickname else email); list sorts + searches by display name.
- **Add via HaoPlay SDK** (`Plugin.SdkAdd.cs`): button (login-only) → summons native panel via stashed
  `__stellar_orig_SDKLogin` → captures JWT. **Capture recipe**: Lua override of BOTH `OnSDKLogin` +
  `OnSDKAutoLogin` (gated on `__stellar_sdk_capturing`, only on **non-empty** token, sets sentinel + `via`
  tag), **blocks** the original so the game does NOT log in; reads the JWT via the **Lua stack-string read**
  (see finding below), reflection fallback `ZLogin.CurrentAccount.get_Token()`. Calls `Z.SDKLogin.Logout()`
  first (+0.4s delay) so back-to-back adds re-summon the panel. `deviceId` extracted from JWT claim → SDK-added
  accounts **auto-refresh** (confirmed: manual refresh works, same code path as auto).
- **DeviceId**: OTP flow mints a **random `Guid.NewGuid()` per account** (`Plugin.Switch.cs:28`), persisted +
  reused for `/api/auth` refresh (device-bound). Not a real hardware id. SDK flow reads it from the JWT.
- **HaoPlay panel suppression** (`Plugin.HaoPlaySuppress.cs`): Lua override of `SDKAutoLogin` — the native
  panel can't be closed (native overlay, no close API), only prevented. Config `suppressNativeHaoPlayLogin`.

---

## 4. Findings to write into the Knowledge Base (✅ DONE 2026-07-30)

All five written up. Where they landed:
- **New doc** `Knowledge Base\Game-Phases-and-Tick-Gating.md` — findings 2, 3, 5 (+ the phase model, the
  two tick bands, the `required ShouldRender` compile-time-only trap, the un-gated deliberate-omissions list).
- `Lua-Injection-from-CSharp.md` — new **§3b** (finding 1) + §3 warning corrected to scope the opacity to
  `get_Item`/`DoString` only.
- `Login-Flow.md` — new **§12** (finding 4, the full SDK-capture recipe) + §11 diagnostics note corrected.
- Cross-links added in `GameMenuState.md` (the Loading bit isn't its probe's) and
  `Login-Screen-UI-Injection.md` (the probe must run un-gated; `login_main` name-CONTAINS).
- `CLAUDE.md` — new doc added to the topic table + Rule 8 routing.
- Memory: new `reference_game_phases_tick_gating`, new `reference_lua_string_readback`,
  `reference_login_flow` extended with §12; **deleted** `reference_framework_prelogin_dormant` (it described
  the removed `[Flags]{None,PreLogin,World}` + `WindowSpec.Phases` model and would actively mislead).

Original list, for reference:

1. **⭐ Lua strings CAN be read back to C#** — the earlier "give up, IL2CPP string opacity" was wrong. The
   `LuaState` (tolua#/`LuaInterface.LuaState`) has `LuaGetGlobal(name)` + `LuaToString(int)` + `LuaGetTop`/
   `LuaSetTop` — a classic stack read (`LuaGetGlobal→LuaToString(top)→LuaSetTop`) returns a real managed
   string. Only `DoString("return x")`/`get_Item` are opaque (no non-void DoString overload). → **update
   `Lua-Injection-from-CSharp.md`** (currently says string readback is impossible).
2. **uGUI injection was `IsWorldActive`-gated** → couldn't inject at the title screen (that's why the
   login-sidebar icon needed the un-gate). Anything that must run at the title screen goes in the un-gated
   `RunGlobalRateWork`, not `_framework.Tick`.
3. **`GameUIState.Loading` was structurally dead** — set by a gated probe that freezes exactly during a load;
   fixed with the un-gated `PandaLoadingScreenProbe`. → note in the phase/GameUIState docs.
4. **SDK-add capture recipe** (Lua override of OnSDKLogin+OnSDKAutoLogin + block + Lua-string/reflection
   capture + logout-first + non-empty guard) → `Login-Flow.md`.
5. **Phase model**: `TitleScreen` = login-view-driven (boot AND logout), not the raw signal — the flash fix.

---

## 5. Remaining work (none in progress)

- ~~**Write up §4 findings** into the Knowledge Base + memory.~~ ✅ done 2026-07-30 (see §4).
- **Coordinated release**: swap every plugin `nuget.config` from the local feed to published `2.0.0`;
  publish framework 2.0.0 SDK; push framework `enhance/game-phases` + all plugin `feature/framework-v2`
  branches in lockstep. Commit the 3 non-git plugins (AccountSwitcher/Maestro/Position) from real clones;
  reconcile Mahiru's synthetic history; handle Experiment's WIP.
- **Remove** the throwaway phase-diag overlay (`stellar.diag.phase`, Shift+PageUp) before merge.
- In-game visual confirm still owed: the login-sidebar Stellar icon glow/size after `bd15f7f`.

## 6. Background agents (this session, resumable by name/id)
Framework: `a2b03c999d40e4fff`. AccountSwitcher: `af149f5105d7d2cfc`. Each plugin has its own porting agent
(resume to continue that plugin). ExchangeBuyer: `a9f74cc2fd30163f3`.
