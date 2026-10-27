# E2E cases: WhisperShortcut for Windows (VM, driven via tools/vm.sh)

Spec: `dist/ANLEITUNG.txt`. `V=tools/vm.sh`. Log = `~/Downloads/wsw-test/wsw.log` (`$V log`, or grep it).
Mode tags: `[wav:<file>]` = `$V start <file>`; `[no-wav]` = `$V start`; `[online]` = needs a valid Gemini key in the VM settings.
Order inside each area = value. Restart the app when the next case needs a different wav.

## Conventions (read once)

- **Before every case:** `: > ~/Downloads/wsw-test/wsw.log` is not safe while the app writes; instead note the line count (`wc -l`) and grep only new lines.
- **Clipboard sentinel:** `$V run powershell -c Set-Clipboard -Value sentinel42` before a paste case; after it `$V run powershell -c Get-Clipboard` must print `sentinel42` (restore promise).
- **Reading Notepad text:** focus Notepad, `$V combo ctrl a`, `$V combo ctrl c`, then `Get-Clipboard`. Do this only after the clipboard check, it overwrites the sentinel.
- **Selecting text:** `$V type ...` into Notepad, then `$V combo ctrl a`.
- **Balloons are not logged** (`Notify` only shows a toast). Verify them with `$V shot`; if the shot is black, record "balloon unverified" and judge by log alone.
- **Settings window:** there is no vm.sh verb to open it. Either start once with `--settings` (manual `prlctl exec ... WhisperShortcut.exe --settings --log Z:\Downloads\wsw-test\wsw.log` detached), or locate the tray icon in a screenshot and click it with `tools/click.ps1 X Y` (copy to the share, run via `$V run powershell -ExecutionPolicy Bypass -File Z:\Downloads\wsw-test\click.ps1 X Y`). Hotkeys are off while the settings window is in front (guide, step 4).
- **Back up settings before any S/O case:** `$V run copy /y %APPDATA%\WhisperShortcut\settings.json %APPDATA%\WhisperShortcut\settings.bak`; restore after. Never print the key.
- Wait for processing: poll the log for `Dictate: Pasted` / `Prompt: Pasted` (up to 30 s online, 60 s offline) instead of fixed sleeps.

---

## D: Dictate

**D1 `[wav:diktat.wav][online]` Basic dictation lands in Notepad**
Promise: "Strg+Umschalt+D drücken, sprechen, nochmal Strg+Umschalt+D. Der Text steht dort, wo der Cursor blinkt."
Steps: `$V notepad`; sentinel; `$V combo ctrl shift d`; sleep 2; `$V combo ctrl shift d`; poll log.
Expect: log `Hotkey Dictate in state Idle`, `Hotkey Dictate in state Recording`, `Dictate: recorded`, `Dictate: N chars raw`, `Paste: sent Ctrl+V`, `Dictate: Pasted, N chars`; then Notepad text (read via ctrl a/c) contains recognisable words from diktat.wav.
Pass: all log lines in order, N > 0, Notepad text non-empty and German prose.

**D2 `[wav:diktat.wav][online]` Clipboard restored after paste**
Promise: "Nach dem Einfügen stellt die App die Zwischenablage wieder her, aber nur reinen Text."
Steps: as D1 with sentinel; after `Pasted`, wait 2 s, `Get-Clipboard`.
Expect: log `Paste: restored previous clipboard`; clipboard = `sentinel42`.
Pass: both. Fail on `restore failed` or dictated text left on clipboard.

**D3 `[wav:diktat.wav][online]` Held modifiers (human hand)**
Promise: implicit in D1; a user keeps Ctrl+Shift down a moment on the second press.
Steps: `$V notepad`; `$V combo ctrl shift d`; sleep 2; `$V hold 1500 ctrl shift d`.
Expect: either `Waited N ms for modifiers to be released` then `Dictate: Pasted`, or `Dictate: ClipboardOnlyKeysHeld` with "Bitte selbst einfügen" balloon. Notepad must NOT contain stray characters (no bold/underline shortcut effects, no lone "v").
Pass: one of the two outcomes, Notepad clean.

**D4 `[wav:diktat.wav][online]` Overlay shown while recording**
Promise: "Während der Aufnahme zeigt unten in der Mitte ein kleines Fenster, dass die App zuhört (Pegel, Zeit)."
Steps: `$V combo ctrl shift d`; sleep 1; `$V shot /tmp/d4.png`; `$V combo ctrl shift d`; after paste `$V shot /tmp/d4b.png`.
Expect: log `Overlay: Recording at {X=...}, visible True`, later `Overlay: Processing`, `Overlay: Done`. Shot shows pill bottom centre.
Pass: overlay log lines with bounds inside the primary screen, bottom-centre; screenshot confirms if not black.

**D5 `[wav:diktat.wav][online]` Recent texts keep the last dictation**
Promise: "Rechtsklick auf das Symbol → „Letzten Text kopieren“."
Steps: after D1, right-click tray (click.ps1 can only left-click: if no right-click available, mark BLOCKED-HARNESS); choose "Letzten Text kopieren"; `Get-Clipboard`.
Expect: clipboard = dictated text.
Pass: matches the Notepad text from D1.

## P: Prompt (edit, compose, quick actions)

**P1 `[wav:anweisung-freundlicher.wav][online]` Edit a selection**
Promise: "Mit Markierung: Text markieren, Strg+Umschalt+P, Anweisung sprechen … Die Markierung wird ersetzt."
Steps: `$V notepad`; `$V type "das ist falsch mach das sofort"`; `$V combo ctrl a`; sentinel; `$V combo ctrl shift p`; sleep 2; `$V combo ctrl shift p`.
Expect: `Copy: selection 30 chars` (approx), `Prompt: selection N chars`, `Prompt: answer N chars`, `Prompt: Pasted`; Notepad text differs from the typed text, is friendlier, has no `*` or `#`; clipboard = sentinel.
Pass: all; original sentence fully replaced (not appended).

**P2 `[wav:angebot-ohne-markierung.wav][online]` Compose without selection uses the template**
Promise: "Ohne Markierung … Ein fertiger Text nach der Vorlage (Einstellungen → Dictate Prompt → Vorlage) wird eingefügt."
Steps: empty Notepad; `$V combo ctrl shift p`; sleep 2; `$V combo ctrl shift p`.
Expect: `Copy: clipboard unchanged after 2000 ms, no selection`, `Prompt: no selection (compose)`, `Prompt: answer`, `Prompt: Pasted`. Notepad has an offer text (Angebot, Heizkörper, Montage, Dienstag), no Markdown.
Pass: all; zero `*`/`#` characters.

**P3 `[wav:anweisung-freundlicher.wav][online]` Bare edit command without selection is refused**
Promise: "Mit Markierung … („mach das freundlicher“)" implies nothing to edit without one.
Steps: empty Notepad, no selection; P hotkey twice.
Expect: `Prompt: bare edit command without selection, refused`; no `Pasted`; Notepad stays empty.
Pass: refusal line, empty Notepad.

**P4 `[wav:diktat.wav][online]` Quick action on dictated text (no selection)**
Promise: "Strg+Umschalt+P, einen Satz diktieren, Strg+3 → kürzere Fassung."
Steps: empty Notepad; `$V combo ctrl shift p`; sleep 2; `$V combo ctrl 3`.
Expect: `Quick action 3`, `Prompt quick action: transcript N chars, selection 0 chars`, `answer M chars`, `Prompt: Pasted`. M shorter than the D1 text for the same wav (if action 3 = "Kürzer fassen"; check quick-action list first).
Pass: lines present; pasted text shorter than plain dictation.

**P5 `[wav:stille.wav][online]` Quick action on selection without speaking**
Promise: "Text markieren und gleich Strg+1 drücken geht auch, ganz ohne Sprechen."
Steps: Notepad, type a sentence, `ctrl a`; `$V combo ctrl shift p`; immediately `$V combo ctrl 1`.
Expect: `Quick action 1`, `transcript 0 chars, selection N chars` (N>0), `Prompt: Pasted`; no "Mikrofon" balloon.
Pass: selection replaced by the action result; no `microphone problem` line.

**P6 `[wav:diktat.wav][online]` Ctrl+1..5 only during recording**
Promise: "Esc und Strg+1 bis Strg+5 gelten nur während einer Aufnahme."
Steps: Idle: `$V combo ctrl 1`. Then Dictate recording (D hotkey), `$V combo ctrl 2`, D hotkey to stop.
Expect: no `Quick action` line in either case (Idle: hotkey not registered; Dictate mode: ignored). D recording ends with `Dictate: Pasted`.
Pass: no `Quick action` line, dictation unaffected.

**P7 `[wav:diktat.wav][online]` Press during processing cancels**
Promise: "Ein Druck während der Verarbeitung bricht ab."
Steps: `$V combo ctrl shift p`; sleep 2; `$V combo ctrl shift p`; within 300 ms `$V combo ctrl shift p` again.
Expect: `Hotkey Prompt in state Processing`, `Prompt: cancelled by user`; no `Pasted`; Notepad unchanged; tray back to Idle (next D1 works).
Pass: all. (If processing finished before the third press, retry; race noted, not a fail.)

**P8 `[wav:anweisung-freundlicher.wav][online]` Unsupported-words warning card**
Promise: "„Nicht in deinem Diktat“ … zeigt die Anzeige sie nach dem Einfügen 20 Sekunden lang an."
Steps: run P1; check log.
Expect: if `word(s) in the reply are not in the dictation, warning shown` appears, `Overlay: Warning` follows and the card is gone after ~20 s (shot at 5 s and 25 s). If absent, case is N/A (no new words).
Pass: when triggered, warning shown and auto-hidden; text still pasted.

## G: Glossary

**G1 `[no-wav]` Add selected word**
Promise: "Wort ins Glossar: ein Wort markieren …, Strg+Umschalt+G."
Steps: settings backup; Notepad; `$V type "goedde"`; `$V combo ctrl a`; `$V combo ctrl shift g`.
Expect: `Copy: selection 6 chars`, `Glossary add: Added (6 chars)`; `$V run type %APPDATA%\WhisperShortcut\settings.json | findstr /i goedde` finds it; overlay `Done`.
Pass: Added + persisted.

**G2 `[no-wav]` Duplicate**
Steps: repeat G1.
Expect: `Glossary add: Duplicate`; settings.json contains the term once.
Pass: both.

**G3 `[no-wav]` Nothing / too much selected**
Promise: implicit in "ein Wort markieren".
Steps: (a) empty Notepad, `ctrl shift g`; (b) type 3 lines? (Enter available) or a >max-length string, `ctrl a`, `ctrl shift g`.
Expect: (a) `Glossary add: Empty` + balloon "Nichts markiert"; (b) `Glossary add:` with a non-Added/Duplicate result + "Zu viel markiert".
Pass: nothing added to settings.json in both.

**G4 `[wav:glossar-goedde.wav][online]` Glossary changes recognition**
Promise: "Ab dann wird es so geschrieben."
Steps: after G1, `$V notepad`; Dictate hotkey twice; read Notepad.
Expect: Notepad contains "Goedde" (not "Göde"/"Gödde").
Pass: exact spelling present. Optional baseline: same run before G1 shows a different spelling.

**G5 `[no-wav]` Glossary hotkey while settings open appends to the field**
Promise: "Strg+Umschalt+G fügt ein markiertes Wort direkt hinzu." (+ code: `AppendGlossaryTerm`)
Steps: open settings, then focus Notepad (settings goes to background so hotkeys re-register), select a new word, `ctrl shift g`; back to settings, `uia list | grep -i <word>`.
Expect: `Glossary add: Added`; term visible in the glossary textbox; Save keeps it.
Pass: term in field and in settings.json after Save.

## S: Settings window

**S1 `[no-wav]` Key check, invalid key**
Promise: "Die App prüft ihn sofort: grüner Haken = Schlüssel funktioniert."
Steps: backup; open settings; focus the key field (Tab or click), `ctrl a`, `$V type "abc123invalid"`; `$V uia invoke Prüfen`; `$V uia list | grep -i schlüssel`.
Expect: log `Gemini key check: Invalid`; label "✗ Google kennt diesen Schlüssel nicht…". Then close WITHOUT saving (S4 path, answer Nein), restore backup.
Pass: red cross text; real key unchanged in settings.json afterwards.

**S2 `[online]` Key check, valid key**
Steps: open settings with the stored key; `$V uia invoke Prüfen`.
Expect: `Gemini key check: Ok`; label "✓ Schlüssel funktioniert".
Pass: both.

**S3 `[wav:diktat.wav][online]` Change dictate hotkey**
Promise: "Tastenkürzel sind immer mit zwei Zusatztasten."
Steps: backup; open settings, `uia invoke "Diktieren"` (sidebar); focus the Tastenkürzel box; `$V combo ctrl shift y` (QWERTZ: sends scancode for physical Y key → check box text); `$V uia invoke Speichern`. Then Notepad: old `ctrl shift d` (expect nothing), new combo twice (expect paste).
Expect: `Settings saved`; old combo: no `Hotkey Dictate` line; new combo: `Dictate: Pasted`. Restore backup + restart.
Pass: old dead, new works, no "Tastenkürzel belegt" balloon.

**S4 `[no-wav]` Unsaved-changes prompt (X / Abbrechen / Esc)**
Promise: "wer mit ungespeicherten Änderungen schließt, wird gefragt."
Steps: open settings; change something harmless (`uia select "Offline auf diesem PC"` or type into Glossar); press `$V combo esc` (focus not in a hotkey box); `$V uia list` / shot for dialog "Änderungen speichern?"; `$V uia invoke Abbrechen` → window stays; Esc again → `uia invoke Nein` → window closes; reopen → change gone.
Expect: dialog appears each time; Cancel keeps window + edits; No discards; no `Settings saved` line.
Pass: all three outcomes.

**S5 `[no-wav]` Esc without changes closes silently**
Steps: open settings, no edits, `$V combo esc`.
Expect: window gone (`uia list` → "no settings window"), no dialog.
Pass: closed, no prompt.

**S6 `[no-wav]` Invalid hotkey rejected on save**
Promise: "immer mit zwei Zusatztasten … Kombinationen mit Alt besser meiden."
Steps: backup; `uia invoke "Dictate Prompt"` page; in its Tastenkürzel box press `$V combo ctrl p` (one modifier) → Speichern; then set it to the same as Diktieren (`ctrl shift d`) → Speichern.
Expect: warning dialogs "Bitte für jede Funktion ein Tastenkürzel mit zwei Zusatztasten…" and "Die Tastenkürzel müssen verschieden sein."; no `Settings saved`.
Pass: both refusals; settings.json unchanged.

**S7 `[no-wav]` Hotkeys off while settings in front, back after**
Promise: "Solange das Einstellungsfenster im Vordergrund ist, sind die Tastenkürzel aus."
Steps: settings in front, `$V combo ctrl shift d` → expect no `Hotkey Dictate`. `$V focus Notepad`, `ctrl shift d` → expect `Hotkey Dictate in state Idle`; Esc.
Pass: exactly that.

**S8 `[no-wav]` Quick-action reset**
Promise: "„Vorschläge wiederherstellen“ setzt die Vorbelegung zurück."
Steps: Dictate Prompt page, Schnellanweisungen card: edit one field, `uia invoke "Vorschläge wiederherstellen"`, Speichern.
Expect: `Settings saved (... quick actions 5 ...)`; edited text gone in `uia list`.
Pass: both.

## O: Offline

**O1 `[wav:diktat.wav]` Offline dictation (Parakeet)**
Promise: "Diktieren geht offline genauso schnell wie online."
Steps: backup; switch to offline (`uia select "Offline auf diesem PC"`, Speichern); confirm speech model loaded (`uia list` shows "✓ Geladen" for Spracherkennung; if not: BLOCKED, do not download 670 MB without a go); Notepad; D hotkey twice.
Expect: `Settings saved (... offline True`, `Parakeet loaded in N ms`, `Dictate: Pasted`; no `Gemini` line.
Pass: pasted, no Gemini request. Record time hotkey→Pasted.

**O2 `[wav:anweisung-freundlicher.wav]` Offline prompt fails cleanly on this CPU**
Promise: offline "Text bearbeiten" needs the language model; this VM cannot run llama-server.
Steps: offline mode on; select text in Notepad; `ctrl shift p`.
Expect: `Hotkey Prompt in offline mode, but this CPU cannot run the language model` (or `models missing` if the LLM is not downloaded); balloon "Offline-Sprachmodell: Der Prozessor dieses PCs kann…"; no recording overlay, no crash, Notepad selection intact.
Pass: German message, app still responsive (D hotkey works after).

**O3 `[wav:glossar-goedde.wav]` Glossary offline**
Promise: "Das Glossar gilt auch offline … einige Sekunden, bis sie die neuen Wörter kennt."
Steps: offline on; G1 with "goedde"; wait for `Parakeet reloaded for glossary`; D hotkey twice.
Expect: reload line with term count +1; Notepad contains "Goedde".
Pass: both. (Single word, so it should work per the guide.)

**O4 `[no-wav]` Offline needs no key**
Promise: "Dann braucht es keinen API-Schlüssel."
Steps: backup; clear key, offline on, Speichern; D hotkey, Esc.
Expect: no `no API key (Schlüssel fehlt)` line; `Hotkey Dictate in state Idle` then `Esc in state Recording`.
Pass: both. Restore backup.

## R: Robustness basics

**R1 `[no-wav]` Esc cancels recording**
Promise: "Esc bricht ab."
Steps: Notepad; sentinel; `ctrl shift d`; sleep 2; `$V combo esc`.
Expect: `Esc in state Recording`, `Overlay: Hidden`; no `recorded`, no `Paste`; clipboard = sentinel; no Ctrl+C side effect.
Pass: all.

**R2 `[no-wav]` Esc outside recording does not get swallowed**
Steps: Notepad, type text, `$V combo esc` while Idle.
Expect: no `Esc in state` line (hotkey not registered when Idle).
Pass: no line.

**R3 `[no-wav]` Double press = "Aufnahme zu kurz"**
Promise: (code) double press reports "Aufnahme zu kurz" instead of a mic error. Wav mode cannot hit this (file length counts).
Steps: `ctrl shift d`; immediately `ctrl shift d`.
Expect: `Dictate: recorded 0.x s` (< 0.5), balloon "Aufnahme zu kurz"; no `Gemini` call, no `microphone problem`.
Pass: all.

**R4 `[wav:stille.wav][online]` Silence**
Steps: Notepad; D hotkey twice.
Expect: no `Pasted`; either `microphone problem AudioSilentException` with "Mikrofon liefert Stille" balloon, or an empty-result path. Notepad empty.
Pass: nothing pasted, no `unexpected error`.

**R5 `[no-wav]` Missing key**
Promise: step 4 (key needed online).
Steps: backup; online mode, empty key, Speichern; `ctrl shift d`.
Expect: `no API key (Schlüssel fehlt)`, balloon "Schlüssel fehlt"; no recording started. Restore.
Pass: both.

**R6 `[wav:diktat.wav]` Invalid key mid-use (stand-in for no network)**
Steps: backup; save key `abc123invalid` (S1 steps, then Speichern); D hotkey twice.
Expect: `Dictate: Gemini InvalidKey`, balloon "Gemini-Fehler"; clicking it opens settings; app returns to Idle (second D press starts a new recording). Restore.
Pass: clean error, recoverable.

**R7 `[no-wav]` Second instance**
Steps: while running, start the exe again via `$V start`.
Expect: `Second instance started, exiting`; one process in `tasklist`; message box "läuft bereits".
Pass: both.

---

## Saboteur ideas

1. Triple-press D within 1 s: Idle → Recording → Processing → cancel. Check final state Idle, no half paste.
2. D recording, then press P hotkey: must stop the D recording (mode stays Dictate) or be ignored, never start a second recording.
3. P recording, press D hotkey, then Ctrl+3: which mode wins? No paste of a quick action into a dictation.
4. Esc during Processing: guide says Esc only during recording; must log `Esc in state Processing` and still paste.
5. Switch window mid-processing (`$V focus` another app, e.g. a second Notepad): text goes to the new foreground window; "Letzte Texte" has it.
6. `hold 5000 ctrl shift d` on the stop press: expect `ClipboardOnlyKeysHeld` after 3 s wait, no stray keystrokes.
7. Ctrl+1 pressed twice fast during P recording: one `Quick action` line, one paste.
8. Ctrl+Shift+G while recording: must be refused (error sound), selection copy of the running P not disturbed.
9. Close settings with unsaved changes via Esc while focus is in a hotkey box: Esc must be recorded as key, window stays.
10. Type an invalid key, hit Prüfen, then immediately Speichern before the check returns: saved key = what was typed, no crash.
11. Change hotkey to one already used by Windows/another app (e.g. ctrl shift esc): expect "Tastenkürzel belegt" balloon, old ones still work.
12. Toggle offline via tray during an online recording: recording completes with the mode it started in.
13. Stop the app (`$V stop`) mid-processing, restart: settings intact, no corrupt settings.json (`settings.json unreadable` must not appear).
14. Select text in Notepad, press P, deselect (click elsewhere) before stopping: selection was copied at start, the replacement goes into the wrong place: document actual behaviour.
15. Quick action with nothing selected and stille.wav: should refuse with a message, not paste an empty or invented text.

## Needs a human / real PC

- Real microphone speech, level bars, "Testen" mic test, mic device selection, 30 min / 5 min limits with remaining-time display.
- x64 hardware: offline LLM (llama-server) load time, answers, 15-min memory release, Vulkan/GPU path, the 5.7 GB model.
- Word / Outlook / Excel targets (Word shortcut clash, Excel cell-as-selection), elevated (admin) target app paste.
- Clipboard history (Win+V) exclusion, image/file clipboard loss.
- SmartScreen / Smart App Control / Defender on first start, autostart after reboot, moved exe, other Windows account, DPAPI after password reset.
- Tray icon colours (red/orange) and pinning; balloon clicks when screenshots are black; right-click tray menu (click.ps1 is left-click only).
- Offline model download (670 MB / 2.5 GB), continue-after-close, "Entfernen".
