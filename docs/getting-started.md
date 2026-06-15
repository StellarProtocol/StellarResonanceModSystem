# Getting started

This guide gets you from a clean install to a running framework with plugins, on **Windows or
Linux**. It assumes you already own and can launch the game. StellarResonance ships **no game code
or assets** — you generate the IL2CPP interop locally from your own install (see
[`../DISCLAIMER.md`](../DISCLAIMER.md)).

> **Quality-of-life only.** This framework is read-only and does not support cheating of any
> kind. If a future game patch adds anti-cheat, stop using it. See the
> [README policy](../README.md#purpose-quality-of-life-only--no-cheating).

## Easiest path — the launcher (Windows + Linux)

For most users, use the [**StellarResonance Launcher**](https://github.com/StellarProtocol/StellarResonance).
It detects your game install, installs/updates the framework, toggles vanilla⇄modded, and launches
the game — on both Windows and Linux. If you only want to *use* mods, stop here and grab the launcher.

The rest of this guide is the **manual / developer** path: building the framework yourself.

## 1. Find your game folder

The framework installs alongside the game's IL2CPP runtime, in the `game_mini` folder:

- **Windows:** `…\Star\StarLauncher\game\release_<ver>\game_mini\`
  (under wherever the official launcher installed the game, e.g. `C:\Program Files\Star\…`).
- **Linux (Wine/Proton):** `<prefix>/drive_c/Star/StarLauncher/game/release_<ver>/game_mini/`.

This folder is referred to as `<game_mini>` below.

## 2. Install the BepInEx loader (once)

The framework runs on **BepInEx 6 (IL2CPP)**. Install the loader into `<game_mini>`:

- **Windows:** download a BepInEx 6 IL2CPP build, extract it into `<game_mini>` so `winhttp.dll`
  and `BepInEx/` sit next to the game executable. (Or run `tools/install-bepinex.sh` from Git Bash /
  WSL.)
- **Linux:** run `tools/install-bepinex.sh`, then add `WINEDLLOVERRIDES=winhttp=n,b` to your
  launcher's per-game environment variables so Wine loads the Doorstop proxy.

Launch the game once. BepInEx generates the IL2CPP **interop assemblies** under
`<game_mini>/BepInEx/interop/` — these are what the framework compiles against. Confirm the log
exists at `<game_mini>/BepInEx/LogOutput.log`.

## 3. Build the framework

Install the **.NET SDK 8.0+** ([Windows / Linux / macOS](https://dotnet.microsoft.com/download)), then:

```bash
dotnet build src/Stellar.sln -c Release \
  -p:GameInterop=<game_mini>/BepInEx/interop \
  -p:BepInExCore=<game_mini>/BepInEx/core
```

Use your platform's real path for `<game_mini>` (Windows: `C:\…\game_mini\BepInEx\interop`;
Linux: `/.../game_mini/BepInEx/interop`). The inner BCL-only projects (Abstractions / Wire /
Application / Analyzers) build without these paths; Infrastructure/Host need them.

## 4. Deploy

- **Linux:** `tools/install-stellar.sh` copies the framework DLLs into
  `<game_mini>/BepInEx/plugins/Stellar.Framework/`.
- **Windows:** copy the built DLLs from `src/Stellar.Host/bin/Release/` (Host, Infrastructure,
  Application, Abstractions, Wire + `ZstdSharp.dll`) into
  `<game_mini>\BepInEx\plugins\Stellar.Framework\`. (Or run the script via Git Bash / WSL.)

## 5. Add plugins

Plugins are separate C# DLLs (kept in their own repository — plugin code is not part of this
framework repo). Drop a built plugin into its own subfolder:

```
<game_mini>/stellar/plugins/<your-plugin>/<YourPlugin>.dll
```

The framework scans `stellar/plugins/**/*.dll` at startup, finds the `IStellarPlugin`, and loads
it. To write your own, see the [**developer guide**](plugin-development.md).

## 6. Run and verify

Launch the game. On a successful boot the BepInEx log shows `[Stellar]` lines (including
`diagnostics=ON|OFF`). Set the env var `STELLAR_DIAGNOSTICS=1` for verbose per-event logging when
investigating an issue (read once at startup — restart to change it). Set it via System environment
variables on Windows, or your launcher's per-game env vars on Linux.

## Troubleshooting

| Symptom | Check |
|---|---|
| No `[Stellar]` lines in the log | **Windows:** is `winhttp.dll` next to the game exe? **Linux:** is `WINEDLLOVERRIDES=winhttp=n,b` set? Did BepInEx generate `BepInEx/interop/`? |
| Build error: `Il2CppInterop` / `Panda.*` not found | The interop assemblies aren't where `GameInterop` points — launch the game once, or pass `-p:GameInterop=...`. |
| Plugin doesn't load | Is the DLL under its own subfolder in `stellar/plugins/`? Does it export a public `IStellarPlugin`? |
| Nothing works after a game patch | A new game build can move the types the framework binds to. Wait for a compatibility update. |

The log at `<game_mini>/BepInEx/LogOutput.log` is the first place to look for anything.
