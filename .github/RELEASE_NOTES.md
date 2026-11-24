Neu in Version 0.7.18:

- **Einstellungen gehen nicht mehr verloren:** Die Einstellungsdatei wird vollständig auf die Festplatte geschrieben, bevor sie die alte ersetzt, und eine Sicherungskopie springt ein, falls sie einmal beschädigt ist. Ein API-Schlüssel, der sich nicht entschlüsseln lässt, wird nicht mehr beim nächsten Speichern gelöscht.
- **Sichereres Update:** Die heruntergeladene Update-Datei bleibt gesperrt, bis die Installation fertig ist.
- **Meetings robuster:** Die Aufnahme schreibt das Audio auf einem eigenen Thread, Audio bleibt erhalten, wenn ein Zwischenstück nicht gespeichert werden kann, und das Glossar kann während eines Meetings gefahrlos ergänzt werden. Lokale Zusammenfassungen passen jetzt in das Kontextfenster des Modells.
- **Kleinere Korrekturen:** Auswahllisten in den Einstellungen speichern nicht mehr den Eintrag unter dem Mauszeiger, ein Bildschirmfoto während einer Prompt-Aufnahme schaltet den Schutz gegen verspätetes Kopieren nicht mehr ab, Beenden während des Einfügens stellt die Zwischenablage wieder her, und Vorlesen schickt höchstens drei Anfragen gleichzeitig.

Alle Änderungen: https://github.com/mgsgde/whisper-shortcut-windows/compare/v0.7.17...v0.7.18
