param([Parameter(Mandatory=$true)][string]$SdkPath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sdkInput = (Resolve-Path -LiteralPath $SdkPath).Path
$work = Join-Path $repoRoot ('.cache/discord-sdk-import-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
if (Test-Path -LiteralPath $sdkInput -PathType Leaf) {
    if ([IO.Path]::GetExtension($sdkInput) -ne '.zip') { throw 'Supply the official Discord Unity SDK ZIP or an extracted SDK folder.' }
    Expand-Archive -LiteralPath $sdkInput -DestinationPath (Join-Path $work 'source')
    $sdkRoot = Join-Path $work 'source'
} else { $sdkRoot = $sdkInput }

# Use Discord's generated bindings, excluding Unity's scene/player-loop components.
$bindingSources = @(Get-ChildItem -LiteralPath $sdkRoot -Recurse -File -Filter '*.cs' | Where-Object {
    $source = Get-Content -LiteralPath $_.FullName -Raw
    $source -match 'namespace\s+Discord\.Sdk' -and $source -notmatch ':\s*(?:UnityEngine\.)?(?:MonoBehaviour|ScriptableObject)' -and $source -notmatch 'using\s+UnityEditor'
})
if ($bindingSources.Count -eq 0) { throw 'No official Discord.Sdk C# bindings found. Download the Unity SDK package from the Discord Developer Portal.' }
$nativeCandidates = @(Get-ChildItem -LiteralPath $sdkRoot -Recurse -File -Filter 'discord_partner_sdk.dll' | Where-Object {
    $bytes = [IO.File]::ReadAllBytes($_.FullName)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return $false }
    $offset = [BitConverter]::ToInt32($bytes, 0x3C)
    $offset -ge 0 -and $offset + 6 -le $bytes.Length -and [BitConverter]::ToUInt32($bytes, $offset) -eq 0x00004550 -and [BitConverter]::ToUInt16($bytes, $offset + 4) -eq 0x8664
} | Sort-Object @{Expression={ if ($_.FullName -match '(?i)release') { 0 } else { 1 } }},FullName)
if ($nativeCandidates.Count -eq 0) { throw 'The package does not contain the Windows x64 Discord Social SDK library.' }
$bindingDirectory = Join-Path $work 'bindings'
New-Item -ItemType Directory -Path $bindingDirectory -Force | Out-Null
$index = 0
foreach ($sourceFile in $bindingSources) {
    $bindingText = Get-Content -LiteralPath $sourceFile.FullName -Raw
    # Discord's Unity generator wires its callback pump into PlayerLoop. WPF supplies
    # the same native pump on its dispatcher, so only the Unity host glue is removed.
    $bindingText = $bindingText -replace '(?m)^using UnityEngine;\r?$', ''
    # Mono's AOT annotations have no role in the .NET desktop JIT runtime.
    $bindingText = $bindingText -replace '(?m)^\s*\[AOT\.MonoPInvokeCallback\(typeof\([^\r\n]+\)\)\]\r?\n', ''
    if ($bindingText.Contains('UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop()')) {
        $loopPattern = '(?s)        var playerLoopSystem = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop\(\);.*?        UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop\(playerLoopSystem\);'
        if ([regex]::Matches($bindingText, $loopPattern).Count -ne 1) { throw 'Unsupported Unity callback initialization. No SDK was installed.' }
        $bindingText = [regex]::Replace($bindingText, $loopPattern, '        // Callback pumping is supplied by the WPF host.')
    }
    $bindingText = $bindingText.Replace('Debug.LogException(ex);', 'System.Diagnostics.Trace.TraceError("Unhandled Discord SDK callback exception.");')
    # Unity AOT turns static methods into fixed entry points. The .NET desktop JIT
    # requires the delegate itself to remain rooted while native code owns its pointer.
    $freePointer = 'Marshal.GetFunctionPointerForDelegate<Discord_FreeFn>(UnmanagedFree)'
    if ($bindingText.Contains($freePointer)) {
        if ([regex]::Matches($bindingText, 'public static void\* Free;').Count -ne 1) { throw 'Unsupported native callback lifetime glue. No SDK was installed.' }
        $bindingText = $bindingText.Replace('public static void* Free;', "public static void* Free;`n        private static readonly Discord_FreeFn UnmanagedFreeCallback = UnmanagedFree;")
        $bindingText = $bindingText.Replace($freePointer, 'Marshal.GetFunctionPointerForDelegate<Discord_FreeFn>(UnmanagedFreeCallback)')
    }
    $bindingText | Set-Content -LiteralPath (Join-Path $bindingDirectory ('Binding' + $index + '.cs')) -Encoding utf8
    $index++
}
$bindingProject = Join-Path $bindingDirectory 'Discord.Sdk.csproj'
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>Discord.Sdk</AssemblyName>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath $bindingProject -Encoding utf8
$compiled = Join-Path $work 'compiled'
& dotnet build $bindingProject -c Release -o $compiled --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'The official bindings could not compile outside Unity. Keep the compiler output and use a matching supported SDK package; nothing was installed.' }
$destination = Join-Path $repoRoot 'tools/DiscordSocialSdk/runtime'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $compiled 'Discord.Sdk.dll') -Destination $destination -Force
Copy-Item -LiteralPath $nativeCandidates[0].FullName -Destination $destination -Force
# Retain x64 native companion libraries from the same platform directory, if present.
foreach ($companion in Get-ChildItem -LiteralPath $nativeCandidates[0].DirectoryName -File -Filter '*.dll') {
    if ($companion.Name -eq 'discord_partner_sdk.dll' -or $companion.Name -eq 'Discord.Sdk.dll') { continue }
    $bytes = [IO.File]::ReadAllBytes($companion.FullName)
    if ($bytes.Length -lt 64) { continue }
    $offset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($offset -ge 0 -and $offset + 6 -le $bytes.Length -and [BitConverter]::ToUInt32($bytes, $offset) -eq 0x00004550 -and [BitConverter]::ToUInt16($bytes, $offset + 4) -eq 0x8664) {
        Copy-Item -LiteralPath $companion.FullName -Destination $destination -Force
    }
}
Get-ChildItem -LiteralPath $nativeCandidates[0].DirectoryName -File -Filter '*.kef' | Copy-Item -Destination $destination -Force
$licenses = @(Get-ChildItem -LiteralPath $sdkRoot -Recurse -File | Where-Object { $_.Name -match '(?i)^(license|notice|third.party)' -and $_.Extension -in @('.txt','.md','') })
$licenseDirectory = Join-Path $destination 'licenses'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
$index = 0
foreach ($license in $licenses) { Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $licenseDirectory ($index.ToString() + '-' + $license.Name)) -Force; $index++ }
@{
    source = 'Official Discord Social SDK Unity package, supplied locally by the developer'
    hostAdaptation = 'WPF dispatcher pumping replaces Unity PlayerLoop; desktop JIT removes Mono AOT annotations; native free callback delegate is permanently rooted; exception diagnostics contain no raw SDK text.'
    importedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    nativeSha256 = (Get-FileHash -LiteralPath (Join-Path $destination 'discord_partner_sdk.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    bindingsSha256 = (Get-FileHash -LiteralPath (Join-Path $destination 'Discord.Sdk.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'sdk-manifest.json') -Encoding utf8
Write-Host 'Discord SDK imported. Rebuild Steamy with -p:DiscordApplicationId=YOUR_PUBLIC_APPLICATION_ID. The runtime is included in build/publish output.'
