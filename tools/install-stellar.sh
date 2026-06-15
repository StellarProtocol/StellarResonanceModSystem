#!/usr/bin/env bash
# Deploy the layered src/ build (Stellar.* DLLs) to the game's test prefix.
# Replaces the legacy framework/ build in BepInEx/plugins/Stellar.Framework/.
#
# Roll back to the legacy build with: tools/install-framework.sh
set -euo pipefail

# Target prefix is overridable so we can deploy to the MAIN client
# (STELLAR_PREFIX=/opt/game/BlueProtocol) as well as the legacy test prefix.
STELLAR_PREFIX="${STELLAR_PREFIX:-/opt/game/BlueProtocol2}"
GAME="$STELLAR_PREFIX/drive_c/Star/StarLauncher/game/release_2.11/game_mini"

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

# Layered framework DLLs that BepInEx must see. ZstdSharp.dll is the managed
# zstd port pulled in by Stellar.Infrastructure via NuGet and lands next to
# Infrastructure.dll after a Release build (CopyLocalLockFileAssemblies=true).
FRAMEWORK_DLLS=(
    "$SRC/Stellar.Host/bin/Release/Stellar.Host.dll"
    "$SRC/Stellar.Infrastructure/bin/Release/Stellar.Infrastructure.dll"
    "$SRC/Stellar.Application/bin/Release/Stellar.Application.dll"
    "$SRC/Stellar.Abstractions/bin/Release/Stellar.Abstractions.dll"
    "$SRC/Stellar.Wire/bin/Release/Stellar.Wire.dll"
    "$SRC/Stellar.Infrastructure/bin/Release/ZstdSharp.dll"
)

# Sample user plugins. Each entry is "subdir-name|path/to/dll".
USER_PLUGINS=(
    "DebugInfo|$SRC/samples/Stellar.DebugInfo/bin/Release/Stellar.DebugInfo.dll"
    "AutoNav|$SRC/samples/Stellar.AutoNav/bin/Release/Stellar.AutoNav.dll"
    "PlayerHUD|$SRC/samples/Stellar.PlayerHUD/bin/Release/Stellar.PlayerHUD.dll"
    "CooldownBar|$SRC/samples/Stellar.CooldownBar/bin/Release/Stellar.CooldownBar.dll"
    "CombatMeter|$SRC/samples/Stellar.CombatMeter/bin/Release/Stellar.CombatMeter.dll"
    "ChatTools|$SRC/samples/Stellar.ChatTools/bin/Release/Stellar.ChatTools.dll"
    "DataInspector|$SRC/samples/Stellar.DataInspector/bin/Release/Stellar.DataInspector.dll"
    "StatInspector|$SRC/samples/Stellar.StatInspector/bin/Release/Stellar.StatInspector.dll"
    "ModuleOptimizer|$SRC/samples/Stellar.ModuleOptimizer/bin/Release/Stellar.ModuleOptimizer.dll"
    "EntityInspector|$SRC/samples/Stellar.EntityInspector/bin/Release/Stellar.EntityInspector.dll"
)

# Build Release FIRST so a deploy never ships a stale bin/Release. This script
# only copies DLLs, so without this it silently deploys whatever Release build
# was lying around — the #1 cause of "nothing changed" after install (e.g. when
# the working build was -c Debug). Skip with SKIP_BUILD=1.
DOTNET="${DOTNET:-(local reference)"
if [ "${SKIP_BUILD:-0}" != "1" ]; then
    echo "building src/ (Release) before deploy…"
    "$DOTNET" build "$SRC/Stellar.sln" -c Release --nologo -v quiet
fi

# Sanity check inputs.
for dll in "${FRAMEWORK_DLLS[@]}"; do
    [ -f "$dll" ] || { echo "missing $dll — build src/ first"; exit 1; }
done
for entry in "${USER_PLUGINS[@]}"; do
    dll="${entry#*|}"
    [ -f "$dll" ] || { echo "missing $dll — build src/ first"; exit 1; }
done

# Framework directory (BepInEx auto-discovers DLLs here).
FW_DIR="$GAME/BepInEx/plugins/Stellar.Framework"
mkdir -p "$FW_DIR"

# Clean prior framework DLLs so a renamed/removed assembly doesn't linger.
rm -f "$FW_DIR"/*.dll

for dll in "${FRAMEWORK_DLLS[@]}"; do
    cp -v "$dll" "$FW_DIR/"
done

# User plugin folders live outside BepInEx/plugins to keep concerns separate.
for entry in "${USER_PLUGINS[@]}"; do
    subdir="${entry%|*}"
    dll="${entry#*|}"
    PLUGIN_DIR="$GAME/stellar/plugins/$subdir"
    mkdir -p "$PLUGIN_DIR"
    cp -v "$dll" "$PLUGIN_DIR/"
done

# Retire the old HelloWorld plugin if it's still around.
if [ -d "$GAME/BepInEx/plugins/Stellar.HelloWorld" ]; then
    rm -rf "$GAME/BepInEx/plugins/Stellar.HelloWorld"
    echo "removed legacy Stellar.HelloWorld plugin"
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

Deployed src/ build — MODE=$MODE
  framework DLLs -> $FW_DIR
  flags          -> $([ -f "$FLAGS" ] && tr '\n' ' ' < "$FLAGS" || echo '(none)')
  BepInEx log    -> InstantFlushing=$FLUSH, UnityLogListening=false, Console=$CONSOLE
  log truncated. Fully close + relaunch via Heroic.

Modes: prod (ship/fast) · test (diagnostics+crash log) · perf (IMGUI off + uncapped)
Rollback to the legacy framework with: tools/install-framework.sh
EOF
