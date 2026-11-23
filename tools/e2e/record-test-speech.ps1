<#
 Records a real-microphone test WAV for SileroRealSpeechTests: 16 kHz, mono, 16 bit, straight from the
 Windows default recording device (winmm MCI, no extra software). Shows the text paragraph by paragraph;
 Enter after each paragraph marks where the speech ended, so the sidecar JSON gets the speech intervals.

 Usage (Windows PowerShell):
   powershell -ExecutionPolicy Bypass -File tools\e2e\record-test-speech.ps1 -Modus leise
   powershell -ExecutionPolicy Bypass -File tools\e2e\record-test-speech.ps1 -Modus rauschen
 Output: test-audio-roh\<modus>.wav and .json (repo root). Nothing is uploaded.
#>
param(
    [ValidateSet('leise', 'rauschen')][string]$Modus = 'leise',
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $OutDir) { $OutDir = Join-Path $repo 'test-audio-roh' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$wav = Join-Path $OutDir "$Modus.wav"
$json = Join-Path $OutDir "$Modus.json"

Add-Type -Namespace Wsw -Name Mci -MemberDefinition @'
[DllImport("winmm.dll", CharSet = CharSet.Unicode)]
public static extern int mciSendString(string command, System.Text.StringBuilder ret, int retLen, IntPtr callback);
[DllImport("winmm.dll", CharSet = CharSet.Unicode)]
public static extern bool mciGetErrorString(int error, System.Text.StringBuilder text, int len);
'@

function Mci([string]$cmd) {
    $ret = New-Object System.Text.StringBuilder 256
    $err = [Wsw.Mci]::mciSendString($cmd, $ret, 256, [IntPtr]::Zero)
    if ($err -ne 0) {
        $msg = New-Object System.Text.StringBuilder 256
        [Wsw.Mci]::mciGetErrorString($err, $msg, 256) | Out-Null
        throw "MCI '$cmd': $msg"
    }
    return $ret.ToString()
}

# Two passes of the same text: over 60 s in one piece, like a real long dictation.
$absaetze = @(
    'Guten Morgen, hier kommen die Notizen zur Besprechung vom Dienstag.',
    'Erstens: Das Angebot für die neue Webseite geht bis Freitag an den Kunden.',
    'Zweitens: Die Rechnung vom letzten Monat ist noch offen, bitte noch einmal nachfragen.',
    'Drittens: Für das Treffen im Januar brauchen wir einen größeren Raum, am besten im zweiten Stock.',
    'Außerdem sollten wir die Liste der Ansprechpartner aktualisieren, weil zwei Kollegen das Team verlassen haben.',
    'Zum Schluss: Der Bericht für das dritte Quartal ist fast fertig, es fehlen nur noch die Zahlen aus dem Vertrieb.',
    'Vielen Dank und bis morgen.'
)
$absaetze = $absaetze + $absaetze

Clear-Host
Write-Host "Testaufnahme '$Modus' fuer WhisperShortcut" -ForegroundColor Cyan
Write-Host ''
Write-Host 'Aufgenommen wird vom Standard-Mikrofon von Windows (Einstellungen > System > Sound > Eingabe).'
Write-Host 'Bitte dort das Mikrofon waehlen, mit dem du sonst diktierst.'
Write-Host ''
if ($Modus -eq 'leise') {
    Write-Host 'So sprechen: LEISE, wie spaetabends im Buero, oder 50 bis 80 cm vom Mikrofon weg.' -ForegroundColor Yellow
} else {
    Write-Host 'So sprechen: normal laut. Nebenher ein Geraeusch laufen lassen (Luefter, leise Musik, offenes Fenster).' -ForegroundColor Yellow
}
Write-Host ''
Write-Host 'Ablauf: Ein Absatz erscheint. Lies ihn vor und druecke danach ENTER.'
Write-Host 'Zwischen den Absaetzen kurz Pause machen. Versprecher sind egal, einfach weiterlesen.'
Write-Host 'Abbrechen: Strg+C.'
Write-Host ''
Read-Host 'Bereit? ENTER startet die Aufnahme' | Out-Null

try { Mci 'close wswrec' | Out-Null } catch { } # a run aborted with Ctrl+C may have left it open
Mci 'open new type waveaudio alias wswrec' | Out-Null
try {
    Mci 'set wswrec time format ms bitspersample 16 channels 1 samplespersec 16000 bytespersec 32000 alignment 2' | Out-Null
    Mci 'record wswrec' | Out-Null
    $clock = [Diagnostics.Stopwatch]::StartNew()

    Write-Host ''
    Write-Host 'Aufnahme laeuft. Erst 3 Sekunden STILLE ...' -ForegroundColor Red
    Start-Sleep -Seconds 3

    $speech = @()
    for ($i = 0; $i -lt $absaetze.Count; $i++) {
        Write-Host ''
        Write-Host ("[{0}/{1}]  " -f ($i + 1), $absaetze.Count) -NoNewline -ForegroundColor DarkGray
        Write-Host $absaetze[$i] -ForegroundColor White
        $start = $clock.Elapsed.TotalSeconds
        Read-Host '        (vorlesen, dann ENTER)' | Out-Null
        $speech += , @([math]::Round($start, 2), [math]::Round($clock.Elapsed.TotalSeconds, 2))
        if ($i -eq 6) {
            Write-Host ''
            Write-Host 'Halbzeit. 2 Sekunden Pause, dann geht es von vorn los ...' -ForegroundColor DarkGray
            Start-Sleep -Seconds 2
        }
    }

    Write-Host ''
    Write-Host 'Zum Schluss noch 3 Sekunden STILLE ...' -ForegroundColor Red
    Start-Sleep -Seconds 3
    Mci 'stop wswrec' | Out-Null
    $seconds = [math]::Round($clock.Elapsed.TotalSeconds, 1)
    if (Test-Path -LiteralPath $wav) { Remove-Item -LiteralPath $wav -Force }
    Mci ('save wswrec "' + $wav + '"') | Out-Null
} finally {
    try { Mci 'close wswrec' | Out-Null } catch { }
}

# Intervals come from the screen (paragraph shown .. ENTER): approximate, reaction time included.
$meta = [ordered]@{
    source = 'microphone'
    mode = $Modus
    text = ($absaetze -join ' ')
    speech_approx = $speech
    note = 'speech_approx: Absatz angezeigt bis ENTER; enthaelt Reaktionszeit, wird vor dem Einchecken nachgemessen.'
}
$meta | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $json -Encoding UTF8

$bytes = [IO.File]::ReadAllBytes($wav)
$rate = [BitConverter]::ToInt32($bytes, 24); $ch = [BitConverter]::ToInt16($bytes, 22); $bits = [BitConverter]::ToInt16($bytes, 34)
Write-Host ''
Write-Host "Fertig: $wav" -ForegroundColor Green
Write-Host ("  {0} s, {1} Hz, {2} Kanal, {3} Bit" -f $seconds, $rate, $ch, $bits)
Write-Host "  Zeiten: $json"
Write-Host ''
Write-Host 'Sag Claude Bescheid, dann wird die Aufnahme ausgewertet.'
