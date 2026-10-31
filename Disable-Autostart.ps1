# Remove only the task installed by Enable-Autostart.ps1.
$ErrorActionPreference = 'Stop'
$taskName = 'Workstation Lens - Auto Start'
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (-not $task) {
    Write-Host 'Workstation Lens autostart is already disabled.'
    exit 0
}
$action = $task.Actions | Select-Object -First 1
if ($action.Arguments -notmatch 'Start-Lens\.ps1') {
    throw "Task '$taskName' has a different action. Nothing was removed."
}
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
Write-Host 'Workstation Lens autostart disabled. Currently running collector is unaffected.'
