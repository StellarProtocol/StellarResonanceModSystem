#!/usr/bin/env bash
# Bootstrap the Stellar dev environment from a fresh clone.
# Idempotent — safe to re-run; only acts when something is missing.
# Does NOT touch the game install (use install-bepinex.sh / install-stellar.sh for that).
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Versions verified against game v1.0.33142.107. Bump only after re-running the
# full recon workflow (docs/recon-workflow.md) against the new game version.
DOTNET_CHANNEL="8.0"
CPP2IL_TAG="2022.1.0-pre-release.21"
CPP2IL_COMMIT_SUFFIX="Linux"
BEPINEX_BUILD="755"
BEPINEX_FULL="6.0.0-be.${BEPINEX_BUILD}+3fab71a"

DOTNET_ROOT="$HOME/.dotnet"
CPP2IL="$REPO/tools/Cpp2IL"
BEPINEX_ZIP="$REPO/tools/BepInEx-IL2CPP.zip"
BEPINEX_STAGE="$REPO/tools/BepInEx-stage"
VENV="$REPO/tools/.venv"

step() { echo; echo "=== $* ==="; }
have() { command -v "$1" >/dev/null 2>&1; }

# 1. .NET SDK ---------------------------------------------------------------
step ".NET SDK ${DOTNET_CHANNEL} (user-local)"
if [ -x "$DOTNET_ROOT/dotnet" ]; then
    echo "  already installed: $($DOTNET_ROOT/dotnet --version)"
elif have dotnet; then
    echo "  system dotnet found: $(dotnet --version)"
else
    echo "  downloading installer..."
    curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    /tmp/dotnet-install.sh --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_ROOT" >/dev/null
    rm -f /tmp/dotnet-install.sh
    echo "  installed: $($DOTNET_ROOT/dotnet --version)"
fi

# 2. Cpp2IL -----------------------------------------------------------------
step "Cpp2IL ${CPP2IL_TAG}"
if [ -x "$CPP2IL" ]; then
    echo "  already at $CPP2IL"
else
    URL="https://github.com/SamboyCoding/Cpp2IL/releases/download/${CPP2IL_TAG}/Cpp2IL-${CPP2IL_TAG}-${CPP2IL_COMMIT_SUFFIX}"
    echo "  downloading $URL"
    curl -sL --fail "$URL" -o "$CPP2IL"
    chmod +x "$CPP2IL"
fi

# 3. BepInEx 6 IL2CPP -------------------------------------------------------
step "BepInEx 6 IL2CPP (be.${BEPINEX_BUILD})"
if [ ! -f "$BEPINEX_ZIP" ]; then
    URL="https://builds.bepinex.dev/projects/bepinex_be/${BEPINEX_BUILD}/BepInEx-Unity.IL2CPP-win-x64-${BEPINEX_FULL}.zip"
    echo "  downloading $URL"
    curl -sL --fail "$URL" -o "$BEPINEX_ZIP"
fi
if [ ! -d "$BEPINEX_STAGE/BepInEx" ]; then
    echo "  extracting to $BEPINEX_STAGE"
    mkdir -p "$BEPINEX_STAGE"
    unzip -q -o "$BEPINEX_ZIP" -d "$BEPINEX_STAGE"
else
    echo "  already extracted at $BEPINEX_STAGE"
fi

# 4. Python venv for DummyDLL inspection ------------------------------------
step "Python venv (dnfile for inspecting Cpp2IL output)"
if [ -d "$VENV" ]; then
    echo "  already at $VENV"
else
    python3 -m venv "$VENV"
    "$VENV/bin/pip" install --quiet dnfile
    echo "  installed dnfile $("$VENV/bin/python" -c 'import dnfile;print(dnfile.__version__)')"
fi

cat <<EOF

Setup complete. Next steps:

  Build the framework:
    cd src && $DOTNET_ROOT/dotnet build -c Release

  Install BepInEx into your Wine prefix (one-time):
    tools/install-bepinex.sh
    # Then set WINEDLLOVERRIDES=winhttp=n,b in your launcher.

  Deploy the framework + sample plugin:
    tools/install-stellar.sh

  Dump the game's IL2CPP for recon (after a game patch):
    tools/dump-il2cpp.sh

See docs/dev-setup.md for the full from-clone walkthrough.
EOF
