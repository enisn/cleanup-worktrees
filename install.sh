#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"
CSPROJ="$SCRIPT_DIR/src/CleanupWorktrees/CleanupWorktrees.csproj"
NUPKG_DIR="$SCRIPT_DIR/src/CleanupWorktrees/nupkg"

rm -rf "$NUPKG_DIR"
dotnet pack "$CSPROJ" -c Release
dotnet tool uninstall --global CleanupWorktrees >/dev/null 2>&1 || true
dotnet tool install --global --add-source "$NUPKG_DIR" CleanupWorktrees
echo "Installed 'cleanup-worktrees' globally."
