# Feedback Christoph, 2026-10-27 (7 WhatsApp-Sprachnachrichten, Windows 0.2.0)

Erster Start auf seinem Windows-PC (englisches Windows, AMD-Grafik, wenig Platz auf C:). Noch nie WhisperShortcut auf Windows benutzt, nur einen OpenAI-Key rüberkopiert.

## Bugs

1. **Warnung „Tastenkürzel belegt“ kommt beim Schließen der Einstellungen noch mal.** `TrayApp.cs:1020` registriert nach dem Schließen mit `notify: true` neu, also erscheint dieselbe Warnung wieder. Sie sollte höchstens einmal pro Kürzel kommen.
2. **„Nicht gespeicherte Änderungen“ kommt und geht, wenn die Maus über die Mikrofonliste fährt**, ohne dass er klickt. Der Speichern-Button ist dauerhaft aktiv. Vermutlich ändert sich `SelectedItem` der `FluentCombo` schon beim Hover, und der 400-ms-Timer (`SettingsForm.cs:444`) zeigt das an.
3. **Klick auf „Laden“ (Offline-Modell herunterladen) zählt als ungespeicherte Änderung.** Ein Download ist ein Vorgang, keine Einstellung.
4. **Ein neu angeschlossenes Mikrofon taucht erst nach Neustart der App auf.** Die Liste wird nur beim Öffnen gefüllt (`SettingsForm.cs:962`).

## Verhalten, das ihn irritiert hat

5. **Das Fenster „verschwindet“ nach Speichern.** Er hat das Kürzel geändert (wahrscheinlich mit Enter, `AcceptButton = save`) bzw. auf Speichern geklickt und dachte, die App sei abgestürzt. Speichern schließt das Fenster. Besser: offen lassen und kurz „Gespeichert“ zeigen, oder zumindest Enter im Kürzelfeld nicht speichern lassen.
6. **Das X beendet die App nicht, man merkt aber nicht, dass sie weiterläuft.** Erst die Meldung „läuft schon“ beim Neustart hat es erklärt.
7. **Tray-Symbol nicht zu finden.** Es liegt unter „Show hidden icons“ (^), zwischen mehreren anderen blauen Symbolen. Hinweis im Willkommen-Text/README fehlt („auf ^ klicken, ggf. anheften“).
8. **Testen beim Mikrofon spielt nichts ab**, er hatte eine Wiedergabe erwartet; der Pegel hat ihm aber gereicht.
9. **„Windows-Standard“ steht da, obwohl gar kein Mikrofon angeschlossen ist.**

## Wünsche

10. **Standardkürzel Strg+Umschalt+G ändern.** Bei ihm global von AMD Radeon Software belegt (Streaming), in Excel/Adobe ebenfalls belegt. Das ist unser Glossar-Kürzel (`Settings.cs:91`). Er hat auf Strg+Umschalt+B gewechselt.
11. **Signaltöne abschaltbar machen.** Start/Stopp-Piepsen stört ihn, eine Einstellung gibt es nicht (`TrayIcons.cs:28`).
12. **OpenAI-Key unterstützen** (oder klar sagen, dass Windows nur Gemini kann). Er dachte, wie am Mac gingen auch andere Anbieter.
13. **Mischbetrieb: Transkription offline, Textverarbeitung online.** Parakeet lokal hat ihn begeistert („hervorragend, beeindruckend schnell“), aber das ~6-GB-Sprachmodell passt nicht auf C:. Heute ist `OfflineMode` alles oder nichts.
14. **Speicherort der Modelle anzeigen und wählbar machen** (andere Partition). Heute fest `%LOCALAPPDATA%\WhisperShortcut\Offline`, nur per Umgebungsvariable `WSW_OFFLINE_ROOT` änderbar.

## Lob

- Offline-Transkription: schnell und genau mit seinem Mikrofon.
