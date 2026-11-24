#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'

dotnet tool uninstall --global CleanupWorktrees
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Uninstalled 'cleanup-worktrees'."
