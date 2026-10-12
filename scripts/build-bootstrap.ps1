param([string]$OutputDirectory = 'artifacts/installer', [string]$PayloadPath, [string]$OutputName = 'Steamy-Setup.exe')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
New-Item -ItemType Directory -Path $output -Force | Out-Null
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'src/Steamy/Steamy.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Invalid setup version.' }
if ([IO.Path]::GetFileName($OutputName) -ne $OutputName -or -not $OutputName.EndsWith('.exe')) { throw 'OutputName must be an EXE filename.' }
$fileVersion = if ($version.Split('.').Count -eq 3) { $version + '.0' } else { $version }
$payloadBytes = 0L
$payloadHash = ''
if ($PayloadPath) {
    $PayloadPath = [IO.Path]::GetFullPath($PayloadPath)
    $payloadBytes = (Get-Item -LiteralPath $PayloadPath).Length
    if ($payloadBytes -le 0) { throw 'The complete setup payload is empty.' }
    $payloadHash = (Get-FileHash -LiteralPath $PayloadPath -Algorithm SHA256).Hash
}
$metadata = Join-Path $output ($OutputName + '.BuildInfo.g.cs')
@"
using System.Reflection;
[assembly: AssemblyTitle("Steamy Setup")]
[assembly: AssemblyProduct("Steamy")]
[assembly: AssemblyCompany("ZazaJr24")]
[assembly: AssemblyVersion("$fileVersion")]
[assembly: AssemblyFileVersion("$fileVersion")]
[assembly: AssemblyInformationalVersion("$version")]
namespace Steamy.Setup { internal static class SetupBuild {
    internal const string Version = "$version";
    internal static readonly long PayloadBytes = ${payloadBytes}L;
    internal const string PayloadSha256 = "$payloadHash";
} }
"@ | Set-Content -LiteralPath $metadata -Encoding utf8
[xml]$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'src/Steamy.Setup/app.manifest')
$manifest.assembly.assemblyIdentity.version = $fileVersion
$manifestPath = Join-Path $output ($OutputName + '.manifest')
$manifest.Save($manifestPath)
$fontOutput = Join-Path $output 'fonts'
New-Item -ItemType Directory -Path $fontOutput -Force | Out-Null
foreach ($face in @('Regular', 'SemiBold')) {
    $source = [IO.File]::OpenRead((Join-Path $repoRoot "src/Steamy/Resources/Fonts/GoogleSans-$face.ttf"))
    $target = [IO.File]::Create((Join-Path $fontOutput "$face.deflate"))
    $compressed = [IO.Compression.DeflateStream]::new($target, [IO.Compression.CompressionLevel]::Optimal)
    try { $source.CopyTo($compressed) } finally { $compressed.Dispose(); $source.Dispose(); $target.Dispose() }
}
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework compiler is unavailable.' }
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+',('/out:' + (Join-Path $output $OutputName)),
    ('/win32icon:' + (Join-Path $repoRoot 'src/Steamy/Resources/Brand/steamy.ico')),
    ('/win32manifest:' + $manifestPath),
    ('/resource:' + (Join-Path $repoRoot 'src/Steamy/Resources/Brand/Brand.xaml') + ',Steamy.Brand.xaml'),
    ('/resource:' + (Join-Path $repoRoot 'src/Steamy.Setup/SetupWindow.xaml') + ',Steamy.SetupWindow.xaml'),
    ('/resource:' + (Join-Path $fontOutput 'Regular.deflate') + ',Steamy.Font.Regular'),
    ('/resource:' + (Join-Path $fontOutput 'SemiBold.deflate') + ',Steamy.Font.SemiBold'),
    ('/resource:' + (Join-Path $repoRoot 'src/Steamy/Resources/Fonts/OFL.txt') + ',Steamy.Font.License'))
if ($PayloadPath) { $arguments += '/resource:' + $PayloadPath + ',Steamy.Setup.Payload.zip' }
foreach ($assembly in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll')) {
    $arguments += '/reference:' + (Join-Path $framework $assembly)
}
foreach ($assembly in @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll')) {
    $arguments += '/reference:' + (Join-Path $framework "WPF/$assembly")
}
$arguments += '/reference:' + (Join-Path $framework 'System.Xaml.dll')
$arguments += Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src/Steamy.Setup') -Filter '*.cs' | ForEach-Object FullName
$arguments += $metadata
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'The bootstrap installer could not be compiled.' }
$exe = Get-Item -LiteralPath (Join-Path $output $OutputName)
if (-not $PayloadPath -and $exe.Length -gt 2MB) { throw 'The bootstrap installer exceeded the 2 MB size budget.' }
Write-Host ('Built ' + $exe.FullName + ' (' + [Math]::Round($exe.Length / 1KB) + ' KB).')
