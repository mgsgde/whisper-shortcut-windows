<#
 Headless dictation smoke: pushes a WAV through the complete dictation path of the built exe
 (Silero speech gate -> Gemini -> cleanup) and checks the text against the expected transcript.
 Needs GEMINI_API_KEY. Works in Windows PowerShell 5.1 and pwsh.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Wav,
    [string]$Expected,
    [int]$TimeoutSec = 240,
    [double]$MinCoverage = 0.7
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Wav) { $Wav = Join-Path $repo 'test-audio\pause-hedda.wav' }
if (-not $Expected) { $Expected = [System.IO.Path]::ChangeExtension($Wav, '.json') }

$key = $env:GEMINI_API_KEY
if ([string]::IsNullOrEmpty($key)) {
    if ($env:SMOKE_ALLOW_MISSING_KEY -eq '1') {
        Write-Host '::warning::GEMINI_API_KEY not set, dictation smoke skipped'
        exit 0
    }
    Write-Error 'GEMINI_API_KEY is not set'
    exit 1
}

$Exe = (Resolve-Path -LiteralPath $Exe).Path
$Wav = (Resolve-Path -LiteralPath $Wav).Path
$Expected = (Resolve-Path -LiteralPath $Expected).Path

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('wsw-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$out = Join-Path $work 'out.txt'
$log = Join-Path $work 'wsw.log'

function Fail([string]$reason) {
    Write-Host "FAIL: $reason"
    if (Test-Path -LiteralPath $log) {
        Write-Host '--- last 60 log lines ---'
        Get-Content -LiteralPath $log -Tail 60 | ForEach-Object { Write-Host $_ }
    }
    exit 1
}

# The key goes only into the child's environment, not the parent's.
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.UseShellExecute = $false
$psi.Arguments = '--test-wav "' + $Wav + '" --smoke-dictate "' + $out + '" --log "' + $log + '"'
$psi.EnvironmentVariables['WSW_SMOKE_GEMINI_KEY'] = $key
# A fresh, empty offline folder each run: the Silero download is exercised too.
$psi.EnvironmentVariables['WSW_OFFLINE_ROOT'] = (Join-Path $work 'Offline')

$proc = [System.Diagnostics.Process]::Start($psi)
if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
    try { $proc.Kill() } catch { }
    $proc.WaitForExit(5000) | Out-Null
    if ($env:SMOKE_LOG_DIR -and (Test-Path -LiteralPath $log)) { Copy-Item -LiteralPath $log -Destination $env:SMOKE_LOG_DIR -Force }
    Fail "timed out after $TimeoutSec s"
}
$exit = $proc.ExitCode

if ($env:SMOKE_LOG_DIR) {
    New-Item -ItemType Directory -Force -Path $env:SMOKE_LOG_DIR | Out-Null
    if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination $env:SMOKE_LOG_DIR -Force }
}

if ($exit -ne 0) { Fail "exit code $exit" }
if (-not (Test-Path -LiteralPath $log)) { Fail 'no log written' }

$lines = @(Get-Content -LiteralPath $log)
$ready = -1; $recorded = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($ready -lt 0 -and $lines[$i] -like '*SPEECH-GATE: Silero VAD ready*') { $ready = $i }
    if ($recorded -lt 0 -and $lines[$i] -like '*Dictate: recorded*') { $recorded = $i }
}
if ($ready -lt 0) { Fail 'log has no "SPEECH-GATE: Silero VAD ready" line' }
if ($recorded -lt 0) { Fail 'log has no "Dictate: recorded" line' }
if ($ready -gt $recorded) { Fail 'the Silero VAD became ready only after the recording was taken' }

$verdict = $null
for ($i = $recorded + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'SPEECH-GATE: (keep|trim|noSpeech)[ (]') { $verdict = $Matches[1]; break }
}
# Without a keep/trim line the gate never classified this recording (not ready, or it threw): the upload went out unfiltered.
if (-not $verdict) { Fail 'no "SPEECH-GATE: keep|trim (...)" line after "Dictate: recorded": the speech gate did not run' }
if ($verdict -eq 'noSpeech') { Fail 'the speech gate judged the audio noSpeech' }
Write-Host "Speech gate verdict: $verdict"

if (-not (Test-Path -LiteralPath $out)) { Fail 'out.txt missing' }
$text = [System.IO.File]::ReadAllText($out, [System.Text.Encoding]::UTF8)
if ($text.StartsWith('ERROR')) { Fail "app reported: $text" }

function Get-Words([string]$s) {
    $s = $s.ToLowerInvariant().Replace([string][char]0xE4, 'ae').Replace([string][char]0xF6, 'oe').Replace([string][char]0xFC, 'ue').Replace([string][char]0xDF, 'ss')
    return @([regex]::Matches($s, '[a-z0-9]+') | ForEach-Object { $_.Value })
}

$expText = (Get-Content -Raw -Encoding UTF8 -LiteralPath $Expected | ConvertFrom-Json).text
$expWords = @(Get-Words $expText | Where-Object { $_.Length -ge 3 } | Select-Object -Unique)
if ($expWords.Count -eq 0) { Fail 'expected text has no words' }
$have = @{}
foreach ($w in (Get-Words $text)) { $have[$w] = $true }
$missing = @($expWords | Where-Object { -not $have.ContainsKey($_) })
$hit = $expWords.Count - $missing.Count
$cov = $hit / $expWords.Count
Write-Host ('Coverage: {0}/{1} ({2:P0})' -f $hit, $expWords.Count, $cov)
Write-Host "Result: $text"
if ($missing.Count -gt 0) { Write-Host ('Missing: ' + ($missing -join ', ')) }
if ($cov -lt $MinCoverage) { Fail ('coverage below {0:P0}' -f $MinCoverage) }

Write-Host 'Dictation smoke passed'
exit 0
