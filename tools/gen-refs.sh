#!/usr/bin/env bash
# Regenerate the interop reference stubs under refs/.
#
#   tools/gen-refs.sh <game_mini-dir> [out-dir]
#
# Reference stubs let CI build Infrastructure/Host (and the plugins, which reuse
# these via a framework checkout) WITHOUT the game's proprietary IL2CPP interop:
# they carry the public API surface only — no method bodies, no game IL/metadata,
# and no Panda.* (game logic) types, which the framework binds by reflection at
# runtime, never at compile time.
#
# MUST be re-run after any game/engine patch that regenerates the interop
# assemblies (do it as part of /recon): a stale stub is the one real risk — an
# ADDED member fails the build loudly (caught), but a CHANGED/REMOVED signature
# could compile green yet break at runtime. Commit the refreshed refs/.
#
# Requires the Refasmer CLI tool:
#   dotnet tool install -g JetBrains.Refasmer.CliTool      # provides `refasmer`
set -euo pipefail

GAME_MINI="${1:?usage: gen-refs.sh <game_mini-dir> [out-dir]}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${2:-$ROOT/refs}"
INTEROP="$GAME_MINI/BepInEx/interop"
CORE="$GAME_MINI/BepInEx/core"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
REFASMER="${REFASMER:-$DOTNET_ROOT/tools/refasmer}"
[ -x "$REFASMER" ] || { echo "refasmer not found at $REFASMER — dotnet tool install -g JetBrains.Refasmer.CliTool"; exit 1; }
[ -d "$INTEROP" ] || { echo "interop dir not found: $INTEROP"; exit 1; }

# Union of every interop assembly referenced by Infrastructure/Host (16) — a
# superset of what the sample plugins reference, so plugin CI reuses these.
INTEROP_DLLS="Il2Cppmscorlib Il2CppSystem UnityEngine UnityEngine.CoreModule \
  UnityEngine.IMGUIModule UnityEngine.InputLegacyModule UnityEngine.TextRenderingModule \
  UnityEngine.ImageConversionModule UnityEngine.UI UnityEngine.UIModule Unity.TextMeshPro"
CORE_DLLS="Il2CppInterop.Runtime Il2CppInterop.Common BepInEx.Core BepInEx.Unity.IL2CPP 0Harmony"

rm -rf "$OUT"; mkdir -p "$OUT"
for d in $INTEROP_DLLS; do "$REFASMER" --all -O "$OUT" "$INTEROP/$d.dll"; done
for d in $CORE_DLLS;    do "$REFASMER" --all -O "$OUT" "$CORE/$d.dll"; done
echo "regenerated $(ls "$OUT"/*.dll | wc -l) stubs in $OUT"
