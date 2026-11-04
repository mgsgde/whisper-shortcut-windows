# Windows vs. macOS: Funktionsabgleich (Stand 03.11.2026)

Geprüft: Windows `integrate/cloud-sessions` (`a17d964`, enthält #18–#27, #29, #30) plus die offenen PRs
#31–#38, gegen die macOS-App **v8.75** (`mgsgde/whisper-shortcut`, `e06174f`, Quellcode gelesen, nicht nur
das Änderungsprotokoll). Der vorige Stand (01.11., gegen v8.73) ist hier aufgegangen; die Nachträge weiter
unten bleiben als Detail stehen.

**Status:** **erledigt** · **PR #n** (offen, schließt die Lücke) · **offen** · **bewusst anders**.
Die beiden Wortlaut-Tests gegen die Swift-Quellen (`SwiftSourceFact`) laufen gegen v8.75 grün.

**Kurz:** Diktieren, Text bearbeiten, Vorlagen, Screenshot, Glossar, Offline, Live-Meeting und MCP sind da.
Der Abgleich hat sechs Lücken gefunden, die Nutzer merken; sie stecken in fünf PRs (#34–#38):
früherer Verlauf bei Schnellanweisungen, leere Antwort löscht die Aufnahme, Antworten werden beschnitten,
Glossar-Echo-Regeln, GPT Transcribe ohne Glossar, Abrechnungs-Meldung. Offen bleiben vor allem UI-Themen,
die echtes Windows brauchen, und der Umfang (Chat, Vorlesen, eigener Server).

## 1. Diktieren

| Funktion | Mac v8.75 | Windows | Status |
|---|---|---|---|
| Retries, Backoff, `Retry-After`, Tageskontingent | 5 Versuche, `retryAfter` | `HttpRetry` (5–6, max. 15 s, `RetryInfo`) | erledigt |
| Letzte Aufnahme erneut verarbeiten | ja | Menü + Klick auf die Meldung (`TrayApp.RetryLast`) | erledigt |
| Aufnahme nach Absturz / Schlaf / voller Platte | – | `RecordingCheckpoint`, Spill-Datei | erledigt (Windows weiter) |
| 0,2 s Nachlauf nach Stopp | `audioTailCaptureDelay` | `MicAudioSource.TailCapture` | erledigt |
| Einfügeziel vom Start gemerkt | `PasteTargetPolicy` (pro App) | `PasteTargetPolicy` (pro Fenster, strenger) | erledigt |
| Sicheres Einfügen, Satzzeichen nur gesprochen (#321/#322) | 8.75 | – | **PR #32** |
| Live diktieren (#324) | 8.75 | – | **PR #33** |
| Lange Diktate: Stücke schon während der Aufnahme | `ChunkedDictateRecorder` | Stücke erst nach Stopp (ab 150 s) | offen (mittel) |
| Ein fehlgeschlagenes Stück | Rest wird trotzdem eingefügt | ganzer Lauf scheitert, bleibt wiederholbar | bewusst anders (nichts fehlt still in der Mitte) |
| Abgebrochene Aufnahme transkribieren | Menüeintrag, nur Diktat | Tray-Eintrag nach Abbruch beim Verarbeiten (Plan Schritt 9) | erledigt |
| Rückfrage bei langen Aufnahmen | Kostenhinweis ab 5 min, nur Cloud | 30 min Limit, verarbeitet automatisch | bewusst anders (kein Verlust, keine Kostenfrage) |
| Upload-Format (#235) | FLAC | WAV | bewusst anders (gleiche Qualität, doppelte Größe) |
| Timeout nach Länge (#285) | nur eigener Server | 60 s + Größe/40 KB, Gemini und OpenAI | erledigt |
| Sprache wählbar | Auswahl, ab Werk Auto | Diktieren → Sprache, nur OpenAI (Plan Schritt 9) | erledigt |
| Spill-Datei nach Mikrofonfehler / nicht wiederholbarem API-Fehler | – | bleibt liegen, kommt beim nächsten Start als „Aufnahme unterbrochen“ | offen (klein, Windows-eigen) |
| Template-Aufnahme nach Absturz | – | kommt als Diktat zurück (`TrayApp` Recovery) | offen (klein, Windows-eigen) |

## 2. Glossar und Modelle

| Funktion | Mac v8.75 | Windows | Status |
|---|---|---|---|
| Glossar-Prompt, Tie-Breaker, Nachkorrektur | `GlossaryDocument` | `GlossaryPrompt` | erledigt |
| Echo verwerfen, ohne Glossar neu fragen (#259, beide Anbieter) | ja | `DiscardGlossaryEcho`, Retry ohne Glossar | erledigt |
| Echo: Bindewörter neutral (#261, 63ded84b) | „Claude Code, MCP und Bachata.“ | wurde durchgelassen | **PR #35** (mit Bindewort mind. 3 Begriffe, strenger als Mac) |
| Einzelnes Glossarwort am Ende (34bb2ce4) | ab 5 Wörtern davor | nur ab 2 Begriffen | **PR #35** |
| Sprecher-Labels im Echo-Test (93458ddb) | Meeting | Windows-Meetings haben keine Labels | entfällt |
| `gpt-transcribe`: Glossar als `keywords`, kein Prompt, keine Temperatur | `isContextFieldStyle` | Glossar als Anweisung, die es ignoriert | **PR #37** |
| Meeting-Abschnitte ohne Glossar-Stichwörter (#261) | ja | gingen mit | **PR #37** |
| OpenAI-Standard für Diktieren | `gpt-transcribe` (Audit 03.11.: 4o 9/9 Echos, Abschaltung 26.02.2027) | `gpt-4o-transcribe` | offen — **Entscheidung Magnus** (Standard + Migration) |
| Gemini-IDs migrieren | `migrateLegacy*RawValue` | `GeminiModels.Renamed` | erledigt; `gemini-2.0-flash` → 3.1 Flash-Lite **PR #37** |
| OpenAI-IDs migrieren | gpt-5 / 5.x → GPT-6 | `OpenAiModels.Renamed` | erledigt |
| Temperatur / Thinking wählbar | Diktieren → Erweitert | Diktieren → Erweitert → Genauigkeit (Plan Schritt 9) | erledigt |

## 3. Text bearbeiten (Dictate Prompt), Schnellanweisungen, Vorlagen, Screenshot

| Funktion | Mac v8.75 | Windows | Status |
|---|---|---|---|
| Gesprächsgedächtnis (5 Runden / 5 min, nicht Compose, nicht offline) | `PromptConversationHistory` | `PromptConversationMemory` | erledigt |
| Schnellanweisung ohne Markierung = Compose, kein Verlauf | ja | schickte bis zu 5 frühere Runden mit (anderer Patient) | **PR #34** |
| Antworten nicht mit Transkript-Heuristik beschneiden (R158, e1cd024f) | `PromptReplyCleanup.normalize` | `Normalize` mit Prompt-Prefix/-Suffix | **PR #36** |
| Leere Antwort ist wiederholbar, Aufnahme bleibt | `emptyModelReply` | „Nichts erkannt“, Aufnahme gelöscht | **PR #36** |
| Bare-Edit-Befehl ohne Markierung | `isBareEditCommand` | `BareEditCommand` | erledigt |
| Eigene Schnellanweisungen ohne Markierung (R119) | wurden abgelehnt (Bug) | wirken auf das Diktierte | bewusst anders (Bug kann nicht auftreten) |
| Anzahl Schnellanweisungen | bis 9 | bis 9 (Strg+1–9) | erledigt |
| Getippte Anweisung in der Overlay-Karte | Textfeld, Return | nur Sprache / Strg+Zahl | offen (UI, Fokus, braucht echtes Windows) |
| Gelernte Schnellanweisungen aus dem Nutzungslog | ja | kein Log | bewusst anders (keine Diktate gespeichert) |
| Vorlagen als eigener Durchlauf (#275, #290) | ja | ja | erledigt |
| Vorlage + Screenshot | nie | Schnipsel geht mit | bewusst anders (Nutzer hat ihn gezielt ausgewählt) |
| Vorlage: Tab = einmal ohne Vorlage, „Erneut ohne Vorlage“ (10 min) | ja | „Nochmal ohne Vorlage“ im Tray (10 min); Tab während der Aufnahme fehlt | teilweise |
| Vorlage während der Aufnahme mit 1–9 wählen | ja | Kürzel nimmt die zuletzt benutzte | bewusst anders |
| Screenshot | automatisch oder Auswahl | nur eigene Auswahl, einmalig, 5 min, ab Werk aus | bewusst anders (Patientendaten) |
| Geleerter System-Prompt bleibt leer (#267) | Fix | kein Autosave, leer = Standard | erledigt |
| Offline: Hinweis, dass lokale Modelle den Prompt ignorieren (#268) | ja | `SettingsForm` | erledigt |

## 4. Fehler, Updates, Einstellungen, Sonstiges

| Funktion | Mac v8.75 | Windows | Status |
|---|---|---|---|
| Abrechnungs-Meldung mit „Aufladen“ (#283) | ja | „noch einmal versuchen“, Gemini-Billing als „Schlüssel ungültig“ | **PR #38** |
| Überschrift doppelt (#287) | Fix | Titel und Text getrennt | erledigt |
| Deutsche Fehlertexte (cc0bcd30) | ja | ja | erledigt |
| Einstellungen: Schlüssel gültig, aber ohne Abrechnung | – | wird wie ungültig gezeigt (`KeyCheck`) | offen (klein) |
| Update-Kanäle Stabil / Neueste (#278) | ja | nur `releases/latest` | offen (klein); oder bewusst anders, solange nur Tester |
| Fenstergröße Einstellungen (#282) | Fix | fest 980×700, an Arbeitsfläche angepasst | erledigt |
| Kürzel abschaltbar | jedes Kürzel, Glossar ab Werk aus | Diktat, Bearbeiten, Glossar immer belegt | offen (klein) |
| Telemetrie (#288) | Zähler | keine | bewusst anders (Praxis, Patientendaten) |
| Problem melden mit letztem Diktat (#240) | Text, Modell, Echo | Version, Modelle, Log, ohne Diktat | bewusst anders (Datenschutz); optional „Letzten Text beilegen“ |
| Benachrichtigungen / englische UI | Sprache, Popup-Einstellungen | fest Deutsch | offen (groß) |
| Welcome-Tour | mehrere Schritte | `WelcomeForm`, fünf Schritte wie der Mac (Plan Schritt 8) | erledigt |

## 5. Live-Meeting und MCP

| Funktion | Mac v8.75 | Windows | Status |
|---|---|---|---|
| Mikrofon + PC-Ton, Modus Automatisch / Mit / Ohne (#258) | ja | WASAPI-Loopback, gleiche drei Modi | erledigt |
| Echte Abtastrate (#262) | ja | `format.SampleRate` | erledigt |
| Ausgabegerät wechselt während des Meetings (#260) | Tap neu aufbauen | bleibt am alten Gerät, nach Fehler nur noch Mikrofon | offen (mittel, braucht echtes Windows) |
| Diktat während Meeting überschreibt nichts (R107–R109) | Fixes | getrennte Zustände | erledigt (by design) |
| Meeting-Fehler mit Grund | – | „N noch nicht transkribiert“ ohne Ursache | offen (klein) |
| MCP-Grundwerkzeuge, Stufen, Undo | ja | ja (Abschnitt E) | erledigt |
| MCP-Meeting-Werkzeuge + Live-Transkript (a5de0e37) | ja | – | offen; danach Datenschutztext unter „Claude & KI-Apps“ anpassen |
| MCP-Vorlagen-Werkzeug, `get_recent_interactions` | ja | – | offen / bewusst anders (keine Diktate gespeichert) |

## 6. Umfang, den Windows nicht hat

Im Chat: Google/Trello/IMAP, Ordner, Memory, eigene Befehle, Bilder (Chat, Vorlesen und Meetings-Seite: Schritt 10
in `plan-mac-angleich.md`), Smart Improvement mit
Interaktionslog, Schreibstil pro App, weitere Anbieter (Grok, OpenRouter, Anthropic), **eigener Server**
(OpenAI-kompatible URL, #251/#285 inkl. LAN im Offline-Modus), Cloud-Modell-Angebot nach Schlüssel (#321,
daher auch dessen Bug nicht), Fn-Taste. Parakeet: Windows TDT 0.6B v3 (sherpa-onnx), Mac empfiehlt Parakeet
Ultra (FluidAudio).

## 7. Wo Windows besser ist oder anders bleibt

Mikrofonwahl mit Pegeltest, Hinweis bei stummem/abgestecktem Mikro, Admin-Fenster und gehaltene Tasten,
Diktate nicht im Win+V-Verlauf, Schlüssel im Header, SHA-256 der Modelle, „Nicht in deinem Diktat“,
Schnellanweisung auf das gerade Diktierte, abgeschnittene Offline-Antwort wird nicht eingefügt, OpenAI und
Gemini gemischt wählbar, Update mit ECDSA-Signatur, Aufnahme übersteht Absturz und volle Platte.

## Was als Nächstes kommt

Nach #32–#38, nach Nutzerwirkung:

1. **Entscheidung Magnus:** OpenAI-Standard auf `gpt-transcribe` umstellen, Bestandsnutzer vor dem
   26.02.2027 migrieren. Code ist klein, sobald #37 drin ist.
2. Stücke schon während der Aufnahme (lange Diktate).
3. Kleine Core-Lücken ohne UI-Risiko: Spill-Datei nach nicht wiederholbarem Fehler aufräumen,
   Kürzel abschaltbar, Meeting-Fehler mit Grund, „Schlüssel gültig, ohne Abrechnung“.
4. **Brauchen echtes Windows (VM ist weg, #31)** — erst mit einem Windows-Rechner angehen:
   getippte Anweisung im Overlay, 9 Schnellanweisungen, abgebrochene Aufnahme transkribieren,
   Vorlage einmal ohne Vorlage, Gerätewechsel im Meeting, Temperatur/Thinking-Karte, Welcome-Tour.
5. Umfang: MCP-Meeting-Werkzeuge, eigener Server, Vorlesen; Chat zuletzt.

## Nachtrag 02.11.2026: Diktiervorlagen (Mac v8.72)

Nachgezogen: Seite „Vorlagen“ (Name, Format-Anweisungen, optional eigenes Kürzel), allgemeines Kürzel für die
zuletzt benutzte Vorlage (ab Werk frei). Eine Vorlagen-Aufnahme ist ein eigener Durchlauf: Die Anweisungen der
Vorlage sind der ganze System-Prompt, die Anweisungen, die „Vorlage für neue Texte“ und die Schnellanweisungen
von „Text bearbeiten“ gelten nicht; eine Markierung wird in das Format gebracht. Anders als auf dem Mac: keine
Auswahl mit 1, 2, 3 während der Aufnahme (das allgemeine Kürzel nimmt die zuletzt benutzte), und die Aufnahme
geht immer erst durch die Spracherkennung (in Stücken) und dann als Text ans Sprachmodell, damit auch mehrminütige
Befunde vollständig ankommen.

## Nachtrag 02.11.2026: Screenshot für „Text bearbeiten“ und Vorlagen

Nachgezogen (Mac: Abschnitt „Screenshot“, `dictatePromptUsesScreenshotSelection`): eigenes Kürzel „Screenshot“
(ab Werk frei) öffnet die Bildschirmauswahl von Windows (`ms-screenclip:`, wie Win+Umschalt+S). Das Bild kommt über
die Zwischenablage, die danach sofort wiederhergestellt wird, und geht einmal mit der nächsten Anweisung oder
Vorlage an Gemini bzw. OpenAI; nach 5 Minuten ungenutzt wird es verworfen. Ab Werk aus (Schalter „Screenshots
mitsenden“, Patientendaten), offline und mit dem lokalen Sprachmodell gesperrt. Anders als auf dem Mac: kein
Bildschirmfoto ohne eigene Auswahl, das Kürzel wirkt nur, wenn keine Aufnahme läuft.

## Nachtrag 03.11.2026: Pilot-Fixes aus Mac v8.75 (#321, #322)

Nachgezogen: **Sicheres Einfügen** (`Core.ClipboardPasteGuard`): Strg+V nur, wenn die Zwischenablage noch genau
unseren Text hat (`GetClipboardSequenceNumber` unverändert und Text gleich), sonst Hinweis „Nicht eingefügt“;
Wiederherstellen nur, wenn die Sequenznummer sich nicht bewegt hat. Wartezeit vor Strg+V 150 ms, Wiederherstellen
nach 3 s (vorher 50 ms / 2 s). „Letzte Aufnahme erneut verarbeiten“ (Tray-Menü, Klick auf die Meldung) und eine
wiederhergestellte Aufnahme kopieren nur und fügen nie ein. **Satzzeichen** unter Diktieren (nur bei
Spracherkennung auf diesem PC): „Automatisch“ (ab Werk) / „Nur wenn gesprochen“, gleiche Regeln wie
`SpokenPunctuation.swift` (inkl. „Schrägstrich“ → „/“ nur in diesem Modus, kein Schlusspunkt); Meetings und
gesprochene Anweisungen bleiben automatisch. MCP: `dictation.punctuation`.
Nicht übernommen: Glossar-Ähnlichkeitsschwellen (Windows hat keinen CTC-Spotter wie FluidAudio, sondern
sherpa-onnx-Hotwords beim Beam Search, Score 2,5), Cloud-Modell-Angebot nach dem Schlüssel (#321, gibt es auf
Windows nicht), „Zwischenablage leeren“ (im Mac-Diff enthalten, aber nicht Teil der Pilot-Fixes).

## E. MCP-Server (Stand 02.11.2026)

Da, unter Einstellungen → Claude & KI-Apps (`src/WhisperShortcut.Core/Mcp/`, `McpService.cs`). Gleiche Namen und
Semantik wie `WhisperShortcut/MCP/` auf dem Mac: `get_configuration` (mit `tune_recipe`), `get_settings` /
`update_settings`, `add_glossary_terms`, `update_system_prompt` (`dictation`, `dictate_prompt`), `report_problem`,
`get_proposal_status`, Prompt `tune_whisper_shortcut`, Ressourcen `whisper-shortcut://…`, Zugriffsstufen
read/propose/apply mit Änderungsliste und Undo, zwei Schlüssel (lesen / ändern, DPAPI). Nur auf Windows:
`update_quick_instructions`, Anbieterwahl (`dictation.engine`, `dictate_prompt.engine`). Bewusst nicht: Schlüssel,
Offline-Modus und Mikrofon über KI-Apps. Fehlt noch: Meeting-Werkzeuge (eigener Provider auf `MeetingStore`, der
jetzt da ist), Vorlagen-Werkzeug (Vorlagen sind jetzt da), `get_recent_interactions` (Windows speichert keine Diktate), Codex.

**Nachtrag 03.11.2026 — Live diktieren (macOS #324 „Dictate Live“):** eigenes Kürzel unter Diktieren (ohne
Vorgabe, also aus, bis man eins wählt), nur mit Parakeet auf diesem PC und „Automatisch einfügen“ an. Schnitt in
Pausen wie auf dem Mac (mind. 1,5 s, 0,6 s Stille; zusätzlich höchstens 30 s), 0,4 s Abstand zwischen den
Stücken, eingefügt nur ins Fenster vom Start (wartet sonst und holt nach). Die Mac-Fn-Taste gibt es nicht; das
Kürzel übernimmt ihre Rolle. Offen: Einstellung „Satzzeichen“ (#322, Hook in `LiveDictation.FinishPiece`) und
„Zwischenablage nach dem Einfügen leeren“ (gibt es auf Windows nicht, Hook in `ClipboardAtEnd`), kein Schutz für
Passwortfelder (Windows hat kein `IsSecureEventInputEnabled`).

**Nachtrag 03.11.2026, Kürzel und Menü wie auf dem Mac:** Ziffern-Kürzel wie `ShortcutConfig.default`, Modifier
Alt statt `⌃⌥` (Strg+Alt ist AltGr): Alt+1 Diktieren, Alt+2 Dictate Prompt, Alt+3 Bildschirmfoto (bleibt in der
Zwischenablage), Alt+0 Einstellungen öffnen/schließen, Alt+5 Live diktieren und Alt+7 Glossar vorgeschlagen, aber
aus. Rechte Strg statt Fn (`FnDictationToggle`, gleiches Verhalten). Bestehende Installationen behalten ihre
alten Kürzel (wie der Mac `legacyCommandDigit`). Neue Installationen: keine Signaltöne, Screenshots an.
Tray-Menü in der Reihenfolge und mit den Titeln von `MenuBarController.createMenu`. Abweichungen:
Chat liegt auf Alt+6 (Mac ⌥Leertaste); „Meeting“ steht zusätzlich als eigenes Untermenü neben „Chat“;
„Letzte Aufnahme erneut verarbeiten“ statt „Abgebrochene Aufnahme transkribieren“; „Feedback senden“ enthält
„Problem melden …“ und „Protokoll öffnen“ statt der Mac-Kanäle; kein „Einrichtung abschließen …“. Der
Offline-Schalter im Menü entfällt (Mac hat keinen), offline bleibt in den Einstellungen.
