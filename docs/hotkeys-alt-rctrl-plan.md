# Plan: Kürzel wie auf dem Mac (Alt+Ziffer, rechte Strg statt Fn)

Stand 03.11.2026. Magnus: „Ich möchte, dass das konsistent zur macOS App ist … Alle Features sollen so
implementiert werden wie in der macOS Application.“ und „macOS ist Command 3 Screenshot, also möchte ich,
dass Alt 3 Screenshot ist.“

Quelle: macOS-App `mgsgde/whisper-shortcut`, `WhisperShortcut/ShortcutConfig.swift` (`ShortcutConfig.default`,
`legacyCommandDigit`) und `WhisperShortcut/FnDictationToggle.swift`, gelesen am 03.11.2026.

## Belegung: Mac-Ziffern, Windows-Modifier Alt

Der Mac belegt dieselbe Ziffer für dieselbe Funktion; nur der Modifier wechselt (alte Installationen `⌘`,
neue `⌃⌥`). Unter Windows ist `Strg+Alt` AltGr (deutsche Tastatur), also nimmt Windows **Alt** als Modifier.

| Mac (`⌘` / `⌃⌥`) | Funktion | Mac ab Werk | Windows neu | Windows heute |
|---|---|---|---|---|
| 1 | Diktieren | an | **Alt+1** | `Strg+Umschalt+D` |
| 2 | Dictate Prompt („Text bearbeiten“) | an | **Alt+2** | `Strg+Umschalt+P` |
| 3 | Screenshot | an | **Alt+3** | nicht belegt |
| 4 | Vorlesen | an | Alt+4 frei halten (Vorlesen gibt es unter Windows noch nicht) | – |
| 5 | Live diktieren | **aus** (nur vorgeschlagen) | Alt+5, ab Werk aus | nicht belegt |
| 7 | Markierung ins Glossar | **aus** (nur vorgeschlagen) | Alt+7, ab Werk aus | `Strg+Umschalt+K` |
| 0 | Einstellungen öffnen | an | **Alt+0** (neu, gibt es unter Windows noch nicht) | – |
| `⌥Leertaste` | Chat | an | entfällt (kein Chat unter Windows; Alt+Leertaste ist das Fenstermenü) | – |
| Fn (🌐) | Diktieren an/aus | an, wenn 🌐 frei | **rechte Strg** | – |

Vorlagen, Live Meeting: auf dem Mac ohne Ziffern-Kürzel, bleiben unter Windows wie sie sind
(eigene Kürzel pro Vorlage, Meeting ohne Standard-Kürzel).

## Rechte Strg = Mac-Fn, mit genau dem Mac-Verhalten

Fn kommt unter Windows nie beim System an (Tastatur-Firmware). Die rechte Strg ist die nächstliegende Taste:
allein gedrückt fast nie gebraucht, mit dem Daumen erreichbar. Übernommen wird `FnDictationToggle` 1:1:

- Reines Umschalten, **kein Halten-zum-Sprechen** (Mac bewusst nicht: Halten und langsames Drücken sind nicht unterscheidbar).
- **1. Druck:** Aufnahme startet schon beim Runterdrücken (Overlay sofort), bestätigt beim Loslassen. Kommt
  dazwischen eine normale Taste (rechte Strg als Modifier, z. B. `Strg+C`), wird die Aufnahme verworfen.
- **2. Druck:** Stopp und Transkription beim Loslassen. Normale Taste dazwischen: Aufnahme läuft weiter.
- **Druck während der Verarbeitung:** bricht ab, beim Loslassen. Aber nicht innerhalb von **2 s** nach dem Stopp
  (`cancelGuardWindow`): dieser Druck ist fast immer schon das nächste Diktat.
- Aufnahme anderweitig gestoppt (Tray, Alt+1): der nächste Druck startet neu.
- Ab Werk **an**, in den Einstellungen abschaltbar (Mac: „fn-Taste diktiert“, `fnKeyDictation`).

## Technische Hürden

1. **Einzelne Modifier-Taste:** geht nicht mit `RegisterHotKey`. Braucht einen Low-Level-Hook
   (`SetWindowsHookEx(WH_KEYBOARD_LL)`), der nur bei rechter Strg (`VK_RCONTROL`) auswertet.
   Eigene eingeschobene Tasten (`LLKHF_INJECTED`, unser `Strg+V`/`Strg+C`) ignorieren.
2. **Alt-Kürzel:** `HotkeyService.cs` dokumentiert, warum sie bisher verboten sind: `RegisterHotKey` verschluckt die
   Ziffer, die App im Vordergrund sieht Alt allein und öffnet ihr Menü, unser `Strg+C` landet dort.
   Maskieren mit vkE8 klappte in der VM am 17.10.2026 nicht. Muss auf dem echten PC gelöst werden (Schritt 0).
3. **Office:** Alt+1…9 löst in Word/Excel/Outlook die Schnellzugriffsleiste aus. Ein globales Alt+1 nimmt das dort weg.
   So gewollt (Mac-Konsistenz), wird in den Release-Notes erwähnt.
4. **Admin-Fenster:** Hook und Kürzel erreichen Fenster mit Admin-Rechten nicht, solange die App nicht als Admin läuft.

## Umsetzung

### Schritt 0: Spike auf dem Windows-PC (entscheidet über Alt)
Prototyp: Hook fängt Alt+Ziffer selbst ab, unterdrückt die Ziffer und verhindert das Menü. Varianten:
a) vkE8 vor dem Alt-Loslassen, b) Strg-Tap einschieben, c) Aktion erst nach dem Loslassen von Alt, `Strg+C` nie bei gedrücktem Alt.
Prüfen in Notepad (WinUI), Chrome, Edge, Word, Explorer, Windows Terminal, Praxissoftware, RDP:
kein Menü, Markierung korrekt gelesen, kein Piepen. Scheitert alles: Rückfrage an Magnus.

### Schritt 1: `KeyboardHook` (App)
`src/WhisperShortcut/KeyboardHook.cs`: `WH_KEYBOARD_LL` auf dem UI-Thread, Callback nur auswerten und per
`BeginInvoke` weitergeben (Windows entfernt langsame Hooks still, `LowLevelHooksTimeout`). Neu setzen nach
Sleep/Resume und Sitzungswechsel. Trägt rechte Strg und, nach Schritt 0, die Alt-Kürzel.

### Schritt 2: `RightCtrlDictationToggle` (Core, testbar)
Zustände wie der Mac: `Idle`, `Starting`, `Recording`, `Stopping`, `Cancelling`; Delegate mit Start/Stop/Discard/
IsRecording/IsProcessing/CancelProcessing; 2-s-Abbruchschutz. Tests `RightCtrlDictationToggleTests` nach den
Mac-Fällen: Tipp startet, Tipp stoppt, Taste beim Start verwirft, Taste beim Stopp behält, Abbruch, Abbruchschutz,
anderweitig gestoppt, Tastenwiederholung, eingeschobene Ereignisse.

### Schritt 3: Einstellungen und Migration (wie Mac)
- Neue Standards in `Settings.cs`: Alt+1, Alt+2, Alt+3, Alt+0 an; Live (Alt+5) und Glossar (Alt+7) als Vorschlag, aus;
  `RightCtrlDictation = true`. „Kürzel abschaltbar“ (Parität-Lücke, `macos-paritaet.md` Zeile 85) gleich mitmachen.
- **Migration wie `loadConfiguration` auf dem Mac:** Bestehende Installationen behalten ihre Kürzel
  (Mac pinnt die alten `⌘`-Ziffern). Für Windows also: wer schon lief, behält `Strg+Umschalt+D/P/K`;
  neue Installationen bekommen Alt+Ziffer. Magnus' eigene Installation stelle ich danach von Hand um.
- Alt+Ziffer und `"RightCtrl"` in `Hotkey.TryParse`, `Mcp/HotkeyRules.cs`, `McpSettingsCatalog` (Mac-Katalog kennt
  `rightControl` schon) und im Kürzel-Recorder erlauben.
- Einstellungsfenster: Schalter „Rechte Strg-Taste diktiert“ unter den Kürzeln, wie Mac „fn-Taste“.
- Neue Funktion „Einstellungen öffnen“ (Alt+0).

### Schritt 4: Texte
Overlay-Stopp-Hinweis, Tray-Infozeilen, Einführung, `ANLEITUNG.txt`, `docs/macos-paritaet.md` (Abweichungen:
Alt statt `⌃⌥`, rechte Strg statt Fn, Alt+4/Chat fehlen).

### Schritt 5: Prüfen und Release
`dotnet test` grün; auf dem Windows-PC alle Kürzel und rechte Strg in Notepad, Chrome, Word, inkl. Markierung lesen,
kein Menü, kein Piepen, nach Sleep weiter aktiv. PR → main → Minor-Version → Release laut `AGENTS.md`.

## Offen
- Schritt 0 entscheidet, ob Alt+Ziffer sauber geht. Rechte Strg ist unabhängig davon machbar.
