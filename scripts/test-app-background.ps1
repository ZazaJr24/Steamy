[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AppPath,
    [string]$ReportDirectory = 'artifacts/secure-relay/background-test',
    [ValidateRange(10,120)][int]$Seconds = 30
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$exe = [IO.Path]::GetFullPath((Join-Path $repo $AppPath))
if (!(Test-Path -LiteralPath $exe)) { throw "App executable missing: $exe" }
$report = [IO.Path]::GetFullPath((Join-Path $repo $ReportDirectory))
$profile = Join-Path $report ('profile-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $profile -Force | Out-Null
@{ActiveRoot=$profile;LegacyImported=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'storage-location.json') -Encoding utf8
@{FirstRunCompleted=$true;TransparencyAcknowledgement='transparency-2';AutoUpdate=$false;AutoRefresh=$false;AutoResume=$false;TrackPlayTime=$false;DiscordAutoConnect=$false;ControllerBackendEnabled=$false;DownloadFolder=(Join-Path $profile 'downloads');Appearance='Dark';Language='English';ReduceEffects=$false;TrailerPreviewsEnabled=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'settings.json') -Encoding utf8
if (-not ('SteamyBackgroundProbe' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SteamyBackgroundProbe {
    public delegate bool EnumCallback(IntPtr hwnd, IntPtr arg);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr arg);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    public static IntPtr FindAppWindow(uint pid) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hwnd, arg) => {
            uint owner; GetWindowThreadProcessId(hwnd, out owner);
            Rect rect;
            // WPF-UI can create a hidden HWND during construction, before the
            // app applies its offscreen startup placement. Observe shown windows.
            if (owner == pid && IsWindowVisible(hwnd) && GetWindowRect(hwnd, out rect) && rect.Right - rect.Left > 500 && rect.Bottom - rect.Top > 200) {
                result = hwnd; return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@
}
$savedData = $env:STEAMY_DATA_DIR; $savedTrace = $env:STEAMY_STARTUP_TRACE
$process = $null; $samples = @(); $latencies = @(); $foregroundViolations = 0; $startupTimeouts = 0; $steadyResponses = 0; $rendered = $false; $started = [Diagnostics.Stopwatch]::StartNew()
try {
    $env:STEAMY_DATA_DIR = $profile
    $env:STEAMY_STARTUP_TRACE = Join-Path $report 'startup.tsv'
    $process = Start-Process -FilePath $exe -ArgumentList '--background-test' -WorkingDirectory (Split-Path $exe -Parent) -WindowStyle Hidden -PassThru
    while ($started.Elapsed.TotalSeconds -lt $Seconds) {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
        if ($process.HasExited) { throw "Background app exited unexpectedly: $($process.ExitCode)" }
        [uint32]$foregroundPid = 0
        [void][SteamyBackgroundProbe]::GetWindowThreadProcessId([SteamyBackgroundProbe]::GetForegroundWindow(), [ref]$foregroundPid)
        if ($foregroundPid -eq $process.Id) { $foregroundViolations++ }
        $handle = [SteamyBackgroundProbe]::FindAppWindow($process.Id)
        if ($handle -ne [IntPtr]::Zero) {
            $rect = New-Object SteamyBackgroundProbe+Rect
            if ([SteamyBackgroundProbe]::GetWindowRect($handle, [ref]$rect)) {
                if ($rect.Left -gt -10000 -or $rect.Top -gt -10000) { throw 'Test window was not kept offscreen.' }
                $rendered = $true
            }
            [IntPtr]$result = [IntPtr]::Zero
            $ping = [Diagnostics.Stopwatch]::StartNew()
            $ok = [SteamyBackgroundProbe]::SendMessageTimeout($handle, 0, [IntPtr]::Zero, [IntPtr]::Zero, 2, 1000, [ref]$result)
            if ($started.Elapsed.TotalSeconds -lt 10) {
                if ($ok -eq [IntPtr]::Zero) { $startupTimeouts++ }
            }
            else {
                $latencies += $ping.Elapsed.TotalMilliseconds
                if ($ok -eq [IntPtr]::Zero) { throw 'Background app did not respond within one second after startup warmup.' }
                $steadyResponses++
            }
        }
        $samples += @{seconds=[Math]::Round($started.Elapsed.TotalSeconds,2);workingSetMiB=[Math]::Round($process.WorkingSet64/1MB,2);privateMiB=[Math]::Round($process.PrivateMemorySize64/1MB,2);cpuSeconds=$process.TotalProcessorTime.TotalSeconds}
    }
    if ($foregroundViolations -gt 0) { throw 'The background test app took foreground focus.' }
    if (!$rendered) { throw 'No real offscreen app window was detected.' }
    if ($Seconds -gt 10 -and $steadyResponses -eq 0) { throw 'No app response after startup warmup.' }
    @{passed=$true;seconds=$Seconds;offscreenWindow=$rendered;foregroundViolations=$foregroundViolations;startupProbeTimeouts=$startupTimeouts;startupWarmupSeconds=10;steadyResponses=$steadyResponses;maximumSteadyResponseMs=($latencies|Measure-Object -Maximum).Maximum;samples=$samples;scope='Isolated real app startup and idle, one-second responsiveness after a ten-second startup warmup; no interactive scrolling or before/after speed comparison.'} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $report 'report.json') -Encoding utf8
    Write-Host "Background test passed: $report"
}
finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit(10000) | Out-Null }
    $env:STEAMY_DATA_DIR = $savedData; $env:STEAMY_STARTUP_TRACE = $savedTrace
}
