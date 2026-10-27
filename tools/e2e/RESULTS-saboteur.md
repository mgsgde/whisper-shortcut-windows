# Saboteur results, 2026-10-27, Windows 11 ARM VM

Build under test: fixes branch `fix/e2e-findings` (e2a64e7, PR #2), `/tmp/wsw-arm64-fixes/WhisperShortcut.exe`.
Log: `~/Downloads/wsw-test/wsw.log` (lines 905 onward). Screenshots: `~/Downloads/wsw-test/e2e/sab-*.png`.
Settings backup: `~/Downloads/wsw-test/settings.backup3.json` (restored at the end, byte-identical by `cmp`). Main build `/tmp/wsw-arm64` redeployed and started normally at 19:21:59.
Glossary during the run: `[goedde, ramipril]`, online, keys as stored. Clipboard sentinel: `sentinel123`.

| # | Attack | Result | Severity | Note |
|---|---|---|---|---|
| 1 | Hold the stop hotkey 1200 ms (D3 regression) | WORKS | – | `Hotkey Dictate in state Processing ignored: key still held`, then `Dictate: Pasted, 111 chars`, clipboard back to sentinel (sab-01-hold.png) |
| 2 | Hold the stop hotkey 5 s (saboteur #6) | WORKS | – | Repeat ignored, after 3 s `Modifiers still held … not sending keystrokes`, `ClipboardOnlyKeysHeld`, Notify "Bitte selbst einfügen", text on the clipboard |
| 3 | Hold the start hotkey 1.5 s | WORKS | – | `in state Recording ignored: key still held`, recording keeps running, later stop pastes normally |
| 4 | Triple press D (saboteur #1) | WORKS | – | Idle > Recording > Processing > `Dictate: cancelled by user`, nothing pasted. Presses < 300 ms apart not reachable with prlctl (~1 s per combo) |
| 5 | D recording, then G hotkey, then P hotkey, then Esc x3 | WORKS | – | G during recording silently refused (no log line, code plays the error sound), P stops the dictation in Dictate mode, Esc after stop goes to Notepad, text pasted once |
| 6 | Silence (stille.wav) + glossary, Dictate (R4 regression) | WORKS | – | `No speech (peak -48 dBFS, loud frames 0/71), Gemini skipped`, Notify "Nichts erkannt", Notepad empty (sab-05d shows the toast) |
| 7 | Silence + glossary, Prompt without selection | WORKS | – | Gemini skipped, "Nichts erkannt", nothing pasted |
| 8 | Silence, no selection, Ctrl+1 (saboteur #15) | WORKS | – | `transcript 0 chars, selection 0 chars`, Notify "Nichts zum Anwenden", clipboard = sentinel |
| 9 | diktat.wav with glossary (echo check) | WORKS | – | 14 runs, always the same 111 chars "Hallo Herr Meyer, … einbauen.", no glossary word appended |
| 10 | glossar-goedde.wav, glossary spelling | WORKS | – | `Glossary spelling: 2 word(s) set to the glossary spelling`, Notepad: "Herr Goedde kommt morgen zur Kontrolle. Wir erhöhen Ramipril …" |
| 11 | Switch to another window mid-recording | WORKS | – | `Paste: foreground changed, brought the target window back`, text in the original Notepad, clipboard = sentinel (sab-07-switch-midrec.png) |
| 12 | Switch window + type during processing | WORKS (partly) | – | Gemini answered in 1.1 s, before `vm.sh focus` (~2 s) could switch; paste went to Notepad, "echo abc" to the console afterwards (sab-08b) |
| 13 | Close the target window during recording | WORKS | – | `foreground changed since the recording began (target window is gone), leaving text on the clipboard`, `ClipboardOnlyTargetChanged`, Notify "Bitte selbst einfügen", dictation on the clipboard |
| 14 | Settings window open but behind Notepad, dictate in Notepad | **APP BUG** | med | Hotkeys stay off although the settings window is not in front; nothing happens, no feedback (see B1) |
| 15 | Settings: activate, then switch to Notepad, dictate (reconciler) | WORKS | – | Deactivate re-registers, `Dictate: Pasted` |
| 16 | Settings: change Dictate to Ctrl+Shift+K, Speichern | WORKS | – | `Settings saved`, `Hotkey registered: Ctrl+Shift+K`, old Ctrl+Shift+D dead, K pastes |
| 17 | Settings: Dictate = Prompt (Ctrl+Shift+P), Speichern; then Esc > Yes (default button) | WORKS | – | Both times "Die Tastenkürzel müssen verschieden sein." (sab-23, sab-25); Esc > No discards, settings.json keeps K |
| 18 | Dictate hotkey while the settings window shows its own warning dialog | **APP BUG** | med | Hotkeys come back on while the app's own MessageBox is in front; the dictation is "pasted" into that MessageBox and is lost (see B2) |
| 19 | Invalid API key, Prüfen then Speichern right away (saboteur #10) | WORKS | – | `key check: Invalid` x2, `Settings saved`, no crash |
| 20 | Dictate and Prompt with the invalid key | WORKS | – | `Gemini InvalidKey`, Notify "Gemini-Fehler: API-Schlüssel ungültig. Bitte in den Einstellungen prüfen." (sab-17), nothing pasted, clipboard = sentinel |
| 21 | Start the app twice | WORKS | – | `Second instance started, exiting`, box "WhisperShortcut läuft bereits …" (sab-18); the second process lives until OK is pressed (known) |
| 22 | Esc spam during Prompt processing | **APP BUG** | low | Esc does nothing during processing, text pasted; the settings page promises "Esc: Aufnahme verwerfen oder Verarbeitung abbrechen" (see B3) |
| 23 | Prompt on a 32 000-char selection, G hotkey mid-recording | **APP BUG** | med | 43 s, `Prompt: answer 23635 chars` pasted over 31 999 chars; 189 of 500 sentences silently gone, no warning (see B4). G during recording: silently refused |
| 24 | Type over the selection during Prompt recording (meant as deselect; vm.sh arrows arrive as numpad "2"/"7") | WORKS | – | Reply inserted at the cursor, no duplicate of the original |
| 25 | Kill the app 0.5 s after the stop press, restart | WORKS | low note | Settings intact (`parsed ok`, glossary, K). Kill landed in the 2 s window after paste, so the clipboard kept the dictation instead of the sentinel: expected for a hard kill |
| 26 | Lost Prompt stop press (Ctrl+Shift+P reaches Notepad: "ppp…" typed, once the Print dialog) | VM ARTEFACT (probable) | – | 3 of ~23 Prompt stop presses, each right after an app restart or heavy prlctl/PowerShell activity; 8/8 and 3/3 clean in later loops, 10/10 Dictate clean, harness control 25/25 clean. See note N1 |

Counts: 26 attacks. WORKS 21, APP BUG 4 (B1 med, B2 med, B4 med, B3 low), VM ARTEFACT 1. No crash in any attack.

## APP BUGs

### B1 (med) Settings window behind another window: hotkeys dead

Steps: start with `WSW_ARGS=--settings tools/vm.sh start diktat.wav` (the window opens without getting the foreground, it ends up behind Notepad, sab-10-settings.png). `vm.sh focus Notepad`, Ctrl+Shift+D twice.
Expected: the window is not in front, so the hotkeys work (guide: "Solange das Einstellungsfenster im Vordergrund ist, sind die Tastenkürzel aus").
Actual: no log line at all for either press; a probe in the VM can register Ctrl+Shift+D/P/G itself (`registerOK=True`), i.e. the app holds none. Only after the settings window is brought to the front and left again (`vm.sh focus WhisperShortcut`, then Notepad) are the hotkeys back.
Evidence:
```
18:50:54.184 INFO Settings page Allgemein
18:50:54.379 INFO Hotkeys unregistered (3)
18:50:54.380 INFO Hotkeys off: settings window in front
(18:51:0x Ctrl+Shift+D x2 in Notepad: nothing logged)
hkprobe: D: registerOK=True  P: registerOK=True  G: registerOK=True
18:52:39.877 INFO Hotkey registered: Ctrl+Shift+D      <- only after AppActivate settings, then Notepad
```
Cause (code reading, `TrayApp.cs` `SyncGlobalHotkeys`): `settingsInFront` is `Form.ActiveForm == f`, set from `Activated`. A form activated inside its own thread without getting the foreground (focus-steal prevention) fires `Activated`, and no `Deactivate` follows when the user works elsewhere. Real-user path: first start (welcome window) or a balloon click while the user already clicked into another program during the 10-20 s first launch. Suggested fix: decide with `GetForegroundWindow()` (root owner == settings form) instead of `Form.ActiveForm`, and re-check on a timer or on `WM_ACTIVATEAPP`.
Note: tester 2 saw dead hotkeys after settings closes in UIA flows; this is a different, reproducible trigger while the window is still open.

### B2 (med) Dictation pasted into the app's own dialog and lost

Steps: settings open on Tastenkürzel, set Diktieren = Ctrl+Shift+P (duplicate), Speichern → warning box "Die Tastenkürzel müssen verschieden sein." stays open. Press the dictate hotkey (Ctrl+Shift+K) twice.
Expected: hotkeys off while the settings window (or its modal dialog) is in front; at the very least no paste into the app's own window.
Actual: when the modal box takes activation the form gets `Deactivate`, the hotkeys come back on, a recording starts with the settings window still open, and the result is pasted into the MessageBox. Log says `Pasted`, the text is gone (not in Notepad, clipboard restored to the previous content). Only the tray's "Letzte Texte" still has it.
Evidence (sab-26-hotkey-during-modal.png shows "Diktat läuft" over the warning box, sab-26b-after-paste.png):
```
19:13:35.273 INFO Hotkey registered: Ctrl+Shift+K      <- warning box open
19:13:44.071 INFO Hotkey Dictate in state Idle
19:13:47.613 INFO Hotkey Dictate in state Recording
19:13:49.041 INFO Paste: foreground changed, brought the target window back
19:13:49.109 INFO Paste: sent Ctrl+V (111 chars)
19:13:49.112 INFO Dictate: Pasted, 111 chars
19:13:51.140 INFO Paste: restored previous clipboard
```
The same happens with the "Änderungen speichern?" box (`Hotkey registered` at 19:13:27 while it was open, sab-24).
Suggested fix: count any window of our own process as "settings in front" (foreground window's PID == own PID), and never take an own-process window as paste target (fall back to clipboard + "Bitte selbst einfügen").

### B3 (low) Esc during processing does nothing, the settings page says it cancels

Steps: select text, Ctrl+Shift+P, after 2 s Ctrl+Shift+P, then Esc x3 while Gemini works (3.4 s).
Expected (settings page Tastenkürzel, `SettingsForm.cs:206`): "Esc – Aufnahme verwerfen oder Verarbeitung abbrechen".
Actual: Esc is unregistered outside Recording (`SyncSessionHotkeys`, `if (_state != State.Recording) Unregister(Escape)`), no `Esc in state Processing` line, the answer is pasted.
Evidence:
```
19:16:59.860 INFO Hotkey Prompt in state Recording
19:17:03.244 INFO Gemini gemini-3.8-flash: HTTP 200 …
19:17:03.321 INFO Prompt: Pasted, 87 chars
```
Either keep Esc registered during Processing (cancel like a hotkey press) or change the copy to "Aufnahme verwerfen" (the guide says only "Esc bricht ab").

### B4 (med) Prompt on a huge selection silently drops a third of the text

Steps: Notepad, paste 500 × "Das ist ein falscher Satz und das muss sofort geaendert werden. " (32 000 chars), Ctrl+A, wav anweisung-freundlicher.wav ("mach das freundlicher"), Ctrl+Shift+P, Ctrl+Shift+P.
Expected: the whole selection rewritten, or a refusal / warning when the reply is much shorter than the selection or was cut off.
Actual: the selection is replaced by 23 635 chars = 311 rewritten sentences; 189 sentences (38 %) are gone. No warning card, no balloon, log says `Pasted`. Recoverable only with Ctrl+Z in the target app.
Evidence (sab-29-huge-selection-result.png; Notepad text read back: `311 Das ist leider nicht ganz richtig und sollte bitte zeitnah geändert werden`):
```
19:20:07.194 INFO Copy: selection 31999 chars
19:20:11.890 INFO Prompt: selection 31999 chars
19:20:55.029 INFO Gemini gemini-3.8-flash: HTTP 200, request 109868 bytes, answer 35106 bytes, attempt 1
19:20:55.096 INFO Prompt: answer 23635 chars
19:20:55.203 INFO Prompt: Pasted, 23635 chars
```
`GeminiClient.cs` sets no `maxOutputTokens` and never reads `finishReason`, so a `MAX_TOKENS` cut cannot be told from a deliberate shortening; the log does not show which one happened. The input was repetitive (a model may condense it), but the app gives the user no hint either way. Suggested: log `finishReason`; on `MAX_TOKENS` leave the answer on the clipboard with a message instead of replacing the selection; for edit instructions warn when the answer is below ~60 % of the selection length. Processing took 43 s with only the generic processing pill.

## Notes

**N1 Lost Prompt stop press (probable VM artefact).** Three times the second Ctrl+Shift+P of a Prompt run never reached the app (no `Hotkey Prompt` line) and went to Notepad instead: 18:45:52 typed 52 × "p" (sab-05b-silence-prompt.png), 19:01:19 typed 84 × "p" over a 32 000-char selection (sab-19-huge-selection.png, recoverable with Ctrl+Z), 19:03:25 opened Notepad's Print dialog (sab-20-firstprompt-2.png). A held "p" auto-repeating for 2-3 s means the key-up arrived seconds late, which points at prlctl event delivery, not at the app. Each case followed an app restart or a burst of prlctl/PowerShell calls. Later loops were clean (Prompt 8/8, 3/3; Dictate 10/10; Ctrl+Shift+D x25 with the app stopped: 25/25). One Dictate stop press (18:54:29) was lost the same way right after settings activity ("D" typed, sab-14-lostpress.png). Worth one check on a real keyboard: Prompt hotkey twice, 2-3 s apart, ten times.

**Harness lessons (for the next run).**
- Mouse clicks via click.ps1 do not register in this VM; drive the settings window with UIA SetFocus + `vm.sh focus WhisperShortcut` + keys. A helper `~/Downloads/wsw-test/sab-nav.ps1` (focus/set/get by wildcard name, prefers Edit controls) can set the API-key field through ValuePattern, which solves tester 2's S1 blocker.
- Sidebar pages: `sab-nav.ps1 focus 'Tastenk*rzel'`, `vm.sh focus WhisperShortcut`, `vm.sh combo space`.
- `uia invoke Speichern` blocks ~3 min (UIA timeout) when Speichern opens a modal box; take a shot instead of listing.
- `vm.sh combo down/home/left` arrive as numpad keys ("2", "7", "4") with NumLock on.
- Ctrl+Shift+Y could not be recorded in a hotkey box (scancode 44 with the VM's layout); Ctrl+Shift+K works.
- `vm.sh focus Notepad` sometimes silently fails when a console window is in front; check with a shot before typing.
- Gemini answers in ~1.2 s here, too fast to switch windows mid-processing with `vm.sh focus` (~2 s).
