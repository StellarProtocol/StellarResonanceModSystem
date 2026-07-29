# Design: Game Phases, Tick Split, and Plugin-Owned Window Visibility

- **Status:** Implemented on `enhance/game-phases` (builds green; **in-game validation per §7 still pending**, not merged)
- **Date:** 2026-07-29
- **Area:** `Stellar.Abstractions`, `Stellar.Application`, `Stellar.Host`, `Stellar.Infrastructure`
- **Baseline:** branch `enhance/game-phases`, cut from `origin/main` @ `ab1e17b` (framework `1.16.1`).

---

## 1. Motivation

The framework's per-tick work is suppressed until the player is in-world. In
`Stellar.Host/Wiring.ServiceTick.cs`, `RunFrameworkTick` early-returns while a scene transition is in
progress:

```csharp
if (IsTickGatedBySceneTransition()) return;   // _sceneTransitioning
```

`_sceneTransitioning` starts `true` at boot and is cleared **only once logged in**
(`Wiring.GameLoop.cs:OnEnterScene`), because the tick's game-state probing corrupts the world-connect
handshake if it runs during boot / title / character-select. Consequences:

- **Nothing the framework drives runs before the player is in-world** — no window draw, no input poll,
  no hotkey. A tool meant to be usable at the login screen (account switcher, server picker) can't
  render or be interacted with there.
- The gate is **blanket** because the corrupting work was never isolated.

The blocker is *not* the render surface: the window canvas is a camera-independent
`ScreenSpaceOverlay` + `DontDestroyOnLoad`, and window interaction runs on a `WindowInteractionTicker`
MonoBehaviour driven by Unity's own `Update`. What's missing before the player is in-world is only the framework tick that
mounts/draws windows and polls input/hotkeys.

This design makes the framework run its UI/input work in every phase (keeping its game-state work
gated), introduces a first-class client **phase** signal, exposes a **UI-state** signal, and moves
window-visibility policy entirely into the plugin via a single `ShouldRender()` predicate.

## 2. Goals

- A first-class, framework-owned **`GamePhase`** signal (`TitleScreen`, `World`).
- Run UI/input work every phase; keep game-state work gated on the world-connect-safe predicate.
- **Plugins own window-visibility policy** through one `ShouldRender()` function; the framework only enacts.
- Expose **`GameUIState`** as informational flags a plugin's `ShouldRender()` can read.

## 3. Non-goals

- Back-compat. Members are removed outright where cleaner.
- Finer phases now (`CharSelect`, …) — the enum is append-friendly for later.
- Per-plugin `Update` scheduling changes.

## 4. Decisions (settled)

| # | Decision | Choice |
|---|----------|--------|
| 1 | Keep `GamePhase`? | **Yes** — it's the game *client* phase, a distinct concept from session state; clearer to plugin devs and extensible. |
| 2 | `GamePhase` vs `IsLoggedIn`/`Login`/`Logout` | **Coexist** — different concepts (client phase vs login/session), not redundant. |
| 3 | Phase delivery | **`IClientState.PhaseChanged` event + `IClientState.Phase` getter** (event on transition; getter for initial / on-demand read). |
| 4 | Event shape | **`event Action<PhaseChange>`**, where `PhaseChange` is a `readonly record struct (GamePhase From, GamePhase To)` — carries both ends so a plugin needn't track the previous phase. |
| 5 | Plugin `Update`s in `TitleScreen` | **Run every phase**; plugins self-gate. |
| 6 | Game-state gating | **`IsWorldActive`** (exposed on `IClientState`) — each game-state unit self-gates on it; **not** `GamePhase` (too coarse — true mid-transition). |
| 7 | Who gates | **Every game-state unit self-gates on `IsWorldActive`** (game-state services, Host plumbing, plugin raw reads). Draw services (`Window`/`Hud`) do **not**. The **tick is a dumb dispatcher** — gates nothing. |
| 8 | Window/HUD visibility | **A single compiler-`required` `ShouldRender : Func<bool>`** (via `IRenderGated`) is the source of truth (`hide = !ShouldRender()`). |
| 9 | Removed | `AutoHideBehindGameMenus`, `HideUntilInWorld` (and no `WindowSpec.Phases`). |
| 10 | `MasterHudKill` | **Kept** as an explicit dev override, outside visibility policy. |
| 11 | `HudSpec` | **Same treatment** — HUDs also gate purely on `ShouldRender()`. |
| 12 | `GameUIState` | **Flat `[Flags]` (backing `int`) + preset masks; informational only** (framework never gates on it). |

`GamePhase` **gates nothing** in the framework — it and `GameUIState` are *signals* a plugin reads.
The only protective gate is `IsWorldActive`, self-gated by each game-state unit (services, Host, plugins).

## 5. Design

### 5.1 `GamePhase` — client lifecycle signal (`Stellar.Abstractions`)

```csharp
namespace Stellar.Abstractions.Domain;

public enum GamePhase { TitleScreen, World }   // plain enum (single-value signal); append-friendly
```

```csharp
namespace Stellar.Abstractions.Services;

public readonly record struct PhaseChange(GamePhase From, GamePhase To);

public interface IClientState
{
    // existing (session state — KEPT, distinct concept):
    bool IsLoggedIn { get; }
    string? CurrentSceneName { get; }
    event Action Login;
    event Action Logout;
    event Action<string?> SceneChanged;

    // new (client phase — distinct from the above):
    GamePhase Phase { get; }                 // current phase — read initial state (e.g. in ctor) / on demand
    event Action<PhaseChange> PhaseChanged;  // fires on transition; From and To both supplied

    // new (safe-to-touch-game-state signal — §5.2): true in a stable world scene, false mid-transition
    bool IsWorldActive { get; }

    // new (UI-state signal — §5.4):
    GameUIState UiState { get; }
}
```

- `ClientStateService` owns `Phase` (boot = `TitleScreen`). Transitions:
  - **`TitleScreen → World`** on the **rising edge of `IsWorldActive`** (the tick gate clearing at the first
    in-world `OnEnterScene` while logged in) — *not* on `Game.OnLogin`. This means "World" = actually in
    a world scene, so character-select stays `TitleScreen` regardless of when `Game.OnLogin` fires.
  - **`World → TitleScreen`** on **`Game.OnLogout`** — *not* the falling edge of `IsWorldActive`. `IsWorldActive`
    dips false on every in-world zone load (`OnLeaveScene`); the phase must stay `World` through those and
    only drop on actual logout/return-to-char-select. So the phase is steady across zone transitions.
- `ClientStateService` fires `PhaseChanged(new PhaseChange(previous, next))` on each transition. A plugin
  reads `Phase` for its initial state (e.g. in its ctor) and subscribes to `PhaseChanged` for transitions,
  unsubscribing in `Dispose` — the same hygiene it already uses for `Framework.Update`.
- **`GamePhase` (client phase) and `IsLoggedIn`/`Login`/`Logout` (session state) coexist** as distinct
  concepts. They correlate today but answer different questions.

### 5.2 Tick & game-state gating (`Stellar.Host/Wiring.ServiceTick.cs`)

The blanket `if (gated) return;` is removed — `RunFrameworkTick` runs, and ticks everything, in **every
phase**. The framework no longer decides *which* work is world-only; instead **anything that touches live
game state self-gates** on a single exposed signal:

```csharp
// IClientState (plugin-facing): true in a stable world scene, false while mid-transition (= !_sceneTransitioning)
bool IsWorldActive { get; }
```

`IsWorldActive` is **stricter than `Phase == World`** — it is also `false` during in-world zone loads (the
connect / scene-switch handshake), which is exactly when live game-state work corrupts the connection. So
the gate everyone uses is `IsWorldActive`, never `Phase` / `IsLoggedIn`.

**The tick is a dumb dispatcher** — it calls all its work unconditionally and knows nothing about who's
world-only:

- **UI / input runs every phase** (inherently safe): `SetScreen`, input poll, hotkeys, **window draw
  (`_windowService.Tick`) and HUD draw (`_hudService.Tick`)** — both draw services; per-element visibility
  is `ShouldRender`, and a hidden element skips its value pull — toasts, layout input, exchange drain, and
  plugin `Update`s.
- **Game-state work self-gates** — `if (!_clientState.IsWorldActive) return;` at the top of each unit:
  framework probes/services (`PlayerState`, `Inventory`, world-attr, equip/loadout), **notice-tips
  (`_noticeTipService.Tick` — runs game Lua)**, the Host's own plumbing (`_framework.Tick`, game-data load,
  `ProbeGameRootOnce`), and any **plugin** doing raw game reads in its `Update`. One signal, one check,
  everywhere.

This drops the "tick knows each service's timing" coupling (adding a game-state service = it self-gates;
the tick is untouched) **and** the method-splitting/hoisting a central gate would need (see §9). The Host
sets `IsWorldActive` at the same two spots it flips the scene-transition flag (`false` in
`BeginSceneTransition`, `true` at the `OnEnterScene` gate-clear).

**Two signals, two jobs** (both plugin-facing):

| Signal | Use for | Across an in-world zone load |
|---|---|---|
| `Phase` (`TitleScreen`/`World`) | **visibility** (`ShouldRender`) | stays `World` — window stays up |
| `IsWorldActive` (bool) | **game-state access** (in `Update`) | dips `false` during the handshake — skip the read |

**Gating is opt-in, per what a unit does — not universal.** A plugin that only draws UI, does HTTP, or
reads *framework-cached* data touches no live game state and **needs no gate — it just runs every phase**.
Only a unit that does **raw** game reads gates.

⚠️ **Blast radius (framework units only):** a *plugin* that forgets its gate harms only itself; a
*framework game-state unit* that forgets `if (!IsWorldActive) return` corrupts the world-connect —
everyone disconnects. So the safety-net targets the **framework's** units: a Roslyn analyzer (existing
`Stellar.Analyzers`) that fails the build if a marked game-state method lacks the guard. It runs on `src/`
only — it **never** applies to plugin projects, so no plugin is ever forced to gate.

### 5.3 Window & HUD visibility — plugin owns the decision, framework enacts

A single **required** predicate is the only source of visibility truth:

```csharp
public interface IRenderGated { Func<bool> ShouldRender { get; } }   // the contract the gate consumes

public sealed record WindowSpec(...) : IRenderGated { public required Func<bool> ShouldRender { get; init; } }
public sealed record HudSpec(...)    : IRenderGated { public required Func<bool> ShouldRender { get; init; } }
```

Gate:

```csharp
hide = !spec.ShouldRender();
```

- **Plugin owns the decision.** `ShouldRender` reads whatever it wants — `Phase`, `UiState`, its own state —
  via the plugin's captured `_services`, and returns draw/don't. Evaluated each apply (~10 Hz), so it is
  always current (a *pull*, not a stored flag).
- **Framework owns the flip.** The renderer `SetActive(false)`s the root and skips the value pull
  (`t.Apply()`) when hidden — the perf win (a hidden window runs zero value funcs). It never touches the
  user's Show/Hide state.
- **`ShouldRender` is `required` (compiler-enforced)** — every `WindowSpec`/`HudSpec` must set it or the
  **build fails**. `required` lives on each record (interfaces can't carry it); `IRenderGated` just declares
  the getter. So: shared contract *and* enforcement. No `null` default, no "magic" default, no
  login-screen-spam footgun by omission. (net6: polyfill `RequiredMemberAttribute` +
  `CompilerFeatureRequiredAttribute` in `Stellar.Abstractions` — they're net7+ BCL types.)
- **`GamePhase` / `GameUIState` are inputs to `ShouldRender`, not framework gates.** The framework does not
  read them to hide a window.

`MasterHudKill` is retained as an explicit **dev override** outside policy:

```csharp
hideAll = !spec.ShouldRender() || (PerfControls.MasterHudKill && spec.Category == WindowCategory.HUD);
```

Typical `ShouldRender` values (hand-written; no framework presets):

```csharp
ShouldRender = () => true;                                             // always (e.g. a login-screen tool)
ShouldRender = () => _services.ClientState.Phase == GamePhase.World;   // gameplay window
ShouldRender = () => _services.ClientState.Phase == GamePhase.World    // gameplay HUD, hidden when HUD covered
            && (_services.ClientState.UiState & GameUIState.GameHudHidden) == 0;
```

### 5.4 `GameUIState` — informational UI flags (`Stellar.Abstractions`)

Purely informational: the framework **detects and exposes** it; it **never gates** on it. A plugin's
`ShouldRender()` optionally reads it.

**Scope:** `GameUIState` describes *in-world* UI and is `None` while `Phase == TitleScreen` (there is no
in-game HUD/menu at the title / login / character-select screens). Use `Phase` for "at the login
screen," not a `GameUIState` value.

```csharp
[Flags]
public enum GameUIState   // backing int → 32-bit headroom
{
    None           = 0,
    GameHud        = 1 << 0,   // gameplay HUD on-screen
    FullScreenMenu = 1 << 1,   // inventory / map / char / gear / skills — covers the HUD
    MainMenu       = 1 << 2,   // ESC functions list
    LineSelector   = 1 << 3,   // SwitchLine panel — OVERLAYS the HUD (co-occurs with GameHud)
    Dialogue       = 1 << 4,   // NPC talk
    Cutscene       = 1 << 5,   // story video / top
    Loading        = 1 << 6,   // loading screen
    Matchmaking    = 1 << 7,   // match-pop confirm

    // ── preset masks (provisional membership — verify cover-vs-overlay in-game) ──
    GameHudHidden = FullScreenMenu | Cutscene | Loading,       // UIs that REPLACE the HUD (not LineSelector)
    AnyMenu       = FullScreenMenu | MainMenu | LineSelector,
    Blocking      = FullScreenMenu | MainMenu | Dialogue | Cutscene | Loading | Matchmaking,
}
```

- **Flat flags** (no base/overlay structure) — the game's UI layers genuinely co-occur (e.g. the line
  selector stays open over a valid HUD: `GameHud | LineSelector`). A single value can't express that;
  flags let a plugin ask precisely. Presets encode the cover-vs-overlay knowledge as named masks so
  plugins don't memorize bits.
- **Detection** reuses/extends `PandaMenuStateProbe`, which already separately detects these ~9 layers
  and today collapses them into one `IsFullScreenMenuOpen` bool. The work is to **un-collapse** (set the
  bits) and **expose** it (lift to `Stellar.Abstractions`, add `IClientState.UiState`); the per-layer
  detection largely exists.
- **Extensible:** new bits are append-only and non-breaking (existing `HasFlag` checks unaffected);
  adding a covering UI means adding it to `GameHudHidden`. Keep backing `int` (32 headroom) and `None = 0`.

## 6. Removed / superseded

`WindowSpec.AutoHideBehindGameMenus`, `WindowSpec.HideUntilInWorld`, `HudSpec.AutoHideBehindGameMenus`,
`HudSpec.HideUntilInWorld`, and the phase-gating clauses in `WindowGatingPolicy` (its window path
collapses to `hide = !ShouldRender()`). No `WindowSpec.Phases`, no render-predicate presets, no separate
title-screen tick path.

## 7. Risk & required validation

Replacing the blanket tick gate with per-unit `IsWorldActive` self-gates (§5.2) must be **verified
in-game**, not assumed:

1. **World-connect succeeds** — login reaches in-world with no `[50000]`/`[50011]` disconnect, and so do
   in-world zone loads. This is exactly what the blanket gate protected. If it regresses, a game-state
   unit is missing its `IsWorldActive` guard (or gates on `Phase`/`IsLoggedIn` instead) — find that unit.
2. **A `TitleScreen` window** (a window whose `ShouldRender()` returns true in `TitleScreen`) renders and is interactive
   at the title screen; hotkeys fire there.
3. **In-world features** unaffected (combat meter, HUD, inventory-driven plugins).
4. **`GameUIState` cover-vs-overlay** — confirm which UIs replace the HUD vs overlay it, to finalize
   `GameHudHidden`/`Blocking` membership (the probe knows "active", not "covers").

Keep a copy of the prior `Stellar.Framework` build for rollback.

## 8. Implementation plan

**`Stellar.Abstractions`**
- Add `Domain/GamePhase.cs`, `Domain/GameUIState.cs`, `Domain/PhaseChange.cs`.
- `IClientState`: add `Phase`, `IsWorldActive`, `UiState` getters + `PhaseChanged` event (keep `IsLoggedIn`/`Login`/`Logout`/`SceneChanged`).
- Add `IRenderGated { Func<bool> ShouldRender { get; } }`; `WindowSpec` + `HudSpec` implement it with a
  **compiler-`required`** `ShouldRender`; remove `AutoHideBehindGameMenus` and `HideUntilInWorld`.
  (net6: polyfill `RequiredMemberAttribute` + `CompilerFeatureRequiredAttribute` in `Stellar.Abstractions`
  — they're net7+ BCL types.)

**`Stellar.Application`**
- `ClientStateService`: track `Phase` (→ `World` on `IsWorldActive`-rising in-world; → `TitleScreen` on
  logout; raise `PhaseChanged`), `IsWorldActive` (via an internal `SetWorldActive` the Host calls), and `UiState`.
- **Game-state services** (`PlayerState`, `Inventory`, world-attr, equip/loadout, **notice-tips**): add
  `if (!_clientState.IsWorldActive) return;` at the top of their `Refresh`/`Tick`. (Draw services —
  `WindowService`/`HudService` — do **not** self-gate; they run every phase and rely on `ShouldRender`.)
- `WindowGatingPolicy`: `hide = !gated.ShouldRender()` — one method taking `IRenderGated` (drops the
  phase/menu clauses **and** the separate HUD overload).
- `WindowService` / `HudService`: pass their spec as `IRenderGated`.

**`Stellar.Host`**
- `Wiring.ServiceTick.cs`: **remove the blanket gate** — the tick runs everything every phase (gates
  nothing). Host plumbing that touches game state (`_framework.Tick` subscribers, game-data load,
  `ProbeGameRootOnce`) self-gates with `if (!_clientState.IsWorldActive) return;`.
- `Wiring.GameLoop.cs` / `Wiring.Wire.cs`: at the scene-transition flips call `_clientState.SetWorldActive(…)`;
  raise `Phase → World` at the `OnEnterScene` gate-clear and `Phase → TitleScreen` at `OnLogout`.
- Safety-net for the **framework's own** game-state units: a `Stellar.Analyzers` rule that fails the build
  if a marked (`[WorldGated]`) game-state method lacks the `IsWorldActive` guard. Runs on `src/` only —
  **not** plugin projects, so plugins are never forced to gate (they opt in only for raw reads).

**`Stellar.Infrastructure`**
- `PandaMenuStateProbe`: un-collapse into `GameUIState` bits; keep `WindowRenderer.ApplyValues`'s
  `MasterHudKill` override.

**Validate** per §7.

## 9. Appendix — tick change (illustrative)

```csharp
// BEFORE — one blanket gate kills the whole tick outside a stable world:
private void RunFrameworkTick(float masterDt)
{
    MaybeApplyPerfExperiment();
    if (IsTickGatedBySceneTransition()) return;      // ← removed
    ...  // exchange drain, plugin Updates, UI + service work
}

// AFTER — the tick runs every phase and gates NOTHING; each game-state unit self-gates.
private void RunFrameworkTick(float masterDt)
{
    MaybeApplyPerfExperiment();
    ...  // everything called unconditionally, exactly as before — just no blanket gate
}

// Each game-state SERVICE / method early-returns on the shared signal (the ONLY new line per unit):
public void Refresh(...)                  // PlayerStateService, InventoryService, NoticeTipService, world-attr, …
{                                         //   (NOT WindowService/HudService — those are draw services, run always)
    if (!_clientState.IsWorldActive) return;
    ...
}
private void TryLoadGameDataEagerOnce()   // Host plumbing does the same (_framework.Tick, ProbeGameRoot, …)
{
    if (!_clientState.IsWorldActive) return;
    ...
}

// The Host sets IsWorldActive where it already flips the scene-transition flag:
private void BeginSceneTransition() { _sceneTransitioning = true;  _clientState.SetWorldActive(false); ... }
// OnEnterScene gate-clear:          { _sceneTransitioning = false; _clientState.SetWorldActive(true);  ... }
```

**Why this is small:** removing the blanket gate makes the tick call everything — so window draw, input,
and hotkeys now run at the title screen (the goal). Correctness moves to a one-line
`if (!IsWorldActive) return;` at the top of each game-state unit. No method splitting, no param threading,
no hoisting — a central `if (IsWorldActive) { … }` block would have needed all three (e.g. `_windowService.Tick`
sits nested inside a game-state method today); self-gating sidesteps that entirely.

## 10. API surface (summary)

```csharp
namespace Stellar.Abstractions.Domain;
public enum GamePhase { TitleScreen, World }
[Flags] public enum GameUIState { None=0, GameHud=1<<0, FullScreenMenu=1<<1, MainMenu=1<<2,
    LineSelector=1<<3, Dialogue=1<<4, Cutscene=1<<5, Loading=1<<6, Matchmaking=1<<7,
    GameHudHidden = FullScreenMenu|Cutscene|Loading, AnyMenu = FullScreenMenu|MainMenu|LineSelector,
    Blocking = FullScreenMenu|MainMenu|Dialogue|Cutscene|Loading|Matchmaking }

namespace Stellar.Abstractions.Services;
public readonly record struct PhaseChange(GamePhase From, GamePhase To);
public interface IClientState { /* IsLoggedIn, Login, Logout, SceneChanged, CurrentSceneName; */
    GamePhase Phase { get; } event Action<PhaseChange> PhaseChanged;
    bool IsWorldActive { get; } GameUIState UiState { get; } }

namespace Stellar.Abstractions.Domain;
public interface IRenderGated { Func<bool> ShouldRender { get; } }
public sealed record WindowSpec(/* ... */) : IRenderGated { public required Func<bool> ShouldRender { get; init; } }
public sealed record HudSpec(/* ... */)    : IRenderGated { public required Func<bool> ShouldRender { get; init; } }
```
