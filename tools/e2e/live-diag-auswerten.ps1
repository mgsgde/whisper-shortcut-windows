# Wertet die LIVE-DIAG-Zeilen aus wsw.log aus (nur Zahlen).
# Aufruf: powershell -NoProfile -ExecutionPolicy Bypass -File tools/e2e/live-diag-auswerten.ps1 [-Minutes 15]
param([int]$Minutes = 15, [string]$Log = "$env:LOCALAPPDATA\WhisperShortcut\wsw.log")
$since = (Get-Date).AddMinutes(-$Minutes)
# Stille Stücke werden nicht dekodiert. Liegt ihr lautestes Fenster knapp unter -45 dB, war es vermutlich leise Sprache.
foreach ($line in Get-Content $Log -Encoding UTF8) {
    if ($line -notmatch '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+) INFO LIVE-DIAG: silent piece .*audioS=([\d.]+).*loudWindowDb=(-?[\d.]+)') { continue }
    $t = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff', $null)
    if ($t -lt $since) { continue }
    if ([double]$Matches[3] -gt -48) { "Stilles Stueck um $($t.ToString('HH:mm:ss')) ($($Matches[2]) s) mit lautestem Fenster $($Matches[3]) dB: vermutlich verlorene leise Sprache" }
}
$rows = foreach ($line in Get-Content $Log -Encoding UTF8) {
    if ($line -notmatch '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+) INFO LIVE-DIAG: piece (\d+) (.*)$') { continue }
    $t = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff', $null)
    if ($t -lt $since) { continue }
    $o = [ordered]@{ Zeit = $t.ToString('HH:mm:ss'); Stueck = [int]$Matches[2] }
    foreach ($kv in ($Matches[3] -split ' ')) { if ($kv -match '^(\w+)=(.*)$') { $o[$Matches[1]] = $Matches[2] } }
    [pscustomobject]$o
}
if (-not $rows) { "Keine LIVE-DIAG-Zeilen der letzten $Minutes Minuten. Diag-Build gestartet und live diktiert?"; return }
$rows | Format-Table Zeit, Stueck, reason, audioS, rmsDb, loudWindowDb, decodeMs, insertMs, cutToInsertedMs, pieceChars, inserted -AutoSize
"Auffaellig:"
foreach ($r in $rows) {
    $w = @()
    if ([int]$r.pieceChars -le 2) { $w += 'pieceChars<=2 (zerhackt?)' }
    if ($r.reason -ne 'Stop' -and [double]$r.audioS -lt 1.5) { $w += 'audioS<1.5 ohne Stopp' }
    if ([double]$r.loudWindowDb -lt -35) { $w += 'Sprache leiser als -35 dB' }
    if ($r.inserted -ne 'true') { $w += 'nicht eingefuegt (gehalten)' }
    if ([int]$r.insertMs -gt 600) { $w += 'Einfuegen > 600 ms' }
    if ($w) { "  Stueck $($r.Stueck) um $($r.Zeit): $($w -join '; ')" }
}
