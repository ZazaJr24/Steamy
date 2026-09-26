# Starts Steamy from a freshly built binary.
#
# Why this exists: an older copy of the executable lying around (for example one that was built
# into a temporary folder) is easy to launch by accident, and it then behaves like a bug report
# against code that is already fixed. This script always rebuilds first and always starts the
# binary it just produced.

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'src\Steamy\Steamy.csproj'
$exe = Join-Path $root 'src\Steamy\bin\Release\net8.0-windows\Steamy.exe'

Write-Host 'Stopping running instances...'
Get-Process -Name Steamy -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host 'Building Release...'
& dotnet build $project -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) {
    Write-Error 'The build failed. The application was not started.'
    exit $LASTEXITCODE
}

if (-not (Test-Path $exe)) {
    Write-Error "The expected executable was not found: $exe"
    exit 1
}

Write-Host "Starting $exe"
Start-Process -FilePath $exe

Write-Host 'Done.'
