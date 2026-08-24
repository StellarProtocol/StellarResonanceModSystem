#!/usr/bin/env bash
# Deploy the layered src/ build (Stellar.* DLLs) to the game's test prefix.
# Replaces the legacy framework/ build in BepInEx/plugins/Stellar.Framework/.
#
# Roll back to the legacy build with: tools/install-framework.sh
set -euo pipefail

# Target prefix is overridable so we can deploy to the MAIN client
# (STELLAR_PREFIX=/opt/game/BlueProtocol) as well as the legacy test prefix.
STELLAR_PREFIX="${STELLAR_PREFIX:-/opt/game/BlueProtocol2}"
# Auto-detect the current release dir instead of hardcoding it (the game patches the release_<ver>
# folder — was release_2.11, now release_3.7, etc.). Pick the highest-versioned release_*/game_mini;
# override with GAME_RELEASE=<absolute path to game_mini> if needed.
GAME="${GAME_RELEASE:-$(ls -d "$STELLAR_PREFIX"/drive_c/Star/StarLauncher/game/release_*/game_mini 2>/dev/null | sort -V | tail -1)}"
[ -n "$GAME" ] && [ -d "$GAME" ] || { echo "no release_*/game_mini found under $STELLAR_PREFIX (set GAME_RELEASE=)"; exit 1; }

# Build/runtime MODE — one switch instead of hand-editing BepInEx.cfg + flags each time.
#   prod (default) — shipping: IMGUI on, diagnostics off, buffered logging, NO console window (fast).
#   test           — diagnostics ON + instant-flush disk logging + console window (crash-debuggable).
#   perf           — IMGUI OFF (NO_OVERLAY) + UNCAP fps, diagnostics off, no console (measure the OnGUI-drop win).
#   vanilla        — DISABLE BepInEx entirely (doorstop enabled=false): pure game, no Stellar, for the
#                    baseline FPS. No build/deploy; re-enabled by any of prod/test/perf.
# prod/test/perf apply game_mini/stellar_perf.flags (NO_OVERLAY/UNCAP/DIAGNOSTICS) + BepInEx.cfg logging +
# re-enable doorstop — no Heroic env edits needed.
MODE="${1:-prod}"
case "$MODE" in prod|test|perf|vanilla) ;; *) echo "usage: install-stellar.sh [prod|test|perf|vanilla]"; exit 2 ;; esac

DOORSTOP="$GAME/doorstop_config.ini"
set_doorstop() {  # $1 = true|false — toggle BepInEx loading within [General] only (not debug_enabled)
    [ -f "$DOORSTOP" ] && sed -i "/^\[General\]/,/^\[/ s/^enabled = .*/enabled = $1/" "$DOORSTOP"
}
if [ "$MODE" = vanilla ]; then
    set_doorstop false
    : > "$GAME/BepInEx/LogOutput.log"
    echo "MODE=vanilla — BepInEx DISABLED (doorstop enabled=false). Pure game, no Stellar."
    echo "Re-enable Stellar with: tools/install-stellar.sh [prod|perf|test]"
    exit 0
fi
set_doorstop true   # ensure Stellar loads (in case a prior 'vanilla' disabled it)
# SRC defaults to the src/ next to THIS script (so worktree deploys use the
# worktree build, not the main checkout). Override with SRC=... if needed.
SRC="${SRC:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../src" && pwd)}"

# Devkit root (framework/tools/../.. == the stellar-devkit checkout) — parent of
# both plugin-repos/ (per-plugin dev repos) and plugins/ (the plugins monorepo,
# whose samples/ holds the dev-only tools without a dedicated repo).
DEVKIT_ROOT="${DEVKIT_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

# Layered framework DLLs that BepInEx must see. ZstdSharp.dll is the managed
# zstd port pulled in by Stellar.Infrastructure via NuGet and lands next to
# Infrastructure.dll after a Release build (CopyLocalLockFileAssemblies=true).
FRAMEWORK_DLLS=(
    "$SRC/Stellar.Host/bin/Release/Stellar.Host.dll"
    "$SRC/Stellar.Infrastructure/bin/Release/Stellar.Infrastructure.dll"
    "$SRC/Stellar.Application/bin/Release/Stellar.Application.dll"
    "$SRC/Stellar.Abstractions/bin/Release/Stellar.Abstractions.dll"
    # Shared inter-plugin contracts (IFrozenEntityViewer). Deployed to the framework dir so cooperating plugins
    # resolve ONE copy (correct type identity for IPluginExchange). Built transitively by the sample plugins.
    "$SRC/Stellar.PluginContracts/bin/Release/Stellar.PluginContracts.dll"
    "$SRC/Stellar.Wire/bin/Release/Stellar.Wire.dll"
    "$SRC/Stellar.Infrastructure/bin/Release/ZstdSharp.dll"
)

# User plugins. Each entry is "install-subdir|MODE|path". Sources moved off the
# retired framework/src/samples/ tree onto the two live trees:
#   - MODE=build  : shipping plugins in plugin-repos/Stellar<Name>Plugin/ — their
#                   active dev homes; buildable here (NuGet interop refs). PATH is
#                   the .csproj; the DLL is <csproj-dir>/bin/Release/Stellar.<subdir>.dll
#                   (all set AppendTargetFrameworkToOutputPath=false → flat bin/Release).
#   - MODE=prebuilt: dev-only tools in the plugins/ monorepo (plugins/samples/). That
#                   tree is reference material published as PREBUILT DLLs — it is not
#                   built here (needs a StellarResonanceModSystem sibling + game-interop
#                   props, per plugins/samples/Directory.Build.props). PATH is the DLL.
USER_PLUGINS=(
    "DebugInfo|prebuilt|$DEVKIT_ROOT/plugins/samples/Stellar.DebugInfo/bin/Release/Stellar.DebugInfo.dll"
    "AutoNav|prebuilt|$DEVKIT_ROOT/plugins/samples/Stellar.AutoNav/bin/Release/Stellar.AutoNav.dll"
    "DataInspector|prebuilt|$DEVKIT_ROOT/plugins/samples/Stellar.DataInspector/bin/Release/Stellar.DataInspector.dll"
    "PlayerHUD|build|$DEVKIT_ROOT/plugin-repos/StellarPlayerHUDPlugin/Stellar.PlayerHUD.csproj"
    "CooldownBar|build|$DEVKIT_ROOT/plugin-repos/StellarCooldownBarPlugin/Stellar.CooldownBar.csproj"
    "CombatMeter|build|$DEVKIT_ROOT/plugin-repos/StellarCombatMeterPlugin/Stellar.CombatMeter.csproj"
    "ChatTools|build|$DEVKIT_ROOT/plugin-repos/StellarChatToolsPlugin/Stellar.ChatTools.csproj"
    "StatInspector|build|$DEVKIT_ROOT/plugin-repos/StellarStatInspectorPlugin/Stellar.StatInspector.csproj"
    "ModuleOptimizer|build|$DEVKIT_ROOT/plugin-repos/StellarModuleOptimizerPlugin/Stellar.ModuleOptimizer.csproj"
    "EntityInspector|build|$DEVKIT_ROOT/plugin-repos/StellarEntityInspectorPlugin/Stellar.EntityInspector.csproj"
    "LoadoutSwitcher|build|$DEVKIT_ROOT/plugin-repos/StellarLoadoutSwitcherPlugin/Stellar.LoadoutSwitcher.csproj"
)

# Framework-only mode (STELLAR_FRAMEWORK_ONLY=1): deploy the framework set and touch NOTHING under
# stellar/plugins — no plugin builds, no slot copies, no case-variant/.bak evacuations there. Exists
# because owner-MAIN framework deploys must not overwrite plugin slots whose dev repos have moved
# past the deployed builds (measured 2026-08-23: 6 of 10 MAIN slots diverged from their repo HEADs).
# Emptying USER_PLUGINS gates every plugin loop in one place; the stellar/plugins shadow sweep below
# carries its own guard.
FW_ONLY="${STELLAR_FRAMEWORK_ONLY:-0}"
if [ "$FW_ONLY" = "1" ]; then
    echo "framework-only mode: skipping user plugins (stellar/plugins untouched)"
    USER_PLUGINS=()
fi

# Selective plugin mode (STELLAR_ONLY_PLUGINS="combatmeter[,cooldownbar…]"): deploy ONLY the named
# plugin slots and NOTHING else — the framework slot (build, copy, shadow sweep) is untouched.
# Exists for single-plugin fix deploys to the owner's MAIN client: 6 of 10 plugin repos have moved
# past the deployed builds (measured 2026-08-23) so a full plugin sweep must never run there, and
# rebuilding the framework at a newer tools-commit HEAD would churn its embedded sha for no source
# change. Names are the LOWERCASE slot names, comma- or space-separated. Mutually exclusive with
# STELLAR_FRAMEWORK_ONLY.
ONLY_PLUGINS="${STELLAR_ONLY_PLUGINS:-}"
if [ -n "$ONLY_PLUGINS" ] && [ "$FW_ONLY" = "1" ]; then
    echo "STELLAR_ONLY_PLUGINS and STELLAR_FRAMEWORK_ONLY are mutually exclusive"; exit 2
fi
if [ -n "$ONLY_PLUGINS" ]; then
    echo "selective plugin mode: deploying ONLY [$ONLY_PLUGINS] (framework slot untouched)"
    FILTERED=()
    for entry in "${USER_PLUGINS[@]}"; do
        subdir="${entry%%|*}"
        slot="$(printf '%s' "$subdir" | tr '[:upper:]' '[:lower:]')"
        case ",$(printf '%s' "$ONLY_PLUGINS" | tr ' ' ',')," in
            *",$slot,"*) FILTERED+=("$entry") ;;
        esac
    done
    [ ${#FILTERED[@]} -gt 0 ] || { echo "STELLAR_ONLY_PLUGINS matched no known plugin slot"; exit 2; }
    USER_PLUGINS=("${FILTERED[@]}")
fi

# Resolve a plugin entry's DLL path from its "subdir|mode|path" spec.
# NOTE: separate `local` statements — a single `local a=.. b="${a..}"` line does
# NOT reliably see `a`'s new value, which would silently return an empty path.
plugin_dll() {  # $1 = "subdir|mode|path" -> echoes the expected DLL path
    local subdir="${1%%|*}"
    local rest="${1#*|}"
    local mode="${rest%%|*}"
    local path="${rest#*|}"
    if [ "$mode" = build ]; then
        echo "${path%/*}/bin/Release/Stellar.$subdir.dll"   # ${path%/*} = csproj dir (pure bash, no dirname)
    else
        echo "$path"
    fi
}

# Build Release FIRST so a deploy never ships a stale bin/Release. This script
# only copies DLLs, so without this it silently deploys whatever Release build
# was lying around — the #1 cause of "nothing changed" after install (e.g. when
# the working build was -c Debug). Skip with SKIP_BUILD=1.
DOTNET="${DOTNET:-/home/dorasu/.dotnet/dotnet}"
if [ "${SKIP_BUILD:-0}" != "1" ]; then
    if [ -n "$ONLY_PLUGINS" ]; then
        echo "selective plugin mode: skipping the framework src/ build"
    else
        echo "building framework src/ (Release) before deploy…"
        "$DOTNET" build "$SRC/Stellar.sln" -c Release --nologo -v quiet
    fi
    # build-mode plugins live in their own repos (plugin-repos/), NOT in
    # Stellar.sln — build each csproj individually so a deploy never ships a stale
    # plugin DLL. prebuilt-mode plugins (plugins/ monorepo) ship as published DLLs
    # and are copied as-is (that tree isn't buildable here — see the array comment).
    for entry in "${USER_PLUGINS[@]}"; do
        subdir="${entry%%|*}"; rest="${entry#*|}"; mode="${rest%%|*}"; path="${rest#*|}"
        if [ "$mode" = build ]; then
            echo "building plugin $subdir…"
            "$DOTNET" build "$path" -c Release --nologo -v quiet
        else
            echo "using prebuilt $subdir (plugins/ monorepo — not built from source here)"
        fi
    done
fi

# Sanity check inputs (framework set skipped in selective plugin mode — not deployed).
if [ -z "$ONLY_PLUGINS" ]; then
    for dll in "${FRAMEWORK_DLLS[@]}"; do
        [ -f "$dll" ] || { echo "missing $dll — build src/ first"; exit 1; }
    done
fi
for entry in "${USER_PLUGINS[@]}"; do
    dll="$(plugin_dll "$entry")"
    [ -f "$dll" ] || { echo "missing $dll — build ${entry#*|} first (or unset SKIP_BUILD)"; exit 1; }
done

# Framework directory (BepInEx auto-discovers DLLs here). Skipped entirely in selective plugin
# mode — the deployed framework build must not churn on a plugin-only fix.
FW_DIR="$GAME/BepInEx/plugins/Stellar.Framework"
if [ -z "$ONLY_PLUGINS" ]; then
    mkdir -p "$FW_DIR"

    # Clean prior framework DLLs so a renamed/removed assembly doesn't linger.
    rm -f "$FW_DIR"/*.dll

    for dll in "${FRAMEWORK_DLLS[@]}"; do
        cp -v "$dll" "$FW_DIR/"
    done
fi

# Where evicted shadow copies go. Defined here because the user-plugin loop below already needs it;
# the later shadow-copy guard reuses the same stash (`:=` keeps ONE timestamp for the whole run).
: "${SHADOW_STASH:=$GAME/stellar-backups/evicted-$(date +%Y%m%d-%H%M%S)}"

# A slot dir differing only in CASE is a second live copy of the same plugin id. The framework scans
# with Directory.GetFiles(..., SearchOption.AllDirectories), finds both, and PluginRegistry keeps
# whichever it reached FIRST — logging `duplicate plugin id '<id>'; second registration ignored.` —
# so which build actually runs is ARBITRARY. Owner incident 2026-07-30: the launcher's `combatmeter/`
# and this script's `CombatMeter/` coexisted, leaving a deployed fix untestable (nothing on disk
# revealed which one loaded). The *.bak* glob below cannot catch this; match case-insensitively.
evacuate_case_variants() {  # $1 = scan dir, $2 = canonical (lowercase) slot name
    local scan="$1" slot="$2" d base
    for d in "$scan"/*; do
        [ -d "$d" ] || continue
        base="${d##*/}"
        [ "$base" = "$slot" ] && continue                                   # the canonical target itself
        [ "$(printf '%s' "$base" | tr '[:upper:]' '[:lower:]')" = "$slot" ] || continue
        mkdir -p "$SHADOW_STASH"
        mv "$d" "$SHADOW_STASH/" \
            && echo "SHADOW GUARD: case-variant slot '$base' evacuated -> $SHADOW_STASH/ (canonical is '$slot')"
    done
}

# User plugin folders live outside BepInEx/plugins to keep concerns separate. The slot dir is
# LOWERCASE — the launcher's canonical slot per CLAUDE.md § Game install reference. Only the
# DIRECTORY is lowercased; the DLL keeps its own casing (Stellar.<Subdir>.dll).
for entry in "${USER_PLUGINS[@]}"; do
    subdir="${entry%%|*}"   # first field only (entry = subdir|mode|path)
    dll="$(plugin_dll "$entry")"
    slot="$(printf '%s' "$subdir" | tr '[:upper:]' '[:lower:]')"
    evacuate_case_variants "$GAME/stellar/plugins" "$slot"  # BEFORE writing, else the stale copy shadows it
    PLUGIN_DIR="$GAME/stellar/plugins/$slot"
    mkdir -p "$PLUGIN_DIR"
    cp -v "$dll" "$PLUGIN_DIR/"
done

# Retire the old HelloWorld plugin if it's still around.
if [ -d "$GAME/BepInEx/plugins/Stellar.HelloWorld" ]; then
    rm -rf "$GAME/BepInEx/plugins/Stellar.HelloWorld"
    echo "removed legacy Stellar.HelloWorld plugin"
fi

# ---- Shadow-copy guard (P0). BepInEx recursively scans BepInEx/plugins and the framework
# scans stellar/plugins; each loads exactly ONE plugin per GUID+version, and when several
# copies share a version it picks ONE ARBITRARILY. A stale Stellar.Framework.bak-*/<slot>.bak-*
# dir left in a scan path can therefore SHADOW the build we just deployed — the fix runs in
# nobody's client while the sha1 on disk "proves" it is deployed. This hid the wire-position
# fallback for a whole owner session (run WkOzO9KMOY). Evacuate every shadow copy OUT of both
# scan paths. Moves, never deletes — backups survive, just outside the scan path.
: "${SHADOW_STASH:=$GAME/stellar-backups/evicted-$(date +%Y%m%d-%H%M%S)}"   # set above; keep one stash per run
evacuate_shadows() {
    for d in "$1"/$2; do
        [ -d "$d" ] || continue          # unmatched glob stays literal / non-dir → skip
        mkdir -p "$SHADOW_STASH"
        mv "$d" "$SHADOW_STASH/" && echo "SHADOW GUARD: evacuated $d -> $SHADOW_STASH/"
    done
}
# Framework scan path: the ONLY valid dir is exactly Stellar.Framework; any suffixed variant shadows it.
# Skipped in selective plugin mode — nothing under BepInEx/plugins may change on a plugin-only deploy.
[ -n "$ONLY_PLUGINS" ] || evacuate_shadows "$GAME/BepInEx/plugins" "Stellar.Framework.*"
# User-plugin scan path: any *.bak* slot shadows the live plugin folder. Skipped in
# framework-only mode — that tree must not be read or written at all.
[ "$FW_ONLY" = "1" ] || evacuate_shadows "$GAME/stellar/plugins" "*.bak*"
if [ -d "$SHADOW_STASH" ]; then
    echo "SHADOW GUARD: moved shadow plugin copies out of the BepInEx/framework scan paths."
    echo "             They could have been loaded INSTEAD of the build just deployed. See $SHADOW_STASH"
fi

# ---- Apply MODE: stellar_perf.flags (game cwd = game_mini) + BepInEx.cfg logging ----
FLAGS="$GAME/stellar_perf.flags"
CFG="$GAME/BepInEx/config/BepInEx.cfg"
case "$MODE" in
    prod) rm -f "$FLAGS"; FLUSH=false; CONSOLE=false ;;                    # ship: no console window, buffered logging
    test) printf 'DIAGNOSTICS\n' > "$FLAGS"; FLUSH=true; CONSOLE=true ;;   # debug: console + diagnostics + crash-flush logging
    perf) printf 'NO_OVERLAY\nUNCAP\n' > "$FLAGS"; FLUSH=false; CONSOLE=false ;;  # measure: IMGUI off, uncapped fps, no console
esac
if [ -f "$CFG" ]; then
    sed -i "s/^InstantFlushing = .*/InstantFlushing = $FLUSH/" "$CFG"
    sed -i "s/^UnityLogListening = .*/UnityLogListening = false/" "$CFG"   # the Unity-log hook: always off (perf)
    # BepInEx console window: off for prod/perf — under Wine each log line is a GDI redraw
    # (a real per-frame perf cost), and it's noise on a shipping build. Scoped to the
    # [Logging.Console] section so it never touches [Logging.Disk]'s own Enabled key.
    sed -i "/^\[Logging\.Console\]/,/^\[/ s/^Enabled = .*/Enabled = $CONSOLE/" "$CFG"
fi

# Truncate the log so the next launch starts clean.
: > "$GAME/BepInEx/LogOutput.log"

cat <<EOF

Deployed src/ build — MODE=$MODE$([ "$FW_ONLY" = "1" ] && printf ' (framework-only: user plugins untouched)')$([ -n "$ONLY_PLUGINS" ] && printf ' (selective plugins: %s — framework slot untouched)' "$ONLY_PLUGINS")
  framework DLLs -> $([ -n "$ONLY_PLUGINS" ] && echo "UNTOUCHED (selective plugin mode)" || echo "$FW_DIR")
  flags          -> $([ -f "$FLAGS" ] && tr '\n' ' ' < "$FLAGS" || echo '(none)')
  BepInEx log    -> InstantFlushing=$FLUSH, UnityLogListening=false, Console=$CONSOLE
  log truncated. Fully close + relaunch via Heroic.

Modes: prod (ship/fast) · test (diagnostics+crash log) · perf (IMGUI off + uncapped)
Rollback to the legacy framework with: tools/install-framework.sh
EOF
