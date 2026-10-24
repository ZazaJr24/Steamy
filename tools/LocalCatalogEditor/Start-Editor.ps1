param([string]$AppPath)
$ErrorActionPreference = 'Stop'
if (-not $AppPath) {
    $projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $AppPath = Join-Path $projectRoot 'artifacts\local-catalog\app\Steamy.exe'
}
if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) { throw "Build the local app first, or pass -AppPath with the Steamy.exe location." }
# The requested editor is interactive; no other app services or controller backend are started.
Start-Process -FilePath $AppPath -ArgumentList '--catalog-editor' -WindowStyle Hidden
