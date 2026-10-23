param(
    [Parameter(Mandatory=$true)][string]$SourceReports,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [switch]$IncludePublicCatalog
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
function FullPath([string]$path) {
    if([IO.Path]::IsPathRooted($path)){return [IO.Path]::GetFullPath($path)}
    return [IO.Path]::GetFullPath((Join-Path $repo $path))
}
$source=FullPath $SourceReports
$output=FullPath $OutputDirectory
if(Test-Path -LiteralPath $output){throw 'Choose a new fixture directory; existing evidence is preserved.'}
$profiles=@(Get-ChildItem -LiteralPath $source -Directory -Filter 'profile-*' | Sort-Object Name)
if($profiles.Count -eq 0){throw 'No benchmark profiles found.'}
$feeds=@(foreach($profile in $profiles){
    $path=Join-Path $profile.FullName 'spotlight.json'
    if(!(Test-Path -LiteralPath $path)){throw 'A source profile has no saved Spotlight feed.'}
    [pscustomobject]@{path=$path;hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
})
if(@($feeds.hash | Sort-Object -Unique).Count -ne 1){throw 'Source profiles contain different feeds; select a uniform source cohort.'}
$feed=Get-Content -LiteralPath $feeds[0].path -Raw | ConvertFrom-Json
if($feed.schemaVersion -ne 1 -or @($feed.games).Count -lt 1 -or @($feed.games).Count -gt 80){throw 'Invalid fixture feed.'}
# This is an isolated benchmark cache, not a product data change. A newer cached
# timestamp makes the existing service retain this exact metadata on refresh.
$feed.updatedAt='2100-01-01T00:00:00+00:00'
New-Item -ItemType Directory -Path (Join-Path $output 'artwork') -Force | Out-Null
$feed | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $output 'spotlight.json') -Encoding utf8
$assets=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($profile in $profiles){
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $profile.FullName 'artwork') -File | Sort-Object Name){
        if($file.Name -notmatch '^\d+_[a-zA-Z0-9_]+\.(jpg|png)$'){continue}
        $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        if($assets.ContainsKey($file.Name)){
            if($assets[$file.Name].sha256 -ne $hash){throw "Public artwork differs between source profiles: $($file.Name)"}
            continue
        }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $output ('artwork/'+$file.Name))
        $assets.Add($file.Name,[pscustomobject]@{name=$file.Name;sha256=$hash;bytes=$file.Length})
    }
}
$files=@($assets.Values | Sort-Object name)
if($files.Count -eq 0 -or ($files.bytes | Measure-Object -Sum).Sum -gt 512MB){throw 'Fixture artwork is empty or exceeds the public disk-cache budget.'}
$feedHash=(Get-FileHash -LiteralPath (Join-Path $output 'spotlight.json') -Algorithm SHA256).Hash
$catalogFiles=@()
if($IncludePublicCatalog){
    $catalog=Join-Path $profiles[0].FullName 'steam-catalog'
    $catalogFiles=@(foreach($sourceFile in Get-ChildItem -LiteralPath $catalog -File -Recurse | Sort-Object FullName){
        $relative=[IO.Path]::GetRelativePath($catalog,$sourceFile.FullName).Replace('\','/')
        $metadata=$relative -eq 'artwork-assets.json' -or $relative -match '^details/\d+\.json$'
        $image=$relative -match '^artwork/\d+_[a-zA-Z0-9_]+\.(jpg|png)$'
        if(!$metadata -and !$image){continue}
        $destination=Join-Path $output ('steam-catalog/'+$relative)
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        if($metadata){
            $saved=Get-Content -LiteralPath $sourceFile.FullName -Raw | ConvertFrom-Json
            if($relative -eq 'artwork-assets.json'){
                foreach($entry in $saved.PSObject.Properties){$entry.Value.FetchedAt='2100-01-01T00:00:00+00:00'}
            } else {$saved.FetchedAt='2100-01-01T00:00:00+00:00'}
            $saved | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $destination -Encoding utf8
        } else {Copy-Item -LiteralPath $sourceFile.FullName -Destination $destination}
        [pscustomobject]@{path=('steam-catalog/'+$relative);sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash;bytes=(Get-Item -LiteralPath $destination).Length}
    })
    if(@($catalogFiles | Where-Object path -Like 'steam-catalog/details/*').Count -eq 0){throw 'The requested fixture has no saved public details.'}
}
$identity=$feedHash+"`n"+($files | ForEach-Object { $_.name+' '+$_.sha256+' '+$_.bytes } | Join-String -Separator "`n")+"`n"+($catalogFiles | ForEach-Object { $_.path+' '+$_.sha256+' '+$_.bytes } | Join-String -Separator "`n")
$fixtureId=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity)))
[ordered]@{
    schemaVersion=1;fixtureId=$fixtureId;sourceReports=$source;sourceFeedSha256=$feeds[0].hash
    spotlightSha256=$feedHash;spotlightGames=@($feed.games).Count;artwork=$files;catalogFiles=$catalogFiles
    scope='Fixed public Steam metadata and artwork from prior isolated benchmark profiles. Only cache freshness timestamps change; game metadata and artwork bytes remain unchanged. Contains no credentials or chat media.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8
[pscustomobject]@{directory=$output;fixtureId=$fixtureId;games=@($feed.games).Count;images=$files.Count;bytes=($files.bytes | Measure-Object -Sum).Sum} | ConvertTo-Json
