#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_PATH="$ROOT_DIR/src/OmniSharp.Stdio.Driver/OmniSharp.Stdio.Driver.csproj"
PACKAGE_ID="minigamess.omnisharp"
CONFIGURATION="Release"
OUTPUT_DIR="$ROOT_DIR/artifacts/tool"

usage() {
  cat <<EOF
One-click install/update OmniSharp to ~/.dotnet/tools/omnisharp

Usage:
  $(basename "$0") [--configuration <Debug|Release>] [--output <dir>]

Examples:
  $(basename "$0")
  $(basename "$0") --configuration Debug
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --configuration|-c)
      CONFIGURATION="${2:-}"
      shift 2
      ;;
    --output|-o)
      OUTPUT_DIR="${2:-}"
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ -z "$CONFIGURATION" ]]; then
  echo "--configuration cannot be empty" >&2
  exit 1
fi

mkdir -p "$OUTPUT_DIR"

echo "[1/3] Packing tool package..."
dotnet pack "$PROJECT_PATH" -c "$CONFIGURATION" -o "$OUTPUT_DIR"

echo "[2/3] Installing/updating global tool..."
echo "Force reinstall: clearing NuGet caches and reinstalling tool"
dotnet nuget locals http-cache --clear
dotnet nuget locals global-packages --clear
dotnet tool uninstall -g "$PACKAGE_ID" || true
dotnet tool install -g "$PACKAGE_ID" --add-source "$OUTPUT_DIR"

echo "[3/3] Verifying install..."
TOOL_PATH="$HOME/.dotnet/tools/omnisharp"
if [[ -x "$TOOL_PATH" ]]; then
  echo "Installed: $TOOL_PATH"
  "$TOOL_PATH" --version || true
else
  echo "Tool installed, but executable not found at $TOOL_PATH" >&2
  echo "Please ensure ~/.dotnet/tools is in PATH." >&2
fi
