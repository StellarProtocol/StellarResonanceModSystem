# Coding standards

Most of these rules are checked by tools, not by reviewers. A pull request that breaks one fails CI.
This page lists what is checked, where, and the conventions reviewers look for on top.

| Gate | What it checks | Where it runs |
|---|---|---|
| Project references | The layer dependency rule | Every build |
| `Stellar.Analyzers` | Method, parameter, constructor and interface size (`STELLAR0001`–`0005`), plus `STELLAR0006` | Every build of a `src/` project |
| `CS1591` as error | XML docs on every public member of `Stellar.Abstractions` | Every build of Abstractions |
| `tools/check-standards.sh` | File size, namespaces, layer `using`s, diagnostics placement, banned patterns | Locally and in CI |

## Layers and the dependency rule

![Stellar framework layers and dependency rule](../diagrams/framework-layers.svg)

The framework is split into assemblies under `src/`. The rule is enforced by what each `.csproj` is
allowed to reference; see [`../architecture.md`](../architecture.md) for the reasoning.

| Assembly | May reference |
|---|---|
| `Stellar.Abstractions` | The .NET BCL. Its only external reference is the `0Harmony` API stub in `refs/`, because `IHarmonyHost.Create` returns a Harmony instance. |
| `Stellar.Wire` | `Stellar.Abstractions` |
| `Stellar.Application` | `Stellar.Abstractions` |
| `Stellar.Infrastructure` | `Stellar.Abstractions`, `Stellar.Application`, `Stellar.Wire`, plus BepInEx, HarmonyX, Il2CppInterop and the Unity interop assemblies. The only layer that touches the game. |
| `Stellar.Host` | Everything above. The BepInEx entry point and dependency wiring; no business logic. |
| `Stellar.PluginContracts` | `Stellar.Abstractions` only. The framework never references it. |
| Plugins | `Stellar.Abstractions` (plus `UnityEngine.*` for drawing). Never Application, Wire or Infrastructure. |

If Application needs something only Infrastructure can do, declare an interface in Application
(for example `ICombatEventSink` in [`../../src/Stellar.Application/Abstractions/ICombatEventSink.cs`](../../src/Stellar.Application/Abstractions/ICombatEventSink.cs)),
implement it in Infrastructure, and connect the two in Host.

`check-standards.sh` backs this up: a `using BepInEx`, `HarmonyLib`, `0Harmony`, `UnityEngine`,
`Il2Cpp…` or `Panda…` line in `Stellar.Abstractions/` or `Stellar.Application/` is a blocker.

## Size limits: the analyzer

The analyzer ([`../../src/Stellar.Analyzers/`](../../src/Stellar.Analyzers)) is injected into every
`src/` project by [`../../src/Directory.Build.props`](../../src/Directory.Build.props). The rules are
declared as warnings in `DiagnosticIds.cs`; [`../../.editorconfig`](../../.editorconfig) raises all of
them to `error`, so any hit fails the build.

| Id | Rule | Limit |
|---|---|---|
| `STELLAR0001` | Method too long (blocker) | > 100 lines |
| `STELLAR0002` | Method too long (major) | > 50 lines |
| `STELLAR0003` | Too many parameters | > 5 parameters |
| `STELLAR0004` | Too many constructor dependencies | > 6 constructor parameters |
| `STELLAR0005` | Interface too wide | > 8 members |
| `STELLAR0006` | A `[WorldGated]` method has no `if (!…IsWorldActive) return;` guard | — |

Details that matter when you are near a limit (from [`SizeAndShapeAnalyzer.cs`](../../src/Stellar.Analyzers/SizeAndShapeAnalyzer.cs)):

- Method length is the physical lines of the body block, from the `{` line to the `}` line inclusive.
  Local functions are measured the same way. Expression-bodied members are not measured.
- A method over 100 lines reports both `STELLAR0001` and `STELLAR0002`.
- Exemptions are hard-coded in the analyzer: the `IPluginServices` aggregator and `IClientState` are
  exempt from `STELLAR0005`; the `PluginServices` constructor is exempt from `STELLAR0004`.

When you hit a limit, split: a partial class when the pieces share state, a separate type when they
don't, a parameter object for long parameter lists, a narrower interface for wide ones. The analyzer
tests in [`../../tests/Stellar.Analyzers.Tests/`](../../tests/Stellar.Analyzers.Tests) show exactly
what each rule flags.

## The text gate: `tools/check-standards.sh`

Run `bash tools/check-standards.sh` from the repo root (or pass file paths to check only those). It
exits non-zero if it finds any blocker or major; minors are printed but don't fail it.

| Check | Severity |
|---|---|
| File longer than 800 lines | blocker |
| File longer than 500 lines | major |
| Block-scoped `namespace X { … }` (use file-scoped `namespace X;`) | blocker |
| Forbidden `using` in Abstractions or Application (see above) | blocker |
| `GUILayout`, `GUIStyle`, `GUIContent` or `GUI.` anywhere in Abstractions, including comments (the plugin contract is uGUI-only) | blocker |
| A file named `*.Phase<N>.cs` | blocker |
| Path-form `GameObject.Find("a/b")` (scans the whole scene) | blocker |
| Inline `if (StellarDiagnostics.IsEnabled)` outside a `*.Diagnostics.cs` file | major |
| Service-locator calls (`PluginServices.Get<`, `Service.Instance.`, `GetService<`) outside Host | major |
| A type named `…Manager` | minor |
| An interface whose name doesn't start with `I` | minor |
| A public field (heuristic) | minor |

## Diagnostics live in `.Diagnostics.cs` partials

Verbose, per-event logging goes in a sibling partial file named `<Type>.Diagnostics.cs`. Each method
there checks `StellarDiagnostics.IsEnabled` itself and returns early; the production file calls it
unconditionally. From [`../../src/Stellar.Application/Services/CombatService.Diagnostics.cs`](../../src/Stellar.Application/Services/CombatService.Diagnostics.cs):

```csharp
// One summary line per seeded entity — never one per buff (a town crowd seeds thousands).
private void DiagBuffSeed(EntityId target, int count)
{
    if (!StellarDiagnostics.IsEnabled) return;
    _log.Info($"[Buff] seed {target.Uid} n={count}");
}
```

and the call site in `CombatService.BuffSeed.cs`:

```csharp
DiagBuffSeed(entityId, snapshot.Count);
EnqueueEvent(new CombatEvent.EntityBuffsSeeded(timestampMs, entityId, snapshot));
```

This keeps production files readable and makes the off-mode cost a single branch. How to turn
diagnostics on is in [dev-environment.md](dev-environment.md#diagnostics-stellar_diagnostics).

## XML docs on the plugin API

[`../../src/Stellar.Abstractions/Stellar.Abstractions.csproj`](../../src/Stellar.Abstractions/Stellar.Abstractions.csproj)
sets `GenerateDocumentationFile` and adds `CS1591` to `WarningsAsErrors`. Every public type and member
in Abstractions needs a `///` doc comment or the build fails. Those comments become the API reference on
the docs site. Internal types don't need XML docs. More in [api-changes.md](api-changes.md).

## Host wiring: feature-named partials

Host's composition root is `BootstrapPlugin.cs` plus one partial per feature:
`Wiring.Core.cs`, `Wiring.Theme.cs`, `Wiring.Wire.cs`, `Wiring.CombatSpec.cs` and so on in
[`../../src/Stellar.Host/`](../../src/Stellar.Host). Add a new `Wiring.<Feature>.cs` for a new
feature. Files named after a phase (`BootstrapPlugin.Phase8.cs`) are rejected by the text gate.

## Conventions reviewers check

These are not all mechanical, but a reviewer will ask for them:

- **`internal sealed` by default.** Only types in `Stellar.Abstractions` (and the few types BepInEx or
  Roslyn must discover, such as the analyzers and `BootstrapPlugin`) are `public`.
- **Constructor injection only.** No service locator and no singletons. Take interfaces, not concrete
  adapters.
- **No new static mutable state outside Host.** Services get what they need through the constructor.
  (A few process-wide switches, such as `PerfControls`, predate this rule.)
- **Readers never throw.** Wire parsers return `false` or skip on bad input; see
  [expose-a-wire-field.md](expose-a-wire-field.md).
- **Language settings** come from `src/Directory.Build.props`: nullable enabled, implicit usings off
  (write explicit `using`s), latest C# version, deterministic builds.

Naming:

| Element | Convention | Example |
|---|---|---|
| Types, methods, properties, events, constants | PascalCase | `CombatService`, `IdleEntityTtlMs` |
| Interfaces | `I` + PascalCase | `ICombatBuffSink` |
| Private fields | `_camelCase` | `_localSnapshot` |
| Locals and parameters | camelCase | `timestampMs` |
| Booleans | `Is` / `Has` / `Can` / `Should` prefix | `IsEnabled` |
| Async methods (returning `Task`/`ValueTask`) | `Async` suffix | `SaveCurrentToAsync` |
| Type roles | `Service`, `Reader`, `Probe`, `Registry`, `Factory`; not `Manager` | `SyncNearEntitiesReader` |

Put new code where it belongs by responsibility, keep the namespace matching the folder, and use one
public type per file.
