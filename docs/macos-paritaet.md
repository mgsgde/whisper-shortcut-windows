# Windows vs. macOS: Funktionsabgleich (Stand 27.10.2026)

Geprüft: `main` (47b5bec) gegen die macOS-App v8.63. Fünf parallele Prüfungen (Diktieren, Dictate Prompt,
Einstellungen/Oberfläche, Gesamtinventar, Zuverlässigkeit), die wichtigsten Befunde von Hand nachgesehen.
Tests: 143 grün, 5 Live-Tests übersprungen.

**Kurz:** Die Funktionen, die Windows anbietet (Diktieren, Dictate Prompt, Glossar, Offline,
Schnellanweisungen, letzte Texte), sind da und an einigen Stellen besser als auf dem Mac. Abgespeckt sind
sie bei den **Schutzmechanismen und der Fehlerbehandlung**: Der Mac fängt erfundene Wörter, Glossar-Echos
und Netzfehler ab, Windows meist nicht. Dazu fehlen Einstellmöglichkeiten, die zum Teil schon auf dem Branch
`feat/settings-sidebar` liegen (nicht gemergt).

## A. Gleiche Funktion, aber schwächer (gehört zur Parität)

| # | Lücke | Windows | Mac | Folge |
|---|---|---|---|---|
| 1 | **Keine Temperatur beim Diktat** | `GeminiClient.cs:143` nur `thinkingLevel` | `TranscriptionModels.swift:332-357`, Temperatur 0.0 | Modell läuft bei 1.0, mehr vertauschte/erfundene Wörter. Ein-Zeilen-Fix. |
| 2 | **Aufnahme geht bei Fehler verloren** | 1 Retry nach 1 s, nur 429/500/503, keine Netzfehler (`GeminiClient.cs:265-285`); ein Stück scheitert → ganzes Diktat weg (`ChunkedTranscription.cs:89-98`); WAV verworfen (`TrayApp.cs:412-427`) | 5–6 Versuche mit Backoff, Netzfehler inkl., Retry pro Stück, Retry-Knopf behält das Audio | 25-min-Diktat weg nach einem WLAN-Aussetzer |
| 3 | **429 = „Kontingent erschöpft“** | ignoriert `RetryInfo` (`GeminiClient.cs:302`) | wartet die genannte Zeit (`GeminiAPIClient.swift:494-523`) | falsche Meldung, wo eine Minute Warten gereicht hätte |
| 4 | **Einfügen ins falsche Fenster** | fügt ins Fenster ein, das am Ende vorne ist (`ClipboardPaster.cs:60-88`) | merkt sich die App beim Start (`PasteTargetPolicy.swift`) | Fensterwechsel während der Verarbeitung → Text landet z. B. in Outlook, Markierung dort wird ersetzt |
| 5 | **Glossar online ungefiltert** | Zeilen roh mit Komma verbunden (`GeminiClient.cs:129-131`); `Bachata (not "Batchata")` geht wörtlich raus | Notizen entfernt, falsche Schreibweisen gefiltert, Tie-Breaker, Nachkorrektur (`SpeechService.swift:124-300`) | die falsche Schreibweise wird dem Modell als Vokabel gegeben |
| 6 | **Kein Schutz vor Glossar-Echo / Absagen** | nur „zu lang“-Prüfung und Vorspann-Entfernung (`TextCleanup.cs`) | Glossar-Echo-Gate + Retry ohne Glossar, angehängte Glossarliste weg, Chatbot-Absagen und Prompt-Leaks erkannt (`TextProcessingUtility.swift:309-560`) | Glossarwörter am Ende oder „Bitte schicken Sie das Audio“ werden eingefügt |
| 7 | **Gespeicherte Modell-IDs werden nie migriert** | Modell-ID steht fest in settings.json (`Settings.cs:103`), 404 → „Gemini-Fehler (HTTP 404)“ | `migrateLegacyTranscriptionRawValue` (`TranscriptionModels.swift:498-537`) | wenn Google 3.8 Flash abschaltet, fällt Dictate Prompt bei allen aus, auch nach einem Update |
| 8 | **Zwischenablage nur als Text gesichert** | nur `CF_UNICODETEXT` (`ClipboardPaster.cs:26`) | alle Typen (`ClipboardManager.swift:68-75`) | kopiertes Bild/Datei/Excel-Zellen nach einem Diktat weg |
| 9 | **Langsames Strg+C = keine Markierung** | nach 2 s „nichts markiert“ (`ClipboardPaster.cs:168`) | wartet auf späte Kopie | Citrix/großes Word: Markierung wird durch neu verfassten Text ersetzt |
| 10 | **Lange Diktate langsamer** | Stücke erst nach Stopp, ab 150 s | Stücke schon während des Sprechens | spürbar längere Wartezeit nach Stopp |
| 11 | **Kein Gesprächsgedächtnis im Prompt-Modus** | jede Anfrage ohne Kontext (`GeminiClient.cs:159-180`) | 5 Runden, 5 min (`PromptConversationHistory.swift`) | „jetzt noch kürzer“ nach einer Bearbeitung weiß nichts mehr |
| 12 | Kein Stall-Timeout bei Offline-Download und lokalem LLM | `OfflineEngine.cs:16`, `LocalLlmClient.cs:37` | – | Download hängt ewig bei 63 %, Anzeige bleibt auf „Wird verarbeitet“ |
| 13 | Log ohne Rotation und ohne Stacktrace | `Log.cs:33,45` | – | Fehlerberichte nicht auswertbar |
| 14 | Kleinkram | 0,2 s Nachlauf nach Stopp fehlt (letztes Wort), 5 statt 9 Schnellanweisungen, kein getippter Prompt, Kürzel nicht abschaltbar | | |

## B. Einstellungen (Martins Liste) — größtenteils auf `feat/settings-sidebar`

Auf dem Branch (nicht in `main`): Seitenleiste, Modell und System-Prompt pro Modus, Schalter „Automatisch
einfügen“ und „Zwischenablage wiederherstellen“, Info-Seite mit Version, Update-Prüfung, Tour, Zurücksetzen.
Mit Windows starten gibt es schon in `main`.

Danach noch offen: **Danksagung/Lizenzhinweis für die Parakeet-Gewichte (CC BY 4.0, Pflicht, weil Windows
Parakeet ausliefert)**, Diktat-Sprache und Genauigkeits-Einstellung, „Abgebrochene Aufnahme transkribieren“,
Benachrichtigungs-Einstellungen, Bestätigung bei langen Aufnahmen, Englische Oberfläche, Fehler melden aus der App.

## C. Funktionen, die es auf Windows gar nicht gibt (Umfang, nicht „abgespeckt“)

Chat-Fenster samt Google/Trello/IMAP/Ordner/Memory/Befehle (~25k Zeilen Swift, XL), Vorlesen (M), Screenshot (S–M),
Live-Meeting (L, auch auf dem Mac nur Mikrofon), MCP-Server für KI-Apps (L, ohne Chat sinnvoll), Smart Improvement
und Interaktionslog (L/M), Schreibstil pro App (M), weitere Anbieter (OpenAI, Grok, OpenRouter, eigener Server; M),
Telemetrie (S–M).

## D. Wo Windows besser ist (beibehalten)

Mikrofonwahl mit Pegeltest, Hinweis bei stummem/abgestecktem Mikro, Erkennung von Admin-Fenstern und gehaltenen
Tasten, Diktate nicht im Win+V-Verlauf, Schlüssel im Header statt URL, SHA-256-Prüfung der Modelle, breiterer
„Nicht in deinem Diktat“-Check, Schnellanweisung auf das gerade Diktierte, abgeschnittene Offline-Antwort wird nicht eingefügt.

## Empfohlene Reihenfolge

1. **Paket „Verlässlich“** (A1–A9): Temperatur 0, Retries mit Backoff + Netzfehler + `RetryInfo`, Retry pro Stück,
   Audio behalten + „Nochmal versuchen“, Einfügeziel merken, Glossar parsen, Echo-/Absage-Schutz,
   Modell-ID-Migration, Zwischenablage komplett sichern. Klein bis mittel, größter Nutzen.
2. `feat/settings-sidebar` mergen, CC-BY-Danksagung ergänzen.
3. Gesprächsgedächtnis im Prompt-Modus, Stücke schon während der Aufnahme.
4. Danach Umfang: MCP-Server, Vorlesen, Screenshot; Chat zuletzt.
