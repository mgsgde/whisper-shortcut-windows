# WhisperShortcut für Windows

Die Windows-Version von [WhisperShortcut](https://github.com/mgsgde/whisper-shortcut): Taste drücken,
sprechen, der Text steht dort, wo der Cursor blinkt. Läuft als Symbol im Infobereich, ohne Installation.

**Status: Testversion.** Sie läuft noch nicht bei vielen Leuten. Fehler bitte als
[Issue](https://github.com/mgsgde/whisper-shortcut-windows/issues) melden.

## Was es kann

- **Diktieren** (Strg+Umschalt+D): Sprache wird zu Text und direkt eingefügt.
- **Text bearbeiten oder schreiben lassen** (Strg+Umschalt+P): Text markieren und sagen, was damit passieren
  soll („freundlicher“, „ins Englische“), oder ohne Markierung einen Text nach einer Vorlage schreiben lassen.
- **Glossar** (Strg+Umschalt+K): ein markiertes Wort wird ab dann immer so geschrieben.
- **Online oder offline:** online mit Google Gemini oder OpenAI (eigener API-Schlüssel), offline komplett auf dem PC
  (Parakeet für die Spracherkennung, ein lokales Sprachmodell über llama.cpp). Offline verlässt nichts den PC.
  Gemischt geht auch, pro Schritt wählbar: z. B. Spracherkennung auf dem PC, Texte bearbeiten online.

## Download

Fertige Builds unter [Releases](https://github.com/mgsgde/whisper-shortcut-windows/releases/latest).
Für die meisten PCs `WhisperShortcut-Windows-x64.exe`, für Windows auf ARM `-arm64.exe`. Keine Installation, kein Entpacken:

1. Rechtsklick auf die exe → Eigenschaften → „Zulassen“ ankreuzen → OK.
2. In einen festen Ordner legen, z. B. `%LOCALAPPDATA%\Programs\WhisperShortcut\`, und doppelklicken.
   Meldet Windows „Der Computer wurde durch Windows geschützt“: „Weitere Informationen“ → „Trotzdem ausführen“
   (die App ist noch nicht signiert).
3. Im Windows-Konto des Nutzers starten, nicht als Administrator. Beim ersten Start öffnen sich die Einstellungen.

Updates kommen danach über die App selbst. Ausführliche Einrichtung: [`dist/ANLEITUNG.txt`](dist/ANLEITUNG.txt).

## Bauen

C# / .NET 10, WinForms. Gebaut wird auch auf dem Mac:

```bash
dotnet test tests/WhisperShortcut.Tests
dotnet publish src/WhisperShortcut -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

Release: `tools/release.sh <version> <notes.md>` testet, baut beide exe und ZIPs (x64, arm64) und signiert sie mit dem
privaten Schlüssel. Die exe ist der Download, die ZIP holt sich das Update in der App. Jede ZIP braucht ihre
`.sig`-Datei, sonst lehnen installierte Apps das Update ab.

Für Windows auf ARM `-r win-arm64`. `tools/vm.sh` steuert eine Parallels-Windows-VM vom Mac aus
(Tastenkürzel, Screenshots, Protokoll) für Tests ohne echten PC.

## Lizenz

[AGPL-3.0](LICENSE), wie die macOS-App. `third_party/vcruntime/` enthält die verteilbaren Microsoft-Visual-C++-Laufzeit-DLLs.
