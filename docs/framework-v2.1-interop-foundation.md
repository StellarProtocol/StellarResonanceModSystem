# Design + Plan: Plugin Interop Foundation (framework v2.1)

- **Status:** Phase 1 (framework surface) **DONE + committed** `384aa18` on `framework-v2` — built clean,
  code-reviewed (GO), hardened (F1/F3/F4), packed `2.0.0` to local feed, cache cleared. Phase 2 (plugin
  migration) IN PROGRESS. **Wave A DONE** (each compile-PASS + code-reviewed GO, committed on its own
  `feature/framework-v2`): Mahiru `24bd72f` (−134), CombatMeter `f5b060f` (−97, +tests fix), CooldownBar
  `3c31fe1` (−30), Experiment `329083e` (−300, overlay untouched). Wave B next. NOT deployed in-game yet
  (game was running); NOT pushed.
- **Date:** 2026-08-03
- **Area:** `Stellar.Abstractions`, `Stellar.Application`, `Stellar.Infrastructure` (+ per-plugin migration)
- **Baseline:** framework branch `framework-v2` (SDK **2.0.0**, local-feed); plugins on `feature/framework-v2`.
- **Relation to v2:** This is a **second, additive migration** on the same unreleased v2 train. v2's first
  wave was the phases / `required ShouldRender` port (see `framework-v2-SESSION-HANDOFF.md`). Nothing here
  reopens that; everything below is purely additive surface + downstream adoption.

---

## 1. Motivation

A four-group audit of all 19 plugins (2026-08-03) found the framework exposes **no IL2CPP-interop or Lua
floor**, so ~11 plugins each re-grow the same reflection / Lua / threading primitives by hand. Duplication
counts (byte-identical or near-identical private copies):

| Primitive | Copies / plugins | Representative sites |
|-----------|------------------|----------------------|
| `FindType(fullName)` assembly scan | **~40+ across 11 plugins** | Experiment ×20+, Mahiru ×4, Maestro ×6, CooldownBar, CombatMeter (`FindLuaType`), AutoFishing, Position, MinimalNameplate, AccountSwitcher, CustomProfileImage, EntityInspector |
| Lua bridge (`LuaState.mainState` + `DoString` + global read/write) | **7 plugins** | AccountSwitcher, Maestro, CustomProfileImage, AutoFishing, Experiment, CombatMeter, Mahiru |
| `ZSingleton<T>.Instance` (FlattenHierarchy) reflection | 8 copies / 4 plugins | Maestro, CustomProfileImage, CooldownBar, Mahiru |
| IL2CPP list walk (`Count` + `Item` indexer) | 2 plugins | CooldownBar, Mahiru |
| Main-thread `Post` (off-thread→tick marshal) | ≥3 plugins | ExchangeBuyer, AccountSwitcher, CombatMeter |
| dt-accumulator throttle (`_accum += dt; if (…)`) | ~everything | EntityInspector, ExchangeBuyer, AutoFishing, overlays |
| Safe monotonic time / server-clock skew | 3 idioms | ModuleOptimizer (`SafeTimeNow`), ExchangeBuyer (`ServerClock`) |
| Harmony instance create + id-suffix + unpatch | **11 sites / 5 plugins** | Experiment ×5+, MinimalNameplate, AutoFishing, CooldownBar, Mahiru |

`ChatToolsPlugin` and `LoadoutSwitcherPlugin` already write substantial features with **zero plumbing** —
proof the framework can absorb the rest.

---

## 2. Scope

**In scope (audit items 1–4):** the four primitives that are pure plumbing, span the most plugins, and each
*delete* plugin code rather than add feature surface:

1. IL2CPP reflection floor → `StellarInterop` (§3.1)
2. Lua bridge → `ILua` (§3.2)
3. `IFramework` timing/threading + server clock (§3.3)
4. Harmony host → `IHarmonyHost` (§3.4)

**Out of scope (deliberately deferred — do NOT bundle here):**
- **#5 World head-follow overlay** (~2,600 LoC forked between Experiment & MinimalNameplate: head-pos read,
  HUD-render-pass submit, `ResolveProfession`, role-color, **sync profession-icon loader gap**). Real design
  effort; its own future doc.
- **#6 Widen too-narrow existing services**: `IEntityPortrait` pitch-clamp knob (EntityInspector Harmony-
  patches framework internals to undo it), `IEntityTransforms.TrySetPosition` (teleport write), name-lookup
  by player uuid (Mahiru). Small but semantic; batch later.
- **#7 Cheap glue**: `StellarFormat.Abbreviate` (K/M), `StellarInput.FilterDigits/ParseLong`, world-ready
  gate preset, embedded-icon loader, make `Compat` `required`-polyfill `public`. Low-risk consolidation batch.

---

## 3. New framework surface (all additive → non-breaking)

Additive-safety: `IPluginServices` / `IFramework` / `ICombatSnapshot` are implemented **only** by the
framework (plugins consume them), so adding members breaks no compiled plugin. New interfaces are net-new.
See `Knowledge Base\PerPluginServices-Decoration.md` (⭐ "interface-member addition breaks implementors only,
safe when framework-internal") before touching the wiring.

Wiring points: `src/Stellar.Application/Services/PluginServices.cs` and
`src/Stellar.Application/Hosting/PerPluginServices.cs`.

### 3.1 `StellarInterop` — static reflection floor (Stellar.Abstractions)
Static (patch classes are static and often have no `IPluginServices` handle). Precedent for logic in
Abstractions: `VirtualListMath.cs`, `Diagnostics/PerfProbe.cs`. Cache `FindType` results. Reference KB:
`Knowledge Base\` IL2CPP-reflection notes + memory `reference_il2cpp_reflection` (IL2CPP fields are
`PropertyInfo`, never `FieldInfo`; `ZList<T>` is not `IEnumerable`), `SkillCD-Tracking.md` (ZSingleton
`FlattenHierarchy`).

```csharp
public static class StellarInterop
{
    Type?        FindType(string fullName);                     // cached AppDomain scan
    object?      GetSingleton(Type t);                          // Instance prop, Public|Static|NonPublic|FlattenHierarchy
    object?      GetSingleton(string typeFullName);
    MethodInfo?  FindMethod(Type t, string name, int paramCount);
    PropertyInfo? FindPropertyUp(Type t, string name);         // walk base chain (IL2CPP fields = properties)
    FieldInfo?   FindFieldUp(Type t, string name);
    int          Count(object il2cppList);                      // GetProperty("Count")
    object?      Item(object il2cppList, int index);           // Item / get_Item indexer
    IEnumerable<object?> Enumerate(object il2cppList);         // Count + Item loop
}
```

### 3.2 `ILua` — game Lua bridge (service on IPluginServices)
Service (needs the live `LuaState` the framework owns; the framework already drives this path internally).
Reference KB: `Knowledge Base\Lua-Injection-from-CSharp.md` (esp. **§3b** — Lua **strings ARE** readable via
`LuaGetGlobal→LuaToString→LuaSetTop`; only `DoString("return x")`/`get_Item` are opaque) + memory
`reference_lua_string_readback`. **Main-thread only** — document it; the native Lua→C# callback crash
(memory `feedback_il2cpp_lua_callback_crash`) means fire-and-forget `DoString`, no C# callbacks into Lua.

```csharp
public interface ILua
{
    bool    Ready { get; }                                     // mainState resolved
    void    DoString(string chunk);                            // fire-and-forget, main thread
    bool    TryReadGlobalBool(string key, out bool value);     // stack read
    string? ReadGlobalString(string key);
    bool    TryReadGlobalNumber(string key, out double value);
}
```
`IPluginServices.Lua { get; }`.

### 3.3 `IFramework` timing/threading + server clock
Adds to the existing `IFramework` (§Update/RequestUpdateRate). Reference: ExchangeBuyer `LicenseGateGlue.cs`
(the "no Unity SynchronizationContext" queue-and-pump), `ServerClock.cs`.

```csharp
// on IFramework:
void        Post(Action action);                 // run on next Update tick (off-thread→main marshal)
IDisposable Every(TimeSpan interval, Action a);  // throttled recurring cb (downsample Update); dispose to cancel
float       TimeNow { get; }                      // safe monotonic seconds (guards IL2CPP Time throw)

// on ICombatSnapshot (already has raw `long ServerNowMs`):
DateTimeOffset ServerNow { get; }                 // skew-corrected wall clock in server domain
```

### 3.4 `IHarmonyHost` — patch lifecycle (service on IPluginServices)
Plugins still author patch classes; the framework owns instance creation, id uniqueness, and teardown so a
plugin can't leak an un-unpatched instance. Reference: the 11 hand-rolled `new Harmony(id)` + `UnpatchSelf`
sites.

```csharp
public interface IHarmonyHost
{
    HarmonyLib.Harmony Create(string suffix = "");   // id = "<pluginId>[.suffix]"; auto-unpatch on plugin dispose
}
```
`IPluginServices.Harmony { get; }`. Pairs with `StellarInterop.FindMethod` for target resolution.

---

## 4. Versioning & feed  ✅ DECIDED

**Re-pack `2.0.0` in place — NO version bump.** v2 is pre-release/unreleased; the whole v2 train ships as a
single `2.0.0`, so added surface folds into it (matches handoff §2, "re-packed several times"). Consequence:
**plugin `PackageReference` versions do NOT change** (stay `2.0.0`) — migration is pure code edits, no csproj
churn.

⚠️ **Mandatory on every re-pack:** clear the NuGet cache before any consumer restores, or the stale cached
`2.0.0` (without the new types) is served → phantom "type not found" compile errors. `build-deploy` runs
`dotnet nuget locals http-cache global-packages -c` (or deletes the cached `2.0.0`) after packing all three
packages (`Stellar.Abstractions`, `Stellar.PluginContracts`, `Stellar.Plugin.InteropRefs`) to
`local-nuget-feed/`. Local-feed only — nothing published.

Dev loop per plugin: (framework re-packed + cache cleared) → migrate code → restore → build/deploy.

---

## 5. Migration map (item → delete → adopt)

Only items 1–4. `nuget.config` already points at the local feed in every repo.

| Plugin | Branch | Deletes | Adopts |
|--------|--------|---------|--------|
| Experiment | feature/framework-v2 | `FindType` ×many, Lua, ZSingleton, list-walk, Harmony boilerplate | `StellarInterop.*`, `ILua`, `IHarmonyHost` |
| Mahiru | feature/framework-v2 | `FindType` ×4, Lua, ZSingleton, list-walk, Harmony | all four |
| CooldownBar | feature/framework-v2 | `FindType`, ZSingleton, list-walk, Harmony (`FindMethod` scan) | `StellarInterop.*`, `IHarmonyHost` |
| CombatMeter | feature/framework-v2 | `FindLuaType`, Lua bridge, main-thread post | `StellarInterop.FindType`, `ILua`, `IFramework.Post` |
| AutoFishing | feature/framework-v2 | `FindType`, Lua, Harmony, dt-throttles | all four |
| EntityInspector | feature/framework-v2 | `FindType`, dt-throttle | `StellarInterop`, `IFramework.Every` *(portrait clamp = #6, out of scope)* |
| ExchangeBuyer | feature/framework-v2 | main-thread post, dt-throttle, `ServerClock` | `IFramework.Post/Every`, `ICombatSnapshot.ServerNow` *(numeric-input = #7)* |
| MinimalNameplate | feature/framework-v2 | `FindType`, Harmony *(overlay body = #5, out of scope)* | `StellarInterop`, `IHarmonyHost` |
| CustomProfileImage | feature/framework-v2 | `FindType`, Lua | `StellarInterop`, `ILua` *(icon loader/file dialog = #7/#8)* |
| ModuleOptimizer | feature/framework-v2 | `SafeTimeNow` | `IFramework.TimeNow` *(int parse = #7)* |
| AccountSwitcher | **main** (new v2 plugin) | `FindType`, Lua bridge | `StellarInterop`, `ILua` |
| Maestro | **new git, main** | `FindType` ×6, Lua, ZSingleton, Harmony | all four |
| Position | **new git, main** | `FindType` | `StellarInterop` *(teleport write = #6, out of scope)* |
| StatInspector / PlayerHUD / RaidManager | feature/framework-v2 | *assess — flagged findings were #7/none* | likely none in 1–4 |
| ChatTools / LoadoutSwitcher | — | none (exemplary) | none |

Excluded: `StellarEntitlementService` — **not a plugin**.

---

## 6. Execution phases (delegated per CLAUDE.md Rule 9)

Main agent designs/reviews/does git; `mod-implementer` writes C#; `build-deploy` builds/packs/deploys;
`code-reviewer` verifies each diff. A subagent "it works" is a claim until code-reviewer confirms it.

- **Phase 0 — this doc.** Review + §4 decision.
- **Phase 1 — framework (branch `framework-v2`).**
  `mod-implementer`: add §3.1–3.4 (interfaces in Abstractions, impls in Infrastructure/Application, wire into
  `PluginServices`/`PerPluginServices`). `build-deploy`: build `Stellar.sln`, **re-pack `2.0.0` (no bump) + clear
  NuGet cache** (§4), pack 3 packages → local feed, regen `docs/api` (xmldocmd — CI does NOT check doc freshness;
  memory `reference_modsystem_build`). `code-reviewer`: verify additive-only + wiring. Commit on `framework-v2`.
- **Phase 2 — plugin migration waves.** Each plugin (refs stay `2.0.0`, no csproj change): restore against the
  re-packed feed → `mod-implementer` swaps helpers → `build-deploy` builds/deploys → `code-reviewer` verifies
  the real diff (esp. that deletions are behavior-preserving) → commit on the plugin's branch.
  - **Wave A (heaviest):** Experiment, Mahiru, CooldownBar, CombatMeter.
  - **Wave B:** AutoFishing, EntityInspector, ExchangeBuyer, MinimalNameplate, CustomProfileImage,
    ModuleOptimizer; assess StatInspector/PlayerHUD/RaidManager.
  - **Wave C (git setup first):** `git init` Maestro & Position on `main` (+ `.gitignore` bin/obj, baseline
    commit of current on-disk state), then migrate; AccountSwitcher on `main`.
- **Phase 3 — close-out.** KB doc(s) + memory (Rule 2/3): a new `Interop-Foundation.md` KB doc for the
  StellarInterop/ILua/IHarmonyHost patterns; update CLAUDE.md Rule 8 routing + memory index.

---

## 6b. Known gaps (evidence-driven — extend when ≥2 consumers hit them)

- **`StellarInterop` collection walk covers int-indexed only** (ZList: `Count`+`Item(int)`). The Mahiru pilot
  surfaced two sites it does NOT cover: an **enum/object-keyed `ZDictionary` indexer** (`["Item", EnumKey]`) and
  an **enumerator-only `Values`** walk (`GetEnumerator`/`MoveNext`/`Current`, no int indexer). Mahiru handles
  these locally (`WalkIl2Cpp`), left untouched (behavior-preserving). **Deferred** — extend `StellarInterop`
  with `Item(object, object key)` + an enumerator-fallback `Enumerate` only once a 2nd/3rd plugin needs it, so
  the surface stays proven. Not a defect in the shipped floor; ZList (the common case) is fully served.

## 6c. Framework-polish follow-ups (deferred, non-blocking)

- **`StellarInterop.Item`/`Enumerate` allocate + re-resolve per element per call** (fresh `object[1]` + a
  `FindPropertyUp("Item")`/`GetGetMethod` each iteration, no caching). Reintroduces the per-item reflection/GC
  churn CooldownBar's history flags as a combat hitch source (demand+visibility-gated, small N → LOW). Fix is
  internal-only (cache the indexer getter per Type; resolve once in `Enumerate`, reuse one arg array) — no API
  change, benefits every consumer. Do in a batched framework-polish pass, not mid-wave.
- **Dead `InputSimPatch.cs`** (Experiment) still hand-rolls Harmony (unreferenced; `_harmony` used as an
  `IsInstalled` flag) and an unused `const BindingFlags SF` in `DitherControl.cs` — optional cleanup, inert.
- **Nullable warnings** from consuming the nullable-returning `StellarInterop` API (CS8605/CS8601 in Experiment)
  — cosmetic; no `TreatWarningsAsErrors`.

## 7. Risks

- **NuGet cache staleness** — re-packing `2.0.0` in place (§4, DECIDED) *will* serve stale cache unless the
  cache is cleared on every re-pack. This is the #1 operational trap; `build-deploy` must do it every time.
- **Main-thread contract** for `ILua`/`IFramework.Post` — mis-use off-thread crashes IL2CPP; document + guard.
- **Behavior-preserving deletions** — the hand-rolled copies have subtle gotchas (FlattenHierarchy, non-generic
  `DoString` overload selection, `Amount`-vs-`ActualAmount`-style traps). code-reviewer must diff against
  original semantics, not just "compiles + runs."
- **Unpushed v2 train** — this rides the same pending coordinated release; do not push/merge independently.
