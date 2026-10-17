# WhisperShortcut für Windows (privat, MVP)

Plan: `~/whisper-shortcut-private/windows/PLAN.md`. Hier liegt nur, was zum Bauen gebraucht wird.
Stand 2026-10-17: **vorbereitet, noch kein App-Code.**

## Was geprüft ist (2026-10-17)

- .NET 10 SDK auf dem Mac (`brew install dotnet`, ohne sudo). Eine WinForms-`.exe` für
  `win-arm64` und `win-x64` baut auf dem Mac (`EnableWindowsTargeting=true`), ca. 120 MB als
  Single-File.
- Parallels-VM „Windows 11“ (ARM64, Guest Tools 27.0.3) lässt sich komplett vom Mac aus steuern,
  ohne Computer Use: `tools/vm.sh` (deploy, start, stop, notepad, focus, type, combo, shot, log).
  Getestet: `.exe` kopieren, starten, in Notepad tippen, Screenshot.
- Freigegeben in die VM ist nur `~/Desktop`, `~/Documents`, `~/Downloads` (als `Z:\`).
  Deshalb geht der Build über `~/Downloads/wsw-test/`.

## Reihenfolge

| # | Wer | Was | Fertig, wenn |
|---|---|---|---|
| 1 | Grok 4.7 (sonst Sonnet) | `briefs/01-grok-core.md`: Gerüst, Prompts, GeminiClient, Settings, Audio, Tests | `dotnet test` grün, `win-arm64`-Build läuft |
| 2 | Opus | HotkeyService, ClipboardPaster (Win32-Stolperfallen aus PLAN.md), Tray, Einstellungsfenster | In der VM: `WSW_TEST_WAV=diktat.wav`, Ctrl+Alt+D zweimal → Text steht in Notepad, alte Zwischenablage wieder da |
| 3 | Opus | Dictate Prompt verdrahten | Markierter Text + `anweisung-freundlicher.wav` → ersetzt; ohne Markierung + `angebot-ohne-markierung.wav` → Angebot eingefügt |
| 4 | Magnus | Einmal mit echtem Mikro in Parallels, dann auf einem echten x64-PC | Ein Tester kann es ohne Magnus einrichten |

## Test-Audio

`test-audio/*.wav` (16 kHz mono, macOS-Stimme Anna): `diktat`, `anweisung-freundlicher`,
`angebot-ohne-markierung`, `stille` (2 s, muss leeren Text ergeben). Die App liest statt des
Mikros diese Datei, wenn `WSW_TEST_WAV` gesetzt ist.

## Was Magnus einmal tun muss

- Gemini-API-Schlüssel selbst im Einstellungsfenster eintragen (bezahltes Projekt, nicht Free
  Tier). Claude tippt keine echten Schlüssel ein.
- Für den Test mit echter Stimme: macOS → Datenschutz → Mikrofon → Parallels Desktop erlauben.
