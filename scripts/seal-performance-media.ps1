param(
    [Parameter(Mandatory=$true)][string]$MediaFixtureDirectory,
    [Parameter(Mandatory=$true)][string]$RecordedHttpDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
function FullPath([string]$path){if([IO.Path]::IsPathRooted($path)){return [IO.Path]::GetFullPath($path)};return [IO.Path]::GetFullPath((Join-Path $repo $path))}
$source=FullPath $MediaFixtureDirectory; $recording=FullPath $RecordedHttpDirectory; $output=FullPath $OutputDirectory
if(Test-Path -LiteralPath $output){throw 'Choose a new sealed fixture directory.'}
$manifest=Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw | ConvertFrom-Json
$entries=@(Get-Content -LiteralPath (Join-Path $recording 'http-manifest.json') -Raw | ConvertFrom-Json)
if($entries.Count -eq 0){throw 'The HTTP recording has no responses.'}
$baseFiles=@([pscustomobject]@{path='spotlight.json';sha256=$manifest.spotlightSha256})
$baseFiles+=@($manifest.artwork | ForEach-Object { [pscustomobject]@{path=('artwork/'+$_.name);sha256=$_.sha256} })
$baseFiles+=@($manifest.catalogFiles)
foreach($file in $baseFiles){
    if($file.path -notmatch '^(spotlight\.json|artwork/\d+_[a-zA-Z0-9_]+\.(jpg|png)|steam-catalog/(artwork-assets\.json|details/\d+\.json|artwork/\d+_[a-zA-Z0-9_]+\.(jpg|png)))$'){throw 'Unexpected base fixture path.'}
    $original=Join-Path $source $file.path
    if((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $file.sha256){throw 'Base fixture bytes changed.'}
    $destination=Join-Path $output $file.path
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $original -Destination $destination
}
$httpFiles=@(foreach($entry in $entries | Sort-Object key){
    if($entry.key -notmatch '^[A-F0-9]{64}$' -or $entry.file -ne ($entry.key+'.bin')){throw 'Unsafe recorded response filename.'}
    $uri=[Uri]$entry.uri
    if($uri.Scheme -ne 'https' -or $uri.UserInfo){throw 'Unexpected recorded response URI.'}
    $key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($uri.AbsoluteUri)))
    $original=Join-Path $recording $entry.file
    if($key -ne $entry.key -or (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $entry.sha256){throw 'Recorded response identity changed.'}
    $destination=Join-Path $output ('http/'+$entry.file)
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $original -Destination $destination
    [pscustomobject]@{path=('http/'+$entry.file);sha256=$entry.sha256;bytes=(Get-Item -LiteralPath $destination).Length}
})
$total=(Get-ChildItem -LiteralPath $output -File -Recurse | Measure-Object Length -Sum).Sum
if($total -gt 512MB){throw 'The sealed public fixture exceeds 512 MiB.'}
$entries | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'http-manifest.json') -Encoding utf8
$httpHash=(Get-FileHash -LiteralPath (Join-Path $output 'http-manifest.json') -Algorithm SHA256).Hash
$parent=$manifest.fixtureId
$identity=$parent+"`n"+$httpHash+"`n"+($httpFiles | ForEach-Object {$_.path+' '+$_.sha256+' '+$_.bytes} | Join-String -Separator "`n")
$manifest.fixtureId=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity)))
$manifest | Add-Member -NotePropertyName parentFixtureId -NotePropertyValue $parent -Force
$manifest | Add-Member -NotePropertyName httpManifestSha256 -NotePropertyValue $httpHash -Force
$manifest | Add-Member -NotePropertyName httpFiles -NotePropertyValue $httpFiles -Force
if($manifest.scope -notlike '*Public GET responses are recorded*'){$manifest.scope+=' Public GET responses are recorded once and replayed locally in controlled runs; live image-network timing is a separate probe. Recording runs are excluded from target comparisons.'}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8
[pscustomobject]@{directory=$output;fixtureId=$manifest.fixtureId;publicResponses=$entries.Count;bytes=$total} | ConvertTo-Json
