#!/usr/bin/env bash
# Install the BepInEx 6 IL2CPP loader into a game folder (game_mini). Idempotent — safe to re-run.
# Installs the loader only; the framework + plugins go in via tools/install-stellar.sh afterwards.
#
#   tools/install-bepinex.sh                         # auto-detect the game folder under STELLAR_PREFIX
#   GAME_RELEASE=/path/to/game_mini tools/install-bepinex.sh
#   BEPINEX_DISK_LOG=1 tools/install-bepinex.sh      # keep BepInEx/LogOutput.log (recommended while developing)
#
# Environment:
#   GAME_RELEASE     absolute path to game_mini (wins over auto-detection)
#   STELLAR_PREFIX   Wine prefix to search (default /opt/game/BlueProtocol2); the highest
#                    drive_c/Star/StarLauncher/game/release_*/game_mini is used — same rule as install-stellar.sh
#   BEPINEX_STAGE    an already-extracted BepInEx build (default tools/BepInEx-stage, which
#                    tools/setup-dev-env.sh creates); downloaded + extracted here if missing
#   BEPINEX_ZIP      the pinned BepInEx archive (default tools/BepInEx-IL2CPP.zip) — point it at a copy you
#                    downloaded yourself if builds.bepinex.dev keeps dropping the connection
#   BEPINEX_DISK_LOG 1 = write BepInEx/LogOutput.log; default 0 (off — see the perf note below)
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Pinned loader build — keep in sync with tools/setup-dev-env.sh (BEPINEX_BUILD / BEPINEX_FULL).
BEPINEX_BUILD="755"
BEPINEX_FULL="6.0.0-be.${BEPINEX_BUILD}+3fab71a"
BEPINEX_URL="https://builds.bepinex.dev/projects/bepinex_be/${BEPINEX_BUILD}/BepInEx-Unity.IL2CPP-win-x64-${BEPINEX_FULL}.zip"
BEPINEX_ZIP="${BEPINEX_ZIP:-$REPO/tools/BepInEx-IL2CPP.zip}"
SRC="${BEPINEX_STAGE:-$REPO/tools/BepInEx-stage}"
DISK_LOG="${BEPINEX_DISK_LOG:-0}"

# ---- Target: game_mini (explicit, or the highest release_* under the prefix) ----
PREFIX="${STELLAR_PREFIX:-/opt/game/BlueProtocol2}"
# `|| true`: under pipefail a no-match ls would otherwise abort here silently, before the helpful message below.
DEST="${GAME_RELEASE:-$( { ls -d "$PREFIX"/drive_c/Star/StarLauncher/game/release_*/game_mini 2>/dev/null || true; } | sort -V | tail -1)}"
[ -n "$DEST" ] && [ -d "$DEST" ] || { echo "no game folder found — set GAME_RELEASE=/path/to/game_mini (searched $PREFIX/drive_c/Star/StarLauncher/game/release_*/game_mini)"; exit 1; }

# ---- Loader source: the extracted stage, fetched on demand ----
if [ ! -f "$SRC/winhttp.dll" ] || [ ! -d "$SRC/BepInEx" ]; then
    if [ ! -f "$BEPINEX_ZIP" ]; then
        echo "Downloading BepInEx ${BEPINEX_FULL} ..."
        # builds.bepinex.dev often drops long transfers: RESUME a partial file (kept, gitignored, so a re-run
        # continues where it stopped) and only move a verified archive into place.
        PART="$BEPINEX_ZIP.part"
        for attempt in 1 2 3 4 5 6; do
            curl -sSL --fail -C - --retry 3 --retry-all-errors --retry-delay 2 "$BEPINEX_URL" -o "$PART" && break
            echo "  download interrupted (attempt $attempt, $(stat -c %s "$PART" 2>/dev/null || echo 0) bytes so far) — resuming"
        done
        if ! unzip -tq "$PART" >/dev/null 2>&1; then
            echo "Could not download a complete BepInEx archive. Re-run to resume, or download it yourself:"
            echo "  $BEPINEX_URL"
            echo "and run again with BEPINEX_ZIP=/path/to/that.zip"
            exit 1
        fi
        mv "$PART" "$BEPINEX_ZIP"
    fi
    echo "Extracting $BEPINEX_ZIP -> $SRC"
    mkdir -p "$SRC"
    unzip -q -o "$BEPINEX_ZIP" -d "$SRC"
fi
[ -f "$SRC/winhttp.dll" ] && [ -d "$SRC/BepInEx" ] || { echo "BepInEx stage at $SRC is incomplete (no winhttp.dll / BepInEx/)"; exit 1; }

echo "Installing BepInEx ${BEPINEX_FULL} into $DEST ..."
for f in winhttp.dll doorstop_config.ini .doorstop_version changelog.txt; do
    [ -e "$SRC/$f" ] && cp "$SRC/$f" "$DEST/"
done
cp -r "$SRC/BepInEx" "$DEST/"
[ -d "$SRC/dotnet" ] && cp -r "$SRC/dotnet" "$DEST/"

# --- Perf config (measured 2026-06-04): BepInEx's Unity log listener fires a managed callback for EVERY game
# Debug.Log; the game logs heavily in-world, costing ~25 fps / ~1.8 ms. It is always turned off here — Stellar's
# own logging does not use it. The console window is also off (under Wine every line is a redraw). The disk
# sink (BepInEx/LogOutput.log) costs ~0 once the listener is off; it stays off by default and BEPINEX_DISK_LOG=1
# turns it on — you want it while developing, since every guide says "check LogOutput.log".
# Idempotent + section-aware; seeds the keys if BepInEx has not generated its cfg yet (it fills the rest on
# first run).
CFG="$DEST/BepInEx/config/BepInEx.cfg"
mkdir -p "$(dirname "$CFG")"
[ "$DISK_LOG" = "1" ] && DISK=true || DISK=false
if [ ! -f "$CFG" ]; then
    printf '[Logging]\nUnityLogListening = false\n\n[Logging.Console]\nEnabled = false\n\n[Logging.Disk]\nEnabled = %s\n' "$DISK" > "$CFG"
fi
set_cfg() {   # $1=section  $2=key  $3=value — set key only within [section]
    # CRLF-tolerant (BepInEx writes the cfg with Windows line endings under wine):
    # strip \r for matching, re-emit the changed line with \r\n to preserve the file's EOLs.
    awk -v sec="[$1]" -v key="$2" -v val="$3" '
        { line=$0; sub(/\r$/,"",line) }
        line ~ /^\[/ { cur=line }
        (cur==sec && line ~ ("^"key" *=")) { printf "%s = %s\r\n", key, val; next }
        { print }
    ' "$CFG" > "$CFG.tmp" && mv "$CFG.tmp" "$CFG"
}
set_cfg Logging         UnityLogListening false
set_cfg Logging.Console Enabled           false
set_cfg Logging.Disk    Enabled           "$DISK"
echo "BepInEx.cfg: UnityLogListening=false, console=false, disk log (LogOutput.log)=$DISK"
[ "$DISK" = "false" ] && echo "  (no LogOutput.log will be written — re-run with BEPINEX_DISK_LOG=1 to enable it)"

cat <<EOF

BepInEx installed. Next:
  - Linux/Wine: add WINEDLLOVERRIDES=winhttp=n,b to the game's launch environment.
  - Launch the game once so BepInEx generates BepInEx/interop/, then deploy the framework:
      GAME_RELEASE="$DEST" STELLAR_FRAMEWORK_ONLY=1 tools/install-stellar.sh

To uninstall BepInEx entirely:
  cd "$DEST" && rm -f winhttp.dll doorstop_config.ini .doorstop_version changelog.txt && rm -rf BepInEx dotnet
EOF
