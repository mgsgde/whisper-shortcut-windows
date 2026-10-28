# Meeting-Funktion für Windows: Umsetzungsplan

Stand 28.10.2026, Windows-App v0.2.0. Anlass: Martin (Pilot) will Meetings aufnehmen, auch eine Stunde und
länger, mit Umschalter **Online-Meeting** (Systemton + Mikro) und **Vor Ort** (nur Mikro). Er sitzt bei
Online-Meetings am Headset. Dieses Dokument ist nur der Plan, Code gibt es noch keinen.

## 1. Was die Mac-App macht und was davon nach Windows kommt

Die Mac-App nimmt im Meeting **nur das Mikrofon** auf (`LiveMeetingRecorder.swift`, zwei `AVAudioRecorder`
im Wechsel, 24 kHz mono). Systemton gibt es dort nicht. Windows wäre mit Loopback an der Stelle voraus.

| Mac-Baustein | Was er tut | Windows v1? |
|---|---|---|
| `LiveMeetingRecorder` | Stücke von 10–60 s, Schnitt bei 1,5 s Pause unter −40 dB, sonst hart nach 60 s. Stumme Stücke werden verworfen. | **Ja**, nachgebaut (Schnittlogik, Stille-Erkennung) |
| `LiveMeetingSession` | Stücke sofort an Gemini, Text mit `[mm:ss]` an `.txt` anhängen. Misslungene Stücke bleiben als Audio liegen (`MeetingAudioBacklog`) und werden später nachgeholt. Beim Beenden des Programms wird das laufende Stück gerettet, ein Marker meldet beim nächsten Start den Abbruch. | **Ja**, das ist der Kern |
| Diarisierungs-Prompt („Speaker A/B …“) | pro Stück | **Ja**, auf Deutsch („Sprecher A …“) |
| Sprecher-Konsolidierung | ein LLM-Lauf über das ganze Transkript am Ende, damit „Sprecher A“ über alle Stücke dieselbe Person ist | Nein, später |
| Zusammenfassung (`.summary.md`) | am Ende per LLM | Nein, als erster Folgeschritt (klein) |
| Live-Notizen, Live-Transkript-Ansicht, Chat zum Meeting, schnellere Stücke beim Zusehen | im Meeting-Fenster | Nein |
| `MeetingListService` | Liste aller Meetings mit Suche | Nein. v1: „Meetings-Ordner öffnen“ und „Letztes Transkript öffnen“ im Tray |
| Fortsetzen eines beendeten Meetings, Umbenennen, Erinnerung nach X Minuten | | Nur die Erinnerung (nach 2 h), Rest später |
| MCP (`list_meetings`, `get_live_transcript` …) | | Nein |

## 2. Aufnahme

**Zwei Quellen, getrennt gespeichert.**

- Mikro: wie `MicAudioSource` über WinMM (`WaveInEvent`, 16 kHz mono 16 bit, Geräteauswahl aus
  `Microphones`), aber gestreamt statt in einen `MemoryStream` für die ganze Aufnahme. Neue Klasse, die
  vorhandene bleibt fürs Diktat unverändert.
- Systemton (nur Online-Modus): `WasapiLoopbackCapture` aus `NAudio.Wasapi` 2.2.1 (passt zu `NAudio.WinMM`
  2.2.1), immer auf dem aktuellen Standard-Wiedergabegerät. Liefert das Mischformat des Geräts, meist 48 kHz
  Float Stereo, manchmal 44,1 kHz oder 5.1. Umrechnung: Kanäle mitteln → `WdlResamplingSampleProvider`
  (reiner Managed-Code, läuft auch auf ARM64) auf 16 kHz → 16 bit.

**Mischen oder zwei Spuren?** Auf der Platte immer zwei Spuren (`mic`, `sys`). An Gemini geht in v1 **eine
gemischte Mono-Spur** pro Stück. Der naheliegende Trick, Ich links und Gegenüber rechts in eine Stereo-Datei
zu legen, geht nicht: laut Gemini-Doku werden mehrere Kanäle vor der Erkennung zu einem zusammengelegt.
„Ich“ vs. „Gegenüber“ heißt also zwei Anfragen pro Stück und danach die Sätze beider Spuren in die richtige
Reihenfolge bringen. Die Kosten sind gering (Audio-Tokens, grob 10–15 Cent pro Stunde und Spur), der
Aufwand steckt im Zusammenführen: innerhalb eines 60-s-Stücks weiß man ohne Zeitstempel pro Satz nicht, wer
zuerst sprach. Deshalb als eigener Schritt nach v1 (siehe Schritt 8). Weil die Spuren schon getrennt liegen,
kostet das später nichts an der Aufnahme.

**Loopback liefert bei Stille nichts.** Spielt kein Programm Ton ab, kommen von WASAPI gar keine Pakete,
nicht Nullen. Die `sys`-Spur wird deshalb nach der Uhr mit Stille aufgefüllt, sonst verrutschen die Spuren
gegeneinander. Dasselbe gleicht die Uhrendrift zwischen Headset und Soundkarte aus: an jedem Schnitt werden
beide Spuren auf die erwartete Länge gebracht (auffüllen oder kürzen), die Abweichung bleibt so unter
~50 ms pro Stück.

**Gerätewechsel mitten im Meeting** (Headset ab, Bluetooth weg, anderes Standardgerät):

- Wiedergabe: `MMDeviceEnumerator.RegisterEndpointNotificationCallback`, bei `OnDefaultDeviceChanged`
  (Render) Loopback auf dem neuen Gerät neu starten. Die Lücke wird mit Stille gefüllt.
- Mikro: `RecordingStopped` mit Fehler (heute schon `DeviceLost`) → nach 1 s auf dem Standardmikro neu
  starten, bis zu 30 s lang im Sekundentakt versuchen. Das Meeting läuft weiter, die Anzeige sagt
  „Mikro getrennt“.
- Bricht beides weg, läuft die Uhr weiter und das Meeting wird nicht abgebrochen. Erst Stopp beendet es.

**Stille-Erkennung**: pro Stück und Spur mit `SpeechGate.Analyze`. Ist die gemischte Spur still, geht keine
Anfrage raus (wie auf dem Mac). Bleibt das Mikro im Online-Modus 2 Minuten bei digitaler Stille (Peak unter
−60 dBFS, das was ein gesperrtes Mikro liefert), zeigt die Anzeige einen Hinweis, wie `AudioSilentException`
es heute beim Diktat tut.

**Echo**: Wer im Online-Meeting über Lautsprecher statt Headset hört, hat das Gegenüber doppelt (über
Loopback und leicht verzögert übers Mikro). Die Erkennung kommt damit klar, der Text wird nicht doppelt.
Kein Echo-Filter in v1, nur ein Satz in der Anzeige beim Start: „Am besten mit Headset“.

## 3. Stückeln und Transkribieren

- **Schnitt** während der Aufnahme, nicht danach: Ziel 60 s, frühestens nach 10 s. Geschnitten wird an der
  leisesten 100-ms-Stelle der letzten 15 s der gemischten Spur. Die Suche steckt schon in `WavChunker`
  (privat), sie wird öffentlich gemacht und für Puffer statt ganzer Dateien wiederverwendet.
  `WavChunker.Split` selbst passt nicht, es braucht die fertige Aufnahme.
- **`ChunkedTranscription` passt nicht**: es bricht beim ersten Fehler alles ab. Im Meeting soll ein Fehler
  genau ein Stück betreffen. Neu: `MeetingTranscriber`, eine Warteschlange mit höchstens 2 parallelen
  Anfragen, Wiederholung mit wachsender Pause (5 s, 30 s, 2 min), danach bleibt das Stück als Audio liegen.
- **`GeminiClient.TranscribeAsync`** wird wiederverwendet, mit einer Überladung, die den Meeting-Prompt
  (Sprecher A/B, nichts erfinden, Füllwörter raus) statt des Diktat-Prompts nimmt. Glossar geht mit.
  Retry-Logik und Fehlerarten (`GeminiErrorKind`) gibt es dort schon.
- Die Stücke gehen **schon während der Aufnahme** raus. Nach Stopp ist meist nur noch das letzte Stück offen,
  das Transkript ist also Sekunden nach dem Ende fertig, nicht Minuten.
- **Offline (Parakeet): nein in v1.** Kann keine Sprecher trennen, braucht in der ARM-VM für 100 s Audio
  schon Minuten und würde parallel zum Videocall die CPU belegen. Ist der Offline-Modus an, sagt der
  Meeting-Start klar „Meeting braucht Internet (Gemini)“, statt still in die Cloud zu schicken. Später
  denkbar: Aufnahme lokal, Transkription nach dem Meeting mit Parakeet, ohne Sprecher.

## 4. Speichern

Ort: `%LOCALAPPDATA%\WhisperShortcut\Meetings\<stem>\`, `stem` wie auf dem Mac `Meeting-2026-10-28-143005`.

```
Meeting-2026-10-28-143005\
  meeting.json        Modus, Start, Status (läuft / fertig / unterbrochen), Stücke mit Startzeit und Zustand
  transkript.txt      "[mm:ss] Sprecher A: …" Blöcke, Leerzeile dazwischen (Mac-Format, parsebar)
  stuecke\
    000060000.mic.wav / .sys.wav / .txt    Startzeit in ms im Namen
```

- Jedes Stück wird **sofort auf die Platte** geschrieben, nicht im Speicher gehalten (eine Stunde, zwei Spuren
  wären ~230 MB RAM). Nach erfolgreicher Transkription bleibt nur `.txt`, das Audio wird gelöscht.
- `transkript.txt` wird aus den `.txt` der Stücke in Zeitreihenfolge zusammengesetzt, jedes Mal wenn ein
  Stück fertig wird. Ein langsames Stück 5 hält Stück 6 nicht auf, landet aber trotzdem an der richtigen
  Stelle. Fehlt ein Stück noch, steht dort `[12:00] (noch nicht transkribiert)`.
- **Netzausfall**: Stücke bleiben als WAV liegen, die Warteschlange versucht es weiter. Nach Stopp mit
  offenen Stücken: Tray-Eintrag „Meeting-Audio erneut verarbeiten“ (es gibt schon `_retryItem` fürs Diktat)
  und ein Versuch bei jedem Programmstart.
- **Absturz oder Windows-Neustart**: Das laufende Stück hat dann keinen gültigen WAV-Header. Beim nächsten
  Start sucht die App Meetings mit Status „läuft“, repariert den Header aus der Dateilänge, setzt den Status
  auf „unterbrochen“, transkribiert die liegengebliebenen Stücke und meldet „Meeting von 14:30 wurde
  unterbrochen, Transkript bis 47:12 gespeichert“. Verlust: höchstens die letzten Sekunden vor dem Absturz.
  Ein Meeting fortsetzen gibt es in v1 nicht, ein neues Meeting beginnt.
- **Achtung „Zurücksetzen“**: `AppData.Reset` löscht `LocalFolder` komplett, damit auch alle Meetings. Der
  Dialog muss das ausdrücklich nennen („auch 12 gespeicherte Meetings“), oder Meetings bleiben außen vor.
  Empfehlung: nennen und mitlöschen.

## 5. UI (nur Skizze, gebaut von der Opus-Session)

- **Start/Stopp**: Tray-Menü bekommt „Meeting aufnehmen“ mit den zwei Einträgen „Online-Meeting (Ton vom PC
  + Mikro)“ und „Vor Ort (nur Mikro)“. Während der Aufnahme wird daraus „Meeting beenden (23:14)“.
  Tastenkürzel startet im zuletzt gewählten Modus und stoppt. Vorschlag `Strg+Alt+M`, **nicht**
  `Strg+Umschalt+M` (das ist in Teams Stummschalten).
- **Escape bricht ein Meeting nicht ab.** Beim Diktat verwirft Esc, im Meeting wäre das eine Stunde weg.
  Verwerfen nur über das Tray mit Rückfrage.
- **Anzeige** (`RecordingOverlay`, neuer Modus): kleine Pille statt der Diktat-Karte, damit sie eine Stunde
  lang nicht stört: roter Punkt, „Meeting · 23:14“, im Online-Modus zwei schmale Pegel (Ich / PC-Ton). Klick
  auf die Pille stoppt. Hinweise in derselben Pille: „Mikro getrennt“, „Offline, Audio wird gespeichert“.
  Tray-Icon auf `recording.ico` wie beim Diktat.
- **Nach Stopp**: Pille zeigt „Wird fertig transkribiert …“, dann eine Windows-Benachrichtigung „Transkript
  gespeichert“, Klick öffnet `transkript.txt` im Editor. Im Tray „Letztes Meeting öffnen“ und
  „Meetings-Ordner öffnen“.
- **Einstellungen**: neuer Bereich „Meeting“ mit Modus-Vorgabe, Tastenkürzel und Prompt (wie die anderen
  Modi). Meeting-Liste kommt später.

## 6. Datenschutz

- Beim **ersten** Meeting-Start ein Dialog, danach nicht mehr (Haken „verstanden“ in `settings.json`):
  „Du nimmst alle Teilnehmer auf, im Online-Modus auch alles, was dein PC abspielt. Sag es den anderen vorher.
  Der Ton geht zur Erkennung an Google Gemini.“
- In der Pille dauerhaft der rote Punkt, damit niemand vergisst, dass die Aufnahme läuft.
- Audio wird nach erfolgreicher Erkennung gelöscht, auf der Platte bleibt nur Text.
- Für Martins Praxis-Fall (Anamnese) reicht ein Hinweis in der App nicht: Einwilligung der Patienten, Gesundheitsdaten
  nach Art. 9 DSGVO, Gemini-Region. Das klärt Magnus mit Martin, nicht der Code. Die App bietet nur den
  Hinweis und das Löschen.

## 7. Schritte

Plumbing geht an Grok 4.7 (`scripts/grok-build.sh`), bei Ausfall an Sonnet 5.5. UI baut Opus selbst. Opus
prüft jeden Schritt am Testlauf. Geschätzt 5–6 Grok-Läufe für v1.

| # | Schritt | Größe | Wer | Tests in `tests/WhisperShortcut.Tests` | In der VM |
|---|---|---|---|---|---|
| 1 | **Audio-Helfer in Core**: Kanäle mitteln, auf 16 kHz/16 bit umrechnen, Stille nach Uhr auffüllen, Spuren mischen, leiseste Stelle aus `WavChunker` öffentlich, WAV-Header reparieren | S | Plumbing | `MeetingAudioTests`: 48 kHz Stereo Float → 16 kHz mono (Länge, Pegel, Sinus bleibt Sinus), 44,1 kHz und 6 Kanäle, Auffüllen bei Lücke, Mischen ohne Übersteuern, Header-Reparatur bei abgeschnittener Datei | – |
| 2 | **`MeetingStore`**: Ordner, `meeting.json`, Stück-Dateien, `transkript.txt` in Reihenfolge zusammensetzen, Wiederherstellung beim Start, `AppData` und Reset | M | Plumbing | `MeetingStoreTests`: Reihenfolge bei ungeordnet fertigen Stücken, Platzhalter für fehlende, Wiederherstellung nach „läuft“, Mac-Parser-Format. `AppDataTests` um Meetings ergänzt | – |
| 3 | **`MeetingTranscriber`**: Warteschlange, 2 parallel, Wiederholung, Audio behalten bei Fehler, Meeting-Prompt in `GeminiClient` und `Prompts` | M | Plumbing | `MeetingTranscriberTests` mit falschem Transkribierer: Fehler → Datei bleibt, später Erfolg → Datei weg, stilles Stück → keine Anfrage. `GeminiClientTests`: Meeting-Prompt im Body | – |
| 4 | **`MeetingRecorder`** (App): Mikro gestreamt, Loopback, Schnitt bei Pause, Gerätewechsel, Paket `NAudio.Wasapi` | M–L | Plumbing, Opus prüft genau | nur die Schnittlogik (in Core, mit synthetischen Puffern: Pause → Schnitt, keine Pause → nach 60 s) | Ja: Testton per PowerShell abspielen, Loopback-Spur prüfen. Standardgerät wechseln. Läuft das in der Parallels-VM (virtuelles Audiogerät)? ist der erste Test. |
| 5 | **Verdrahtung in `TrayApp`**: Start/Stopp, Hotkey, Einstellungen `MeetingMode`/`MeetingHotkey`/`MeetingNoticeSeen`, Erinnerung nach 2 h, Retry-Eintrag | S | Plumbing | `SettingsTests`: neue Felder Rundreise und Standardwerte | Start per `tools/vm.sh combo ctrl alt m` |
| 6 | **UI**: Tray-Einträge, Pille im Meeting-Modus, Datenschutz-Dialog, Benachrichtigung, Einstellungsbereich | M | **UI (Opus)** | – | Screenshots per `tools/vm.sh shot`, UIA für Einstellungen |
| 7 | **E2E in der VM**: 60 min mit wiederholter Test-WAV, Prozess mittendrin abschießen, Netz trennen und wiederholen, Headset-Wechsel simulieren. Ergebnisse in `tools/e2e/RESULTS-meeting.md` | M | Plumbing führt aus, Opus wertet aus | – | das ist der Schritt |
| 8 | **Danach, eigene Runde**: „Ich“ / „Gegenüber“ aus den getrennten Spuren (zwei Anfragen, Zeitstempel pro Satz, zusammenführen), Zusammenfassung (`PromptTextAsync` gibt es), Sprecher-Konsolidierung, Meeting-Liste in den Einstellungen | je S–M | gemischt | | |

Schritte 1–3 hängen nicht voneinander ab und können parallel laufen. 4 braucht 1, 5 braucht 2–4, 6 kann nach
5 anfangen. Ein Release (0.3.0) erst nach 7.

## Offene Fragen an Magnus

1. **„Ich“ / „Gegenüber“ gleich in v1?** Empfehlung: nein, erst Schritt 8. v1 trennt Sprecher wie der Mac
   per Prompt, die Spuren liegen aber schon getrennt, damit es später ohne Umbau geht.
2. **Zusammenfassung in v1?** Empfehlung: direkt danach als kleiner Schritt, sobald klar ist, was Martin
   damit will. Für ein Anamnesegespräch sieht eine brauchbare Zusammenfassung anders aus als fürs Teams-Meeting.
3. **Audio nach erfolgreicher Erkennung behalten?** Empfehlung: löschen. Nur Text auf der Platte ist für die
   Praxis-Fälle das kleinere Risiko. Wer Audio braucht, bekommt später einen Schalter.
4. **Offline-Meeting mit Parakeet?** Empfehlung: nicht in v1. Martin hat am 19.10. selbst „dann über Cloud“
   gesagt. Wenn eine Praxis es verlangt, dann als Nach-Transkription ohne Sprecher.
5. **Speicherort `%LOCALAPPDATA%` oder `Dokumente\WhisperShortcut`?** Empfehlung: `%LOCALAPPDATA%` plus
   Menüeinträge zum Öffnen. Dokumente würde in OneDrive synchronisiert, was bei Gesundheitsdaten keiner will.
6. **Tastenkürzel `Strg+Alt+M`?** Empfehlung: ja, einstellbar.
