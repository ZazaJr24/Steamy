param(
    [Parameter(Mandatory=$true)][string]$MediaFixtureDirectory,
    [Parameter(Mandatory=$true)][string]$UriFile,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
function FullPath([string]$path){if([IO.Path]::IsPathRooted($path)){return [IO.Path]::GetFullPath($path)};return [IO.Path]::GetFullPath((Join-Path $repo $path))}
$fixture=FullPath $MediaFixtureDirectory; $output=FullPath $OutputDirectory
if(Test-Path -LiteralPath $output){throw 'Choose a new recording directory.'}
$uris=@(Get-Content -LiteralPath (FullPath $UriFile) -Raw | ConvertFrom-Json)
# Explicit public inputs only. No account endpoints, headers or private media.
foreach($text in $uris){
    $uri=[Uri]$text
    $art=$uri.Host -match '^(shared|cdn)\.(akamai|fastly)\.steamstatic\.com$' -and $uri.AbsolutePath -match '^/(store_item_assets/steam/apps|steam/apps)/\d+/[a-zA-Z0-9_./-]+\.(jpg|png)$' -and (!$uri.Query -or $uri.Query -match '^\?t=\d+$')
    $details=$uri.Host -eq 'store.steampowered.com' -and $uri.AbsolutePath -eq '/api/appdetails' -and $uri.Query -match '^\?appids=\d+&l=english&cc=us$'
    $browse=$false
    if($uri.Host -eq 'api.steampowered.com' -and $uri.AbsolutePath -eq '/IStoreBrowseService/GetItems/v1/' -and $uri.Query -match '^\?input_json=([^&]+)$'){
        $query=[Uri]::UnescapeDataString($Matches[1]) | ConvertFrom-Json
        $keys=@($query.PSObject.Properties.Name | Sort-Object) -join ','
        $contextKeys=@($query.context.PSObject.Properties.Name | Sort-Object) -join ','
        $requestKeys=@($query.data_request.PSObject.Properties.Name | Sort-Object) -join ','
        $badIds=@($query.ids | Where-Object { (@($_.PSObject.Properties.Name) -join ',') -ne 'appid' -or ($_.appid -isnot [int] -and $_.appid -isnot [long]) -or $_.appid -le 0 -or $_.appid -gt [int]::MaxValue })
        $browse=$keys -eq 'context,data_request,ids' -and $contextKeys -eq 'country_code,language,steam_realm' -and $requestKeys -eq 'include_assets,include_release' -and @($query.ids).Count -gt 0 -and @($query.ids).Count -le 100 -and $badIds.Count -eq 0 -and $query.context.language -eq 'english' -and $query.context.country_code -eq 'US' -and $query.context.steam_realm -eq 1 -and $query.data_request.include_assets -eq $true -and $query.data_request.include_release -eq $true
    }
    if($uri.Scheme -ne 'https' -or $uri.UserInfo -or (!$art -and !$details -and !$browse)){throw 'Only explicitly listed public Steam artwork/details/browse queries are supported.'}
}
New-Item -ItemType Directory -Path $output | Out-Null
$entries=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
foreach($entry in @(Get-Content -LiteralPath (Join-Path $fixture 'http-manifest.json') -Raw | ConvertFrom-Json)){
    if($entry.key -notmatch '^[A-F0-9]{64}$' -or $entry.file -ne ($entry.key+'.bin')){throw 'Unsafe inherited response filename.'}
    $body=Join-Path $fixture ('http/'+$entry.file)
    if((Get-FileHash -LiteralPath $body -Algorithm SHA256).Hash -ne $entry.sha256){throw 'Inherited response bytes changed.'}
    Copy-Item -LiteralPath $body -Destination (Join-Path $output $entry.file)
    $entries.Add($entry.key,$entry)
}
$client=[Net.Http.HttpClient]::new()
$client.Timeout=[TimeSpan]::FromSeconds(8)
try {
    foreach($text in $uris){
        $uri=[Uri]$text
        $key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($uri.AbsoluteUri)))
        if($entries.ContainsKey($key)){continue}
        $bytes=[byte[]]::new(0); $status=0; $contentType=$null; $failure=$null; $response=$null
        try {
            $response=$client.GetAsync($uri).GetAwaiter().GetResult()
            $status=[int]$response.StatusCode
            $contentType=[string]$response.Content.Headers.ContentType
            $bytes=$response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            if($bytes.Length -gt 16MB){throw 'Public response exceeds 16 MiB.'}
        } catch {
            $cause=$_.Exception
            while($cause.InnerException -and $cause -isnot [Net.Http.HttpRequestException] -and $cause -isnot [OperationCanceledException]){$cause=$cause.InnerException}
            if($cause -is [OperationCanceledException]){$failure='timeout'}
            elseif($cause -is [Net.Http.HttpRequestException]){$failure='http:'+$cause.HttpRequestError}
            else {throw}
            $status=0; $bytes=[byte[]]::new(0); $contentType=$null
        } finally {if($response){$response.Dispose()}}
        [IO.File]::WriteAllBytes((Join-Path $output ($key+'.bin')),$bytes)
        $entries.Add($key,[pscustomobject]@{key=$key;uri=$uri.AbsoluteUri;file=($key+'.bin');sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes));status=$status;contentType=$contentType;error=$failure})
        [pscustomobject]@{endpoint=($uri.Host+$uri.AbsolutePath);status=$status;error=$failure;bytes=$bytes.Length} | ConvertTo-Json -Compress
    }
} finally {$client.Dispose()}
@($entries.Values | Sort-Object key) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'http-manifest.json') -Encoding utf8
