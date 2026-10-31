param([int]$WaitSeconds = 75)

$ErrorActionPreference = 'Stop'
$baseUrl = 'http://127.0.0.1:18765'
function Get-Lens {
    Invoke-RestMethod -Uri "$baseUrl/api/status" -TimeoutSec 5
}
$status = $null
$deadline = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $deadline) {
    try { $status = Get-Lens; if ($status.samples -ge 1) { break } }
    catch { Start-Sleep -Seconds 2 }
}
if ($null -eq $status -or $status.samples -lt 1) {
    throw 'Collector is not running. Start Workstation Lens from Start Menu and retry.'
}
$version = Invoke-RestMethod -Uri "$baseUrl/api/update" -TimeoutSec 10
if (-not $version.canAutoUpdate) {
    throw 'This is not a Velopack-installed customer build. Did you run Setup.exe?'
}
$page=Invoke-WebRequest -Uri $baseUrl -UseBasicParsing -TimeoutSec 10
$script=Invoke-WebRequest -Uri "$baseUrl/app.js" -UseBasicParsing -TimeoutSec 10
if ($page.Content -notmatch 'id="export-download"' -or
    $page.Content -notmatch 'id="custom-picker"' -or
    $page.Content -match 'id="export-overlay"' -or
    $script.Content -notmatch 'machineToChartRow') {
    throw 'Expected direct Export, Custom dates, or first-sample Live UI missing.'
}
$bundle=Join-Path $env:TEMP ('customer-evidence-'+[Guid]::NewGuid().ToString('N')+'.zip')
try {
    Invoke-WebRequest -Uri "$baseUrl/api/export/bundle?hours=1" -UseBasicParsing -OutFile $bundle -TimeoutSec 120
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($bundle)
    try {
        $names=@($zip.Entries|ForEach-Object FullName)
        foreach($required in @('workstation-lens-records.csv','report.md','evidence.json')){
            if($required -notin $names){throw "Unified export missing $required"}
        }
    } finally {$zip.Dispose()}
} finally {if(Test-Path -LiteralPath $bundle){Remove-Item -LiteralPath $bundle -Force}}
if (-not $status.machine -or $status.machine.ramTotal -le 0) {
    throw 'First Live sample unavailable before first completed historical minute.'
}
$firstLive=Invoke-RestMethod -Uri "$baseUrl/api/live?after=0" -TimeoutSec 10
if(@($firstLive.machines).Count -lt 1){throw 'Dashboard has no 15-second live sample.'}
$lastTime=[int64]$firstLive.machines[-1].time
Start-Sleep -Seconds 17
$nextLive=Invoke-RestMethod -Uri "$baseUrl/api/live?after=$lastTime" -TimeoutSec 10
if(@($nextLive.machines).Count -lt 1){throw 'No fresh 15-second chart point arrived.'}
if([int64]$nextLive.machines[-1].time -le $lastTime){throw 'Live chart sample timestamp did not advance.'}
$now=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$customFrom=$now-600
$customTo=$now-60
$test=Invoke-RestMethod -Uri "$baseUrl/api/insights?from=$customFrom&to=$customTo" -TimeoutSec 10
if ($null -eq $test.recordedMinutes){throw 'Custom time range API unavailable.'}
$running=Get-CimInstance Win32_Process -Filter "Name='WorkstationLens.App.exe'"|Select-Object -First 1
if(-not $running){throw 'Customer process is not running.'}
$exeBytes=[IO.File]::ReadAllBytes($running.ExecutablePath)
$pe=[BitConverter]::ToInt32($exeBytes,0x3C)
$subsystem=[BitConverter]::ToUInt16($exeBytes,$pe+24+68)
if($subsystem -ne 2){throw 'Installer still launches a Windows console process.'}
$path = Join-Path $env:LOCALAPPDATA 'WorkstationLens\measurements.db'
if (-not (Test-Path -LiteralPath $path)) { throw "Customer SQLite database not found: $path" }
$initialCount = [int]$status.databaseMinutes
Start-Sleep -Seconds ([Math]::Max(50,$WaitSeconds-17))
$after = Get-Lens
if ([int]$after.databaseMinutes -le $initialCount) { throw 'Collector did not persist another minute of history.' }
if ($after.lastError) { throw "Collector error: $($after.lastError)" }
[pscustomobject]@{
    Verdict = 'PASS'
    InstalledVersion = $version.currentVersion
    InstalledUpdateEnabled = $version.canAutoUpdate
    RecordedMinutesBefore = $initialCount
    RecordedMinutesAfter = $after.databaseMinutes
    ProcessCount = $after.machine.processes
    SkippedProcesses = $after.skippedProcesses
    NoConsoleWindow = $true
    UnifiedExportZIP = 'OK'
    CustomRangeAPI = 'OK'
    FirstLiveSample = 'OK'
    Live15SecondUpdate = 'OK'
    DataPath = $path
} | Format-List
Write-Host 'First-install customer smoke test PASS. Version-to-version update not yet tested.' -ForegroundColor Green
