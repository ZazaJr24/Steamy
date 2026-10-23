param(
    [Parameter(Mandatory=$true)][string]$BeforeDirectory,
    [Parameter(Mandatory=$true)][string]$AfterDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
function FullPath([string]$path) {
    if ([IO.Path]::IsPathRooted($path)) { return [IO.Path]::GetFullPath($path) }
    return [IO.Path]::GetFullPath((Join-Path $repo $path))
}
function MiB($bytes) { if ($null -ne $bytes) { return [double]$bytes / 1MB }; return $null }
function Growth($start, $end, [string]$property) {
    if ($null -eq $start.$property -or $null -eq $end.$property) { return $null }
    return ([double]$end.$property - [double]$start.$property) / 1MB
}
function Percentile($values, [double]$fraction) {
    $ordered = @($values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
    if ($ordered.Count -eq 0) { return $null }
    return $ordered[[Math]::Max(0, [Math]::Ceiling($fraction * $ordered.Count) - 1)]
}
function Stats($values) {
    $numbers = @($values | Where-Object { $null -ne $_ })
    return [ordered]@{count=$numbers.Count;minimum=(Percentile $numbers 0);median=(Percentile $numbers .5);p95=(Percentile $numbers .95);maximum=(Percentile $numbers 1)}
}
function ReadGroup([string]$directory, [string]$label) {
    $directory = FullPath $directory
    $identityFile = Join-Path $directory 'comparison-input.json'
    $manifest = if (Test-Path -LiteralPath $identityFile) { Get-Content -LiteralPath $identityFile -Raw | ConvertFrom-Json } else { $null }
    if ($manifest.profiled -eq $true) { throw "$label is a profiled diagnostic run; use unprofiled runs for target comparisons." }
    if ($manifest.memoryMapping -eq $true) { throw "$label includes memory-map diagnostics; use standard runs for target comparisons." }
    if($manifest.identity.publicHttpRecording -eq $true){throw "$label records live public responses and is not a controlled performance series."}
    $identity = $manifest.identity
    $files = @(Get-ChildItem -LiteralPath $directory -Filter 'run-*.json' -File | Sort-Object Name)
    if ($files.Count -ne 5) { throw "$label requires exactly five run reports; found $($files.Count) in $directory." }
    $rows = @(); $navigation = @(); $diskTimes = @(); $network = @(); $helpers = @(); $geometries = @(); $mediaChecks = @()
    foreach ($file in $files) {
        $run = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($run.error) { throw "$label/$($file.Name): $($run.error)" }
        $expectedMessages = if ($run.discordEnabled -eq $false) { 0 } else { 2000 }
        if ($run.datasetStable -ne $true -or $run.games -ne 1000 -or $run.messages -ne $expectedMessages) { throw "$label/$($file.Name): unexpected game/message workload for shipping Discord configuration." }
        $warmFile = Join-Path $directory ($file.Name.Replace('run-', 'warm-'))
        $warm = Get-Content -LiteralPath $warmFile -Raw | ConvertFrom-Json
        if ($warm.error) { throw "$label/$warmFile`: $($warm.error)" }
        if($identity.mediaFixtureId){
            foreach($sample in @($run,$warm)){
                $liveStarts=if($null -ne $sample.controlledMedia.liveStressRequests){$sample.controlledMedia.liveStressRequests}else{$sample.controlledMedia.stressRequestStarts}
                if($sample.controlledMedia.fixtureId -ne $identity.mediaFixtureId -or $sample.controlledMedia.controlled -ne $true -or $liveStarts -ne 0 -or $sample.controlledMedia.activeRequestsAtEnd -ne 0){throw "$label/$($file.Name): fixed-media verification failed."}
            }
            if($run.controlledMedia.discoveryIdsHash -ne $warm.controlledMedia.discoveryIdsHash){throw "$label/$($file.Name): warm/cold discovery IDs differ."}
            $mediaChecks += [pscustomobject]@{feed=$run.controlledMedia.feedIdsHash;discoveries=$run.controlledMedia.discoveryIdsHash;workload=$run.controlledMedia.workloadHash}
        } elseif($run.controlledMedia -or $warm.controlledMedia){throw "$label mixes unmarked controlled-media reports with the legacy protocol."}
        $revisits = @($run.navigation | Where-Object cycle -gt 0)
        $allocationSamples = @($revisits | Where-Object { $null -ne $_.allocatedBytes })
        $navigation += $revisits
        $disk = @($run.freshDiskImages.samples)
        $diskTimes += @($disk | Where-Object loaded -eq $true | ForEach-Object elapsedMs)
        $network += [ordered]@{run=$file.BaseName;measurement=$run.networkImage}
        $helpers += [ordered]@{run=$file.BaseName;summary=$run.helperSummary}
        $geometries += [ordered]@{run=$file.BaseName;cold=$run.environment;warm=$warm.environment}
        $rows += [pscustomobject][ordered]@{
            group=$label;run=$file.BaseName;coldReadyMs=$run.firstReadyMs;warmReadyMs=$warm.firstReadyMs
            discordEnabled=($run.discordEnabled -ne $false);inputTarget=$run.inputTarget;navigationRoutes=($run.navigationRoutes -join ',')
            earlyIdleMiB=(MiB $run.idle.privateBytes);settledIdleMiB=(MiB $run.settledIdle.privateBytes);warmIdleMiB=(MiB $warm.idle.privateBytes)
            retainedPrivateGrowthMiB=(Growth $run.retainedStart $run.retainedEnd 'privateBytes')
            retainedWorkingSetGrowthMiB=(Growth $run.retainedStart $run.retainedEnd 'workingSetBytes')
            managedLiveGrowthMiB=(Growth $run.retainedStart $run.retainedEnd 'managedBytes')
            gcCommittedGrowthMiB=(Growth $run.retainedStart $run.retainedEnd 'gcCommittedBytes')
            gcHeapGrowthMiB=(Growth $run.retainedStart $run.retainedEnd 'gcHeapSizeBytes')
            retainedEndMiB=(MiB $run.retainedEnd.privateBytes);imageCacheMiB=(MiB $run.imageCacheBytes)
            inputP95Ms=$run.inputToRenderP95Ms;revisitMedianMs=(Percentile $revisits.elapsedMs .5);revisitP95Ms=(Percentile $revisits.elapsedMs .95);revisitMaxMs=(Percentile $revisits.elapsedMs 1)
            revisitAllocatedMiB=if ($allocationSamples.Count -eq $revisits.Count -and $allocationSamples.Count -gt 0) { (MiB (($allocationSamples | Measure-Object allocatedBytes -Sum).Sum)) } else { $null }
            diskLoaded=@($disk | Where-Object loaded -eq $true).Count;diskSamples=$disk.Count;diskP95Ms=(Percentile @($disk | Where-Object loaded -eq $true | ForEach-Object elapsedMs) .95)
            networkLoaded=$run.networkImage.loaded;networkTotalMs=$run.networkImage.elapsedMs;networkError=$run.networkImage.error
            settledCpuOneCorePercent=$run.settledIdleCpu.oneCorePercent;minimizedCpuOneCorePercent=$run.minimizedCpu.oneCorePercent
            maxHelperMiB=(MiB $run.helperSummary.maxHelperPrivateBytes);maxCombinedMiB=(MiB $run.helperSummary.maxCombinedPrivateBytes)
            associatedPages=$run.associatedPages;observedPagesAlive=$run.visitedMainPagesStillAlive
        }
    }
    $metrics = [ordered]@{}
    foreach ($name in @('coldReadyMs','warmReadyMs','earlyIdleMiB','settledIdleMiB','warmIdleMiB','retainedPrivateGrowthMiB','retainedWorkingSetGrowthMiB','managedLiveGrowthMiB','gcCommittedGrowthMiB','gcHeapGrowthMiB','retainedEndMiB','imageCacheMiB','inputP95Ms','revisitAllocatedMiB','settledCpuOneCorePercent','minimizedCpuOneCorePercent','maxHelperMiB','maxCombinedMiB')) {
        $metrics[$name] = Stats @($rows | ForEach-Object { $_.$name })
    }
    $routes = @($navigation | Group-Object route | ForEach-Object { [ordered]@{route=$_.Name;timing=(Stats $_.Group.elapsedMs)} })
    $controlledMedia=$null
    if($mediaChecks.Count -gt 0){
        foreach($property in @('feed','discoveries','workload')){
            if(@($mediaChecks | ForEach-Object {$_.$property} | Sort-Object -Unique).Count -ne 1 -or !$mediaChecks[0].$property){throw "$label has inconsistent fixed-media $property hashes."}
        }
        $controlledMedia=[ordered]@{fixtureId=$identity.mediaFixtureId;feedIdsHash=$mediaChecks[0].feed;discoveryIdsHash=$mediaChecks[0].discoveries;workloadHash=$mediaChecks[0].workload;verifiedRuns=$mediaChecks.Count}
    }
    return [pscustomobject]@{label=$label;directory=$directory;identity=$identity;controlledMedia=$controlledMedia;rows=$rows;metrics=$metrics;revisits=(Stats $navigation.elapsedMs);routes=$routes;freshDiskReadDecodeMs=(Stats $diskTimes);network=$network;helpers=$helpers;environment=$geometries}
}
$before = ReadGroup $BeforeDirectory 'before'
$after = ReadGroup $AfterDirectory 'after'
if (@($before.rows.discordEnabled + $after.rows.discordEnabled | Sort-Object -Unique).Count -ne 1) { throw 'Different Discord workloads cannot be compared.' }
if([bool]$before.controlledMedia -ne [bool]$after.controlledMedia){throw 'Controlled-media and legacy series cannot be compared.'}
if($before.controlledMedia){
    if($before.identity.hookSha256 -ne $after.identity.hookSha256){throw 'Controlled-media comparisons require identical measurement hooks.'}
    foreach($property in @('fixtureId','feedIdsHash','discoveryIdsHash','workloadHash')){
        if($before.controlledMedia.$property -ne $after.controlledMedia.$property){throw "Controlled-media $property differs between builds."}
    }
}
$output = FullPath $OutputDirectory
New-Item -ItemType Directory -Path $output -Force | Out-Null
@($before.rows) + @($after.rows) | Export-Csv -LiteralPath (Join-Path $output 'performance-runs.csv') -NoTypeInformation -Encoding utf8
$summary = [ordered]@{
    protocol='Five process-cold/isolated-profile stress runs and five same-profile warm starts per build. Revisited routes exclude only cycle 0. Percentiles use nearest rank; outliers remain included. Times are task/dispatcher probes, not physical input-to-display latency.'
    identicalHook = if ($before.identity.hookSha256 -and $after.identity.hookSha256) { $before.identity.hookSha256 -eq $after.identity.hookSha256 } else { $null }
    before=$before;after=$after
}
$summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $output 'comparison-summary.json') -Encoding utf8
[pscustomobject]@{Group='before';ColdMedianMs=$before.metrics.coldReadyMs.median;WarmMedianMs=$before.metrics.warmReadyMs.median;IdleMedianMiB=$before.metrics.settledIdleMiB.median;GrowthMedianMiB=$before.metrics.retainedPrivateGrowthMiB.median;NavigationP95Ms=$before.revisits.p95}
[pscustomobject]@{Group='after';ColdMedianMs=$after.metrics.coldReadyMs.median;WarmMedianMs=$after.metrics.warmReadyMs.median;IdleMedianMiB=$after.metrics.settledIdleMiB.median;GrowthMedianMiB=$after.metrics.retainedPrivateGrowthMiB.median;NavigationP95Ms=$after.revisits.p95}
