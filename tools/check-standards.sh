#!/usr/bin/env bash
# Mechanical enforcement of rules in docs/coding-standards.md.
# Runs as a PostToolUse hook and as a pre-commit check.
#
# Rules enforced (each row = one rule, with severity):
#   > 800 LoC file                                              blocker
#   > 500 LoC file                                              major
#   block-scoped namespace                                      blocker
#   `using BepInEx|HarmonyLib|UnityEngine|Il2Cpp|Panda` in
#       Abstractions/ or Application/                           blocker
#   IMGUI token (GUILayout|GUIStyle|GUIContent|GUI.) in
#       Abstractions/ (the plugin contract is uGUI-only)        blocker
#   inline `if (StellarDiagnostics.IsEnabled)` outside
#       *.Diagnostics.cs                                        major
#   `PluginServices.Get<` / `Service.Instance.` /
#       service-locator patterns                                major
#   `(class|interface) ...Manager`                              minor
#   `public interface [A-HJ-Z]` (interface missing I prefix)    minor
#   `\sNEW_PUBLIC_FIELD` (heuristic)                            minor
#
# Method-size (> 50 / > 100 LoC) is NOT enforced here — robust brace-aware
# parsing in shell is error-prone. Use the qa agent + code-review-graph's
# find_large_functions_tool for that.
#
# Exit code:
#   0 = zero blockers and zero majors (minors are reported but don't fail)
#   N = number of blockers + majors found
#
# Invocation:
#   - bash tools/check-standards.sh           # scan all src/
#   - bash tools/check-standards.sh FILE1 …   # scan only specific files
#                                              (used by the PostToolUse hook)

set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
blocker_count=0
major_count=0
minor_count=0

# Build the file list. If args provided, use them (filtered to .cs under src/).
# Otherwise scan all .cs under src/.
files=()
if [ "$#" -gt 0 ]; then
    for arg in "$@"; do
        # Resolve to absolute; skip non-existent or non-.cs.
        if [[ "$arg" != /* ]]; then
            arg="$ROOT/$arg"
        fi
        if [ -f "$arg" ] && [[ "$arg" == *.cs ]] && [[ "$arg" == "$ROOT"/src/* ]]; then
            files+=("$arg")
        fi
    done
    if [ "${#files[@]}" -eq 0 ]; then
        # Args provided but none matched — nothing to check, exit clean.
        exit 0
    fi
else
    while IFS= read -r f; do
        files+=("$f")
    done < <(/usr/bin/find "$ROOT/src" -name '*.cs' -not -path '*/bin/*' -not -path '*/obj/*')
fi

report() {
    local severity=$1 file=$2 message=$3
    case "$severity" in
        blocker) echo "BLOCKER: $file — $message"; blocker_count=$((blocker_count + 1)) ;;
        major)   echo "major:   $file — $message"; major_count=$((major_count + 1)) ;;
        minor)   echo "minor:   $file — $message"; minor_count=$((minor_count + 1)) ;;
    esac
}

for f in "${files[@]}"; do
    relpath="${f#$ROOT/}"

    # File size
    lines=$(wc -l < "$f")
    if [ "$lines" -gt 800 ]; then
        report blocker "$relpath" "$lines LoC (> 800)"
    elif [ "$lines" -gt 500 ]; then
        report major "$relpath" "$lines LoC (> 500)"
    fi

    # Block-scoped namespace (must be file-scoped)
    if grep -qE '^namespace [A-Za-z0-9_.]+[[:space:]]*\{' "$f"; then
        report blocker "$relpath" "block-scoped namespace; use file-scoped"
    fi

    # Layer dependency rule — Abstractions and Application may not reference
    # BepInEx / HarmonyX / Unity / Il2Cpp / Panda.
    if [[ "$relpath" == src/Stellar.Abstractions/* || "$relpath" == src/Stellar.Application/* ]]; then
        bad=$(grep -nE '^using[[:space:]]+(BepInEx|HarmonyLib|0Harmony|UnityEngine|Il2Cpp|Panda)' "$f" || true)
        if [ -n "$bad" ]; then
            while IFS= read -r line; do
                report blocker "$relpath" "forbidden cross-layer ref: $line"
            done <<< "$bad"
        fi
    fi

    # IMGUI tokens forbidden in the plugin-contract assembly (Abstractions is
    # uGUI-only since Phase E; Great Cleanup Phase 2 sketch (e)). Catches code
    # use and doc-comment mentions of the deleted IMGUI surface.
    if [[ "$relpath" == src/Stellar.Abstractions/* ]]; then
        if grep -qE '\b(GUILayout|GUIStyle|GUIContent)\b|\bGUI\.' "$f"; then
            report blocker "$relpath" "IMGUI token (GUILayout/GUIStyle/GUIContent/GUI.) in plugin contract; Abstractions is uGUI-only"
        fi
    fi

    # Phase-named partials banned — use feature-named Wiring.*.cs instead.
    # Matches any *.Phase[0-9]*.cs file under src/ (e.g. BootstrapPlugin.Phase8.cs).
    if [[ "$f" =~ \.Phase[0-9] ]]; then
        report blocker "$relpath" "phase-named partial; use feature-named Wiring.*.cs"
    fi

    # Inline diagnostics in production files (must be in *.Diagnostics.cs)
    if [[ "$relpath" != *.Diagnostics.cs ]]; then
        if grep -qE 'if[[:space:]]*\(\s*StellarDiagnostics\.IsEnabled' "$f"; then
            report major "$relpath" "inline StellarDiagnostics.IsEnabled gate; move to .Diagnostics.cs partial"
        fi
    fi

    # Service locator patterns
    if grep -qE '(PluginServices\.Get<|Service\.Instance\.|\bGetService<)' "$f"; then
        # Whitelist: the IPluginServices aggregator itself, and Host composition root.
        if [[ "$relpath" != src/Stellar.Host/* && "$relpath" != src/Stellar.Abstractions/IPluginServices.cs ]]; then
            report major "$relpath" "service-locator pattern (PluginServices.Get / Service.Instance / GetService<>)"
        fi
    fi

    # Manager suffix forbidden
    if grep -qE '\b(class|interface|struct)[[:space:]]+[A-Za-z_][A-Za-z0-9_]*Manager\b' "$f"; then
        report minor "$relpath" "'Manager' type suffix; use Service / Host / Coordinator / Registry / Repository / Factory"
    fi

    # Interface missing I-prefix
    if grep -qE '^[[:space:]]*(public|internal)[[:space:]]+(sealed[[:space:]]+|partial[[:space:]]+)?interface[[:space:]]+[A-HJ-Z]' "$f"; then
        report minor "$relpath" "interface missing 'I' prefix"
    fi

    # Public fields (rough heuristic — match `public TYPE _name` or `public TYPE Name` but exclude properties, methods, classes, constants).
    # Triggers on `public int foo;` style declarations.
    if grep -qE '^[[:space:]]*public[[:space:]]+[A-Za-z_][A-Za-z0-9_<>,?\[\]]*[[:space:]]+[A-Za-z_][A-Za-z0-9_]*[[:space:]]*;' "$f" \
       | head -1 > /dev/null; then
        # Re-check with negative filter to skip false positives.
        if grep -nE '^[[:space:]]*public[[:space:]]+[A-Za-z_][A-Za-z0-9_<>,?\[\]]*[[:space:]]+[A-Za-z_][A-Za-z0-9_]*[[:space:]]*;' "$f" \
           | grep -vE '(class|interface|struct|enum|event|delegate|const|readonly|=>|=)' > /dev/null; then
            # Still might be false positive (e.g. `public partial class Foo;` in C# 12);
            # leave as minor.
            report minor "$relpath" "possible public field declaration; use properties"
        fi
    fi
done

echo
echo "Summary: $blocker_count blocker(s), $major_count major(s), $minor_count minor(s)"
# Blockers AND majors fail the gate (minors are advisory). See the conformance
# plan 2026-05-28: majors (e.g. >500 LoC file) now block commits.
exit $((blocker_count + major_count))
