#!/usr/bin/env bash
# Regenerate the architecture diagrams from docs/diagrams/src/*.json.
#
#   docs/diagrams/build.sh            # validate + render interactive HTML into docs/diagrams/out/
#   docs/diagrams/build.sh --svg      # ...and refresh the committed static SVGs (needs Google Chrome)
#
# Requires Node 22+ and Archify (https://github.com/tt-a1i/archify, MIT). Point ARCHIFY at its
# bin/archify.mjs if it is not installed at the default skill path.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
ARCHIFY="${ARCHIFY:-$HOME/.agents/skills/archify/bin/archify.mjs}"
OUT="$HERE/out"
mkdir -p "$OUT"

[ -f "$ARCHIFY" ] || { echo "Archify not found at $ARCHIFY — set ARCHIFY=/path/to/archify/bin/archify.mjs" >&2; exit 1; }

for src in "$HERE"/src/*.json; do
    file="$(basename "$src" .json)"      # e.g. framework-layers.architecture
    name="${file%.*}"                    # framework-layers
    type="${file##*.}"                   # architecture
    extra=()
    # Architecture sources pin repository evidence (clickable source links); verify it against this repo.
    if [ "$type" = architecture ] && grep -q '"repository"' "$src"; then extra=(--repo-root "$REPO"); fi
    echo "== $name ($type)"
    node "$ARCHIFY" deliver "$type" "$src" "$OUT/$name.html" --quality showcase "${extra[@]}"
done

if [ "${1:-}" = "--svg" ]; then
    node "$HERE/export-svg.mjs" "$OUT" "$HERE"
fi
