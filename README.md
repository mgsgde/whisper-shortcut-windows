# WhisperShortcut für Windows (privat, MVP)

Plan: `~/whisper-shortcut-private/windows/PLAN.md`. Hier liegt nur, was zum Bauen gebraucht wird.

**Releases (seit 2026-10-27):** öffentlich nur die Builds in `mgsgde/whisper-shortcut-windows-releases`
(Code bleibt privat). Fester Link auf die neueste:
`https://github.com/mgsgde/whisper-shortcut-windows-releases/releases/latest/download/WhisperShortcut-Windows-x64.zip`.
Neuer Release: `dotnet publish src/WhisperShortcut -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true` (dito `win-arm64`),
exe + `dist/ANLEITUNG.txt` als `WhisperShortcut/` zippen, dann
`gh release create vX.Y.Z -R mgsgde/whisper-shortcut-windows-releases <zips> dist/ANLEITUNG.txt`.

**Stand 2026-10-26: UI/UX überarbeitet** (vor der Weitergabe an den ersten Tester).

- **Einstellungsfenster neu:** links Navigation (Allgemein, Tastenkürzel, Glossar,
  Schnellanweisungen, Vorlage), rechts die Seite, unten Speichern/Abbrechen mit Hinweis
  „Nicht gespeicherte Änderungen“; Schließen mit Änderungen fragt nach. Ungültige Tastenkürzel
  springen auf ihre Seite. Glossar und Vorlage füllen die Höhe, das Glossar zählt die Begriffe.
- **Erster Start:** Einstellungen öffnen sich mit Willkommens-Banner (`--welcome` zum Testen).
- **Schlüssel prüfen:** `GeminiClient.CheckKeyAsync` (GET models, Header statt Query), Status
  ✓/✗/keine Verbindung direkt unter dem Feld, 700 ms nach dem Tippen und beim Öffnen.
- **Mikrofon wählen und testen:** Gerät aus WinMM (`Microphones`, 31-Zeichen-Namen wie WinMM sie
  liefert; fehlt das Gerät, gilt der Windows-Standard), „Testen“ zeigt einen Pegelbalken
  (`LevelProbe`). `Settings.Microphone`.
- **Tray-Menü:** „Offline: nichts verlässt den PC“ als Haken, schaltet nur im Leerlauf und nur,
  wenn Modell bzw. Schlüssel da sind; die Anzeige bestätigt den Wechsel.
- **Vorlage:** „Beispiel einsetzen ▾“ mit Praxis-Vorlage (Anamnese/Befund/Therapie, keine
  erfundenen Diagnosen oder Dosierungen, Marker „(nicht diktiert)“) und Angebots-Vorlage.
- Gebaut: Plumbing (Mikrofon, Pegel, Schlüsselprüfung) Sonnet 5.5, UI Opus 5.5.

**Stand 2026-10-21, abends: Paket „Praxis-Doku“ eingebaut** (für den Praxisfall).

- **Schnellanweisungen:** Während einer Dictate-Prompt-Aufnahme wird die Anzeige zur Karte mit fünf
  Anweisungen (Einstellungen → „Schnellanweisungen“, vorbelegt: „Als Anamnese, Befund und Therapie
  gliedern“, „In Stichpunkten zusammenfassen“, „Rechtschreibung und Grammatik korrigieren“, „Kürzer
  fassen“, „Freundlicher formulieren“). Strg+1–5 (nur während dieser Aufnahme belegt) oder ein Klick
  (die Karte nimmt dabei keinen Fokus) wendet sie an: auf die Markierung, Gesagtes geht als
  „Zusätzlich gesagt“ mit; ohne Markierung auf das gerade Diktierte („Befund diktieren, Strg+1“).
  Markieren und sofort Strg+1 geht ohne ein Wort.
- **„Nicht in deinem Diktat“:** deterministischer Wortabgleich (Port von
  `DictationFidelityCheck.swift` samt Tests) zwischen Antwort und Diktat/Markierung/Anweisung/Vorlage.
  Gefundene Wörter (bis 8) stehen nach dem Einfügen 20 s in einer bernsteinfarbenen Karte, Klick
  schließt. Läuft überall, wo es ein Transkript gibt: offline immer, online ohne Markierung und bei
  jeder Schnellanweisung (online mit Markierung hört Gemini selbst, da gibt es keins). Im Log nur die
  Anzahl. Schwäche: Weil die Vorlage als Quelle zählt, fallen Beispielwörter aus einer eigenen
  Vorlage nicht auf.
- **Reiner Text:** Antworten verlieren Markdown (`## Befund` → „Befund:“, `**x**` → x, `* ` → `- `,
  Code-Zäune weg), weil Praxisprogramme und Word die Zeichen sonst roh zeigen.
- In der VM geprüft (arm64, echtes Gemini, Test-WAV „Herr Goedde … Ramipril … Pantoprazol“): Karte
  mit und ohne Markierung; Strg+1 ohne Markierung → Anamnese/Befund/Therapie als Klartext, Warnung
  mit 2 Wörtern; Klick auf Zeile 2 mit Markierung → Stichpunkte ersetzen die Markierung, Fokus blieb
  in Notepad, keine Warnung. Einstellungsfeld per UIA. Offline-Sprachmodell nicht (M1, siehe unten).

**Stand 2026-10-21: Paket 1 „Alltagstauglich“ eingebaut.**

- Aufnahme-Anzeige unten mittig (nimmt keinen Fokus): roter Punkt, Pegelbalken, Zeit, „Esc
  bricht ab“; bei Dictate Prompt, ob eine Markierung bearbeitet wird; Warnung nach 3 s ohne Ton;
  danach Spinner und kurz „Eingefügt“. Esc ist nur während Aufnahme/Verarbeitung belegt.
- Lange Diktate bis 30 min: Gemini in 2-min-Stücken (4 parallel), Parakeet offline in 30-s-Stücken
  (auf dem M1 gemessen: 5,5 min Audio in 14,6 s statt 24 s mit 100-s-Stücken), Schnitt in der
  leisesten Stelle. Dictate Prompt bleibt bei 5 min (eine Anfrage).
- Tray: „Letzten Text kopieren“ und „Letzte Texte“ (5, nur im Arbeitsspeicher).
- Ctrl+Shift+G: Markierung ins Glossar (Dubletten, Anführungszeichen, Satzzeichen abgefangen).
- „Mit Windows starten“ (HKCU Run, beim ersten Start an; Pfad wird nachgezogen, wenn die exe
  wandert).
- In der VM geprüft (arm64): Anzeige für Diktat/Prompt mit und ohne Markierung, Esc, Glossar
  (neu, Dublette, leer), Prompt mit `--fake-gemini`, Diktat online mit echtem Gemini.
- Offen: langes Diktat in der VM (Mac war durch andere Builds ausgelastet), Tray-Untermenü und
  Autostart-Haken von Hand, Pegel mit echtem Mikro, echter x64-PC.
- **Glossar offline** (gleicher Tag): die Glossar-Wörter gehen als „Hotwords“ an Parakeet
  (sherpa-onnx, `modified_beam_search`, Stärke 2,5; 3,0 verbog schon normale Wörter). Gemessen:
  „Herrgöde/Ramepril/Vetilego/Chirschwitz“ → „Goedde/Ramipril/Vitiligo/Tschirschwitz“, keine
  Fehltreffer in normalen Sätzen, 5,5 min Audio gleich schnell (13,8 s statt 15,4 s). Die
  `bpe.vocab`, die sherpa dafür braucht, wird aus `tokens.txt` abgeleitet. Kleingeschriebene
  Einträge bekommen eine großgeschriebene Variante dazu (Parakeet schreibt Namen groß). Ändert
  sich das Glossar, lädt Parakeet im Hintergrund neu (VM: 16 s), bis dahin gilt das alte. In der
  VM geprüft: Ctrl+Shift+G „ramipril“ → nächstes Diktat schreibt „Ramipril“. Offen: Namen aus
  zwei Wörtern („Sabaki Dance“) greifen noch nicht.

**Stand 2026-10-20: Offline-Modus eingebaut** (Einstellungen → Arbeitsweise → „Offline auf diesem
PC“). Spracherkennung Parakeet TDT 0.6B v3 (sherpa-onnx, im Prozess), Sprachmodell Qwen3 4B
oder 9B über `llama-server` (llama.cpp b11755, Kindprozess am Job-Objekt, stoppt nach 15 min
Leerlauf). Modelle lädt die App selbst nach `%LOCALAPPDATA%\WhisperShortcut\Offline`
(Fortsetzen, SHA-256-Prüfung).

- In der VM geprüft (arm64): Download beider Zeilen über das Einstellungsfenster (UIA,
  `vm.sh uia`), Offline-Diktat in Notepad (7 s Audio → 1,7 s, Text korrekt). Dictate Prompt
  offline: llama-server startet, aber das **M1 im Mac kann den arm64-Build nicht ausführen**
  (Illegal Instruction, braucht I8MM: Snapdragon X und Apple M2+ ja, M1/8cx nein). Die App sagt
  das jetzt klar statt „hat nicht geantwortet“; Diktieren geht offline weiter.
- x64-Paket (Vulkan-Build + VC-Laufzeit) in der VM unter Emulation gestartet: antwortet korrekt,
  emuliert aber nur 0,4 Token/s. Echte Geschwindigkeit nur auf einem x64-PC messbar.
- Gefunden und behoben: llama.cpp braucht `msvcp140.dll`/`vcruntime140.dll`, die ein frisches
  Windows nicht hat (Exit 0xC0000135). Die DLLs aus Microsofts vc_redist liegen in
  `third_party/vcruntime/<rid>/`, stecken in der exe und werden neben `llama-server.exe` gelegt.
- Offen: Glossar offline (Parakeet kennt keins), Test auf echtem x64-PC (Ladezeit 4B/9B,
  Antwortzeit), Test mit echter Stimme offline.

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
