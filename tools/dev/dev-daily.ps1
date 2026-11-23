<#
.SYNOPSIS
  Keeps "WhisperShortcut Dev" on Magnus's PC at the newest green main build. Port of the macOS repo's
  scripts/dev-daily.sh. Runs from the Windows Task Scheduler every 10 minutes (tools/dev/register-task.ps1).

.DESCRIPTION
  update (default)
    1. Newest CI run on main that succeeded (push or dispatch) and kept the artifact WhisperShortcut-dev-x64
       (the exe that passed the dictation smoke check). Commits listed in bad-sha.txt are skipped.
    2. Nothing to do if it is installed already; never a downgrade (GitHub compare must say "ahead").
    3. If the Dev app runs: wait while it records a meeting (live-meeting-recording.json with a live pid) or
       while the user was active in the last 90 s. Then ask it to quit (its Quit event), kill after 10 s.
    4. Swap the exe (the old one becomes previous.exe), keep dev-instance.txt next to it, start it again if
       it ran before. Still alive after 20 s, or the commit goes into bad-sha.txt and previous.exe comes back.
    The normal app (%LOCALAPPDATA%\Programs\WhisperShortcut) is never touched.
  use-dev / use-release   start that instance with --takeover (it makes the other one quit). Refuses while
                          the instance being replaced records a meeting.
  copy-settings           one-time copy of the normal app's settings and data into the Dev data folders
                          (Dev must not run). The API keys come along (DPAPI, same Windows user). Offline models
                          are shared anyway. -Force overwrites an existing Dev setup.
  status                  installed commit, newest green build, state folder.

  State and log: %LOCALAPPDATA%\WhisperShortcut Dev\dev-daily\ (installed-sha, bad-sha.txt, previous.exe,
  dev-daily.log). Needs the GitHub CLI (gh), signed in. Works in Windows PowerShell 5.1.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\dev\dev-daily.ps1 update -DryRun
#>
param(
  [ValidateSet('update', 'use-dev', 'use-release', 'copy-settings', 'status')]
  [string]$Action = 'update',
  [switch]$DryRun,   # decide and print; no download, no stop, no swap
  [switch]$Force     # update: ignore the 90 s idle rule; copy-settings: overwrite
)
$ErrorActionPreference = 'Stop'

$Repo = 'mgsgde/whisper-shortcut-windows'
$Artifact = 'WhisperShortcut-dev-x64'
$Programs = Join-Path $env:LOCALAPPDATA 'Programs'
$DevDir = Join-Path $Programs 'WhisperShortcut Dev'
$DevExe = Join-Path $DevDir 'WhisperShortcut.exe'
$NormalExe = Join-Path $Programs 'WhisperShortcut\WhisperShortcut.exe'
$DevLocal = Join-Path $env:LOCALAPPDATA 'WhisperShortcut Dev'
$NormalLocal = Join-Path $env:LOCALAPPDATA 'WhisperShortcut'
$State = Join-Path $DevLocal 'dev-daily'
$MarkerName = 'live-meeting-recording.json'
$DevQuitEvent = 'Local\WhisperShortcut.Windows.Dev.Quit'
$IdleSeconds = 90

New-Item -ItemType Directory -Force $State | Out-Null
$LogFile = Join-Path $State 'dev-daily.log'
function Log([string]$msg) {
  $line = '{0:yyyy-MM-dd HH:mm:ss} {1}{2}' -f (Get-Date), $(if ($DryRun) { 'DRY ' } else { '' }), $msg
  Write-Host $line
  try {
    if ((Test-Path $LogFile) -and (Get-Item $LogFile).Length -gt 1MB) { Move-Item $LogFile "$LogFile.1" -Force }
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
  } catch { }
}

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe' }
function Gh { $out = & $gh @args 2>&1; if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' '): $out" }; $out }

function Get-InstanceProcess([string]$exe) {
  Get-Process -Name WhisperShortcut -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -ieq [IO.Path]::GetFullPath($exe)) } catch { $false }
  }
}

function Test-MeetingRecording([string]$localDir) {
  $f = Join-Path $localDir $MarkerName
  if (-not (Test-Path $f)) { return $false }
  try { $m = Get-Content $f -Raw | ConvertFrom-Json } catch { return $false }
  if (-not ($m.pid -as [int])) { return $false }
  return [bool](Get-Process -Id ([int]$m.pid) -ErrorAction SilentlyContinue)
}

Add-Type -Namespace WswDev -Name Idle -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
public static double Seconds() {
  var i = new LASTINPUTINFO(); i.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(i);
  if (!GetLastInputInfo(ref i)) return 0;
  return unchecked((uint)System.Environment.TickCount - i.dwTime) / 1000.0;
}
'@

function Stop-Dev {
  $p = Get-InstanceProcess $DevExe
  if (-not $p) { return }
  try {
    $ev = [System.Threading.EventWaitHandle]::OpenExisting($DevQuitEvent)
    [void]$ev.Set(); $ev.Dispose()
  } catch { Log "Quit event not found ($DevQuitEvent); stopping the process" }
  $p | ForEach-Object { [void]$_.WaitForExit(10000) }
  Get-InstanceProcess $DevExe | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 500
}

function Read-Lines([string]$path) { if (Test-Path $path) { @(Get-Content $path | Where-Object { $_ }) } else { @() } }

function Find-Candidate {
  $bad = Read-Lines (Join-Path $State 'bad-sha.txt')
  $runs = Gh api "repos/$Repo/actions/workflows/ci.yml/runs?branch=main&status=success&per_page=20" `
    --jq '.workflow_runs[] | select(.event == \"push\" or .event == \"workflow_dispatch\") | \"\(.id) \(.head_sha)\"'
  foreach ($r in @($runs)) {
    $id, $sha = "$r".Split(' ')
    if ($bad -contains $sha) { Log "skip $($sha.Substring(0,8)): in bad-sha.txt"; continue }
    $has = Gh api "repos/$Repo/actions/runs/$id/artifacts" --jq "[.artifacts[] | select(.name == \`"$Artifact\`" and (.expired | not))] | length"
    if ([int]"$has" -gt 0) { return @{ Id = $id; Sha = $sha } }
  }
  return $null
}

function Invoke-Update {
  $mutex = New-Object System.Threading.Mutex($false, 'Local\WhisperShortcut.DevDaily')
  if (-not $mutex.WaitOne(0)) { Log 'another run is busy'; return }
  try {
    $c = Find-Candidate
    if (-not $c) { Log 'no green main build with a Dev artifact'; return }
    $shaFile = Join-Path $State 'installed-sha'
    $installed = if (Test-Path $shaFile) { (Get-Content $shaFile -Raw).Trim() } else { '' }
    if ($installed -eq $c.Sha -and (Test-Path $DevExe)) { Log "up to date at $($c.Sha.Substring(0,8))"; return }
    if ($installed -and (Test-Path $DevExe)) {
      $rel = Gh api "repos/$Repo/compare/$installed...$($c.Sha)" --jq .status
      if ("$rel" -ne 'ahead') { Log "newest green $($c.Sha.Substring(0,8)) is $rel of installed $($installed.Substring(0,8)); no downgrade"; return }
    }
    $running = [bool](Get-InstanceProcess $DevExe)
    if ($running) {
      if (Test-MeetingRecording $DevLocal) { Log 'Dev records a meeting; try again later'; return }
      $idle = [WswDev.Idle]::Seconds()
      if (-not $Force -and $idle -lt $IdleSeconds) { Log ("user active {0:0} s ago; try again later" -f $idle); return }
    }
    Log "installing $($c.Sha.Substring(0,8)) (run $($c.Id)), Dev running: $running"
    if ($DryRun) { return }

    $staging = Join-Path $State 'staging'
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    Gh run download $c.Id -R $Repo -n $Artifact -D $staging | Out-Null
    $newExe = Join-Path $staging 'WhisperShortcut.exe'
    if (-not (Test-Path $newExe)) { throw "artifact has no WhisperShortcut.exe" }

    New-Item -ItemType Directory -Force $DevDir | Out-Null
    Set-Content -Path (Join-Path $DevDir 'dev-instance.txt') -Value 'Diese Datei macht WhisperShortcut.exe hier zur Dev-Version. Nicht löschen.' -Encoding UTF8
    if ($running) { Stop-Dev }
    $previous = Join-Path $State 'previous.exe'
    if (Test-Path $DevExe) { Copy-Item $DevExe $previous -Force }
    Copy-Item $newExe $DevExe -Force
    Set-Content -Path $shaFile -Value $c.Sha -Encoding ASCII
    if (-not $running) { Log "installed $($c.Sha.Substring(0,8)) (not started: it was not running)"; return }

    Start-Process -FilePath $DevExe
    Start-Sleep -Seconds 20
    if (Get-InstanceProcess $DevExe) { Log "installed and restarted $($c.Sha.Substring(0,8))"; return }
    Log "Dev exited within 20 s after $($c.Sha.Substring(0,8)); restoring the previous exe"
    Add-Content -Path (Join-Path $State 'bad-sha.txt') -Value $c.Sha
    if (Test-Path $previous) {
      Copy-Item $previous $DevExe -Force
      if ($installed) { Set-Content -Path $shaFile -Value $installed -Encoding ASCII } else { Remove-Item $shaFile -Force }
      Start-Process -FilePath $DevExe
    }
  } finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
}

function Switch-To([string]$targetExe, [string]$otherLocal, [string]$label) {
  if (-not (Test-Path $targetExe)) { throw "$label is not installed: $targetExe" }
  if (Test-MeetingRecording $otherLocal) { throw "The running instance records a meeting; not switching." }
  Log "starting $label with --takeover"
  if (-not $DryRun) { Start-Process -FilePath $targetExe -ArgumentList '--takeover' }
}

function Copy-Settings {
  if (Get-InstanceProcess $DevExe) { throw 'Quit WhisperShortcut Dev first.' }
  $pairs = @(
    @{ From = (Join-Path $env:APPDATA 'WhisperShortcut'); To = (Join-Path $env:APPDATA 'WhisperShortcut Dev') },
    @{ From = $NormalLocal; To = $DevLocal }
  )
  foreach ($p in $pairs) {
    if (-not (Test-Path $p.From)) { continue }
    $existing = Join-Path $p.To 'settings.json'
    if ((Test-Path $existing) -and -not $Force) { throw "Dev already has settings ($existing). Use -Force to overwrite." }
    Log "copy $($p.From) -> $($p.To)"
    if ($DryRun) { continue }
    New-Item -ItemType Directory -Force $p.To | Out-Null
    # Not the log, not the meeting marker, not this script's state; offline models are shared, not copied.
    Get-ChildItem $p.From -Force | Where-Object { $_.Name -notin @('wsw.log', 'wsw.log.1', $MarkerName, 'dev-daily', 'offline', 'Offline') } |
      ForEach-Object { Copy-Item $_.FullName (Join-Path $p.To $_.Name) -Recurse -Force }
  }
}

try {
  switch ($Action) {
    'update' { Invoke-Update }
    'use-dev' { Switch-To $DevExe $NormalLocal 'WhisperShortcut Dev' }
    'use-release' { Switch-To $NormalExe $DevLocal 'WhisperShortcut' }
    'copy-settings' { Copy-Settings }
    'status' {
      $shaFile = Join-Path $State 'installed-sha'
      Write-Host "Dev exe:      $DevExe ($(if (Test-Path $DevExe) { 'installed' } else { 'missing' }))"
      Write-Host "installed:    $(if (Test-Path $shaFile) { (Get-Content $shaFile -Raw).Trim() } else { '-' })"
      $c = Find-Candidate
      Write-Host "newest green: $(if ($c) { "$($c.Sha) (run $($c.Id))" } else { '-' })"
      Write-Host "state, log:   $State"
    }
  }
} catch {
  Log "ERROR: $($_.Exception.Message)"
  exit 1
}
