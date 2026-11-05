# Live diktieren: Diagnose Windows gegen Mac, 2026-11-05

Auftrag: `C:\Users\Admin\benchmark-audio\PROMPT-live-diktat-diagnose.md`. Nur Diagnose, keine Fixes.
Branch `diag/live-diktat`. Rohdaten (nur Zahlen): `live-diktat-diagnose-2026-11-05-daten/`.
Werkzeug: `tools/LiveBench` (Audio aus Datei statt Mikro). Die 46 Aufnahmen bleiben außerhalb des Repos.

**Stand: zweite Fassung.** Die erste Fassung (Sonnet, Commit `7fa1380`) wurde mit Opus 5.5 nachgeprüft und korrigiert:
siehe „Was an der ersten Fassung falsch war“ am Ende.

## Kurzfassung

- **Engine und Tempo sind nicht das Problem.** Auf Magnus' Aufnahmen: normales Diktieren 7,1 % WER, live 7,6 %,
  Echtzeitfaktor 0,10–0,11 mit den 3 Threads der App.
- **Hauptursache (belegt als Mechanismus, am echten Mikro noch nicht gemessen): der feste Pegel von -45 dB im Live-Schnitt.**
  Leiser gemacht, bleibt normales Diktieren stabil (8,1 % bei −20 dB). Live bricht ab etwa −17 dB ein:
  10,0 % → 14,8 % → 26,2 % → **31,9 %** bei −17/−18/−19/−20 dB. Leise Sprache gilt dann als Pause:
  Stücke werden mitten im Wort geschnitten („Akten|lage“) oder ganz als „still“ verworfen, dann fehlen ganze Wörter
  („Gangbild“, „Leistungsfähigkeit auf dem allgemeinen“). Das ist genau die Art Fehler, die nur Live macht.
- Ob Magnus' Headset im Alltag in diese Zone kommt, ist **offen**. Die Live-Sitzungen im Log hatten Spitzenpegel 29 % und 67 %
  (unkritisch). Einzelne normale Diktate hatten nur 8–9 % (etwa 5 dB vor dem Einbruch). Prüfen mit dem Diag-Build, Abschnitt 6.
- Die erste Fassung hatte „Schnitte bei -40 dB“ als Hauptverdacht. Das stützte sich auf Stücke mit 1 Zeichen im Log. Die sind
  aber sehr wahrscheinlich ein gesprochenes „Punkt“/„Komma“ nach einer Pause, also korrekt. Dieser Beleg fällt weg.

## 1. System

| | |
|---|---|
| CPU | Intel Core i5-9400T @ 1,8 GHz, 6 Kerne / 6 Threads |
| RAM | 15,9 GB |
| Windows | 11 Pro 10.0.26100 |
| App | 0.7.3, Diag-Build auf `diag/live-diktat` |
| Mikro | Windows-Standard (`microphone = ""`), einziges Gerät „Mikrofon (Logi USB Headset)“ |
| Einstellungen | Parakeet v3 int8 (`parakeet-tdt-0.6b-v3-int8`), Satzzeichen „Nur gesprochen“, Glossar leer |
| NumThreads | `ProcessorCount / 2` = **3** (`OfflineEngine.LoadParakeetAsync`, Log „Parakeet loaded … 3 threads“) |

## 2. Tabelle a/b (46 Dateien, 42 Fälle, 221 s Audio, 3 Threads, Glossar leer)

a) Normales Diktieren wie `TrayApp.TranscribeOfflineAsync` + `DictateAsync` („Nur gesprochen“: Satzzeichen per Token-Zeit, `SpokenPunctuation.Apply`,
`TextCleanup.ForDictation` ohne Schlusspunkt). b) Live wie `TrayApp.FeedLive`/`DecodeLivePieceAsync`: 100-ms-Puffer in den `MeetingChunker` mit
`LiveDictation.ChunkPolicy`, Stücke einzeln dekodiert, `LiveDictation.FinishPiece`, `LiveChunkJoiner`. Mehrteilige Fälle (`-1`, `-2`) werden aneinandergehängt.

| | a) am Stück | b) live | Mac (Zahlen aus dem Auftrag) |
|---|---|---|---|
| WER (Wörter; Satzzeichen und Groß-/Kleinschreibung ignoriert; bester Treffer aus expected/alternatives) | **7,1 %** (15/210) | **7,6 %** (16/210) | v3 10,8 %, Ultra 8,1 % |
| exakte Treffer inkl. Satzzeichen | 25/42 | 26/42 | – |
| Echtzeitfaktor | 0,110 | 0,104 | – |
| Stücke | 46 | 65 (44 Pause, 21 Stopp, 0 Max; 15 still verworfen) | – |
| Stücke ≤ 2 Zeichen mit Buchstaben | – | 0 | – |
| Wartezeit nach dem Stoppen | Median 0,52 s, max 1,02 s | Median 0,22 s, p90 0,51 s, max 0,73 s | – |
| größter Rückstand eines Stücks | – | 0,93–1,06 s | – |

Zwei Läufe je Modus. Die WER war gleich, a) war Wort für Wort gleich, b) wich an einer Stelle ab („Yeah“ gegen „Yeah.“).
Exakte Treffer schwanken deshalb um ±1–3 und taugen nicht als Vergleich.
Mac-Vergleich: Welches Set und welche Normalisierung der Mac-Benchmark nutzt, ist hier nicht prüfbar (Mac-Quellcode liegt nicht auf diesem PC). Windows ist auf diesen Aufnahmen nicht schlechter.

Unterschied a → b (netto +1 Wort):
- live schlechter: `doppelpunkt-diagnose` +2 (2,6-s-Stück ohne Kontext: „beid Salz“), `semikolon` +1 (Tastenklick beim Beenden, 1,1 s Tail mit -21 dB → „Yeah“), `wort-kommas` +1, `anschluss-komma` +1.
- live besser: `schraegstrich-wirbel` −2, `fuellwort-hm` −1, `fuellwort-aeh` −1. Live entfernt Füllwörter (`FinishPiece`), normales Diktieren nicht.
- Satzzeichen an der Naht: `pause-weiter` ergibt live „mal. Weiter“ statt „mal weiter“, `ohne-nebensatz` behält ein Komma.
- Nebenbefund normales Diktieren: Füllwörter bleiben stehen („Basistherapie, äh …“, „Hm, Gangbild …“), und in „Nur gesprochen“ bleibt oft ein nicht gesprochener Schlusspunkt.

## 3. Pegel: wo Live einbricht (neu)

Dieselben 46 Aufnahmen, digital leiser gemacht (`LIVEBENCH_GAIN_DB`). Magnus' Aufnahmen haben unverändert Spitzen um -10,6 dBFS (≈ 30 %),
Sprache (p90 der 0,2-s-Fenster) -24 dB, Ruhe -63 dB.

| Dämpfung | Spitze ≈ | a) WER | b) WER | b) Stücke / still verworfen |
|---|---|---|---|---|
| 0 dB | 30 % | 7,1 % | 7,6 % | 65 / 15 |
| −10 dB | 9 % | 5,7 % | 5,7 % | 70 / 19 |
| −15 dB | 5 % | 8,6 % | 8,1 % | 84 / 29 |
| −17 dB | 4 % | – | 10,0 % | 98 / 33 |
| −18 dB | 3,7 % | – | 14,8 % | 106 / 36 |
| −19 dB | 3,3 % | – | 26,2 % | 116 / 46 |
| −20 dB | 3 % | 8,1 % | **31,9 %** | 127 / 53 |

Mechanismus (`MeetingChunker.Write`/`Complete`, `LiveDictation.ChunkPolicy` -45 dB): Ein 0,2-s-Fenster unter -45 dB zählt als still. Drei stille Fenster
schneiden, auch mitten im Wort. Ein Stück ohne ein einziges Fenster über -45 dB gilt als `Silent` und wird **gar nicht dekodiert**. Normales Diktieren
dekodiert immer alles, deshalb bleibt es stabil. Parakeet selbst ist pegelunabhängig (a bleibt bei ~8 %).

Mit der Schwelle allein (Aufnahmen in Originallautstärke, -45/-40/-35/-30 dB) ändert sich die WER nicht (7,6 % in allen vier Läufen).
Die Schwelle ist also nur dann schädlich, wenn die Sprache leise ist.

Was im Log über die echten Pegel steht (`Mic: … peak N %`, Spitzenwert, nicht Sprachpegel): Live-Sitzungen 04.11. 22:12 **29 %**, 22:14 **67 %**.
Normale Diktate am 04.11. zwischen 8 % und 96 %, die leisesten (14:49–14:50) **8–9 %**. Das liegt etwa 5 dB vor dem Einbruch.
Ein Headset, das etwas weiter vom Mund weg sitzt, reicht dafür.

## 4. NumThreads (kurze Datei 2,2 s, längste 10,0 s, Median von 5; zweiter Lauf)

| Threads | kurz s | kurz RTF | lang s | lang RTF |
|---|---|---|---|---|
| 1 | 0,56 | 0,26 | 2,30 | 0,23 |
| 2 | 0,34 | 0,16 | 1,28 | 0,13 |
| **3 (App)** | 0,34 | 0,16 | 1,08 | 0,11 |
| 4 | 0,38 | 0,18 | 1,00 | 0,10 |
| 6 | 0,51 | 0,23 | 1,15 | 0,11 |

Der erste Lauf war 10–20 % schneller (im selben Checkout baute gleichzeitig eine andere Session). Das Bild ist gleich: Alle Werte liegen klar unter 0,3, und mehr als 3–4 Threads bringen nichts.
Die echten Logwerte passen dazu: 170–770 ms je Stück.

## 5. Beobachtungen aus `wsw.log` (Sitzungen 04.11. 22:12–22:14, vor 0.7.3)

- **Stücke mit 1–2 Zeichen** (4 von 14): Der Joiner setzt vor jedes Wort ein Leerzeichen, also hat ein Wortstück mindestens 2 Zeichen.
  Ein Stück mit 1 Zeichen ist damit fast sicher ein einzelnes Satzzeichen, zum Beispiel ein „Punkt“ nach einer Pause. Das ist korrekt und kein Zerhacken.
  Die 2 Zeichen können ein Ein-Buchstaben-Wort sein. Ohne Text im Log ist das nicht zu klären. Die erste Fassung hat das als Beleg für B gewertet; das hält nicht.
- **Sitzung 22:14:10, alles gehalten (83 Zeichen):** Die App war 4 s vorher neu gestartet (22:14:06). Um 22:14:10.095 öffnete sich die Einstellungsseite „Allgemein“,
  370 ms später startete Live. Das Ziel war also vermutlich das eigene Einstellungsfenster. Ein Testartefakt, keine verlorenen Einfügungen.
  `ab3c29c` („no start inside a window build“) zielt genau darauf.
- Die Sitzungen um 22:12 und 22:14:25 endeten mit Esc (Abbruch durch den Nutzer); eingefügter Text bleibt stehen.
- Verloren gegangenes Strg+V zeigt das Log nicht (jedes Stück hat „inserted N chars“).

## 6. Messung am echten Mikro (vorbereitet, noch nicht ausgeführt: dafür muss jemand sprechen)

Das Ruhegeräusch des Headsets habe ich gemessen (6 s, gleicher Aufnahmepfad WaveIn 16 kHz): Median **-69 dB**, max -52 dB, 0 von 29 Fenstern über -45.
Störgeräusche können also keine Pause verdecken. Offen ist nur der **Sprechpegel**.

Ablauf:
1. Diag-Build starten. Der Branch ist im Checkout gerade mit unfertiger Arbeit einer anderen Session gemischt, darum in einem eigenen Worktree:
   `git worktree add ..\wsw-diag diag/live-diktat` und darin `dotnet run --project src/WhisperShortcut -c Release`.
   Achtung: Der Branch enthält auch `b6aea52` (Startmenü-Eintrag), ein Release-Start legt also den Startmenü-Eintrag an.
2. Live diktieren in Notepad: „Wirbelsäule Komma Hüfte beidseits Komma Knie rechts Punkt“, dann drei kurze Sätze mit deutlichen Pausen.
   Dasselbe in einem Chrome-Textfeld. Einmal normal laut, einmal leise mit dem Mikro etwas weiter weg.
3. Auswerten: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/e2e/live-diag-auswerten.ps1 -Minutes 15`.
   Das Skript zeigt je Stück Länge, Pegel, Dekodier- und Einfügezeit. Es meldet stille Stücke, deren lautestes Fenster über -48 dB liegt (vermutlich verlorene
   leise Sprache), Stücke ≤ 2 Zeichen, Sprache leiser als -35 dB, nicht eingefügte Stücke und Einfügen über 600 ms.
   Entscheidend: Liegt `loudWindowDb` der echten Stücke näher an -45 als an -25, ist die Hauptursache bestätigt.

## 7. Bewertung A–E

| | Urteil | Beleg |
|---|---|---|
| A Rechenzeit | **ausgeschlossen** | RTF 0,10–0,11 (3 Threads), auch mit 1 Thread 0,23; Wartezeit nach Stopp max 0,73 s; Log 170–770 ms je Stück. |
| B Schnitte | **belegt als Mechanismus bei leiser Sprache**, bei normalem Pegel ausgeschlossen | Originalpegel: keine Wortstücke ≤ 2 Zeichen, Schnitte an echten Pausen, WER live ≈ am Stück. Ab −17 dB: Schnitte im Wort und verworfene Sprachstücke, WER bis 31,9 %. |
| C Modell | **kein Nachteil sichtbar**, direkter Gegentest fehlt | Am Stück 7,1 % auf Magnus' Stimme. Ultra/fp32 nicht getestet (Download > 1 GB nur mit Freigabe). |
| D Einfügen und Nähte | **Nähte: kleiner Schaden belegt; Einfügen: kein Beleg für Verluste** | „mal. Weiter“, Komma nach Zuschnitt, „Yeah“ aus dem Tastenklick. Die gehaltene Sitzung im Log war ein Start im eigenen Einstellungsfenster. |
| E Mikro/Pegel | **Rauschen ausgeschlossen; Sprechpegel = Hauptverdacht, unbelegt** | Ruhe -69 dB. Einbruch ab Spitze ≈ 4 %; im Log einzelne Diktate mit 8–9 %, Live-Sitzungen 29 % und 67 %. |

**Hauptursache:** der feste Schwellwert von -45 dB im Live-Schnitt zusammen mit dem Sprechpegel des Headsets (B über E). Als Mechanismus ist das belegt: Live bricht bei
leiser Sprache ein, normales Diktieren nicht. Ob Magnus' Alltagspegel dort liegt, zeigt erst Abschnitt 6. Alles andere (Rechenzeit, Modell, Einfügen) ist
ausgeschlossen oder kostet höchstens ein Wort von 210.

## 8. Fix-Vorschläge (nicht umgesetzt), nach Wirkung

1. **Stille nicht absolut, sondern relativ messen:** Schwelle = gemessener Ruhepegel + etwa 10–15 dB, nach unten bei -45 begrenzt; dazu `Silent` erst setzen, wenn das Stück
   auch relativ still ist. Stelle: `MeetingChunker.Write`/`Complete` (`src/WhisperShortcut.Core/MeetingChunker.cs`, Vergleich `db >= _policy.SilenceThresholdDb`),
   die Policy in `LiveDictation.ChunkPolicy` (`src/WhisperShortcut.Core/LiveDictation.cs:123`). Absicherung: der Pegeltest aus Abschnitt 3 als Unit-Test
   (gedämpfte Aufnahme darf keine Sprachstücke verwerfen). Vorher den Mac prüfen (`ChunkedDictateRecorder.silenceThresholdDB`): Er hat dieselben -45 dB,
   bekommt aber vom eingebauten Mac-Mikro mit automatischer Verstärkung vermutlich lautere Sprache.
2. **Kein Sprachstück still verwerfen:** Ein `Silent`-Stück, dessen lautestes Fenster nur knapp unter der Schwelle liegt (z. B. über -55 dB), trotzdem dekodieren.
   Parakeet liefert für echte Stille "" zurück. Stelle: `LiveDictationSession.Add` (`LiveDictation.cs`, Zweig `if (silent)`) bzw. `TrayApp.DrainLivePieces`.
   Das wirkt sofort und unabhängig von Fix 1 gegen die fehlenden Wörter.
3. **Nähte in „Nur gesprochen“:** Ein vom Modell gesetzter Satzpunkt am Ende eines Pausen-Stücks soll nicht stehen bleiben, wenn „Punkt“ nicht gesprochen wurde („mal. Weiter“).
   Ebenso ein kurzes Stopp-Stück, das nur ein Tastenklick ist („Yeah“). Stellen: `LiveDictation.FinishPiece` (`LiveDictation.cs:162`),
   `ParakeetPunctuationTiming.KeepSpokenMarks` (`Offline/ParakeetPunctuationTiming.cs:189`), `TrayApp.ProcessLiveAsync` (`tail`). Wirkung klein (1–2 exakte Treffer).

## Was an der ersten Fassung falsch war

1. **a) war nicht der Pfad fürs normale Diktieren**, sondern der Live-Pfad auf die ganze Datei. Damit kam 6,2 % statt 7,1 % heraus, und die Live-Logik wirkte
   um 1,4 Prozentpunkte schädlicher, als sie ist (richtig: +0,5, ein Wort).
2. **„Text beim Stoppen sofort, Median 0,00 s“** war ein Messfehler: Lief nach dem Stoppen noch ein Pausen-Stück, wurde das nicht gezählt. Richtig: Median 0,22 s, max 0,73 s.
3. **Stücke mit 1 Zeichen im Log als „zerhackt“ gedeutet:** Das sind fast sicher gesprochene Satzzeichen. Damit fiel der einzige Beleg für B im Alltag weg.
4. **Die gehaltene Sitzung als Einfügeproblem (D) gewertet:** Sie startete 0,4 s nach dem Öffnen des eigenen Einstellungsfensters.
5. **„Mac-Werte aus anderem Set“** war eine Behauptung ohne Beleg. Das Set ist von hier aus nicht bestimmbar.
6. **Logzeile `waitedForChainMs`** war falsch berechnet (Dekodierzeit abgezogen); korrigiert. Stille Stücke loggen jetzt auch ihren Pegel.
7. **Exakte Treffer als Vergleich:** Sie schwanken zwischen zwei Läufen um ±1–3, weil Parakeet mit mehreren Threads nicht ganz deterministisch ist.
8. **Nicht geprüft** hatte die erste Fassung die Pegel-Abhängigkeit. Die ist jetzt die Hauptursache.
9. **Branch:** Eine andere Session hat `b6aea52` (Startmenü-Eintrag) auf `diag/live-diktat` committet und dabei die Live-Instrumentierung in `TrayApp.cs` mitgenommen.
   Die Startmenü-Arbeit liegt also nur auf diesem Branch, nicht auf `main`. Dieselbe Session arbeitet weiter im Checkout (Installer-Dateien, nicht committet).
