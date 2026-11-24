#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $ScriptDir
try {
    $Csproj = Join-Path $ScriptDir 'src/CleanupWorktrees/CleanupWorktrees.csproj'
    $NupkgDir = Join-Path $ScriptDir 'src/CleanupWorktrees/nupkg'

    if (Test-Path $NupkgDir) {
        Remove-Item -Recurse -Force $NupkgDir
    }

    dotnet pack $Csproj -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet tool uninstall --global CleanupWorktrees 2>$null | Out-Null
    dotnet tool install --global --add-source $NupkgDir CleanupWorktrees
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "Installed 'cleanup-worktrees' globally."
}
finally {
    Pop-Location
}
