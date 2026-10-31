$ErrorActionPreference = 'Stop'
$executables = @(
    (Join-Path $PSScriptRoot 'WorkstationLens.App\bin\Release\net10.0\WorkstationLens.App.exe'),
    (Join-Path $PSScriptRoot 'WorkstationLens.App\bin\Release\net10.0-windows\WorkstationLens.App.exe')
)
$running = @(Get-CimInstance Win32_Process -Filter "Name='WorkstationLens.App.exe'" |
    Where-Object { $_.ExecutablePath -in $executables })
if ($running.Count -eq 0) { Write-Host 'Workstation Lens is not running.'; exit 0 }
try {
    Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/control/stop' -Method Post `
        -Headers @{ 'X-Lens-Control' = 'stop' } -TimeoutSec 3 | Out-Null
} catch { Write-Warning 'Graceful stop request failed; checking process state.' }
for ($i = 0; $i -lt 20; $i++) {
    if (-not (Get-Process -Id $running[0].ProcessId -ErrorAction SilentlyContinue)) {
        Write-Host 'Workstation Lens stopped gracefully.'
        exit 0
    }
    Start-Sleep -Milliseconds 200
}
foreach ($p in $running) {
    $still = Get-CimInstance Win32_Process -Filter "ProcessId=$($p.ProcessId)" -ErrorAction SilentlyContinue
    if ($still -and $still.ExecutablePath -in $executables) {
        Stop-Process -Id $p.ProcessId -ErrorAction Stop
        Write-Host "Stopped Workstation Lens PID $($p.ProcessId)."
    }
}
