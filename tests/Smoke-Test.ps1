param([string]$BaseUrl = 'http://127.0.0.1:18765')
$ErrorActionPreference = 'Stop'

$s = Invoke-RestMethod -Uri "$BaseUrl/api/status" -TimeoutSec 10
for ($attempt = 0; -not $s.machine -and $attempt -lt 10; $attempt++) {
    Start-Sleep -Milliseconds 500
    $s = Invoke-RestMethod -Uri "$BaseUrl/api/status" -TimeoutSec 10
}
if (-not $s.machine) { throw 'No current machine sample.' }
$live = Invoke-RestMethod -Uri "$BaseUrl/api/live?after=0" -TimeoutSec 10
if (@($live.machines).Count -lt 1 -or [long]$live.machines[-1].time -lt 1700000000) {
    throw 'No initial 15-second samples available for new Dashboard sessions.'
}
$newer = Invoke-RestMethod -Uri "$BaseUrl/api/live?after=$($live.machines[-1].time)" -TimeoutSec 10
if (@($newer.machines | Where-Object { $_.time -le $live.machines[-1].time }).Count -gt 0) {
    throw 'Delta Live API returned already received samples.'
}
if ($s.machine.processes -ne ($s.enumeratedProcesses - $s.skippedProcesses)) {
    throw "Process count mismatch: $($s.machine.processes) / $($s.enumeratedProcesses)."
}
if ($s.machine.cpu -lt 0 -or $s.machine.cpu -gt 100) { throw 'Machine CPU outside 0..100%.' }
if ($s.machine.gpuEngine -lt 0 -or $s.machine.gpuEngine -gt 100) { throw 'WDDM GPU engine outside 0..100%.' }
if ($s.machine.ramUsed -gt $s.machine.ramTotal -or $s.machine.ramTotal -le 0) { throw 'Invalid RAM measurement.' }

$history = Invoke-RestMethod -Uri "$BaseUrl/api/history?hours=24" -TimeoutSec 10
$apps = Invoke-RestMethod -Uri "$BaseUrl/api/apps?hours=24" -TimeoutSec 10
$page = Invoke-WebRequest -Uri $BaseUrl -UseBasicParsing -TimeoutSec 10
$script = Invoke-WebRequest -Uri "$BaseUrl/app.js" -UseBasicParsing -TimeoutSec 10
$csv = Invoke-WebRequest -Uri "$BaseUrl/api/export?kind=apps&hours=24" -UseBasicParsing -TimeoutSec 10
$evidence = Invoke-RestMethod -Uri "$BaseUrl/api/evidence?hours=24" -TimeoutSec 20
$labels = Invoke-RestMethod -Uri "$BaseUrl/api/labels" -TimeoutSec 10
$md = Invoke-WebRequest -Uri "$BaseUrl/api/evidence/report.md?hours=24" -UseBasicParsing -TimeoutSec 10
$update = Invoke-RestMethod -Uri "$BaseUrl/api/update" -TimeoutSec 10
$updateScript = Invoke-WebRequest -Uri "$BaseUrl/assets/clemar-update-banner.js" -UseBasicParsing -TimeoutSec 10
if ($page.StatusCode -ne 200 -or $page.Content -notmatch 'Workstation Lens') { throw 'Dashboard HTML invalid.' }
if ($page.Content -notmatch 'id="export-download"' -or $page.Content -notmatch 'id="custom-picker"') {
    throw 'Direct Export or Custom date selector is missing.'
}
if ($page.Content -notmatch 'Charts update every 15s' -or
    $script.Content -notmatch 'customRangeLabel') {
    throw '15-second charts or selected Custom date label missing.'
}
foreach($card in @('cpu-live','gpu-live','ram-live','vram-live')){
    if($page.Content -notmatch ('id="'+$card+'"')){throw "Missing primary usage card: $card"}
}
if($page.Content -match 'id="core-live"' -or $page.Content -match 'id="paging-live"'){
    throw 'Advanced diagnostic counters still appear as primary cards.'
}
if($page.Content -notmatch 'id="history-info"' -or
    $page.Content -notmatch 'Up to 90 days of history'){
    throw 'Concise retention summary / explanation is missing.'
}
if ($page.Content -match 'Applications by accumulated CPU activity' -or
    $page.Content -match 'id="check-updates"' -or $page.Content -match 'id="export-overlay"') {
    throw 'Removed legacy dashboard controls are still visible.'
}
if ($md.StatusCode -ne 200 -or $md.Content -notmatch '# Workstation Lens' -or $md.Content -notmatch 'Measurement limitations') {
    throw 'AI-ready Markdown report is missing or invalid.'
}
if ($updateScript.StatusCode -ne 200 -or $updateScript.Content -notmatch 'mountClemarUpdater') {
    throw 'Reusable update banner is missing.'
}
if ($update.stage -notin @('idle','checking','downloading','ready','installing','unavailable','failed')) {
    throw 'Invalid update state.'
}
if ($script.StatusCode -ne 200 -or $script.Content -notmatch 'refreshStatus') { throw 'Dashboard JavaScript missing.' }
if ($csv.StatusCode -ne 200 -or $csv.Headers['Content-Type'] -notlike 'text/csv*') { throw 'CSV export invalid.' }
if ($script.Content -notmatch 'pointermove' -or $script.Content -notmatch 'tooltip') {
    throw 'Interactive chart hover is missing.'
}
if ($script.Content -notmatch 'machineToChartRow' -or $script.Content -notmatch 'rangeParams') {
    throw 'First-sample live charts or Custom range logic missing.'
}
$lastPoint=@($history)[-1]
$windowStart=[long]$lastPoint.time - 25
$windowEnd=[long]$lastPoint.time + 35
$customHistory=@(Invoke-RestMethod -Uri "$BaseUrl/api/history?from=$windowStart&to=$windowEnd" -TimeoutSec 10)
if($customHistory.Count -lt 1 -or @($customHistory|Where-Object {
    $_.time -lt ($windowStart-60) -or $_.time -ge ($windowEnd+60)
}).Count) {throw 'Custom date history filtering failed.'}
$customInsights=Invoke-RestMethod -Uri "$BaseUrl/api/insights?from=$windowStart&to=$windowEnd" -TimeoutSec 10
if([int]$customInsights.recordedMinutes -lt 1){throw 'Custom range summary empty.'}
if($null -eq $customInsights.cpuHighMinutes -or $null -eq $customInsights.gpuHighMinutes){
    throw 'CPU / GPU pressure summaries are missing.'
}
try{
    $bad=Invoke-WebRequest -Uri "$BaseUrl/api/export/bundle?from=$windowEnd&to=$windowStart" -UseBasicParsing -TimeoutSec 10
    throw 'Invalid Custom window was accepted.'
}catch{
    if($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 400){}
    else{throw}
}
$bundlePath = Join-Path $env:TEMP ("lens-test-" + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    Invoke-WebRequest -Uri "$BaseUrl/api/export/bundle?hours=1" -OutFile $bundlePath -UseBasicParsing -TimeoutSec 120
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive=[System.IO.Compression.ZipFile]::OpenRead($bundlePath)
    try {
        $names=@($archive.Entries | ForEach-Object FullName)
        foreach($required in @('workstation-lens-records.csv','report.md','evidence.json','README.txt')) {
            if($required -notin $names){throw "ZIP is missing $required"}
        }
        $readmeEntry=$archive.GetEntry('README.txt')
        $guideReader=[System.IO.StreamReader]::new($readmeEntry.Open())
        try { $guide=$guideReader.ReadToEnd() } finally { $guideReader.Dispose() }
        foreach($requiredText in @('START HERE','report.md','evidence.json','workstation-lens-records.csv',
            'CSV QUICK GUIDE','record_type','machine_minute','application_quarter','process_minute',
            'time_utc','UTC','NOT zero','14 days','90 days','cloud LLM')) {
            if(-not $guide.Contains($requiredText)) { throw "ZIP README is missing reading guidance: $requiredText" }
        }
        if($guide -match '(?<![-\w])records\.csv' -or $guide.Length -gt 2200) {
            throw 'ZIP README has an incorrect filename or is too long.'
        }
        $csvEntry=$archive.GetEntry('workstation-lens-records.csv')
        $reader=[System.IO.StreamReader]::new($csvEntry.Open())
        try {
            $header=$reader.ReadLine()
            if($header -notmatch '^record_type,time_utc,time_unix,application,pid') {
                throw 'Unified CSV is missing typed schema.'
            }
            $seen=@{}
            while(($line=$reader.ReadLine()) -ne $null) {
                $kind=$line.Split(',')[0]
                $seen[$kind]=$true
            }
            foreach($required in @('machine_minute','application_quarter','process_minute')) {
                if(-not $seen.ContainsKey($required)){throw "ZIP CSV has no $required rows."}
            }
    $customZip=Join-Path $env:TEMP ('lens-custom-'+[Guid]::NewGuid().ToString('N')+'.zip')
    try{
        Invoke-WebRequest -Uri "$BaseUrl/api/export/bundle?from=$windowStart&to=$windowEnd" -OutFile $customZip -UseBasicParsing -TimeoutSec 120
        $customArchive=[System.IO.Compression.ZipFile]::OpenRead($customZip)
        try{
            $evidenceEntry=$customArchive.GetEntry('evidence.json')
            $streamReader=[System.IO.StreamReader]::new($evidenceEntry.Open())
            try{$customEvidence=($streamReader.ReadToEnd()|ConvertFrom-Json)}finally{$streamReader.Dispose()}
            if(-not $customEvidence.customWindow -or
                [int64]([datetimeoffset]$customEvidence.requestedWindowStartUtc).ToUnixTimeSeconds() -ne $windowStart -or
                [int64]([datetimeoffset]$customEvidence.requestedWindowEndUtc).ToUnixTimeSeconds() -ne $windowEnd) {
                throw 'ZIP evidence did not preserve the requested Custom window.'
            }
        }finally{$customArchive.Dispose()}
    }finally{if(Test-Path $customZip){Remove-Item -LiteralPath $customZip -Force}}
        } finally {$reader.Dispose()}
    } finally {$archive.Dispose()}
} finally {
    if(Test-Path -LiteralPath $bundlePath){Remove-Item -LiteralPath $bundlePath -Force}
}
if ($evidence.format -ne 'workstation-lens-evidence-v1' -or $evidence.observationMinutes -lt 1) {
    throw 'Hardware evidence export is missing or empty.'
}
if ($evidence.all.cpuPct.observations -gt $evidence.observationMinutes) {
    throw 'Evidence CPU observation count is inconsistent.'
}
if ($s.machine.committedBytes -le 0 -or $s.machine.commitLimit -lt $s.machine.committedBytes) {
    throw 'Windows committed memory / commit limit measurement invalid.'
}
if ($s.machine.gpuMemoryTotal -gt 0) {
    $invalidGpuAttribution = @($apps | Where-Object {
        $_.vramPeak -ne $null -and $_.vramPeak -gt $s.machine.gpuMemoryTotal
    })
    if ($invalidGpuAttribution.Count -gt 0) {
        throw 'Untrustworthy per-process GPU attribution was presented as physical VRAM.'
    }
}
if ($s.databaseMinutes -gt 0 -and ($history.Count -eq 0 -or $apps.Count -eq 0)) {
    throw 'Recorded minutes exist but one of the archive APIs is empty.'
}

if ($apps.Count -gt 0) {
    $name = [uri]::EscapeDataString($apps[0].name)
    $null = Invoke-RestMethod -Uri "$BaseUrl/api/app-history?appName=$name&hours=24" -TimeoutSec 10
    $null = Invoke-RestMethod -Uri "$BaseUrl/api/processes?appName=$name&hours=24" -TimeoutSec 10
}

[pscustomobject]@{
    Result = 'PASS'
    Samples = $s.samples
    Processes = $s.machine.processes
    Skipped = $s.skippedProcesses
    SamplingMilliseconds = [math]::Round($s.lastSampleMs, 1)
    RecordedMinutes = $s.databaseMinutes
    ApplicationGroups = $apps.Count
    HistoricalPoints = $history.Count
    GpuMemoryGiB = [math]::Round($s.machine.gpuMemoryUsed / 1GB, 2)
    CommitGiB = [math]::Round($s.machine.committedBytes / 1GB, 2)
    PageReadsPerSec = [math]::Round($s.machine.pageReadsPerSec, 2)
    EvidenceVersion = $evidence.format
    UpdateStage = $update.stage
    ReportMarkdown = 'PASS'
    UnifiedZip = 'PASS'
    CustomRange = 'PASS'
    LiveSampleRing = 'PASS'
} | Format-List
