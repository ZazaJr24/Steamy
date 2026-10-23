[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/steamy-0.8/controller-host', [switch]$SourcePackage)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$commit = '722c561c3d7aa65dcf063e716c04ca69e4ca33bd'
$archive = Join-Path $repoRoot ".cache/DS4Windows-$commit.zip"
$expected = 'E81F5DE4E41AC3C205A5DA4CB7C733265A1D4D193D0DED936AABA938D625666D'
New-Item -ItemType Directory -Force (Split-Path $archive) | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://codeload.github.com/schmaldeo/DS4Windows/zip/$commit" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'The pinned DS4Windows source archive failed SHA-256 verification.' }
$sourceTree = Join-Path $repoRoot ".cache/controller-host-source/DS4Windows-$commit"
if (!(Test-Path -LiteralPath $sourceTree)) { Expand-Archive -LiteralPath $archive -DestinationPath (Split-Path $sourceTree) }
$projectRoot = Join-Path $sourceTree 'DS4Windows'
Copy-Item -LiteralPath (Join-Path $repoRoot 'tools/ControllerHost/HostProgram.cs') -Destination (Join-Path $projectRoot 'SteamyHostProgram.cs') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'src/Steamy/Services/Controllers/ControllerProtocol.cs') -Destination (Join-Path $projectRoot 'SteamyControllerProtocol.cs') -Force
$projectPath = Join-Path $projectRoot 'DS4WinWPF.csproj'
$projectText = [IO.File]::ReadAllText($projectPath)
$projectText = $projectText.Replace('net8.0-windows', 'net10.0-windows').Replace('<AssemblyName>DS4Windows</AssemblyName>', '<AssemblyName>Steamy.ControllerHost</AssemblyName>').Replace('<StartupObject>DS4WinWPF.App</StartupObject>', '<StartupObject>Steamy.ControllerHost.HostProgram</StartupObject>').Replace('..\libs\$(Platform)', 'libs\$(Platform)')
$projectText = $projectText.Replace('<PackageReference Include="System.Memory" Version="4.5.5" />', '')
# The pinned project's imports refer to libs one level above their actual checked-in location.
[IO.File]::WriteAllText($projectPath, $projectText)
$controlPath = Join-Path $projectRoot 'DS4Control/ControlService.cs'
$controlText = [IO.File]::ReadAllText($controlPath).Replace('DS4Devices.RequestElevation += DS4Devices_RequestElevation;', '// Steamy: driver installation/device hiding requires a separate explicit action.')
[IO.File]::WriteAllText($controlPath, $controlText)
$editorPath = Join-Path $projectRoot 'DS4Forms/ProfileEditor.xaml.cs'
$editorText = [IO.File]::ReadAllText($editorPath)
if (!$editorText.Contains('using ContextMenu = System.Windows.Controls.ContextMenu;')) {
    $editorText = "using ContextMenu = System.Windows.Controls.ContextMenu;`r`nusing MenuItem = System.Windows.Controls.MenuItem;`r`n" + $editorText
    [IO.File]::WriteAllText($editorPath, $editorText)
}
$outputPath = if ([IO.Path]::IsPathFullyQualified($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }
& dotnet publish $projectPath -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.13 -p:PublishSingleFile=false -o $outputPath -v minimal
if ($LASTEXITCODE -ne 0) { throw 'ControllerHost publish failed.' }
Copy-Item -LiteralPath (Join-Path $sourceTree 'COPYING'), (Join-Path $sourceTree 'LICENSE.txt'), (Join-Path $sourceTree 'NOTICE.txt') -Destination $outputPath -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'tools/ControllerHost/README.md') -Destination (Join-Path $outputPath 'CONTROLLER-SOURCE.md') -Force
if ($SourcePackage) {
    # Retain the original archive plus the full modifications and build instructions.
    $packageRoot = Join-Path $repoRoot 'artifacts/steamy-0.8/controller-source'
    New-Item -ItemType Directory -Force "$packageRoot/scripts", "$packageRoot/tools/ControllerHost", "$packageRoot/src/Steamy/Services/Controllers", "$packageRoot/.cache" | Out-Null
    Copy-Item -LiteralPath $archive -Destination "$packageRoot/.cache" -Force
    Copy-Item -LiteralPath $PSCommandPath -Destination "$packageRoot/scripts" -Force
    Copy-Item -LiteralPath "$repoRoot/tools/ControllerHost/HostProgram.cs", "$repoRoot/tools/ControllerHost/README.md", "$repoRoot/tools/ControllerHost/PROTOCOL-LICENSE.txt" -Destination "$packageRoot/tools/ControllerHost" -Force
    Copy-Item -LiteralPath "$repoRoot/src/Steamy/Services/Controllers/ControllerProtocol.cs" -Destination "$packageRoot/src/Steamy/Services/Controllers" -Force
    Copy-Item -LiteralPath "$repoRoot/global.json" -Destination $packageRoot -Force
    $sourceEntries = @(Get-ChildItem -LiteralPath $packageRoot -Force | ForEach-Object FullName)
    Compress-Archive -LiteralPath $sourceEntries -DestinationPath "$repoRoot/artifacts/steamy-0.8/Steamy-ControllerHost-source.zip" -Force
    Copy-Item -LiteralPath "$repoRoot/artifacts/steamy-0.8/Steamy-ControllerHost-source.zip", "$repoRoot/tools/ControllerHost/PROTOCOL-LICENSE.txt" -Destination $outputPath -Force
}
