# Plan: Windows wie die Mac-App (Stand 03.11.2026)

Ziel (Magnus): Beide Apps sind in UI/UX und Funktionen grundsätzlich gleich, nicht zu 100 %.
Vorlage: [`macos-referenz.md`](macos-referenz.md) und der Mac-Quellcode. Was Windows bewusst anders macht,
steht dort in Abschnitt 8 und bleibt. Ein Schritt ist je ein Commit, den Magnus prüft, bevor der nächste kommt.

| # | Schritt | Mac-Vorlage | Status |
|---|---|---|---|
| 1 | Einstellungen: Seiten und Reihenfolge wie auf dem Mac (Allgemein mit allen Kürzeln und rechter Strg, Konten, Diktieren mit Glossar und Offline-Modell, Dictate Prompt mit Sprachmodell, Vorlagen, Bildschirmfoto, Claude & KI-Apps, Datenschutz & Berechtigungen, Über) | `Settings/Tabs/*` | erledigt |
| 2 | Einstellungen wirken sofort, kein „Speichern“ (0,5 s nach der letzten Eingabe); Kürzelprobleme unten im Fenster statt Meldung | `SettingsWindowController`, Tabs | erledigt |
| 3 | Abschnitte wie auf dem Mac: Symbol, Überschrift, ein Satz Untertitel; Selten Gebrauchtes in „Erweitert“ („Version X“ unten rechts: erledigt) | `SectionHeader`, `AdvancedSettingsGroup` | erledigt |
| 4 | Modellwahl als Kacheln (Empfohlen, Am schnellsten, Offline) mit „Weitere Modelle“, Offline-Modus als Schalter | `ModelPickerCuration`, `PrivacyPermissionsTab` | offen |
| 5 | Zwischenablage nach dem Einfügen: „Diktat stehen lassen“ (Standard) / „Vorherigen Inhalt zurücklegen“ / „Leeren“; Automatisch einfügen wie Mac-Standard | `GeneralSettingsTab` | offen, Standardwerte mit Magnus klären |
| 6 | Meldungen wie die Mac-Popups (unten Mitte, Erfolg 3 s, Fehler mit Knopf), Wortlaut der Mac-Meldungen | `PopupNotificationWindow`, `SpeechErrorFormatter` | offen |
| 7 | Aufnahmeanzeige wie die Pill („Hört zu“, „Prompt“, „Denkt nach“, ✕) | Pill-Views | offen |
| 8 | Einführung als Tour in Schritten statt Banner | `Onboarding/WelcomeSteps` | offen |
| 9 | Fehlende Abläufe: abgebrochene Aufnahme transkribieren, 9 Schnell-Prompts, „Erneut ohne Vorlage“, Sprache wählbar, Temperatur/Thinking unter Erweitert | `MenuBarController`, Tabs | offen |
| 10 | Umfang: Vorlesen, Meetings-Seite, Chat (zuletzt, nur nach Rücksprache) | `ReadAloudSettingsTab`, `ChatSettingsTab` | offen, Magnus entscheidet |
