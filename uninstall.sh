#!/usr/bin/env bash
set -euo pipefail

dotnet tool uninstall --global CleanupWorktrees
echo "Uninstalled 'cleanup-worktrees'."
