param(
    [Parameter(Mandatory=$true)][string]$PortableDirectory,
    [Parameter(Mandatory=$true)][string]$ReportDirectory,
    [ValidateRange(1,100)][int]$Runs = 5,
    [string]$HookPath = 'tests/Steamy.PerformanceHook/bin/Release/net10.0-windows/Steamy.PerformanceHook.dll',
    [switch]$ExtendedIdle,
    [switch]$TraceNavigation,
    [switch]$MapMemory,
    [switch]$SoftwareRendering,
    [switch]$RenderTree,
    [switch]$ReducedEffects,
    [switch]$ConstructSettings,
    [string]$MediaFixtureDirectory = '',
    [string]$RecordPublicHttpDirectory = ''
)
$ErrorActionPreference = 'Stop'
if ($ReducedEffects -and !$MapMemory) { throw 'Reduced-effects counterfactuals require -MapMemory and are excluded from normal performance comparisons.' }
if ($SoftwareRendering -and !$MapMemory) { throw 'Software-rendering counterfactuals require -MapMemory and are excluded from normal performance comparisons.' }
if ($RenderTree -and !$MapMemory) { throw 'Render-tree diagnostics require -MapMemory and are excluded from normal performance comparisons.' }
if ($ConstructSettings -and !$MapMemory) { throw 'Additional settings construction requires -MapMemory and is excluded from normal performance comparisons.' }
$repo = Split-Path -Parent $PSScriptRoot
$portable = [IO.Path]::GetFullPath((Join-Path $repo $PortableDirectory))
$reports = [IO.Path]::GetFullPath((Join-Path $repo $ReportDirectory))
$hook = [IO.Path]::GetFullPath((Join-Path $repo $HookPath))
$traceTool = Join-Path $repo '.cache/diagnostics-trace/dotnet-trace.exe'
$mediaFixture=$null
if($RecordPublicHttpDirectory){
    if(!$MediaFixtureDirectory){throw 'Public HTTP recording requires a media fixture.'}
    $RecordPublicHttpDirectory=if([IO.Path]::IsPathRooted($RecordPublicHttpDirectory)){[IO.Path]::GetFullPath($RecordPublicHttpDirectory)}else{[IO.Path]::GetFullPath((Join-Path $repo $RecordPublicHttpDirectory))}
    if(Test-Path -LiteralPath $RecordPublicHttpDirectory){throw 'Choose a new HTTP recording directory.'}
    if($Runs -ne 1){throw 'A recording warmup is one diagnostic run, not a performance series.'}
}
if($MediaFixtureDirectory){
    $MediaFixtureDirectory=if([IO.Path]::IsPathRooted($MediaFixtureDirectory)){[IO.Path]::GetFullPath($MediaFixtureDirectory)}else{[IO.Path]::GetFullPath((Join-Path $repo $MediaFixtureDirectory))}
    $mediaFixture=Get-Content -LiteralPath (Join-Path $MediaFixtureDirectory 'manifest.json') -Raw | ConvertFrom-Json
    if($mediaFixture.schemaVersion -ne 1){throw 'Unsupported media fixture.'}
    if((Get-FileHash -LiteralPath (Join-Path $MediaFixtureDirectory 'spotlight.json') -Algorithm SHA256).Hash -ne $mediaFixture.spotlightSha256){throw 'Media fixture feed changed.'}
    foreach($asset in $mediaFixture.artwork){
        if($asset.name -notmatch '^\d+_[a-zA-Z0-9_]+\.(jpg|png)$' -or (Get-FileHash -LiteralPath (Join-Path $MediaFixtureDirectory ('artwork/'+$asset.name)) -Algorithm SHA256).Hash -ne $asset.sha256){throw 'Media fixture artwork changed.'}
    }
    foreach($file in $mediaFixture.catalogFiles){
        if($file.path -notmatch '^steam-catalog/(artwork-assets\.json|details/\d+\.json|artwork/\d+_[a-zA-Z0-9_]+\.(jpg|png))$' -or (Get-FileHash -LiteralPath (Join-Path $MediaFixtureDirectory $file.path) -Algorithm SHA256).Hash -ne $file.sha256){throw 'Public catalog fixture changed.'}
    }
    if($mediaFixture.httpManifestSha256){
        if((Get-FileHash -LiteralPath (Join-Path $MediaFixtureDirectory 'http-manifest.json') -Algorithm SHA256).Hash -ne $mediaFixture.httpManifestSha256){throw 'Public HTTP fixture manifest changed.'}
        foreach($file in $mediaFixture.httpFiles){
            if($file.path -notmatch '^http/[A-F0-9]{64}\.bin$' -or (Get-FileHash -LiteralPath (Join-Path $MediaFixtureDirectory $file.path) -Algorithm SHA256).Hash -ne $file.sha256){throw 'Public HTTP fixture response changed.'}
        }
    }
}
if ($TraceNavigation -and !(Test-Path -LiteralPath $traceTool)) { throw 'Install the project-local dotnet-trace tool before requesting a profiled run.' }
foreach ($file in @((Join-Path $portable 'Steamy.exe'),$hook)) { if (!(Test-Path -LiteralPath $file)) { throw "Missing benchmark component: $file" } }
New-Item -ItemType Directory -Path $reports -Force | Out-Null
# Never mistake an earlier result for the process being launched below. Keeping
# existing profiles/reports intact also makes the comparison reproducible.
$existingOutputs = @(Get-ChildItem -LiteralPath $reports -File | Where-Object { $_.Name -like 'run-*.json' -or $_.Name -like 'warm-*.json' -or $_.Name -eq 'comparison-input.json' })
if ($existingOutputs.Count -gt 0) { throw "The report directory already contains benchmark output. Choose a new ReportDirectory; existing reports were not changed: $reports" }
$identity = @{
    executableSha256 = (Get-FileHash -LiteralPath (Join-Path $portable 'Steamy.exe') -Algorithm SHA256).Hash
    hookSha256 = (Get-FileHash -LiteralPath $hook -Algorithm SHA256).Hash
    machine = $env:COMPUTERNAME
    os = [Environment]::OSVersion.VersionString
    startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    mediaFixtureId = $mediaFixture.fixtureId
    publicHttpRecording = [bool]$RecordPublicHttpDirectory
    otherSteamyProcessesAtStart = @(Get-Process -Name Steamy -ErrorAction SilentlyContinue | ForEach-Object {
        try { [pscustomobject]@{pid=$_.Id;cpuMs=$_.TotalProcessorTime.TotalMilliseconds;workingSetBytes=$_.WorkingSet64;started=$_.StartTime.ToUniversalTime().ToString('O')} }
        catch { [pscustomobject]@{pid=$_.Id;details='Unavailable'} }
    })
}
$applicationAssembly = Join-Path $portable 'Steamy.dll'
if (Test-Path -LiteralPath $applicationAssembly) { $identity.applicationAssemblySha256 = (Get-FileHash -LiteralPath $applicationAssembly -Algorithm SHA256).Hash }
for ($run = 0; $run -lt $Runs; $run++) {
    $profile = Join-Path $reports ('profile-' + $run + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $profile -Force | Out-Null
    @{ActiveRoot=$profile;LegacyImported=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'storage-location.json') -Encoding utf8
    @{FirstRunCompleted=$true;TransparencyAcknowledgement='transparency-2';AutoUpdate=$false;AutoRefresh=$false;AutoResume=$false;TrackPlayTime=$false;Language='English';Appearance='Dark';AppearanceDesignVersion=1;BackgroundColor='#000000';ReduceEffects=[bool]$ReducedEffects;DiscordAutoConnect=$false;ControllerBackendEnabled=$false;AmbientEnabled=$true;DownloadFolder=(Join-Path $profile 'downloads')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'settings.json') -Encoding utf8
    $artwork = Join-Path $profile 'artwork'
    New-Item -ItemType Directory -Path $artwork -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'artifacts/steamy-0.8/qa-artwork/1808500_hero.jpg') -Destination (Join-Path $artwork '1808500_hero.jpg')
    if($mediaFixture){
        Copy-Item -LiteralPath (Join-Path $MediaFixtureDirectory 'spotlight.json') -Destination (Join-Path $profile 'spotlight.json')
        foreach($asset in $mediaFixture.artwork){ Copy-Item -LiteralPath (Join-Path $MediaFixtureDirectory ('artwork/'+$asset.name)) -Destination (Join-Path $artwork $asset.name) }
        foreach($file in $mediaFixture.catalogFiles){
            $destination=Join-Path $profile $file.path
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $MediaFixtureDirectory $file.path) -Destination $destination
        }
    }
    $report = Join-Path $reports ('run-' + $run + '.json')
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $portable 'Steamy.exe'))
    $info.WorkingDirectory = $portable; $info.UseShellExecute = $false; $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.Environment['STEAMY_DATA_DIR'] = $profile
    $info.Environment['DOTNET_STARTUP_HOOKS'] = $hook
    $info.Environment['STEAMY_PERFORMANCE_REPORT'] = $report
    $info.Environment['STEAMY_PERFORMANCE_WINDOWED'] = '1'
    if($mediaFixture){$info.Environment['STEAMY_PERFORMANCE_MEDIA_FIXTURE']=$MediaFixtureDirectory}
    if($RecordPublicHttpDirectory){$info.Environment['STEAMY_PERFORMANCE_RECORD_PUBLIC_HTTP']=$RecordPublicHttpDirectory}
    if ($ExtendedIdle) { $info.Environment['STEAMY_PERFORMANCE_EXTENDED_IDLE'] = '1' }
    if ($MapMemory) { $info.Environment['STEAMY_PERFORMANCE_MEMORY_MAP'] = '1' }
    $info.Environment['STEAMY_PERFORMANCE_SOFTWARE_RENDERING'] = $(if ($SoftwareRendering) { '1' } else { '0' })
    $info.Environment['STEAMY_PERFORMANCE_RENDER_TREE'] = $(if ($RenderTree) { '1' } else { '0' })
    $info.Environment['STEAMY_STARTUP_TRACE'] = (Join-Path $reports ('startup-' + $run + '.txt'))
    $info.Environment['STEAMY_PERFORMANCE_SETTINGS_CONSTRUCTION'] = $(if ($ConstructSettings) { '1' } else { '0' })
    $info.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $info.RedirectStandardOutput = [bool]$MapMemory
    $info.RedirectStandardError = [bool]$MapMemory
    $process = [Diagnostics.Process]::Start($info)
    $ownedOutput = if($MapMemory){$process.StandardOutput.ReadToEndAsync()}else{$null}
    $ownedError = if($MapMemory){$process.StandardError.ReadToEndAsync()}else{$null}
    $traceProcess = $null
    try {
        if ($TraceNavigation) {
            # Attach only to the PID launched immediately above. Profiled runs are
            # diagnostic evidence, never substitutes for the unprofiled target runs.
            $traceInfo = [Diagnostics.ProcessStartInfo]::new($traceTool)
            $traceInfo.UseShellExecute = $false; $traceInfo.CreateNoWindow = $true
            $traceInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
            $traceInfo.RedirectStandardOutput = $true; $traceInfo.RedirectStandardError = $true
            foreach ($argument in @('collect','--process-id',[string]$process.Id,'--profile','gc-verbose,dotnet-sampled-thread-time','--buffersize','32','--output',(Join-Path $reports ('navigation-' + $run + '.nettrace')))) { $traceInfo.ArgumentList.Add($argument) }
            $traceProcess = [Diagnostics.Process]::Start($traceInfo)
            $traceOutput = $traceProcess.StandardOutput.ReadToEndAsync()
            $traceError = $traceProcess.StandardError.ReadToEndAsync()
        }
        $started = [Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath $report) -and !$process.HasExited -and $started.Elapsed.TotalSeconds -lt 240) { Start-Sleep -Milliseconds 200 }
        if (!(Test-Path -LiteralPath $report)) {
            $termination = if($process.HasExited){'exit code '+$process.ExitCode}else{'still running at observation deadline'}
            throw "Run $run did not produce its report ($termination). Profile: $profile"
        }
        $sample = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if ($sample.error) { throw $sample.error }
        if ($SoftwareRendering -and $sample.environment.renderPreference -ne 'SoftwareOnly') { throw 'The diagnostic process did not confirm SoftwareOnly rendering preference.' }
        if($mediaFixture -and !$RecordPublicHttpDirectory -and ($sample.controlledMedia.fixtureId -ne $mediaFixture.fixtureId -or $sample.controlledMedia.controlled -ne $true)){throw 'The fixed-media workload was not controlled. Inspect the preserved report.'}
        if ($ConstructSettings -and @($sample.settingsConstruction.samples).Count -ne 10) { throw 'The diagnostic run did not produce ten settings-construction samples.' }
        if ($MapMemory -and (@($sample.nativeMemory).Count -lt 4 -or @($sample.nativeMemory | Where-Object { $_.map.available -ne $true -or $_.map.complete -ne $true }).Count -gt 0)) { throw 'The diagnostic run did not produce four complete native-memory maps.' }
        Write-Host "Run $run complete: ready $([Math]::Round($sample.firstReadyMs)) ms, idle private $([Math]::Round($sample.idle.privateBytes / 1MB,1)) MiB."
        $process.WaitForExit(15000) | Out-Null
    } finally {
        # Only the process started above belongs to this benchmark.
        if (!$process.HasExited) { $process.CloseMainWindow() | Out-Null; if (!$process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() } }
        try {
            if($MapMemory){
                @{pid=$process.Id;exitCode=$process.ExitCode;exitCodeHex=('{0:X8}' -f [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$process.ExitCode),0));softwareRendering=[bool]$SoftwareRendering;profile=$profile} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reports ('exit-'+$run+'.json')) -Encoding utf8
                $ownedOutput.WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $reports ('stdout-'+$run+'.txt')) -Encoding utf8
                $ownedError.WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $reports ('stderr-'+$run+'.txt')) -Encoding utf8
            }
        } finally { $process.Dispose() }
        if ($null -ne $traceProcess) {
            try {
                if (!$traceProcess.WaitForExit(30000)) { $traceProcess.Kill(); $traceProcess.WaitForExit(); throw 'The owned trace collector did not finalize within 30 seconds.' }
                $traceOutput.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $reports ('trace-' + $run + '.log')) -Encoding utf8
                $traceError.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $reports ('trace-' + $run + '.error.log')) -Encoding utf8
                if ($traceProcess.ExitCode -ne 0) { throw "The trace collector failed with code $($traceProcess.ExitCode)." }
            } finally { $traceProcess.Dispose() }
        }
    }
    # A response-recording warmup is diagnostic only and needs one process. It is
    # never substituted for the five cold/warm target runs below.
    if($RecordPublicHttpDirectory){continue}
    # Reopen the same profile to measure an actual warm launch separately from the stress run.
    $warmReport = Join-Path $reports ('warm-' + $run + '.json')
    $info.Environment['STEAMY_PERFORMANCE_REPORT'] = $warmReport
    $info.Environment['STEAMY_PERFORMANCE_STARTUP_ONLY'] = '1'
    $info.Environment['STEAMY_STARTUP_TRACE'] = (Join-Path $reports ('startup-warm-' + $run + '.txt'))
    $process = [Diagnostics.Process]::Start($info)
    $ownedOutput = if($MapMemory){$process.StandardOutput.ReadToEndAsync()}else{$null}
    $ownedError = if($MapMemory){$process.StandardError.ReadToEndAsync()}else{$null}
    try {
        $started.Restart()
        while (!(Test-Path -LiteralPath $warmReport) -and !$process.HasExited -and $started.Elapsed.TotalSeconds -lt 120) { Start-Sleep -Milliseconds 200 }
        if (!(Test-Path -LiteralPath $warmReport)) {
            $termination = if($process.HasExited){'exit code '+$process.ExitCode}else{'still running at observation deadline'}
            throw "Warm run $run did not produce its report ($termination)."
        }
        $warm = Get-Content -LiteralPath $warmReport -Raw | ConvertFrom-Json
        if ($warm.error) { throw $warm.error }
        if ($SoftwareRendering -and $warm.environment.renderPreference -ne 'SoftwareOnly') { throw 'The warm diagnostic process did not confirm SoftwareOnly rendering preference.' }
        if($mediaFixture -and !$RecordPublicHttpDirectory -and ($warm.controlledMedia.fixtureId -ne $mediaFixture.fixtureId -or $warm.controlledMedia.controlled -ne $true)){throw 'The fixed-media warm run was not controlled. Inspect the preserved report.'}
        if ($MapMemory -and (@($warm.nativeMemory).Count -lt 1 -or @($warm.nativeMemory | Where-Object { $_.map.available -ne $true -or $_.map.complete -ne $true }).Count -gt 0)) { throw 'The warm diagnostic run did not produce a complete native-memory map.' }
        Write-Host "Warm $run complete: ready $([Math]::Round($warm.firstReadyMs)) ms."
        $process.WaitForExit(15000) | Out-Null
    } finally {
        if (!$process.HasExited) { $process.CloseMainWindow() | Out-Null; if (!$process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() } }
        try {
            if($MapMemory){
                @{pid=$process.Id;exitCode=$process.ExitCode;exitCodeHex=('{0:X8}' -f [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$process.ExitCode),0));softwareRendering=[bool]$SoftwareRendering;profile=$profile} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reports ('exit-warm-'+$run+'.json')) -Encoding utf8
                $ownedOutput.WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $reports ('stdout-warm-'+$run+'.txt')) -Encoding utf8
                $ownedError.WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $reports ('stderr-warm-'+$run+'.txt')) -Encoding utf8
            }
        } finally { $process.Dispose() }
    }
}
$samples = @(Get-ChildItem -LiteralPath $reports -Filter 'run-*.json' | Sort-Object Name | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
$warmSamples = @(Get-ChildItem -LiteralPath $reports -Filter 'warm-*.json' | Sort-Object Name | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
@{runs=$samples.Count;reports=$samples;warmReports=$warmSamples;identity=$identity;profiled=[bool]$TraceNavigation;memoryMapping=[bool]$MapMemory;settingsConstruction=[bool]$ConstructSettings;reducedEffects=[bool]$ReducedEffects;softwareRendering=[bool]$SoftwareRendering;renderTree=[bool]$RenderTree;runtime=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $portable 'Steamy.exe')).FileVersion;configuration='Release portable, isolated profile, 1440x900, glass enabled unless reducedEffects=true; process-cold launches, OS filesystem cache not flushed. SoftwareRendering sets a process-local diagnostic preference; equivalent appearance is not asserted. If profiled=true or memoryMapping=true, runs include additional diagnostics and must not be used to claim performance-target compliance.'} | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $reports 'comparison-input.json') -Encoding utf8
