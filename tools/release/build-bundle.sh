#!/usr/bin/env bash
# Assemble the drop-in Stellar framework bundle and zip it. Self-contained in the
# framework repo so CI can cut a release (see .github/workflows/release.yml).
#
#   tools/release/build-bundle.sh <version> [out_dir]
#
# Builds the WHOLE solution against the committed interop reference stubs (refs/,
# see tools/gen-refs.sh) by default, so no game install is needed. Override
# GameInterop/BepInExCore to build against a real install locally.
#
# The BepInEx loader files (winhttp.dll, doorstop, BepInEx/, dotnet/) come from an
# extracted BepInEx-Unity.IL2CPP stage. Point STELLAR_BEPINEX_STAGE at it; CI
# restores it from STELLAR_BEPINEX_STAGE_URL.
set -euo pipefail

VERSION="${1:?usage: build-bundle.sh <version> [out_dir]}"
OUT_DIR="${2:-dist}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC="$ROOT/src"
STAGE_SRC="${STELLAR_BEPINEX_STAGE:?set STELLAR_BEPINEX_STAGE to an extracted BepInEx IL2CPP stage}"
DOTNET="${DOTNET:-dotnet}"
GAME_INTEROP="${GameInterop:-$ROOT/refs}"
BEPINEX_CORE="${BepInExCore:-$ROOT/refs}"

[ -d "$STAGE_SRC" ] || { echo "BepInEx stage not found: $STAGE_SRC"; exit 1; }

echo "building framework (Release) against ${GAME_INTEROP}…"
# Build the runtime projects only — Host pulls in Infrastructure/Application/Wire/
# Abstractions; PluginContracts builds separately. NOT the whole sln: its test
# projects reference plugin samples (src/samples symlink) absent on CI.
"$DOTNET" build "$SRC/Stellar.Host/Stellar.Host.csproj" -c Release --nologo -v quiet \
  -p:GameInterop="$GAME_INTEROP" -p:BepInExCore="$BEPINEX_CORE"
"$DOTNET" build "$SRC/Stellar.PluginContracts/Stellar.PluginContracts.csproj" -c Release --nologo -v quiet

BUNDLE="$OUT_DIR/bundle"
rm -rf "$BUNDLE"; mkdir -p "$BUNDLE"

# 1) BepInEx loader (version-agnostic drop-in)
cp "$STAGE_SRC/winhttp.dll" "$STAGE_SRC/doorstop_config.ini" \
   "$STAGE_SRC/.doorstop_version" "$BUNDLE/"
cp -r "$STAGE_SRC/BepInEx" "$STAGE_SRC/dotnet" "$BUNDLE/"
# default = modded
sed -i "/^\[General\]/,/^\[/ s/^enabled = .*/enabled = true/" "$BUNDLE/doorstop_config.ini"

# 2) framework DLLs (Stellar.PluginContracts ships too — cooperating plugins
#    reference it at runtime; omitting it silently breaks those plugins on load).
FW_DIR="$BUNDLE/BepInEx/plugins/Stellar.Framework"; mkdir -p "$FW_DIR"
for dll in Stellar.Host Stellar.Infrastructure Stellar.Application \
           Stellar.Abstractions Stellar.Wire Stellar.PluginContracts; do
    cp "$SRC/$dll/bin/Release/$dll.dll" "$FW_DIR/"
done
cp "$SRC/Stellar.Infrastructure/bin/Release/ZstdSharp.dll" "$FW_DIR/"

# 3) (plugins ship via the registry the launcher reads — the bundle is framework-only.)

# 4) zip contents at root (python zipfile — abs-path safe, no external 'zip' dep).
#    DETERMINISTIC: sorted entries + fixed mtime/perms so identical content always
#    yields a byte-identical zip (stable sha256). This is what keeps version.json's
#    recorded sha256 in lockstep with the served bundle across rebuilds — a mismatch
#    is exactly what makes the launcher hang at "downloading 100%".
ZIP="$OUT_DIR/Stellar-$VERSION.zip"
rm -f "$ZIP"
PY="${PYTHON:-python3}"
"$PY" - "$BUNDLE" "$ZIP" <<'PYEOF'
import os, sys, zipfile
src, out = sys.argv[1], os.path.abspath(sys.argv[2])
paths = []
for root, dirs, files in os.walk(src):
    dirs.sort()
    for f in files:
        full = os.path.join(root, f)
        paths.append((full, os.path.relpath(full, src)))
paths.sort(key=lambda p: p[1])          # stable entry order regardless of FS walk order
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for full, arc in paths:
        zi = zipfile.ZipInfo(arc, date_time=(1980, 1, 1, 0, 0, 0))   # fixed mtime (zip epoch)
        zi.compress_type = zipfile.ZIP_DEFLATED
        zi.external_attr = 0o644 << 16                                # fixed perms
        with open(full, "rb") as fh:
            z.writestr(zi, fh.read())
print("bundle ->", out)
PYEOF

# 5) Guard: the framework ships NO bundled plugins (they install via the registry).
LEAK="$("$PY" - "$ZIP" <<'PYEOF'
import sys, zipfile
print("\n".join(n for n in zipfile.ZipFile(sys.argv[1]).namelist() if n.startswith("stellar/")))
PYEOF
)"
if [ -n "$LEAK" ]; then
    echo "ERROR: bundle contains stellar/ entries — the framework must ship no plugins:" >&2
    echo "$LEAK" >&2
    exit 1
fi
echo "guard ok: no bundled plugins"
