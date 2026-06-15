#!/usr/bin/env bash
# Install BepInEx 6 IL2CPP into the test prefix's game folder.
# Idempotent — safe to re-run. Deploys the loader only; framework + plugins
# go in via tools/install-stellar.sh after this.
set -euo pipefail

SRC="(local reference)"
# Target prefix is overridable so we can deploy to the MAIN client
# (STELLAR_PREFIX=/opt/game/BlueProtocol) as well as the legacy test prefix.
# Default keeps the documented test-prefix workflow.
PREFIX="${STELLAR_PREFIX:-/opt/game/BlueProtocol2}"
DEST="$PREFIX/drive_c/Star/StarLauncher/game/release_2.11/game_mini"

[ -d "$SRC"  ] || { echo "missing $SRC — re-extract tools/BepInEx-IL2CPP.zip"; exit 1; }
[ -d "$DEST" ] || { echo "missing $DEST — wrong game version?"; exit 1; }

echo "Copying BepInEx loader to $DEST ..."
cp -v "$SRC/winhttp.dll" "$SRC/doorstop_config.ini" "$SRC/.doorstop_version" "$SRC/changelog.txt" "$DEST/"
cp -rv "$SRC/BepInEx" "$SRC/dotnet" "$DEST/"

# --- Perf config (measured 2026-06-04, docs/perf-finding-bepinex-loglistener-2026-06-04.md):
# BepInEx's Unity log listener fires a managed callback for EVERY game Debug.Log; the game
# logs heavily in-world, costing ~25 fps / ~1.8 ms. Disabling it makes loaded-BepInEx ~free
# (== vanilla). We also disable the console + disk sinks here. UnityLogListening does NOT
# affect Stellar's own logging; with the disk sink off you lose the on-disk [Perf] log (the
# Shift+End overlay still works) — flip [Logging.Disk] Enabled back to true for diagnostics
# (cost ~0 once the listener is off). Idempotent + section-aware; seeds the keys if the cfg
# has not been generated yet (BepInEx fills the remaining defaults on first run).
CFG="$DEST/BepInEx/config/BepInEx.cfg"
mkdir -p "$(dirname "$CFG")"
if [ ! -f "$CFG" ]; then
    printf '[Logging]\nUnityLogListening = false\n\n[Logging.Console]\nEnabled = false\n\n[Logging.Disk]\nEnabled = false\n' > "$CFG"
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
set_cfg Logging.Disk    Enabled           false
echo "perf: BepInEx.cfg — UnityLogListening + console + disk logging disabled (+~25 fps in-world)"

cat <<EOF

BepInEx installed. Next:
  tools/install-stellar.sh    # deploy the framework + sample plugin

To uninstall BepInEx entirely:
  cd $DEST && rm -f winhttp.dll doorstop_config.ini .doorstop_version changelog.txt && rm -rf BepInEx dotnet
EOF
