<#
.SYNOPSIS
  Sets up (or removes) the Task Scheduler task "WhisperShortcut Dev Update", which runs dev-daily.ps1 update every
  10 minutes while you are signed in. Run it yourself in a normal PowerShell window (not from Claude: Claude's
  container redirects AppData).

.DESCRIPTION
  The task runs a copy of dev-daily.ps1 in %LOCALAPPDATA%\WhisperShortcut Dev\dev-daily\, so switching branches in
  the checkout does not change it. Run this script again after dev-daily.ps1 changed on main.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\dev\register-task.ps1
  powershell -ExecutionPolicy Bypass -File tools\dev\register-task.ps1 -Remove
#>
param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$TaskName = 'WhisperShortcut Dev Update'

if ($Remove) {
  Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
  Write-Host "Removed task '$TaskName'. The Dev app and its data stay; uninstall it from its tray menu if wanted."
  return
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) is missing: winget install GitHub.cli, then gh auth login' }
& gh auth status 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'gh is not signed in: gh auth login' }

$state = Join-Path $env:LOCALAPPDATA 'WhisperShortcut Dev\dev-daily'
New-Item -ItemType Directory -Force $state | Out-Null
$script = Join-Path $state 'dev-daily.ps1'
Copy-Item (Join-Path $PSScriptRoot 'dev-daily.ps1') $script -Force

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
  -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$script`" update"
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 10)
# Interactive: only while you are signed in, so a restarted Dev app appears on your desktop.
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
  -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 15)
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
  -Description 'Installs the newest green main build of WhisperShortcut as "WhisperShortcut Dev" (tools/dev/dev-daily.ps1).' -Force | Out-Null

Write-Host "Task '$TaskName' runs every 10 minutes: $script"
Write-Host "First install now (without waiting): powershell -ExecutionPolicy Bypass -File `"$script`" update"
Write-Host "Log: $state\dev-daily.log"
