<p align="center">
  <img src=".github/logo.png" alt="StellarResonance" width="160">
</p>

# StellarResonance

A Dalamud-style plugin framework for **Blue Protocol: Star Resonance** (Tencent SEA, executable `StarSEA.exe`). It loads via BepInEx 6 IL2CPP, drives an in-game **uGUI** overlay, and hosts third-party C# plugins behind a clean, read-only service API.

> ⚠️ **Experimental.** Verified against Unity 2022.3.59f1 LTS, IL2CPP. Future game patches may break the framework. You are responsible for understanding your game's Terms of Service before installing any client-side modifications.

> **Not affiliated with, endorsed by, or connected to the game's publisher or developer.** "Stellar" and "StellarResonance" refer to *this framework*, not to the game. This project ships **no game code, assets, or binaries** (see [Disclaimers](#disclaimers)) and exists purely for interoperability and quality-of-life research, in the spirit of FFXIV's Dalamud.

---

## Purpose: quality-of-life only — **no cheating**

This framework exists for **client-side quality-of-life modifications** — the same niche FFXIV's Dalamud occupies: chat enhancements, UI overlays, performance and player HUDs, log viewers, accessibility helpers, debug tooling.

**The framework deliberately does not, and will not, support cheating.** Specifically:

- **No packet construction or modification.** The game-event surface is *read-only* — subscribers observe events and state the game already produces. There is no public API to forge, intercept, drop, or rewrite outbound packets, and there is no roadmap for one. Game actions (e.g. sending chat, installing a module) are only ever performed through the game's *own* dispatcher methods, which apply the game's own validation — the framework never builds a request by hand.
- **No memory editing helpers.** No exposed read/write primitives, no pointer chains, no "set player HP" / "set position" APIs.
- **No anti-cheat evasion.** This framework runs openly via BepInEx; it makes no attempt to hide itself or defeat detection. The current game build ships *no* anti-cheat; if a future patch adds one, **stop using this framework** — the threat model changes completely.

The architecture enforces this by design: plugins reference only `Stellar.Abstractions`, which contains zero APIs for memory or raw-network access. They physically cannot reach lower-level capabilities — the build fails.

If you want to write a plugin that does any of the above, **do not use this framework.** Pull requests adding cheat-shaped capabilities will be rejected.

---

## Architecture

Five assemblies, Clean Architecture, with the dependency rule enforced at compile time by project references:

```
Plugin DLL
    │ references
    ▼
Stellar.Abstractions    — plugin-facing contracts only (IPluginServices, IFramework, …); BCL-only
    ▲           ▲
Stellar.Application     Stellar.Wire   — use cases / services + outbound interfaces (App);
    ▲                                    wire-protocol parsing + routing (Wire). Both BCL-only.
    ▲
Stellar.Infrastructure  — the only layer that touches BepInEx / HarmonyX / Unity IL2CPP /
    ▲                      Il2CppInterop / MessagePipe / the game's Panda.* assemblies
Stellar.Host            — BepInEx [BepInPlugin] composition root; wires Infrastructure into Application
```

**The dependency rule:** arrows point inward. `Abstractions` references nothing outside the .NET BCL. `Application` and `Wire` reference `Abstractions` only. `Infrastructure` is the sole layer permitted to reference BepInEx, HarmonyX, Unity, Il2CppInterop, or game (`Panda.*`) assemblies — it implements the outbound interfaces `Application` declares. `Host` references everything and performs DI wire-up, but contains no business logic.

Plugins reference **only** `Stellar.Abstractions.dll` (plus `UnityEngine.*` if they draw directly). They physically cannot touch framework internals — the build fails.

Deeper detail: [`docs/architecture.md`](docs/architecture.md).

---

## What the framework gives you

- **Plugins as C# DLLs.** Drop a `.dll` into `<game_mini>/stellar/plugins/<your-plugin>/` and it loads at startup. A plugin is a single class implementing `IStellarPlugin` with a constructor that takes `IPluginServices` — no other boilerplate.
- **A broad, read-only service surface** via `IPluginServices`: framework lifecycle and per-frame tick (`IFramework`), session and scene state (`IClientState`), game events (`IGameEvents`), live player state and stats (`IPlayerState`, `IPlayerStats`), inventory and module equip (`IInventory`, `IModuleEquip`), chat (`IChat`), combat snapshots/events/lookup (`ICombatSnapshot`, `ICombatEvents`, `ICombatLookup`), party roster/snapshot/events, static game tables (`IGameData`), persistent config (`IPluginConfig`), theming (`ITheme`, `INamedTheme`), hotkeys (`IHotkeys`), game assets (`IGameAssets`), and the UI toolkits below.
- **A uGUI UI toolkit.** Plugins describe their UI declaratively as a `HudElement` tree, and the framework renders it as native Unity uGUI:
  - `IHudHost` — register non-interactive HUD overlays (HP bars, meters, status strips).
  - `IWindowHost` — register draggable, interactive windows with title bars and close buttons.
  - `INativeUiHost` — inject mod UI (menu buttons, indicators, panels) into the game's own UI anchors.
  - `ILauncher` — register a tile in the Stellar launcher menu.
- **Lifecycle integration.** Login / logout / scene-change and per-frame `Update` callbacks are delivered to your plugin from the game's own `Panda.Core.Game.*` methods via HarmonyX patches.

---

## Sample plugins

Reference plugins (DebugInfo, PlayerHUD, StatInspector, CombatMeter, CooldownBar, ChatTools, DataInspector, ModuleOptimizer, …) live in the **separate plugins repository** — plugin code is kept out of the framework so the two evolve independently. Each plugin demonstrates a slice of the API (declarative HUDs, combat/inventory/chat services, game-validated actions). See the plugins repo for source and the [developer guide](docs/plugin-development.md) for how to build your own.

---

## Writing a plugin

A plugin is a single .NET 6 class library that references **only** `Stellar.Abstractions` (and optionally `UnityEngine.*`), and exports one public type implementing `IStellarPlugin`:

```csharp
public interface IStellarPlugin : IDisposable
{
    string Name { get; }
}
```

The framework constructs your plugin once via constructor injection of `IPluginServices`, then calls `Dispose()` on shutdown. From the constructor you read whatever sub-services you need off `IPluginServices`, subscribe to events, register HUDs/windows, and store the references for cleanup.

A typical HUD plugin describes its layout as a `HudElement` tree, registers it with `IPluginServices.Hud` (or `.Windows` for an interactive window), and updates state on the `IFramework.Update` tick or in response to a domain event. See **PlayerHUD** and **DebugInfo** for the smallest complete examples.

The full developer guide — every service interface, the plugin lifecycle, the uGUI toolkit, and the mandatory IL2CPP-aware quirks — is in [**`docs/plugin-development.md`**](docs/plugin-development.md). For the complete generated **API reference** (every public interface/type you can consume), see [**`docs/api/`**](docs/api/) — start at [`IPluginServices`](docs/api/Stellar.Abstractions.Services/IPluginServices.md).

Drop the built DLL into a subfolder of `<game_mini>/stellar/plugins/`; the framework scans `stellar/plugins/**/*.dll` at startup, finds your `IStellarPlugin`, and constructs it.

---

## Build and install

### Prerequisites

- The game installed on **Windows** (native) or **Linux** (Wine/Proton — Heroic / Lutris / Bottles). Locate the `game_mini` folder:
  - Windows: `…\Star\StarLauncher\game\release_<ver>\game_mini\`
  - Linux: `<prefix>/drive_c/Star/StarLauncher/game/release_<ver>/game_mini/`
- **.NET SDK** (8.0+) to build the framework and plugins (Windows / Linux / macOS).
- The **BepInEx 6 IL2CPP** loader installed into the `game_mini` folder (one-time).

New here? Start with the [**getting-started guide**](docs/getting-started.md) — it covers both Windows and Linux, and the easiest path (the cross-platform launcher).

### Build

```bash
dotnet build src/Stellar.sln -c Release
```

Build a single project:

```bash
dotnet build src/Stellar.Application/Stellar.Application.csproj -c Release
```

Output DLLs land in each project's `bin/Release/`. `src/Directory.Build.props` carries the shared TFM (`net6.0`) and the `GameInterop` path to the game's generated IL2CPP interop assemblies — override on the command line if your install differs:

```bash
dotnet build src/Stellar.sln -c Release -p:GameInterop=/your/path/to/game_mini/BepInEx/interop
```

### Install

**Linux:**

```bash
tools/install-bepinex.sh   # one-time — installs the BepInEx loader
tools/install-stellar.sh   # deploy the framework to the game
```

Set `WINEDLLOVERRIDES=winhttp=n,b` in your launcher's per-game env vars so Wine loads the Doorstop proxy.

**Windows:** copy the built DLLs from `src/Stellar.Host/bin/Release/` (Host, Infrastructure, Application, Abstractions, Wire + `ZstdSharp.dll`) into `<game_mini>\BepInEx\plugins\Stellar.Framework\`; the BepInEx `winhttp.dll` proxy loads natively (no env var needed). You can also run the bash scripts from Git Bash / WSL.

Either way, the framework DLLs live in `<game_mini>/BepInEx/plugins/Stellar.Framework/` and plugin DLLs go under `<game_mini>/stellar/plugins/<plugin>/`. The BepInEx log is at `<game_mini>/BepInEx/LogOutput.log` — start there if anything's wrong.

> The easiest install on **both** platforms is the [StellarResonance Launcher](https://github.com/StellarProtocol/StellarResonance), which does all of the above for you.

---

## Disclaimers

- **Game ToS.** Modifying the client may violate Star Resonance's Terms of Service. Use at your own risk; account actions taken by the publisher are your responsibility.
- **No game binaries redistributed.** This repository contains no content extracted from the game — no Cpp2IL output, no `Panda.*` interop stubs, no asset dumps. You generate those locally from your own install.
- **No warranty.** This is experimental research software. It can crash the game, corrupt the BepInEx install, or stop working entirely after a game patch.

---

## Contributing

The dependency rule (Abstractions ← Application/Wire ← Infrastructure ← Host) and the SOLID/size guardrails are mechanically enforced by a Roslyn analyzer, `tools/check-standards.sh`, and CI. If a change wants to cross a layer boundary the wrong way, you've identified an outbound interface to define. Contributions are accepted under the project's license (below); by submitting a pull request you agree your contribution is licensed under AGPL-3.0.

---

## License

**[GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0).**

This is free, open-source software. You may use, study, modify, and redistribute it — but any distributed or network-deployed derivative **must also be released in full under AGPL-3.0**. You cannot take this code closed-source. See [`LICENSE`](LICENSE) for the full terms.
