param(
    [string]$PortableDirectory = 'artifacts/distribution/portable',
    [string]$ReportDirectory = '.probe-glass/portable-startup'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$portable = [IO.Path]::GetFullPath((Join-Path $repoRoot $PortableDirectory))
$reportRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $ReportDirectory))
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $portable 'WindowsBase.dll'))
if ($assembly.Version.Major -ne 9) { throw 'WindowsBase.dll is not the WPF 9 runtime assembly.' }
if (-not ('SteamyStartupNative' -as [type])) {
    Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SteamyStartupNative {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
}
'@
}
$savedEnvironment = @{}
foreach ($key in @('STEAMY_DATA_DIR','DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_MULTILEVEL_LOOKUP')) {
    $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
}
$results = @()
try {
    # Exercise the actual shipping hosts without using a global .NET installation.
    $env:DOTNET_ROOT = Join-Path $reportRoot 'no-global-runtime'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    foreach ($scenario in @('returning-user','first-run')) {
        $profile = Join-Path $reportRoot ($scenario + '-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $profile | Out-Null
        @{ActiveRoot=$profile;LegacyImported=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'storage-location.json') -Encoding utf8
        @{FirstRunCompleted=($scenario -eq 'returning-user');TransparencyAcknowledgement='transparency-2';AutoUpdate=$false;AutoRefresh=$false;AutoResume=$false;TrackPlayTime=$false;Language='en-US';ReduceEffects=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'settings.json') -Encoding utf8
        $env:STEAMY_DATA_DIR = $profile
        $process = Start-Process -FilePath (Join-Path $portable 'Steamy.exe') -WorkingDirectory $portable -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $profile 'stderr.txt')
        try {
            $timer = [Diagnostics.Stopwatch]::StartNew()
            do { Start-Sleep -Milliseconds 250; $process.Refresh() } while (-not $process.HasExited -and $process.MainWindowHandle -eq 0 -and $timer.Elapsed.TotalSeconds -lt 25)
            if ($process.HasExited -or $process.MainWindowHandle -eq 0) { throw "$scenario failed to open Steamy. See $profile." }
            $windowClass = [Text.StringBuilder]::new(256)
            [SteamyStartupNative]::GetClassName($process.MainWindowHandle, $windowClass, $windowClass.Capacity) | Out-Null
            if (-not $windowClass.ToString().StartsWith('HwndWrapper[')) { throw "$scenario opened an error dialog instead of the WPF window." }
            $perMonitorV2 = [SteamyStartupNative]::AreDpiAwarenessContextsEqual([SteamyStartupNative]::GetWindowDpiAwarenessContext($process.MainWindowHandle), [IntPtr]::new(-4))
            if (-not $perMonitorV2) { throw "$scenario did not create a Per Monitor V2 window; Windows could bitmap-scale text." }
            $startupMs = $timer.ElapsedMilliseconds
            Start-Sleep -Seconds 8
            $process.Refresh()
            if ($process.HasExited -or -not $process.Responding -or (Test-Path -LiteralPath (Join-Path $profile 'crash.log'))) { throw "$scenario failed after its first render. See $profile." }
            $results += @{Scenario=$scenario;Passed=$true;StartupMilliseconds=$startupMs;WindowClass=$windowClass.ToString();Responding=$process.Responding;PerMonitorV2=$perMonitorV2;Profile=$profile}
        } finally {
            # Only this process belongs to the test; no existing app or tracker is stopped.
            if (-not $process.HasExited) {
                $process.CloseMainWindow() | Out-Null
                if (-not $process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() }
            }
            $process.Dispose()
        }
    }
    $trackerProfile = Join-Path $reportRoot ('tracker-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $trackerProfile | Out-Null
    '{"Enabled":false}' | Set-Content -LiteralPath (Join-Path $trackerProfile 'playtime-config.json') -Encoding utf8
    $env:STEAMY_DATA_DIR = $trackerProfile
    $tracker = Start-Process -FilePath (Join-Path $portable 'PlayTimeTracker.exe') -WorkingDirectory $portable -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $trackerProfile 'stderr.txt')
    try {
        if (-not $tracker.WaitForExit(10000)) { $tracker.Kill(); $tracker.WaitForExit(); throw 'Disabled tracker did not exit.' }
        if ($tracker.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $trackerProfile 'playtime.json'))) { throw 'Portable tracker host failed to run.' }
        $results += @{Scenario='tracker';Passed=$true;ExitCode=$tracker.ExitCode;Profile=$trackerProfile}
    } finally { $tracker.Dispose() }
    @{Passed=$true;WindowsBase=$assembly.FullName;GlobalRuntimeDisabled=$true;Checks=$results} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $reportRoot 'startup-report.json') -Encoding utf8
    $results | ConvertTo-Json -Depth 4
} finally {
    foreach ($entry in $savedEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }
}
