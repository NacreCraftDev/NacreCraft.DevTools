#!/usr/bin/env sh
# One-shot installer for the NacreCraft development toolchain on Linux.
# Installs the .NET 10 SDK, the dotnet new project template and the ncm CLI.
# Idempotent: existing pieces are skipped or updated in place, so reruns are safe.
# Usage: ./tools/install-linux.sh

set -eu

CHANNEL="10.0"
TEMPLATE_ID="NacreCraft.ModsProjectType"
TOOL_ID="NacreCraft.ModBuild.Tools"
INSTALL_ROOT="$HOME/.dotnet"
TOOLS_DIR="$INSTALL_ROOT/tools"

TestCommand() {
    command -v "$1" >/dev/null 2>&1
}

#TestSdk checks whether the machine has an SDK for the given major version
TestSdk() {
    TestCommand dotnet || return 1
    dotnet --list-sdks 2>/dev/null | grep -q "^$1\."
}

Download() {
    if TestCommand curl; then
        curl -fsSL "$1" -o "$2"
    elif TestCommand wget; then
        wget -qO "$2" "$1"
    else
        echo "need curl or wget to download $1" >&2
        exit 1
    fi
}

#InstallSdk uses the official script to install the SDK into the user directory without touching the system copy, so no root is needed
InstallSdk() {
    script="$(mktemp)"
    echo "downloading dotnet-install.sh"
    Download "https://dot.net/v1/dotnet-install.sh" "$script"
    echo "installing .NET SDK $CHANNEL into $INSTALL_ROOT"
    sh "$script" --channel "$CHANNEL" --install-dir "$INSTALL_ROOT" --no-path
    rm -f "$script"
}

echo "==> checking .NET SDK"

if TestSdk 10; then
    echo "found SDK $(dotnet --version)"
else
    InstallSdk
    PATH="$INSTALL_ROOT:$PATH"
    export PATH
    TestSdk 10 || { echo ".NET 10 SDK still missing after install" >&2; exit 1; }
    echo "installed SDK $(dotnet --version)"
fi

#Tools directory must also be on PATH for ncm to be callable later in this session
PATH="$TOOLS_DIR:$PATH"
export PATH

echo "==> installing project template"

#Install from a temp directory, otherwise a same-named folder in the current directory is treated as the template source and local sources get installed instead of the NuGet package
(
    cd "$(mktemp -d)"
    dotnet new install "$TEMPLATE_ID" --force
)

echo "==> installing ncm"

if dotnet tool list -g 2>/dev/null | grep -qi "$TOOL_ID"; then
    dotnet tool update -g "$TOOL_ID"
else
    dotnet tool install -g "$TOOL_ID"
fi

echo "==> verifying"

if TestCommand ncm; then
    echo "ncm ok -> $(command -v ncm)"
else
    echo "ncm not on PATH in this session, open a new shell"
fi

echo ""
echo "done"
echo ""
echo "if dotnet or ncm is not found in a new shell, add these to your shell profile:"
echo "  export DOTNET_ROOT=\"$INSTALL_ROOT\""
echo "  export PATH=\"$INSTALL_ROOT:$TOOLS_DIR:\$PATH\""
