# Windows vs. macOS: Funktionsabgleich (Stand 1.11.2026)

Geprüft: Windows `main` 0.5.4 (`206a89b`) plus diese Branch gegen die macOS-App **v8.73**
(`https://github.com/mgsgde/whisper-shortcut`, `dd51bb8`). Der vorherige Abgleich (27.10.2026,
v8.63) ist überholt: das Paket „Verlässlich“ (A1–A9) und die Einstellungs-Seitenleiste sind in
0.5.x angekommen.

**Kurz:** Diktieren, Dictate Prompt, Glossar, Offline (Parakeet + lokales LLM), Schnellanweisungen,
letzte Texte, Einfügeziel, Retries und Glossar-Echo sind auf Windows da. Die großen macOS-Blöcke
(Chat, Vorlesen, Screenshot, Live-Meeting, MCP, Smart Improvement) fehlen weiter. Neu in diesem
Abgleich: Gesprächsgedächtnis bei Online-Edits, OpenAI-Modell-Migration, 0,2 s Nachlauf nach Stopp,
Log-Rotation, Stall-Timeouts, CC-BY-Hinweis für Parakeet.

## A. Gleiche Funktion — erledigt seit 27.10. (nicht mehr Lücke)

| # | Damals | Jetzt (Windows 0.5.4+) |
|---|---|---|
| A1 | Temperatur 1.0 | `GeminiClient` / `OpenAiClient` senden Temperatur 0 |
| A2 | 1 Retry, Audio weg | 5–6 Versuche mit Backoff, Netzfehler, WAV bleibt, Menü „Letzte Aufnahme erneut verarbeiten“ |
| A3 | 429 immer „Kontingent“ | `RetryInfo` / `Retry-After`, Tageskontingent getrennt |
| A4 | Einfügen ins vordere Fenster | Ziel beim Start gemerkt, zurückgeholt oder Zwischenablage (`ClipboardPaster` / `PasteOutcome.ClipboardOnlyTargetChanged`) |
| A5 | Glossar roh | `GlossaryPrompt`: Notizen weg, falsche Schreibweisen gefiltert, Tie-Breaker, Nachkorrektur |
| A6 | Kein Echo-Schutz | `DiscardGlossaryEcho`, Retry ohne Glossar, `StripTrailingGlossaryEcho`, `LooksLikeRefusal` |
| A7 | Gemini-IDs nie migriert | `GeminiModels.Normalize*` (3.7/3.6/2.5/… → 3.8 bzw. 3.1 Flash-Lite) |
| A8 | Nur Unicode-Text | alle Speicher-Formate, 64-MB-Deckel |
| A9 | Strg+C nach 2 s tot | 3 s Late-Copy-Watch |

Einstellungen (Martins Liste): Seitenleiste, Modell und System-Prompt pro Modus, automatisch
einfügen, Zwischenablage wiederherstellen, Info mit Version/Update/Tour/Zurücksetzen, Problem
melden, Mit Windows starten — in `main`.

## B. Noch offen, aber näher (dieser Branch schließt die markierten)

| # | Lücke | Windows | Mac | Dieser Branch |
|---|---|---|---|---|
| 1 | **Gesprächsgedächtnis** | jede Anfrage isoliert | 5 Runden / 5 min, nur Online-Edits, nicht Compose, nicht Offline (`PromptConversationHistory.swift`) | **ja:** `PromptConversationMemory`, an Gemini/OpenAI gehängt |
| 2 | **OpenAI-Modell-IDs** | unbekannte IDs → Default ohne Nachfolger | `migrateLegacyPromptRawValue` (gpt-5 → GPT-6 …) | **ja:** `OpenAiModels.Renamed` |
| 3 | **Letztes Wort abgeschnitten** | Stopp sofort | 0,2 s Nachlauf (`audioTailCaptureDelay`) | **ja:** `MicAudioSource.TailCapture` |
| 4 | **Log wächst, kein Stack** | append only, Exception ohne Stack | – | **ja:** Rotation bei 2 MB, Stacktrace |
| 5 | **Download/LLM hängt** | kein Stall-Timeout | Stream-Stall-Watchdogs | **ja:** 30 s Download, 90 s lokales LLM |
| 6 | **CC-BY Parakeet** | nur AGPL auf der Info-Seite | Acknowledgements mit Links | **ja:** Info + README |
| 7 | Lange Diktate erst nach Stopp | Stücke nach Stopp, ab 150 s | Stücke schon während des Sprechens (`ChunkedDictateRecorder`) | offen (mittel/groß) |
| 8 | 5 statt 9 Schnellanweisungen | `QuickActionList.Max = 5` | bis 9, plus getipptes Feld in der Karte | offen (UI) |
| 9 | Getippte Anweisung in der Overlay-Karte | nur Sprache / Strg+1–5 | Textfeld, Return | offen (UI, Fokus) |
| 10 | Kürzel nicht abschaltbar | immer drei globale Hotkeys | jeder Shortcut änderbar, Glossar ohne Default | offen (klein) |
| 11 | Temperatur / Thinking wählbar | fest 0 / low bzw. minimal | Settings → Dictate → Advanced | offen (UI) |
| 12 | Abgebrochene Aufnahme transkribieren | weg | Menüeintrag | offen (klein) |
| 13 | Bestätigung bei langen Aufnahmen | 30 min hartes Limit | Safeguard-Dialog | offen (UI) |
| 14 | Benachrichtigungen / englische UI | fest Deutsch, Balloons immer | Sprache, Popup-Einstellungen | offen (groß) |

## C. Funktionen, die es auf Windows gar nicht gibt (Umfang)

Chat-Fenster samt Google/Trello/IMAP/Ordner/Memory/Befehle, Vorlesen,
Smart Improvement und Interaktionslog, Schreibstil pro App, weitere Anbieter (Grok,
OpenRouter, Anthropic, eigener Server), Telemetrie, Welcome-Tour
über den ersten Start hinaus, Fn-Taste.

**Nachtrag 02.11.2026:** Live-Meeting ist da (Mikrofon + PC-Ton per WASAPI-Loopback, Modus Automatisch /
Nur Mikrofon / Mikrofon + PC-Ton, Abschnitte bis 60 s mit Schnitt in Pausen wie auf dem Mac, Ton fehlgeschlagener
Abschnitte bleibt und wird nachgetragen, Ordner pro Meeting mit `transcript.md`, `meeting.json`, `summary.md`).
Lese-API für den MCP-Server: `MeetingStore` in Core. Ohne Chat-Fenster, Live-Notizen und Sprecher-Zuordnung.

Parakeet: Windows lädt NVIDIA TDT 0.6B v3 (int8, sherpa-onnx). Der Mac empfiehlt Parakeet Ultra
(Moondream-Retrain, FluidAudio). Gleiche Familie, nicht dasselbe Gewicht.

## D. Wo Windows besser ist oder anders bleibt (beibehalten)

Mikrofonwahl mit Pegeltest, Hinweis bei stummem/abgestecktem Mikro, Admin-Fenster und gehaltene
Tasten, Diktate nicht im Win+V-Verlauf, Schlüssel im Header, SHA-256 der Modelle, „Nicht in
deinem Diktat“, Schnellanweisung auf das gerade Diktierte, abgeschnittene Offline-Antwort wird
nicht eingefügt, OpenAI und Gemini gemischt wählbar, Update mit ECDSA-Signatur.

## Empfohlene Reihenfolge (was noch kommt)

1. Stücke schon während der Aufnahme (B7) — größter spürbarer Gewinn bei langen Diktaten.
2. Overlay: getippte Anweisung + 9 Schnellanweisungen (B8–B9).
3. Kürzel abschaltbar, abgebrochene Aufnahme behalten (B10, B12).
4. Danach Umfang: Vorlesen; Chat zuletzt (Screenshot, Live-Meeting und MCP sind nachgezogen, siehe unten).

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
