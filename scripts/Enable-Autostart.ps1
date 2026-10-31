# Register a per-user, non-elevated logon task for the local collector.
$ErrorActionPreference = 'Stop'
$taskName = 'Workstation Lens - Auto Start'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$launcher = Join-Path $repoRoot 'Start-Lens.ps1'
$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
if (-not (Test-Path -LiteralPath $launcher)) { throw "Launcher is missing: $launcher" }

$powershell = Join-Path $PSHOME 'powershell.exe'
$arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' +
    $launcher + '" -NoBrowser'
$action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $repoRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
$task = New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description 'Start Workstation Lens collector in the background when this user signs in.'

$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) {
    $existingAction = $existing.Actions | Select-Object -First 1
    if ($existingAction.Execute -ne $powershell -or $existingAction.Arguments -notmatch 'Start-Lens\.ps1') {
        throw "A task already uses the name '$taskName' and is not recognised as Workstation Lens."
    }
}

Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null
$installed = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
$details = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction Stop
if (-not $installed.Settings.Enabled -or -not (@($installed.Triggers).Count -gt 0)) {
    throw 'The autostart task was registered but does not have an enabled logon trigger.'
}
Write-Host "Enabled logon autostart: $taskName"
Write-Host "Account: $user"
Write-Host "Action: $($installed.Actions[0].Execute) $($installed.Actions[0].Arguments)"
Write-Host "State: $($installed.State) / Last result: $($details.LastTaskResult)"
