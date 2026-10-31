# Paritäts-Audit A1–A9: Zwischenstand (31.10.2026, pausiert)

Pausiert auf Wunsch von Magnus bis Mo/Di (Claude-Kontingent). Keine Briefs geschrieben, kein Grok-Lauf.

## Wichtigster Befund

`docs/macos-paritaet.md` ist veraltet. Er prüft 47b5bec (27.10., 16:45). Um 19:31 kam der Merge f848018
„PR #1: reliability safeguards“ (Commits 28f9fae, aedb6e8, a966891, 37e3e8f, e4d3de3, 3206ba5), der die meisten
Lücken schon schließt. Vor neuen Briefs: pro Lücke den Rest-Unterschied zum Mac bestimmen und nicht von der Tabelle ausgehen.

## Schon in `main` (geprüft am Code)

- **A1** Temperatur 0.0 im Diktat: `GeminiClient.cs:178`.
- **A2 (teilweise)** 5 Versuche (6 bei 5xx), exponentieller Backoff ab 1,5 s (max. 15 s), Netzfehler und Timeouts
  werden wiederholt: `GeminiClient.cs:271-346`. Retry-Menü „Letzte Aufnahme erneut verarbeiten“ mit `FailedRun`
  (WAV, Dauer, Markierung): `TrayApp.cs:111, 422-482, 584-633`.
- **A3** RetryInfo bei 429 (+2 s, max. 60 s), Tageskontingent schlägt sofort fehl, getrennte Meldungen:
  `GeminiClient.cs:309-312, 332-337, 349-376, 391-395`.
- **A5** Glossar-Hinweis mit Tie-Breakern über `GlossaryPrompt.Hint`: `GeminiClient.cs:161-163`.
- **A6 (teilweise)** `TextCleanup.DiscardGlossaryEcho`, `IsPureGlossaryList`, `StripTrailingGlossaryEcho`,
  `LooksLikeRefusal`: `TextCleanup.cs:124-250`.
- **A7** `GeminiModels.Renamed` + `NormalizeTranscription/NormalizePrompt`: `GeminiModels.cs:29-60`.
- **A4/A8/A9** laut Commit-Titeln in 37e3e8f/e4d3de3/0fd33db angegangen („Paste only into the recording's window,
  full clipboard snapshot, late-copy watch“, „Retry pastes into the original window“), `TrayApp.cs:54` merkt das
  Fenster bei Aufnahmebeginn, `_paster.LateCopyArrived` existiert.

## Offen, noch zu prüfen (der abgebrochene Audit sollte das klären)

- A2: Bricht ein Stück nach ausgeschöpften Retries noch das ganze Diktat ab (`ChunkedTranscription.cs:89-98`
  bricht alle ab), und macht der Mac das anders? Bleibt das WAV bei jeder Fehlerart, auch bei Stück-Läufen?
- A7: Wird `Normalize` beim Laden der settings.json und vor jedem Aufruf angewandt?
- A5: Gibt es die Nachkorrektur aus `SpeechService.swift` (falsche Schreibweise nach der Antwort ersetzen)?
- A6: Ist „Echo-Gate + Retry ohne Glossar“ und die Prompt-Leak-Erkennung verdrahtet, auch für OpenAI/Offline?
- A4/A8/A9: am Code gegen `PasteTargetPolicy.swift` / `ClipboardManager.swift` gegenlesen, dann in der VM prüfen.

## Basis

`dotnet test tests/WhisperShortcut.Tests`, 31.10.: `Passed!  - Failed: 0, Passed: 392, Skipped: 5, Total: 397`.
Grok heute: 2/40 Läufe genutzt (nicht von dieser Session).
