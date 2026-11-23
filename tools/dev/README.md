# WhisperShortcut Dev auf dem Windows-PC

Wie beim Mac („WhisperShortcut Dev“ neben der normalen App): eine zweite Instanz, die immer den neuesten
**grünen** Stand von `main` hat. Die Aufgabenplanung aktualisiert sie alle 10 Minuten. Die normale App bleibt
unberührt und bekommt ihre Updates wie bisher über den Update-Kanal.

| | Normale App | Dev |
|---|---|---|
| Programm | `%LOCALAPPDATA%\Programs\WhisperShortcut\` | `%LOCALAPPDATA%\Programs\WhisperShortcut Dev\` (mit `dev-instance.txt`) |
| Einstellungen, Verlauf, Log | `%APPDATA%\WhisperShortcut`, `%LOCALAPPDATA%\WhisperShortcut` | `… WhisperShortcut Dev` (eigene, anfangs leer) |
| Offline-Modelle | gemeinsam | gemeinsam; Löschen ist in Dev gesperrt |
| Updates | Update-Kanal (Stabil/Neueste) | nur dieses Skript |
| Kennzeichen | – | „WhisperShortcut Dev“, orangefarbenes D am Tray-Symbol |

Es läuft immer nur eine der beiden mit Tastenkürzeln: Tray-Menü **„Zur Dev-Version wechseln“** bzw. **„Zur
normalen Version wechseln“** startet die andere, die die erste beendet. Während eine Besprechung aufnimmt, wird
weder gewechselt noch ausgetauscht.

## Einrichten (einmal, selbst in PowerShell, nicht über Claude)

```powershell
cd C:\whisper-shortcut-windows
git pull
powershell -ExecutionPolicy Bypass -File tools\dev\register-task.ps1
```

Voraussetzung: `gh` ist angemeldet (`gh auth status`). Das Skript legt die Aufgabe „WhisperShortcut Dev Update“
an und zeigt den Befehl für die erste Installation sofort. Danach optional die Einstellungen der normalen App
übernehmen (Dev beendet; API-Schlüssel kommen mit, Offline-Modelle sind ohnehin geteilt):

```powershell
powershell -ExecutionPolicy Bypass -File tools\dev\dev-daily.ps1 copy-settings
```

## Was die Aufgabe tut

1. Neuester erfolgreicher CI-Lauf auf `main` mit dem Artefakt `WhisperShortcut-dev-x64` (die Exe, die den
   Diktat-Durchstich bestanden hat). Nie ein älterer Stand als der installierte.
2. Läuft Dev gerade: warten, solange eine Besprechung aufnimmt oder die letzte Eingabe keine 90 s her ist.
3. Dev beenden, Exe tauschen, wieder starten. Läuft sie nach 20 s nicht mehr, kommt die vorige Exe zurück und der
   Commit landet in `bad-sha.txt` (wird nicht wieder versucht).

Log und Zustand: `%LOCALAPPDATA%\WhisperShortcut Dev\dev-daily\` (`dev-daily.log`, `installed-sha`, `bad-sha.txt`).

## Befehle

| Befehl | Wirkung |
|---|---|
| `dev-daily.ps1 status` | installierter Commit, neuester grüner Stand |
| `dev-daily.ps1 update -DryRun` | zeigt, was passieren würde |
| `dev-daily.ps1 update -Force` | sofort, auch wenn gerade getippt wird |
| `dev-daily.ps1 use-dev` / `use-release` | wechseln wie im Tray-Menü |

## Anhalten

- Pausieren: `Disable-ScheduledTask -TaskName "WhisperShortcut Dev Update"` (weiter: `Enable-ScheduledTask …`)
- Ganz entfernen: `register-task.ps1 -Remove`; Dev selbst über ihr Tray-Menü deinstallieren (löscht nur Dev-Daten,
  nie die geteilten Offline-Modelle).
- Einen kaputten Commit überspringen: seine SHA in `bad-sha.txt` eintragen.
