param([string]$OutputDirectory = 'artifacts/distribution', [switch]$IncludeCompleteSetup)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'src/Steamy/Steamy.csproj')
$version = $project.Project.PropertyGroup.Version
$output = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$app = Join-Path $output 'app'
$portable = Join-Path $output 'portable'
$tracker = Join-Path $output 'tracker-portable'
$build = (Join-Path $output 'build') + '/'
foreach ($variant in @(@{Path=$app;Contained='false'},@{Path=$portable;Contained='true'})) {
    & dotnet publish (Join-Path $repoRoot 'src/Steamy/Steamy.csproj') -c Release -r win-x64 --self-contained $variant.Contained -p:PublishSingleFile=false -p:RuntimeFrameworkVersion=9.0.20 -p:RestoreIgnoreFailedSources=true "-p:BaseOutputPath=$build" -o $variant.Path --verbosity minimal -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'App publishing failed.' }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE'),(Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $variant.Path
    $tool = Join-Path $variant.Path 'Tools/DepotDownloaderMod'
    New-Item -ItemType Directory -Path $tool -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'tools/DepotDownloaderMod/Release/net9.0/DepotDownloaderMod.exe'),(Join-Path $repoRoot 'tools/DepotDownloaderMod/Release/net9.0/Steamy-depotdownloader-mod.json') -Destination $tool
}
# Both app hosts must work without a globally installed .NET runtime in the portable package.
& dotnet publish (Join-Path $repoRoot 'src/PlayTimeTracker/PlayTimeTracker.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:RuntimeFrameworkVersion=9.0.20 -p:RestoreIgnoreFailedSources=true "-p:BaseOutputPath=$build" -o $tracker --verbosity minimal -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw 'Portable tracker publishing failed.' }
# The app already contains the Core and Windows Desktop runtimes. Copy only the
# tracker host and its manifest: its Core-only WindowsBase facade would replace
# the real WPF assembly and crash Steamy before App.OnStartup can report an error.
foreach ($name in @('PlayTimeTracker.exe','PlayTimeTracker.dll','PlayTimeTracker.deps.json','PlayTimeTracker.runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $tracker $name) -Destination $portable -Force
}
foreach ($directory in @($app,$portable)) {
    foreach ($file in @('Steamy.exe','Steamy.dll','PlayTimeTracker.exe','PlayTimeTracker.dll','Tools/DepotDownloaderMod/DepotDownloaderMod.exe','Tools/DepotDownloaderMod/Steamy-depotdownloader-mod.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $directory $file))) { throw "Missing component: $file" }
    }
}
foreach ($name in @('Steamy','PlayTimeTracker')) {
    $config = Get-Content -LiteralPath (Join-Path $portable ($name + '.runtimeconfig.json')) -Raw | ConvertFrom-Json
    if (-not $config.runtimeOptions.includedFrameworks) { throw "$name portable runtime is missing." }
}
$windowsBase = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $portable 'WindowsBase.dll'))
if ($windowsBase.Version.Major -ne 9) { throw 'Portable WPF runtime was overwritten: WindowsBase.dll must be version 9.' }
$appZip = Join-Path $output "Steamy-v$version-app.zip"
$portableZip = Join-Path $output "Steamy-v$version.zip"
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $appZip -Force
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $portableZip -Force
& (Join-Path $PSScriptRoot 'build-bootstrap.ps1') -OutputDirectory $OutputDirectory
Copy-Item -LiteralPath (Join-Path $output 'Steamy-Setup.exe') -Destination (Join-Path $output "Steamy-v$version-Setup.exe") -Force
if ($IncludeCompleteSetup) {
    & (Join-Path $PSScriptRoot 'build-bootstrap.ps1') -OutputDirectory $OutputDirectory -PayloadPath $portableZip -OutputName 'Steamy-FullSetup.exe'
    Copy-Item -LiteralPath (Join-Path $output 'Steamy-FullSetup.exe') -Destination (Join-Path $output "Steamy-v$version-FullSetup.exe") -Force
}
$assets = foreach ($path in @($appZip,$portableZip)) {
    $item = Get-Item -LiteralPath $path
    @{name=$item.Name; browser_download_url="https://github.com/ZazaJr24/Steamy/releases/download/v$version/$($item.Name)"; size=$item.Length; digest=('sha256:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())}
}
@{tag_name="v$version"; draft=$false; prerelease=$false; assets=@($assets)} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'release-packages.json') -Encoding utf8
Write-Host 'Distribution ready: small download setup, app payload and complete portable ZIP.'
