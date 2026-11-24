# „Problem melden“ einfach machen – Kurzbeschreibung für die Mac-App

Stand 24.11.2026. Windows hat das so umgesetzt (Branch `feat/simple-problem-report`, `ProblemReportWindow.cs`,
`ResultReport.Draft`). Der Mac soll dasselbe bekommen.

## Warum

Der heutige Dialog (`DictationReportWindow.swift`) hat zu viele Teile: Ergebnis-Auswahl (ohne Diktat nur ein
Eintrag), Nachrichtenfeld, aufklappbare Details (bei allgemeinem Feedback nur „Offline-Modus: aus“), Schalter
„Konfiguration mitsenden“, „Bericht ansehen“, Teilen, Kopieren. Ohne Apple Mail öffnet sich nach „Senden“ unerwartet
ein Finder-Fenster. Man sieht nicht auf einen Blick, was gesendet wird.

## Das neue Fenster

```
┌ Problem melden ───────────────────────────────────────────┐
│ Schreib oben deine Nachricht. Darunter steht alles, was    │
│ mitgeschickt wird – du kannst es ändern oder löschen.      │
│ ┌───────────────────────────────────────────────────────┐ │
│ │ ▌ (Cursor hier)                                        │ │
│ │                                                       │ │
│ │ ──────── Wird mitgeschickt ────────                   │ │
│ │ ## Diktat · 14:32                                      │ │
│ │ Eingefügter Text: …                                   │ │
│ │ WhisperShortcut configuration … Glossar … Protokoll … │ │
│ └───────────────────────────────────────────────────────┘ │
│ Es geht erst etwas raus, wenn du die E-Mail absendest.     │
│ [Kopieren]                  [Abbrechen] [Per E-Mail senden] │
└───────────────────────────────────────────────────────────┘
```

- **Ein Textfeld.** Oben die Nachricht (vorausgefüllt bei Fehler-Popup, MCP-Bericht), darunter die Trennzeile
  „Wird mitgeschickt“, dann das neueste Ergebnis als Abschnitt („## Diktat · HH:mm“, „## Prompt-Antwort · …“,
  „## Vorlesen · …“, bei `/feedback` „## Kontext“ mit dem Chat-Ende), dann die Konfiguration (Einstellungen,
  Prompts, Glossar, Vorlagen, beim Mac ggf. Hang-Berichte).
- **Schlüssel werden geschwärzt, bevor der Text angezeigt wird**, und beim Senden noch einmal.
- **Gesendet wird genau, was im Feld steht.** Was der Nutzer löscht (ein Patientenname, das Glossar), geht nicht mit.
- **Entfällt:** Ergebnis-Auswahl (immer das neueste; „allgemein“ bei Fehler-Popup, Über, `/feedback`, MCP),
  Details-Aufklapper, Konfigurations-Schalter, „Bericht ansehen“, Teilen, Berichtsdatei, Datenschutz-Varianten.
- Ein offener, bearbeiteter Entwurf bleibt beim erneuten Öffnen erhalten (wie bisher).

## Versand: Text statt Datei

- **Apple Mail:** Compose-Dienst mit Empfänger, Betreff und dem ganzen Text als Body, kein Anhang. Fenster schließt.
- **Ohne Apple Mail:** ganzer Text in die Zwischenablage, dann `mailto:` mit Betreff und dem Body
  „[Hier ⌘V drücken: Der Bericht von WhisperShortcut ist in der Zwischenablage.]“. Das Fenster bleibt offen, zeigt
  „Die E-Mail wird geöffnet. Klick in den Text der E-Mail und drück ⌘V – dann steht der Bericht drin. Danach dort
  absenden.“ und „Fertig“ statt „Senden“. Kein Finder-Fenster.
- **Kein Mailprogramm:** derselbe Hinweis mit der Adresse zum Selbst-Schreiben.
- Der Bericht ist für die KI-Auswertung als Text in der Mail genauso gut lesbar wie als Datei; die `##`-Überschriften
  halten ihn zerlegbar.
