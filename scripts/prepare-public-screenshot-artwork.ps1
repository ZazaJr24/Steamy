param(
    [Parameter(Mandatory=$true)][string]$BaseArtworkDirectory,
    [Parameter(Mandatory=$true)][string]$MediaFixtureDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
function FullPath([string]$path){if([IO.Path]::IsPathRooted($path)){return [IO.Path]::GetFullPath($path)};return [IO.Path]::GetFullPath((Join-Path $repo $path))}
$fixture=FullPath $MediaFixtureDirectory; $output=FullPath $OutputDirectory
if(Test-Path -LiteralPath $output){throw 'Choose a new screenshot artwork directory.'}
$manifest=Get-Content -LiteralPath (Join-Path $fixture 'manifest.json') -Raw | ConvertFrom-Json
$feed=Get-Content -LiteralPath (Join-Path $fixture 'spotlight.json') -Raw | ConvertFrom-Json
if((Get-FileHash -LiteralPath (Join-Path $fixture 'spotlight.json') -Algorithm SHA256).Hash -ne $manifest.spotlightSha256){throw 'Fixture feed changed.'}
$http=@(Get-Content -LiteralPath (Join-Path $fixture 'http-manifest.json') -Raw | ConvertFrom-Json)
$base=@(Get-ChildItem -LiteralPath (FullPath $BaseArtworkDirectory) -File | Where-Object Name -match '^\d+_(hero|header|portrait|logo|screenshot_\d+)\.jpg$')
if($base.Count -eq 0){throw 'No canonical base artwork files found.'}
New-Item -ItemType Directory -Path $output | Out-Null
foreach($file in $base){Copy-Item -LiteralPath $file.FullName -Destination $output}
$aliases=@(foreach($game in $feed.games){foreach($kind in @('hero','header','portrait','logo')){
    $url=$game.($kind+'Url'); $source=$null; $expectedHash=$null
    if($url){
        $fingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($url))).Substring(0,12)
        $name='{0}_spotlight_{1}_{2}.jpg' -f $game.appId,$kind,$fingerprint
        $entry=$manifest.artwork | Where-Object name -eq $name | Select-Object -First 1
        if($entry){$source=Join-Path $fixture ('artwork/'+$name);$expectedHash=$entry.sha256}
        else {
            $entry=$http | Where-Object {$_.uri -eq $url -and $_.status -eq 200 -and $_.contentType -like 'image/*'} | Select-Object -First 1
            if($entry){
                if($entry.file -notmatch '^[A-F0-9]{64}\.bin$'){throw 'Unsafe recorded image filename.'}
                $source=Join-Path $fixture ('http/'+$entry.file);$expectedHash=$entry.sha256
            }
        }
    }
    if(!$source){
        $legacy=if($kind -eq 'portrait'){'library'}else{$kind}
        $name='{0}_{1}.jpg' -f $game.appId,$legacy
        $entry=$manifest.artwork | Where-Object name -eq $name | Select-Object -First 1
        if($entry){$source=Join-Path $fixture ('artwork/'+$name);$expectedHash=$entry.sha256}
    }
    if($source){
        $hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        if($hash -ne $expectedHash){throw 'Public source artwork changed.'}
        $name='{0}_{1}.jpg' -f $game.appId,$kind
        Copy-Item -LiteralPath $source -Destination (Join-Path $output $name) -Force
        [pscustomobject]@{name=$name;source=[IO.Path]::GetRelativePath($fixture,$source);sha256=$hash}
    }
}})
[ordered]@{fixtureId=$manifest.fixtureId;scope='Canonical aliases of existing public artwork; image bytes are unchanged. No credentials or chat media.';aliases=$aliases} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'public-aliases.json') -Encoding utf8
[pscustomobject]@{directory=$output;artworkFiles=@(Get-ChildItem -LiteralPath $output -File -Filter '*.jpg').Count;publicAliases=$aliases.Count} | ConvertTo-Json
