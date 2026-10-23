param(
    [string]$PortableDirectory = 'artifacts/distribution/portable',
    [string]$ReportDirectory = '.probe-glass/portable-startup',
    [int]$ExpectedRuntimeMajor = 10,
    [string]$PreviousPortableDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$portable = [IO.Path]::GetFullPath((Join-Path $repoRoot $PortableDirectory))
$reportRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $ReportDirectory))
$previousPortable = if ($PreviousPortableDirectory) { [IO.Path]::GetFullPath((Join-Path $repoRoot $PreviousPortableDirectory)) } else { '' }
if ($previousPortable -and !(Test-Path -LiteralPath (Join-Path $previousPortable 'Steamy.exe'))) { throw 'The previous portable version is missing.' }
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $portable 'WindowsBase.dll'))
if ($assembly.Version.Major -ne $ExpectedRuntimeMajor) { throw "WindowsBase.dll is not the WPF $ExpectedRuntimeMajor runtime assembly." }
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
$returningProfile = $null
$preservedHashes = @{}
try {
    # Exercise the actual shipping hosts without using a global .NET installation.
    $env:DOTNET_ROOT = Join-Path $reportRoot 'no-global-runtime'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $scenarios = @('returning-user','returning-user-restart','first-run')
    if ($previousPortable) { $scenarios = @('previous-version') + $scenarios }
    foreach ($scenario in $scenarios) {
        $returning = $scenario -ne 'first-run'
        if (!$returning -or !$returningProfile) {
            $profile = Join-Path $reportRoot ($scenario + '-' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $profile | Out-Null
            @{ActiveRoot=$profile;LegacyImported=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'storage-location.json') -Encoding utf8
            $preferences = @{FirstRunCompleted=$returning;TransparencyAcknowledgement='transparency-2';AutoUpdate=$false;AutoRefresh=$false;AutoResume=$false;TrackPlayTime=$false;Language='English';Appearance='Dark';ReduceEffects=$true;DiscordAutoConnect=$false;ControllerBackendEnabled=$false;ParallelDownloads=3;DownloadFolder=(Join-Path $profile 'download-root')}
            New-Item -ItemType Directory -Path $preferences.DownloadFolder -Force | Out-Null
            $preferences | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'settings.json') -Encoding utf8
            if ($returning) {
                $returningProfile = $profile
                New-Item -ItemType Directory -Path "$profile/preserved-game", "$profile/games", "$profile/credentials" -Force | Out-Null
                'Local library preservation fixture; this is not an executable.' | Set-Content -LiteralPath "$profile/preserved-game/fixture.txt"
                @(@{AppId=10;Name='Preserved library fixture';Folder=(Join-Path $profile 'preserved-game');Source='DepotDownloader';Executable=''}) | ConvertTo-Json -AsArray | Set-Content -LiteralPath "$profile/installed-downloads.json" -Encoding utf8
                @{Notes='Keep this game note';LaunchArguments='-windowed';LaunchExecutable=''} | ConvertTo-Json | Set-Content -LiteralPath "$profile/games/10.json" -Encoding utf8
                # A synthetic DPAPI credential exercises file preservation only. Auto-connect
                # stays disabled, so no fixture token is submitted to Discord.
                $credentialName = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('discord-social-session'))).ToLowerInvariant()
                $syntheticSession = '{"ApplicationId":1559657404994429101,"AccessToken":"fixture-not-a-discord-token","RefreshToken":"fixture-not-a-refresh-token","ExpiresAt":"2030-01-01T00:00:00Z"}'
                $protectedSession = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($syntheticSession), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
                [IO.File]::WriteAllBytes("$profile/credentials/$credentialName", $protectedSession)
                foreach ($relative in @('games/10.json', "credentials/$credentialName")) { $preservedHashes[$relative] = (Get-FileHash -LiteralPath (Join-Path $profile $relative) -Algorithm SHA256).Hash }
            }
        } else { $profile = $returningProfile }
        $env:STEAMY_DATA_DIR = $profile
        $hostDirectory = if ($scenario -eq 'previous-version') { $previousPortable } else { $portable }
        $process = Start-Process -FilePath (Join-Path $hostDirectory 'Steamy.exe') -WorkingDirectory $hostDirectory -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $profile ($scenario + '-stderr.txt'))
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
            $sample = @{Scenario=$scenario;Passed=$true;StartupMilliseconds=$startupMs;WindowClass=$windowClass.ToString();Responding=$process.Responding;PerMonitorV2=$perMonitorV2;Profile=$profile;FileVersion=([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $hostDirectory 'Steamy.exe')).FileVersion)}
        } finally {
            # Only this process belongs to the test; no existing app or tracker is stopped.
            if (-not $process.HasExited) {
                $process.CloseMainWindow() | Out-Null
                if (-not $process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() }
            }
            $process.Dispose()
        }
        if ($returning) {
            $after = Get-Content -LiteralPath (Join-Path $profile 'settings.json') -Raw | ConvertFrom-Json
            if ($after.ParallelDownloads -ne 3 -or $after.Appearance -ne 'Dark' -or $after.DiscordAutoConnect -ne $false -or $after.DownloadFolder -ne $preferences.DownloadFolder) { throw "$scenario changed saved preferences." }
            $catalog = @(Get-Content -LiteralPath (Join-Path $profile 'installed-downloads.json') -Raw | ConvertFrom-Json)
            if (@($catalog | Where-Object { $_.AppId -eq 10 -and $_.Name -eq 'Preserved library fixture' -and [IO.Path]::GetFullPath($_.Folder) -eq (Join-Path $profile 'preserved-game') }).Count -ne 1) { throw "$scenario did not preserve the library entry." }
            foreach ($relative in $preservedHashes.Keys) {
                if (!(Test-Path -LiteralPath (Join-Path $profile $relative)) -or (Get-FileHash -LiteralPath (Join-Path $profile $relative) -Algorithm SHA256).Hash -ne $preservedHashes[$relative]) { throw "$scenario did not preserve $relative." }
            }
            $clear = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes("$profile/credentials/$credentialName"), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
            if ([Text.Encoding]::UTF8.GetString($clear) -cne $syntheticSession) { throw "$scenario changed the encrypted credential." }
            $sample.ProfilePreserved = $true
            $sample.LibraryEntryPreserved = $true
            $sample.CredentialBytesPreserved = $true
        }
        $results += $sample
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
