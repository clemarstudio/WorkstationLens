param([switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'WorkstationLens.App'
$executable = Join-Path $project 'bin\Release\net10.0-windows\WorkstationLens.App.exe'
$url = 'http://127.0.0.1:18765/'

if (-not (Test-Path -LiteralPath $executable)) {
    & dotnet build (Join-Path $project 'WorkstationLens.App.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Workstation Lens build failed.' }
}

$current = @(Get-CimInstance Win32_Process -Filter "Name='WorkstationLens.App.exe'" |
    Where-Object { $_.ExecutablePath -eq $executable })
if ($current.Count -eq 0) {
    Start-Process -FilePath $executable -ArgumentList '--background' -WorkingDirectory $project -WindowStyle Hidden
}

$status = $null
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline) {
    try {
        $candidate = Invoke-RestMethod -Uri ($url + 'api/status') -TimeoutSec 3 -ErrorAction Stop
        if ($candidate.machine -and $candidate.samples -ge 1) {
            $status = $candidate
            break
        }
    } catch {
        # Windows logon can take a few seconds to load .NET and start HTTP.
        # The collector can be healthy even if its first API request fails.
    }
    Start-Sleep -Milliseconds 800
}
if (-not $status) {
    throw 'Collector did not deliver its first sample within 40 seconds; check the port or Windows event logs.'
}
Write-Host "Workstation Lens running: $url (samples $($status.samples))"
if (-not $NoBrowser) { Start-Process $url }
