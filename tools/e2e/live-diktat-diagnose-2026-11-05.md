# Live diktieren: Diagnose Windows gegen Mac, 2026-11-05

Auftrag: `C:\Users\Admin\benchmark-audio\PROMPT-live-diktat-diagnose.md`. Nur Diagnose, keine Fixes.
Branch `diag/live-diktat`. Rohdaten (nur Zahlen, kein diktierter Text): `live-diktat-diagnose-2026-11-05-daten/`.
Werkzeug: `tools/LiveBench` (Audio aus Datei; ruft `ParakeetTranscriber`, `MeetingChunker` mit `LiveDictation.ChunkPolicy`,
`LiveChunkJoiner` und `LiveDictation.FinishPiece` im Modus „Nur gesprochen“ auf, wie `TrayApp.DecodeLivePieceAsync`).
Die 46 Aufnahmen bleiben außerhalb des Repos.

## Kurzfassung

- Auf den Mac-Aufnahmen ist die Windows-Engine **nicht schlechter** als die Mac-Referenz und **nicht zu langsam**
  (am Stück 6,2 % WER, Echtzeitfaktor 0,11; live 7,6 % WER, Text steht beim Stoppen sofort da).
  A ist ausgeschlossen. C zeigt keinen Nachteil, ein direkter Gegentest mit Ultra/fp32 fehlt. E ist für den Ruhepegel ausgeschlossen.
- Der Schaden durch die Live-Logik ist messbar, aber klein: +1,4 Prozentpunkte WER (3 Wörter von 210) und 3 verlorene
  exakte Treffer. Ursachen: Stück ohne Kontext dekodiert, Satzpunkt an einer Denkpause, Tastenklick am Ende als „Yeah“.
- **Eine Hauptursache für „schlechter als auf dem Mac“ lässt sich aus den Dateien nicht belegen.** Schritt 5
  (echtes Mikro, Notepad/Chrome) ging nicht: dafür muss jemand sprechen. Hinweise auf Schäden im echten Betrieb stehen nur in
  `wsw.log` vom 2026-11-04 (B und D unten) und stammen aus Builds **vor** 0.7.3.
  Wahrscheinlichste Hauptursache dort: **B, Schnitte mitten im Satz bei -40 dB** (1–2-Zeichen-Stücke), seit
  `ab3c29c`/0.7.3 auf -45 dB wie auf dem Mac. Das ist Hypothese, kein Beleg. Nachprüfen mit dem Diag-Build (Abschnitt 6).

## 1. System

| | |
|---|---|
| CPU | Intel Core i5-9400T @ 1,8 GHz, 6 Kerne / 6 Threads (kein SMT) |
| RAM | 15,9 GB |
| Windows | 11 Pro 10.0.26100 |
| App | 0.7.3 (`WhisperShortcut.csproj`), Diag-Build auf `diag/live-diktat` |
| Mikro | Windows-Standard (`microphone = ""`), einziges Gerät: „Mikrofon (Logi USB Headset)“ |
| Einstellungen | Parakeet v3 int8 (`parakeet-tdt-0.6b-v3-int8`), Satzzeichen „Nur gesprochen“, Modus zuletzt Live (04.11. 22:12), Glossar leer |
| NumThreads | `OfflineEngine.LoadParakeetAsync`: `ProcessorCount / 2` = **3** (Log: „Parakeet loaded … 3 threads“) |
| Ladezeit | 2,8–3,2 s |

## 2. Tabelle a/b (46 Dateien, 42 Fälle, 221 s Audio, 3 Threads wie in der App, ohne Glossar)

| | a) am Stück | b) live (Pausen-Schnitt, Joiner) | Mac (local-asr-Benchmark, anderes Set) |
|---|---|---|---|
| WER (Wörter, ohne Satzzeichen und Groß-/Kleinschreibung, bester Treffer aus expected/alternatives) | **6,2 %** (13/210) | **7,6 %** (16/210) | v3 10,8 %, Ultra 8,1 % |
| exakte Treffer inkl. Satzzeichen | 26/42 | 23/42 | – |
| Dekodierzeit gesamt | 23,6 s | 22,1 s | – |
| Echtzeitfaktor | 0,107 | 0,100 | Ultra ~0,1 s je Clip < 5 s |
| Stücke | 46 | 65 (44 Pause, 21 Stopp, 0 Max, davon 15 still übersprungen) | – |
| Stücke mit 1–2 Zeichen | – | 0 | – |
| Text nach dem Stoppen | Median 0,53 s, max 1,04 s | Median 0,00 s, p90 0,29 s, max 0,45 s | – |
| größter Rückstand eines Stücks | – | 0,93 s | – |

Vorsicht beim Mac-Vergleich: anderes Set, andere Normalisierung. Die Mac-Zahlen sagen nur, dass 6–8 % für Parakeet v3 kein schlechter Wert sind.
Die Fehler am Stück sind überwiegend Glossarwörter (GdB, VersMedV; in der App wären das Hotwords, hier ist das Glossar leer),
„Nur gesprochen“-Fehlgriffe (Fragezeichen oder Komma bleiben) und Zahlen als Ziffern. Ein Nicht-int8- oder Ultra-Modell habe ich **nicht getestet**
(Download > 1 GB, nur mit deiner Freigabe). C ist deshalb nicht direkt bewiesen, nur „nicht auffällig“.

Unterschiede a → b, Fall für Fall:
- `doppelpunkt-diagnose`: +2 Fehler. Das 2,6-s-Stück nach „Diagnose:“ wird ohne Kontext schlechter erkannt (Schnitt lag auf einer echten Pause, nicht im Wort).
- `semikolon`: +1. Der Tastenklick beim Beenden (1,1 s Tail, nicht still, -21 dB) wird zu „Yeah“.
- `pause-weiter` (1 s Pause mitten im Satz): Wörter gleich, aber „mal. Weiter“ statt „mal weiter“. Der Punkt des Stücks überlebt die Filterung.
- `ohne-nebensatz`, `ohne-weil`, `zahl-blutdruck`: Wörter richtig, exakter Treffer verloren (ein Komma bleibt nach dem Zuschnitt des Stücks).
- `wort-kommas`, `anschluss-komma`: je +1 (Erkennungsvarianz an Stückgrenzen).
- `schraegstrich-wirbel`: live besser (−2).

Schwelle des Pausen-Schnitts: -45 dB 7,6 % / 0 kurze Stücke · -40 dB 7,6 % / 0 · -35 dB 7,6 % / 1 · -30 dB 7,6 % / 2.
Bei diesen Mac-Aufnahmen (Ruhepegel -63 dB, Sprache -24 dB) ist die Schwelle fast egal. Das alte -40-dB-Problem zeigt sich hier nicht.

## 3. NumThreads (kurze Datei 2,2 s, längste 10,0 s, Median von 5)

| Threads | kurz s | kurz RTF | lang s | lang RTF |
|---|---|---|---|---|
| 1 | 0,49 | 0,22 | 2,05 | 0,20 |
| 2 | 0,30 | 0,14 | 1,19 | 0,12 |
| **3 (App)** | 0,28 | 0,13 | 0,97 | 0,10 |
| 4 | 0,30 | 0,14 | 0,89 | 0,09 |
| 6 | 0,38 | 0,18 | 1,00 | 0,10 |

Der RTF liegt überall unter 0,3, auch mit 1 Thread. Mehr Threads helfen kaum, 6 sind langsamer als 3.
Die echten Logwerte passen dazu: 170–770 ms je Stück.

## 4. Beobachtungen aus `wsw.log` (Sitzungen 2026-11-04 22:12–22:14, Builds vor 0.7.3)

- Von 14 echten Stücken in zwei Sitzungen hatten 4 nur 1–2 Zeichen (22:12:45, 22:12:49, 22:14:45, 22:14:48). Das ist das Bild von Schnitten
  mitten im Satz. Die Aufnahme lief vermutlich noch mit -40 dB; `ab3c29c` (22:27) hat auf -45 dB geändert.
- Eine Sitzung (22:14:10, 3 Stücke, 83 Zeichen) hat **nichts eingefügt**: „holding … the dictated-into window is not in front“,
  `heldChars=83` am Ende. Damals fehlte noch die Logzeile mit dem Zielfenster; warum es nicht vorn war, ist nicht zu sehen.
- Lange Stille erzeugt alle 1,6 s ein „silent piece skipped“ (22:13:06–22:13:46). Harmlos.
- Das Log hatte weder Audiolänge noch Pegel noch Schnittgrund noch Zeit Sprechende → Einfügen. Das ist jetzt drin (Abschnitt 5).

## 5. Instrumentierung (nur Zahlen, nie Text)

`LiveDictation.cs`/`TrayApp.cs`: je Stück eine Zeile
`LIVE-DIAG: piece N reason=Pause|Max|Stop audioS=… rmsDb=… quietWindowDb=… loudWindowDb=… waitedForChainMs=… decodeMs=… insertMs=… cutToInsertedMs=… rawChars=… pieceChars=… heldBefore=… inserted=true|false`,
dazu `LIVE-DIAG: silent piece …`. `cutToInsertedMs` zählt ab dem Moment, in dem der Chunker das Stück abgibt (bei Pause 0,6 s nach Sprechende,
bei Stopp ab Stopp) bis zum Einfügen. Beispiel: `live-diktat-diagnose-2026-11-05-daten/beispiel-live-diag-log.txt`
(echte `LiveDictationSession`, Einfügen simuliert). Alle 1528 Tests grün. Im echten App-Lauf mit Mikro noch nicht gesehen.

## 6. Schritt 5 (echtes Mikro, Notepad/Chrome): nicht ausgeführt

Dafür muss jemand sprechen. Gemessen habe ich nur das Rauschen des echten Geräts im Ruhezustand (6 s, gleicher Aufnahmepfad
WaveIn 16 kHz): Median **-69 dB**, lautestes 0,2-s-Fenster -52 dB, **0 von 29 Fenstern über -45 dB**. Rauschen verhindert also keine Schnitte.
Offen ist der **Sprechpegel** des Logi-Headsets (die Mac-Aufnahmen liegen bei -24 dB; leise Sprache unter -45 dB würde als Pause mitten im Satz gelten).

Zum Nachholen mit dem Diag-Build: je ein Lauf in Notepad und in einem Chrome-Textfeld mit „Wirbelsäule Komma Hüfte beidseits Komma Knie rechts Punkt“
und drei kurzen Sätzen mit Pausen, danach `wsw.log` nach `LIVE-DIAG` durchsuchen. Auffällig wären `audioS` < 1,5 (nur bei Stopp erlaubt),
`pieceChars` ≤ 2, `loudWindowDb` < -35, `inserted=false`, `insertMs` > 600.

## 7. Bewertung A–E

| | Urteil | Beleg |
|---|---|---|
| A Rechenzeit | **ausgeschlossen** | RTF 0,10–0,11 mit 3 Threads (0,20 mit 1), größter Rückstand 0,93 s, Text beim Stoppen sofort; Log 170–770 ms je Stück. |
| B Schnitte | **teilweise belegt, Hauptverdacht** | Mit -45 dB auf den Dateien keine 1–2-Zeichen-Stücke, Schnitte an echten Pausen, nicht im Wort. Im Log vom 04.11. (vor 0.7.3) 4 von 14 Stücken mit 1–2 Zeichen. Echtes Mikro mit 0.7.3 nicht geprüft. |
| C Modell | **kein Nachteil sichtbar, Gegentest fehlt** | Am Stück 6,2 % WER auf Magnus' Stimme; Ultra/fp32 nicht getestet. |
| D Einfügen und Nähte | **teilweise belegt** | Nähte: Punkt am Pausen-Stück („mal. Weiter“), Kommas nach Zuschnitt (3 exakte Treffer verloren). Einfügen: eine Sitzung im Log hat alles gehalten (Fenster nicht vorn). Strg+V-Verlust nicht gemessen. |
| E Mikro/Pegel | **Ruhepegel ausgeschlossen, Sprechpegel unklar** | Ruhe -69 dB (max -52) gegen -45; Sprechpegel des Headsets nicht gemessen. |

**Hauptursache:** aus diesem Material nicht eindeutig. Belegt ist: Engine und Tempo sind in Ordnung, die Live-Logik kostet etwa 1,4 Prozentpunkte WER.
Was als „schlechter als Mac“ erlebt wurde, passt am besten zu B (-40-dB-Schnitte, in 0.7.3 behoben) und zu D (Stück gehalten, weil das Zielfenster nicht vorn war).
Beides gehört mit dem Diag-Build am echten Mikro bestätigt oder widerlegt.

## 8. Fix-Vorschläge (nicht umgesetzt), nach Wirkung

1. **Erst messen, ob 0.7.3 reicht** (zwei echte Live-Diktate mit dem Diag-Build, Abschnitt 6). Zeigt `loudWindowDb` Sprache über -35 dB und keine Stücke ≤ 2 Zeichen, ist B erledigt.
   Sonst die Schwelle relativ zum Ruhepegel setzen (z. B. Ruhepegel + 15 dB statt fix -45), in `MeetingChunker.Write`
   (`src/WhisperShortcut.Core/MeetingChunker.cs`, Vergleich `db >= _policy.SilenceThresholdDb`). Mac-Parität prüfen: dort ist -45 fest.
2. **Satzpunkt und Komma an der Naht in „Nur gesprochen“:** das vom Modell gesetzte Satzende eines durch Pause beendeten Stücks darf nicht überleben, wenn nicht „Punkt“ gesprochen wurde.
   Stelle: `LiveDictation.FinishPiece` (`src/WhisperShortcut.Core/LiveDictation.cs:155`) und `ParakeetPunctuationTiming.KeepSpokenMarks` (`Offline/ParakeetPunctuationTiming.cs:189`).
   Erst die Swift-Logik (`LivePieceSplitter`) lesen: Der Mac-Korpus erwartet „Ich arbeite mal weiter damit.“.
3. **Tastenklick am Ende:** ein Stopp-Stück unter etwa 1,2 s, dessen laute Fenster zusammen < 0,3 s sind, nicht dekodieren.
   Stelle: `TrayApp.ProcessLiveAsync` (`tail`) oder `MeetingChunker.Complete`/`Silent`.
