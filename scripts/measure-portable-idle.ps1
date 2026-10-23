param(
    [Parameter(Mandatory=$true)][string]$PortableDirectory,
    [Parameter(Mandatory=$true)][string]$ReportDirectory,
    [Parameter(Mandatory=$true)][string]$MediaFixtureDirectory,
    [ValidateRange(1,10)][int]$Runs = 5,
    [ValidateRange(15,120)][int]$Seconds = 30,
    [switch]$Foreground
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
function FullPath([string]$path) { if([IO.Path]::IsPathRooted($path)){return [IO.Path]::GetFullPath($path)}; return [IO.Path]::GetFullPath((Join-Path $repo $path)) }
$portable=FullPath $PortableDirectory; $reports=FullPath $ReportDirectory; $fixture=FullPath $MediaFixtureDirectory
if(Test-Path -LiteralPath $reports){throw 'Choose a new report directory; earlier evidence must remain intact.'}
$manifest=Get-Content -LiteralPath (Join-Path $fixture 'manifest.json') -Raw | ConvertFrom-Json
if($manifest.schemaVersion -ne 1){throw 'Unsupported public media fixture.'}
if((Get-FileHash -LiteralPath (Join-Path $fixture 'spotlight.json')).Hash -ne $manifest.spotlightSha256){throw 'Public feed fixture changed.'}
foreach($asset in $manifest.artwork){
    if($asset.name -notmatch '^\d+_[a-zA-Z0-9_]+\.(jpg|png)$' -or (Get-FileHash -LiteralPath (Join-Path $fixture ('artwork/'+$asset.name))).Hash -ne $asset.sha256){throw 'Public artwork fixture changed.'}
}
foreach($asset in $manifest.catalogFiles){
    if($asset.path -notmatch '^steam-catalog/(artwork-assets\.json|details/\d+\.json|artwork/\d+_[a-zA-Z0-9_]+\.(jpg|png))$' -or (Get-FileHash -LiteralPath (Join-Path $fixture $asset.path)).Hash -ne $asset.sha256){throw 'Public catalog fixture changed.'}
}
$executable=Join-Path $portable 'Steamy.exe'
$identity=@{executableSha256=(Get-FileHash -LiteralPath $executable).Hash;applicationAssemblySha256=(Get-FileHash -LiteralPath (Join-Path $portable 'Steamy.dll')).Hash;fixtureId=$manifest.fixtureId;startedUtc=[DateTimeOffset]::UtcNow.ToString('O');machine=$env:COMPUTERNAME;scriptSha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash;foregroundRequested=[bool]$Foreground}
if(-not ('SteamyIdleNative' -as [type])){
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SteamyIdleNative {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect rectangle);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
 [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr window);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
}
'@
}
New-Item -ItemType Directory -Path $reports | Out-Null
$runReports=@()
for($run=0;$run -lt $Runs;$run++){
    $profile=Join-Path $reports ('profile-'+$run+'-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $profile | Out-Null
    @{ActiveRoot=$profile;LegacyImported=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'storage-location.json')
    @{FirstRunCompleted=$true;TransparencyAcknowledgement='transparency-2';AutoUpdate=$false;AutoRefresh=$false;AutoResume=$false;TrackPlayTime=$false;Language='English';Appearance='Dark';AppearanceDesignVersion=1;BackgroundColor='#000000';ReduceEffects=$false;DiscordAutoConnect=$false;ControllerBackendEnabled=$false;AmbientEnabled=$true;DownloadFolder=(Join-Path $profile 'downloads')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'settings.json')
    New-Item -ItemType Directory -Path (Join-Path $profile 'artwork') | Out-Null
    Copy-Item -LiteralPath (Join-Path $fixture 'spotlight.json') -Destination $profile
    foreach($asset in $manifest.artwork){Copy-Item -LiteralPath (Join-Path $fixture ('artwork/'+$asset.name)) -Destination (Join-Path $profile ('artwork/'+$asset.name))}
    foreach($asset in $manifest.catalogFiles){
        $destination=Join-Path $profile $asset.path
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $fixture $asset.path) -Destination $destination
    }
    $info=[Diagnostics.ProcessStartInfo]::new($executable)
    $info.WorkingDirectory=$portable; $info.UseShellExecute=$false; $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    # The child runs the ordinary shipped entry point. No startup hook, renderer
    # override, test fixtures injected into services, or in-process observer.
    $info.Environment.Remove('DOTNET_STARTUP_HOOKS') | Out-Null
    foreach($key in @($info.Environment.Keys | Where-Object {$_ -like 'STEAMY_PERFORMANCE_*' -or $_ -eq 'STEAMY_STARTUP_TRACE'})){$info.Environment.Remove($key) | Out-Null}
    $info.Environment['STEAMY_DATA_DIR']=$profile
    $process=[Diagnostics.Process]::Start($info)
    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
    $samples=@(); $failure=$null; $readyMs=$null; $exitCode=$null
    try{
        $timer=[Diagnostics.Stopwatch]::StartNew()
        do{Start-Sleep -Milliseconds 100; $process.Refresh()}while(!$process.HasExited -and $process.MainWindowHandle -eq 0 -and $timer.Elapsed.TotalSeconds -lt 25)
        if($process.HasExited -or $process.MainWindowHandle -eq 0){throw 'The ordinary portable process did not open a window.'}
        $readyMs=$timer.Elapsed.TotalMilliseconds
        if($Foreground){[SteamyIdleNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null}
        $observed=[Diagnostics.Stopwatch]::StartNew(); $previousCpu=$process.TotalProcessorTime.TotalMilliseconds; $previousTime=$observed.Elapsed.TotalMilliseconds
        while($observed.Elapsed.TotalSeconds -lt $Seconds){
            Start-Sleep -Milliseconds 2000; $process.Refresh()
            if($process.HasExited){throw 'The ordinary portable process exited during idle sampling.'}
            $time=$observed.Elapsed.TotalMilliseconds; $cpu=$process.TotalProcessorTime.TotalMilliseconds
            $rect=[SteamyIdleNative+Rect]::new(); if(![SteamyIdleNative]::GetClientRect($process.MainWindowHandle,[ref]$rect)){throw 'Cannot inspect owned window geometry.'}
            $children=@(Get-CimInstance Win32_Process -Filter ('ParentProcessId = '+$process.Id) | ForEach-Object {
                try{$child=Get-Process -Id $_.ProcessId -ErrorAction Stop; [pscustomobject]@{pid=$child.Id;name=$child.ProcessName;privateBytes=$child.PrivateMemorySize64;workingSetBytes=$child.WorkingSet64}}catch{}
            })
            $samples += [pscustomobject]@{elapsedAfterWindowMs=$time;privateBytes=$process.PrivateMemorySize64;workingSetBytes=$process.WorkingSet64;cpuMs=$cpu;oneCorePercent=100*($cpu-$previousCpu)/($time-$previousTime);responding=$process.Responding;isForeground=([SteamyIdleNative]::GetForegroundWindow() -eq $process.MainWindowHandle);isMinimized=[SteamyIdleNative]::IsIconic($process.MainWindowHandle);isMaximized=[SteamyIdleNative]::IsZoomed($process.MainWindowHandle);clientWidth=$rect.Right-$rect.Left;clientHeight=$rect.Bottom-$rect.Top;dpi=[SteamyIdleNative]::GetDpiForWindow($process.MainWindowHandle);directChildren=$children;totalPrivateBytes=$process.PrivateMemorySize64+($children | Measure-Object privateBytes -Sum).Sum}
            $previousTime=$time; $previousCpu=$cpu
        }
    }catch{$failure=$_.Exception.ToString()}
    finally{
        # Only the retained Process instance launched above is ever closed.
        if(!$process.HasExited){$process.CloseMainWindow() | Out-Null; if(!$process.WaitForExit(10000)){$process.Kill();$process.WaitForExit()}}
        $exitCode=$process.ExitCode; $process.Dispose()
        $stdout.WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $profile 'stdout.txt')
        $stderr.WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $profile 'stderr.txt')
        $report=[pscustomobject]@{run=$run;profile=$profile;windowHandleObservedMs=$readyMs;exitCode=$exitCode;error=$failure;samples=$samples}
        $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reports ('run-'+$run+'.json'))
        $runReports += $report
    }
    if($failure){throw $failure}
    Write-Host "Ordinary idle run $run completed; exit $exitCode, samples $($samples.Count)."
}
@{scope='External process samples, ordinary Release portable entry point, glass/ambient enabled, isolated profiles, no startup hook or render preference override. Public disk fixtures seeded; public network lookups remain live and no fixed-media equivalence is asserted. Initial scan, carousel, and public requests can overlap idle. Direct children sampled separately; brief or deeper descendants can escape these samples. Window-handle time is not full UI readiness.';identity=$identity;runs=$runReports} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $reports 'idle-report.json')
