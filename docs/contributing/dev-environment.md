# Setting up a development environment

This guide gets you from a fresh clone to a framework build running in your own game client. It covers
Windows and Linux (Wine/Proton). For the plugin side (writing a plugin, not changing the framework), see
[`../plugin-development.md`](../plugin-development.md).

There are two build paths: the **stub build** needs no game and is exactly what CI runs; the **interop build** targets your own client so you can deploy and test in game.

![Two build paths — CI stubs or your own game](../diagrams/build-paths.svg)

## What you need

| Tool | Why |
|---|---|
| Git | Clone the repo. |
| .NET SDK 8.0 or newer | Builds everything. Framework projects target `net6.0` (the BepInEx 6 IL2CPP runtime, set in [`../../src/Directory.Build.props`](../../src/Directory.Build.props)); the test projects target `net8.0` ([`../../tests/Directory.Build.props`](../../tests/Directory.Build.props)). |
| Bash | Runs [`../../tools/check-standards.sh`](../../tools/check-standards.sh) and the install scripts. On Windows use Git Bash or WSL. |
| The game (optional) | Only needed to run your build in-game. Building and testing need no game install. |

On Linux, [`../../tools/setup-dev-env.sh`](../../tools/setup-dev-env.sh) installs a user-local .NET SDK
under `~/.dotnet` if none is found, plus the reverse-engineering tools (Cpp2IL, a BepInEx 6 IL2CPP
stage, a Python venv). It is idempotent and never touches the game install.

## Clone and build (no game needed)

The `Infrastructure` and `Host` projects compile against the game's IL2CPP interop assemblies. The
repo ships API-only **reference stubs** of those assemblies in [`../../refs/`](../../refs), so the whole
solution builds without the game:

```bash
git clone https://github.com/StellarProtocol/StellarResonanceModSystem.git
cd StellarResonanceModSystem
dotnet build src/Stellar.sln -c Release -p:GameInterop=$PWD/refs -p:BepInExCore=$PWD/refs
```

This is exactly the command CI runs ([`../../.github/workflows/ci.yml`](../../.github/workflows/ci.yml)).
The Roslyn analyzer runs during this build at error severity, so a size or shape violation fails it
(see [coding-standards.md](coding-standards.md)).

On Windows (PowerShell) use `-p:GameInterop=$PWD\refs -p:BepInExCore=$PWD\refs`.

Outputs land in `src/<Project>/bin/Release/<Project>.dll`.

## Run the tests and the standards gate

```bash
dotnet test src/Stellar.sln -c Release --no-build -p:GameInterop=$PWD/refs -p:BepInExCore=$PWD/refs
bash tools/check-standards.sh
```

`check-standards.sh` exits non-zero on any blocker or major, the same as in CI. Run both before you
open a pull request. [testing.md](testing.md) explains the test projects in detail.

## Building against your own game install

The stubs carry the public API surface only. If you want to build against the real interop that
BepInEx generated for your client, point the two properties at your game folder instead:

```bash
dotnet build src/Stellar.sln -c Release \
  -p:GameInterop=<game_mini>/BepInEx/interop \
  -p:BepInExCore=<game_mini>/BepInEx/core
```

`<game_mini>` is the game folder described in [`../getting-started.md`](../getting-started.md#1-find-your-game-folder).
BepInEx creates `BepInEx/interop/` the first time the game launches with the loader installed.

If you don't pass the properties, `src/Directory.Build.props` falls back to `.local/interop` and
`.local/bepinex/core` at the repo root. `.local/` is gitignored, so you can copy or symlink your
interop there once and then run a plain `dotnet build src/Stellar.sln -c Release`.

Never commit game DLLs, interop output or decompiler dumps. When a game patch changes the interop
surface, the stubs are regenerated with [`../../tools/gen-refs.sh`](../../tools/gen-refs.sh) (see
[`../../CONTRIBUTING.md`](../../CONTRIBUTING.md#how-ci-builds-this-repo-no-game-install-needed)).

## Deploying to your own game

Install the BepInEx loader first ([`../getting-started.md`](../getting-started.md#2-install-the-bepinex-loader-once)).
The framework loads from `<game_mini>/BepInEx/plugins/Stellar.Framework/`.

### Linux: `tools/install-stellar.sh`

The script builds `src/Stellar.sln` (Release) and copies the full framework set (Host, Infrastructure,
Application, Abstractions, PluginContracts, Wire, `ZstdSharp.dll`) into the framework folder. Point it
at your game folder and skip the plugin sweep:

```bash
DOTNET="$(command -v dotnet)" GAME_RELEASE=/path/to/game_mini STELLAR_FRAMEWORK_ONLY=1 \
  bash tools/install-stellar.sh test
```

| Variable / argument | Effect |
|---|---|
| `GAME_RELEASE` | Absolute path to `game_mini`. Without it the script looks for a `release_*/game_mini` under `STELLAR_PREFIX`. |
| `DOTNET` | The `dotnet` binary to build with. Set it explicitly. |
| `STELLAR_FRAMEWORK_ONLY=1` | Deploy the framework only; leave `stellar/plugins/` untouched. |
| `SKIP_BUILD=1` | Copy the existing `bin/Release` output without rebuilding (for example after building with your own `-p:` paths). |
| `prod` / `test` / `perf` | Mode. `test` writes `DIAGNOSTICS` into `<game_mini>/stellar_perf.flags` and turns on the BepInEx console and instant log flushing. `prod` removes the flags file. |

The script's own build step passes no `-p:` properties, so it uses the `.local/` fallback above. If you
build against `refs/` or another path, build first and deploy with `SKIP_BUILD=1`. It also truncates
`BepInEx/LogOutput.log` so the next launch starts clean.

### Windows: `Local.props`

Copy [`../../src/Stellar.Host/Local.props.example`](../../src/Stellar.Host/Local.props.example) to
`src/Stellar.Host/Local.props` (gitignored) and set `GameInstallDir` to your `game_mini` folder. Its
`DeployFramework` target runs after every Release build and copies the same DLL set into
`<game_mini>\BepInEx\plugins\Stellar.Framework\`. Close the game before you build.

## Where to look when it runs

- **The log:** `<game_mini>/BepInEx/LogOutput.log`. A successful boot prints
  `[Stellar] diagnostics=ON` or `OFF` ([`../../src/Stellar.Host/BootstrapPlugin.cs`](../../src/Stellar.Host/BootstrapPlugin.cs)).
- **To tell which build loaded**, read the BepInEx boot lines for the framework, not the file on disk.
  BepInEx loads one copy per plugin GUID; a stray backup copy anywhere under `BepInEx/plugins/` can be
  loaded instead of yours. Keep backups outside `BepInEx/plugins/` and `stellar/plugins/`.

## Diagnostics: `STELLAR_DIAGNOSTICS`

`StellarDiagnostics.IsEnabled` ([`../../src/Stellar.Abstractions/Diagnostics/StellarDiagnostics.cs`](../../src/Stellar.Abstractions/Diagnostics/StellarDiagnostics.cs))
turns on the per-event logging that lives in the `*.Diagnostics.cs` partials. It is true when either:

- the environment variable `STELLAR_DIAGNOSTICS` is `1` or `true`, or
- a line `DIAGNOSTICS` (or `DIAGNOSTICS=1`) is in `stellar_perf.flags` in the game's working directory
  (`game_mini`). This is what `install-stellar.sh test` writes.

It is read once at startup ([`../../src/Stellar.Abstractions/Diagnostics/PerfControls.cs`](../../src/Stellar.Abstractions/Diagnostics/PerfControls.cs)),
so restart the game after changing it. Set the variable in Windows' system environment variables, or in
your launcher's per-game environment settings on Linux. The flags file works on both.

## Wire capture: `STELLAR_WIRECAP`

`STELLAR_WIRECAP` records decoded network frames to a JSON Lines file. It is how you see what the server
actually sends before you write a reader for it ([expose-a-wire-field.md](expose-a-wire-field.md)).
The value is a filter, parsed by
[`../../src/Stellar.Infrastructure/Game/Capture/CaptureFilter.cs`](../../src/Stellar.Infrastructure/Game/Capture/CaptureFilter.cs):

| Value | Captures |
|---|---|
| `all` | Every frame. |
| `svc:<uuid>` | Frames of one service, e.g. `svc:1664308034` for `WorldNtf` (ids in [`../../src/Stellar.Wire/WorldNtfMethodIds.cs`](../../src/Stellar.Wire/WorldNtfMethodIds.cs)). |
| `svc:<uuid>:<Kind>` | One service, one message kind (`Call`, `Notify`, `Return`, `Echo`). |
| `kind:<Kind>` | One message kind across all services. |
| `team` | The party services (a built-in preset). |

Terms can be combined with commas. An empty or unset value disables capture; a malformed value logs
`[WireCap] disabled — bad STELLAR_WIRECAP: …` and captures nothing.

Where it writes ([`../../src/Stellar.Infrastructure/Game/PandaWireTap.cs`](../../src/Stellar.Infrastructure/Game/PandaWireTap.cs)):
a new file `stellar-wirecap-<yyyyMMdd-HHmmss>.jsonl` in the process base directory. The exact path is
logged at startup as `[WireCap] ENABLED spec='…' → <path>`. A session is capped at 500,000 lines.

Each line carries a header (`dir`, `type`, `svc`, `method`, `len`, …) and a `decoded` field: a
schema-less protobuf walk of the payload (field numbers, wire kinds, varint values, nested messages, and
hex for short byte fields). When the walk cannot decode a frame completely, the raw payload is added as
base64 in `raw`. Capture files contain your character's data; don't post them publicly without looking
at what's in them.
