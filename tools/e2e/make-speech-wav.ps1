# Generates the real-speech test WAVs (16 kHz, 16-bit, mono) with the Windows TTS voice Hedda.
# Why TTS: the Silero regression (one bogus segment at the end) needs real speech with known
# positions; the sidecar JSON holds those positions so tests need no hand-labelled audio.
# Run on Windows PowerShell 5.1:  powershell -File tools\e2e\make-speech-wav.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech

$voiceName = 'Microsoft Hedda Desktop'
$outDir = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'test-audio'
$tmpDir = Join-Path ([IO.Path]::GetTempPath()) ('make-speech-wav-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmpDir | Out-Null
$rate = 16000

# Speaks one phrase to a temp WAV and returns its PCM bytes (header stripped).
function Get-PhrasePcm([string]$text) {
    $file = Join-Path $tmpDir ([guid]::NewGuid().ToString('N') + '.wav')
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $synth.SelectVoice($voiceName)
        $fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(
            $rate, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
        $synth.SetOutputToWaveFile($file, $fmt)
        $synth.Speak($text)
    } finally { $synth.Dispose() }   # Dispose finalizes the header
    $bytes = [IO.File]::ReadAllBytes($file)
    # Find the "data" chunk instead of assuming a 44-byte header.
    $pos = 12
    while ($pos + 8 -le $bytes.Length) {
        $id = [Text.Encoding]::ASCII.GetString($bytes, $pos, 4)
        $size = [BitConverter]::ToUInt32($bytes, $pos + 4)
        if ($id -eq 'data') {
            $len = [Math]::Min([long]$size, [long]($bytes.Length - $pos - 8))
            # TTS pads each clip with ~0.6 s of digital silence; trim it so the sidecar marks real speech only.
            $first = 0; $last = [int]($len / 2) - 1
            while ($first -lt $last -and [Math]::Abs([BitConverter]::ToInt16($bytes, $pos + 8 + 2 * $first)) -le 64) { $first++ }
            while ($last -gt $first -and [Math]::Abs([BitConverter]::ToInt16($bytes, $pos + 8 + 2 * $last)) -le 64) { $last-- }
            $pcm = New-Object byte[] (2 * ($last - $first + 1))
            [Array]::Copy($bytes, $pos + 8 + 2 * $first, $pcm, 0, $pcm.Length)
            return , $pcm
        }
        $pos += 8 + $size + ($size % 2)
    }
    throw "no data chunk in $file"
}

function New-SpeechWav([string]$name, [string[]]$phrases, [double]$gapSeconds) {
    $ms = New-Object IO.MemoryStream
    $intervals = @()
    $silence2 = New-Object byte[] ($rate * 2 * 2)
    $gap = New-Object byte[] ([int]($rate * $gapSeconds) * 2)
    $ms.Write($silence2, 0, $silence2.Length)
    for ($i = 0; $i -lt $phrases.Count; $i++) {
        if ($i -gt 0) { $ms.Write($gap, 0, $gap.Length) }
        $pcm = Get-PhrasePcm $phrases[$i]
        $start = $ms.Length / 2 / $rate
        $ms.Write($pcm, 0, $pcm.Length)
        $intervals += , @([Math]::Round($start, 3), [Math]::Round($ms.Length / 2 / $rate, 3))
    }
    $ms.Write($silence2, 0, $silence2.Length)
    $data = $ms.ToArray()

    $wav = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter($wav)
    $w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + $data.Length))
    $w.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $w.Write([int]16)
    $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]$rate); $w.Write([int]($rate * 2)); $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int]$data.Length); $w.Write($data)
    $w.Flush()
    [IO.File]::WriteAllBytes((Join-Path $outDir "$name.wav"), $wav.ToArray())

    $speech = ($intervals | ForEach-Object { '[' + $_[0].ToString([Globalization.CultureInfo]::InvariantCulture) + ', ' + $_[1].ToString([Globalization.CultureInfo]::InvariantCulture) + ']' }) -join ', '
    $json = [ordered]@{ voice = $voiceName; text = ($phrases -join ' ') }
    $text = ($json | ConvertTo-Json -Compress)
    # ConvertTo-Json cannot emit nested numeric arrays compactly in PS 5.1; splice "speech" in by hand.
    $text = $text.TrimEnd('}') + ',"speech":[' + $speech + ']}'
    [IO.File]::WriteAllText((Join-Path $outDir "$name.json"), $text, (New-Object Text.UTF8Encoding($false)))
    Write-Host "$name.wav: $([Math]::Round($data.Length / 2 / $rate, 2)) s, speech $speech"
}

try {
    New-SpeechWav 'satz-hedda' @('Heute Nachmittag schicke ich Ihnen das überarbeitete Angebot für die neue Webseite.') 0
    New-SpeechWav 'pause-hedda' @('Der erste Teil des Berichts ist fertig.', 'Den zweiten Teil schreibe ich morgen früh im Büro.') 1.5
} finally {
    Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue
}
