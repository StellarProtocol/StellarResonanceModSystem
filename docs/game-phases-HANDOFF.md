# Handoff — Game Phases design + Account Switcher

_Written end of session 2026-07-29. Two parallel threads; the framework design is the active one._

---

## Thread A — Game Phases framework design (ACTIVE)

**Artifact:** `StellarResonanceModSystem/docs/game-phases-design.md` — status **Proposed**.
**Branch:** `enhance/game-phases`, cut from `origin/main` @ `ab1e17b` (framework `1.16.1`). Clean tree
except the design doc (untracked). **Nothing implemented yet** — the doc is design-only.

### What it is
Make the framework's tick run UI/input every phase (so a plugin window can appear at the **title
screen**), while keeping game-state work safe during the world-connect handshake. Plus a first-class
client-phase signal and a plugin-owned window-visibility model.

### Settled decisions (all in the doc's decision table)
- **`GamePhase { TitleScreen, World }`** — a *signal* (gates nothing). `IClientState.Phase` getter +
  `event Action<PhaseChange> PhaseChanged` where `PhaseChange` is `readonly record struct(From, To)`.
  Coexists with `IsLoggedIn`/`Login`/`Logout` (distinct concept: client phase ≠ session state).
- **Transitions:** `TitleScreen→World` on the rising edge of `IsWorldActive` (the `OnEnterScene` gate-clear);
  `World→TitleScreen` on `Game.OnLogout`. Steady `World` across in-world zone loads.
- **`IsWorldActive`** (new `IClientState` bool = `!_sceneTransitioning`) — the game-state gate. **Dumb
  tick**: it ticks everything every phase and gates nothing. Every unit that touches live game state
  **self-gates** `if (!_clientState.IsWorldActive) return;` — framework services, Host plumbing
  (`_framework.Tick`, game-data load, `ProbeGameRootOnce`), notice-tips (`DoString`), and any plugin
  doing **raw** game reads. **Draw services (`Window`/`Hud`) and UI/input do NOT gate** — run every phase.
- **Two signals, two jobs:** `Phase` for **visibility** (stays `World` through zone loads);
  `IsWorldActive` for **game-state access** (dips false during a transition). Gate game-state on
  `IsWorldActive`, NEVER `Phase`/`IsLoggedIn` (those are true mid-transition).
- **Visibility = `ShouldRender`** — a `Func<bool>` that is the *single source of truth*
  (`hide = !gated.ShouldRender()`). Factored into `interface IRenderGated { Func<bool> ShouldRender {get;} }`;
  `WindowSpec`+`HudSpec` implement it. **Compiler-`required`** on each record (net6 needs
  `RequiredMemberAttribute`+`CompilerFeatureRequiredAttribute` polyfilled in `Stellar.Abstractions`).
  **Removed** `AutoHideBehindGameMenus`, `HideUntilInWorld`. **Kept** `MasterHudKill` (dev override).
- **`GameUIState`** — flat `[Flags] : int` (bits `GameHud`,`FullScreenMenu`,`MainMenu`,`LineSelector`,
  `Dialogue`,`Cutscene`,`Loading`,`Matchmaking`) + preset masks (`GameHudHidden`,`AnyMenu`,`Blocking`).
  **Informational only** (framework never gates on it); `None` in `TitleScreen`. Detection = un-collapse
  the existing `PandaMenuStateProbe` bool.
- **Safety-net:** a `Stellar.Analyzers` rule (`[WorldGated]` → build fails without the `IsWorldActive`
  guard) for the **framework's own** game-state units. NEVER applies to plugins (separate projects).

### ✅ RESOLVED — `ShouldRender` stays compiler-`required` (decided 2026-07-29)
No open design questions remain. `ShouldRender` is `required` on `WindowSpec`/`HudSpec` (clean cut,
no magic default). **Consequence is a migration task, not a question:** this is a **coordinated
`Stellar.Abstractions` bump + recompile of every UI plugin** — each won't compile until it adds
`ShouldRender = …` and drops the removed `AutoHideBehindGameMenus`/`HideUntilInWorld`. Non-UI plugins
(no `WindowSpec`/`HudSpec`) are unaffected.

**Migration checklist (do as part of implementing §8):**
1. Bump `Stellar.Abstractions` (breaking) — add `IRenderGated`, `required ShouldRender`, the net6
   `RequiredMemberAttribute`+`CompilerFeatureRequiredAttribute` polyfills; remove the two bools.
2. Update the framework's own windows/HUDs (launcher, settings, perf overlay, combat meter, …) —
   add `ShouldRender = () => _services.ClientState.Phase == GamePhase.World` (or a custom predicate;
   `() => true` for a title-screen tool like the account switcher).
3. Recompile every UI plugin against the new Abstractions; sweep for the two removed bools.
4. Release in lockstep (framework + plugins) — a plugin built against old Abstractions won't load.

### Remaining implementation-time items (not decisions; in §7)
- Verify world-connect survives the self-gate change (the core test) + in-world zone loads.
- `GameUIState` preset membership (which UIs *cover* the HUD vs *overlay* — probe knows "active" not
  "covers") and `GameHud` bit detection.
- Sanity-check char-select stays `TitleScreen` on first run (does the gate-clear log fire at char-select?).

### To implement (once the open question is resolved): follow §8 → validate per §7.
Build the framework: `dotnet build src/Stellar.sln -c Release -p:GameInterop=<game>/BepInEx/interop
-p:BepInExCore=<game>/BepInEx/core`. Deploy to `<game>/BepInEx/plugins/Stellar.Framework/`
(the build auto-deploys; **game must be closed** or the DLL copy is locked). Back up the prior framework
first. Local game: `E:\BPSR\StarLauncher\game\release_3.7\game_mini`.

---

## Thread B — StellarAccountSwitcherPlugin (built, NOT yet run in-game)

**Location:** `Mod/StellarAccountSwitcherPlugin/` — builds clean, 54 KB DLL, deployed to
`<game>/stellar/plugins/accountswitcher/`. Auth core (parity-verified, live-tested) shared with
`Mod/HaoPlayAuthHarness/` (`HaoPlayAuth.cs` + `AccountStore.cs` DPAPI-via-crypt32 + `AccountRefreshService.cs`).

**Goal:** OTP a HaoPlay account once, cache the JWT (DPAPI-encrypted), auto-refresh via `/api/auth`, and
switch accounts by injecting `account_data` (replays `Z.VMMgr.GetVM("login"):OnSDKLogin(data)` via DoString).

**Identity constants CONFIRMED** via `Repository/MagicalFinder` (a working headless Python BPSR client):
SDKType=**10** (HaoplaySEA), PlatformType=**6** (SoutheastAsia), channel/LoginType=**1001**, OS=**5**,
OpenID=JWT `uid`. Plugin defaults corrected. HaoPlay auth protocol fully in `Knowledge Base/Login-Flow.md` §10.

**Next for the plugin:** load-test in-game (BepInEx log `[AccountSwitcher] constructed`), then add an
account (email→OTP→login) and try a switch. **BLOCKED by nothing** except needing an in-game run.
⚠️ This plugin's window is a login-screen tool — it's the very use case Thread A's design enables. Right
now the framework can't show it pre-login (that's why Thread A exists). Until Thread A ships, the switcher
window only works in-world.

---

## Key paths
- Design: `StellarResonanceModSystem/docs/game-phases-design.md`
- Framework src: `StellarResonanceModSystem/src/` (Abstractions, Application, Host, Infrastructure)
  - Tick: `src/Stellar.Host/Wiring.ServiceTick.cs`; gate flag: `src/Stellar.Host/Wiring.GameLoop.cs`
    (`_sceneTransitioning`, `OnEnterScene`); login/logout hooks: `src/Stellar.Host/Wiring.Wire.cs`
  - `ClientStateService`: `src/Stellar.Application/Services/ClientStateService.cs`
  - `WindowGatingPolicy`/`WindowService`/`HudService`: `src/Stellar.Application/Services/`
  - `IClientState`/`WindowSpec`/`HudSpec`: `src/Stellar.Abstractions/`
  - Menu-state probe: `src/Stellar.Infrastructure/Game/PandaMenuStateProbe.cs`
- Switcher: `Mod/StellarAccountSwitcherPlugin/`; auth harness: `Mod/HaoPlayAuthHarness/`
- Login/auth research: `Mod/Knowledge Base/Login-Flow.md`; headless ref: `Repository/MagicalFinder/`
