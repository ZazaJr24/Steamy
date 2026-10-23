param([Parameter(Mandatory=$true)][string]$ReportName)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$directory=Join-Path $repo 'artifacts/performance-refresh'
if($ReportName -notmatch '^[a-z0-9-]+$'){throw 'ReportName must be a simple filename prefix.'}
$marker=Join-Path $directory ($ReportName+'.pid')
if(Test-Path -LiteralPath $marker){throw 'Diagnostic output already exists; choose a new ReportName.'}
$env:STEAMY_UI_LIFETIME_DIAGNOSTIC=$marker
Add-Type @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class GalleryTestDump {
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("dbghelp.dll", SetLastError=true)] public static extern bool MiniDumpWriteDump(IntPtr process, int pid, SafeFileHandle file, uint flags, IntPtr exception, IntPtr user, IntPtr callback);
}
'@
$runner=Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @('test','tests/Steamy.UiTests/Steamy.UiTests.csproj','-c','Release','--no-restore','--disable-build-servers','-m:1','--filter','FullyQualifiedName~RemovedDocumentGalleryIsCollectible','--logger',('trx;LogFileName='+$ReportName+'.trx'),'--results-directory','artifacts/performance-refresh/checks') -WorkingDirectory $repo -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $directory ($ReportName+'-stdout.txt')) -RedirectStandardError (Join-Path $directory ($ReportName+'-stderr.txt'))
try {
    $wait=[Diagnostics.Stopwatch]::StartNew()
    while(!(Test-Path -LiteralPath $marker) -and !$runner.HasExited -and $wait.Elapsed.TotalSeconds -lt 50){Start-Sleep -Milliseconds 100}
    if(!(Test-Path -LiteralPath $marker)){throw 'The owned test did not reach its diagnostic pause.'}
    $testProcessId=[int](Get-Content -LiteralPath $marker -First 1)
    $test=Get-Process -Id $testProcessId -ErrorAction Stop
    if($test.ProcessName -ne 'testhost' -or $test.StartTime -lt $runner.StartTime){throw 'Diagnostic process identity mismatch.'}
    $handle=[GalleryTestDump]::OpenProcess(0x450,$false,$testProcessId)
    if($handle -eq [IntPtr]::Zero){throw 'Could not read the owned test process.'}
    $file=[IO.File]::Create((Join-Path $directory ($ReportName+'.dmp')))
    try {
        if(![GalleryTestDump]::MiniDumpWriteDump($handle,$testProcessId,$file.SafeFileHandle,0x1826,[IntPtr]::Zero,[IntPtr]::Zero,[IntPtr]::Zero)){
            throw ('External dump failed, Win32 error '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())
        }
    } finally {$file.Dispose(); [GalleryTestDump]::CloseHandle($handle) | Out-Null}
    'continue' | Set-Content -LiteralPath ($marker+'.continue')
    $runner.WaitForExit(10000) | Out-Null
    Get-Content -LiteralPath (Join-Path $directory ($ReportName+'-stdout.txt')) -Tail 16
    Get-Content -LiteralPath $marker
    Get-Content -LiteralPath ($marker+'.after')
} finally {$runner.Dispose(); Remove-Item Env:STEAMY_UI_LIFETIME_DIAGNOSTIC -ErrorAction SilentlyContinue}
