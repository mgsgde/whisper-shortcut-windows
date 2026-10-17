# WhisperShortcut für Windows (privat, MVP)

Plan: `~/whisper-shortcut-private/windows/PLAN.md`. Hier liegt nur, was zum Bauen gebraucht wird.
Stand 2026-10-17, 19:10: **Diktieren und Dictate Prompt funktionieren in der VM mit echtem Mikro
und echtem Gemini** (Magnus getestet). Tasten jetzt Ctrl+Shift+D/P (Alt öffnete App-Menüs).
Ältere Notiz: **MVP gebaut, in der VM ohne Gemini getestet.** Schritte 1 und 2 fertig,
Schritt 3 (echte Gemini-Antworten) wartet auf Magnus' Schlüssel.

- In der VM geprüft (arm64, `--fake-gemini`, `vm.sh hold 600`): Diktat wird eingefügt, alte
  Zwischenablage kommt zurück; Dictate Prompt ersetzt die Markierung (52 Zeichen per Strg+C
  erkannt), ohne Markierung wird eingefügt; „Schlüssel fehlt“-Meldung; Einstellungsfenster mit
  Vorlage. x64-Build startet in der VM (emuliert).
- Testschalter: `--test-wav <datei>` (statt Mikro), `--fake-gemini` (feste Antwort statt Gemini,
  zum Testen von Tasten und Zwischenablage ohne Schlüssel), `--settings` (Fenster beim Start öffnen).
- Bewusste Abweichungen vom Plan: Modifier-Wartezeit 3 s statt 500 ms (bei 600 ms Halten wurde
  Strg+V sonst zu AltGr+V = „√“); bleiben die Tasten länger gedrückt, wird nichts getippt und der
  Text liegt in der Zwischenablage. Die Vorlage ersetzt nicht den Bearbeitungs-Prompt, sie wird
  angehängt und gilt nur ohne Markierung. „Mach das freundlicher“/„korrigiere“ ohne Markierung
  wird abgelehnt (parallele Transkription prüft das).
- Nicht geprüft: echte Gemini-Aufrufe, echtes Mikro, DPAPI-Speichern des Schlüssels, Word/Outlook/
  Browser als Ziel, Admin-Fenster (UIPI), echter x64-PC.

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
| 2 | Opus | HotkeyService, ClipboardPaster (Win32-Stolperfallen aus PLAN.md), Tray, Einstellungsfenster | In der VM: `vm.sh start diktat.wav`, `vm.sh hold 600 ctrl shift d` zweimal → Text steht in Notepad, alte Zwischenablage wieder da |
| 3 | Opus | Dictate Prompt verdrahten | Markierter Text + `anweisung-freundlicher.wav` → ersetzt; ohne Markierung + `angebot-ohne-markierung.wav` → Angebot eingefügt |
| 4 | Magnus | Einmal mit echtem Mikro in Parallels, dann auf einem echten x64-PC | Der Handwerker richtet es mit der Anleitung ohne Magnus ein |

## Test-Audio

`test-audio/*.wav` (16 kHz mono, macOS-Stimme Anna): `diktat`, `anweisung-freundlicher`,
`angebot-ohne-markierung`, `stille` (2 s, muss leeren Text ergeben). Die App liest statt des
Mikros diese Datei, wenn `WSW_TEST_WAV` gesetzt ist.

## Was Magnus einmal tun muss

- Gemini-API-Schlüssel selbst im Einstellungsfenster eintragen (bezahltes Projekt, nicht Free
  Tier). Claude tippt keine echten Schlüssel ein.
- Für den Test mit echter Stimme: macOS → Datenschutz → Mikrofon → Parallels Desktop erlauben.
