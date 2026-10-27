# E2E results, tester 1 (areas D, G, O, R), 2026-10-27

Build: fresh /tmp/wsw-arm64/WhisperShortcut.exe, Windows 11 ARM VM. Log: ~/Downloads/wsw-test/wsw.log. Screenshots: ~/Downloads/wsw-test/e2e/.
Settings backup: ~/Downloads/wsw-test/settings.backup.json (restored at the end, app restarted normally).

| ID | Result | Note |
|----|--------|------|
| D1 | PASS | Full log chain, 111 chars German prose in Notepad, stop-to-paste 2.6 s |
| D2 | PASS | `Paste: restored previous clipboard`, clipboard = sentinel42 |
| D3 | FAIL | Holding Ctrl+Shift+D on the stop press >= ~500 ms cancels the dictation (see below) |
| D4 | PASS | Overlay logged at 760,912 400x62 (bottom centre), screenshots show pill "Diktat läuft", then Done |
| D5 | BLOCKED | Needs right-click on tray icon; click.ps1 is left-click only |
| G1 | PASS | `Copy: selection 6 chars`, `Glossary add: Added (6 chars)`, found in settings.json |
| G2 | PASS | `Glossary add: Duplicate`, term once in settings.json |
| G3 | PASS | (a) `Empty` + Notify "Nichts markiert"; (b) 91 chars: `TooLong` + Notify "Zu viel markiert"; nothing added |
| G4 | FAIL | Glossary term sent to Gemini, but "Goedde" never produced (Görde / Gödde x3) |
| G5 | PASS | Hotkey with settings open: `Added`, "Nicht gespeicherte Änderungen" shown, after Save settings.json keeps the new term |
| O1 | PASS | `Parakeet loaded in 24750 ms` (cold), dictation pasted 1.4 s after stop, no Gemini line |
| O2 | PASS | `Prompt in offline mode, but this CPU cannot run the language model` + Notify "Offline-Sprachmodell" (screenshot: German text), selection intact, D works afterwards |
| O3 | PASS | Baseline "Herrgöde", after Ctrl+Shift+G `Parakeet reloaded for glossary (4 terms) in 4591 ms`, then "Herr Goedde" |
| O4 | PASS | Empty key + offline: no key line, `Hotkey Dictate in state Idle`, `Esc in state Recording` |
| R1 | PASS | `Esc in state Recording`, no recorded/Paste, clipboard sentinel intact, overlay gone in screenshot. Note: no `Overlay: Hidden` line is logged on cancel (log gap only) |
| R2 | PASS | Idle Esc produces no log line, "abc" untouched |
| R3 | PASS | `recorded 0.5 s`, `Mic: 15584 bytes, peak 0 %`, Notify "Aufnahme zu kurz", no Gemini call |
| R4 | FAIL | Near-silent wav makes Gemini return the glossary words and they get pasted (see below) |
| R5 | PASS | `Hotkey pressed but no API key (Schlüssel fehlt)`, Notify "Schlüssel fehlt", no recording |
| R6 | PASS | `Gemini InvalidKey` (HTTP 400), Notify "Gemini-Fehler", next D press starts a new recording (Idle again). Balloon click not tested |
| R7 | PASS | `Second instance started, exiting`; the second process stays alive showing the "läuft bereits" box until OK is pressed, then one process |

Counts: 15 PASS, 3 FAIL (D3, G4, R4), 1 BLOCKED (D5). 19 cases.

Saboteur extras run (all PASS): #1 triple press on D (Idle > Recording > Processing > `cancelled by user`, next press starts cleanly, no paste); #4 Esc during processing (by design Esc is not registered then, text still pasted).

Test-environment notes (not counted as bugs):
- `vm.sh stop` sometimes fails with `PrlJob_GetRetCode/GetResult: Invalid argument` and leaves the old process running; `prlctl exec ... taskkill /IM WhisperShortcut.exe /F` worked every time. A following `vm.sh start` then spawns a second instance with a message box that has to be dismissed (focus "WhisperShortcut", Enter).
- `vm.sh notepad` called a second time does not focus the window: call `vm.sh focus Notepad` before typing (my first G1 attempts hit an unfocused Notepad).
- `uia select Glossar` fails (TabItem has no SelectionItem pattern), `uia invoke Glossar` reports success but does not switch the page.
- Hotkeys stayed dead after `uia invoke Speichern` when the settings window was opened with `--settings` and was not the active window; the same flow with a real mouse click (click.ps1 on Speichern) worked. Log of the dead case: two groups of `Hotkey registered` within 15 ms after `Settings saved`, then no reaction to Ctrl+Shift+D/G until restart (17:05:32 and 17:08:16). Likely an activation artefact of UIA invoke, but a real user may hit it too, tester 2 should watch for it in S cases.

## FAIL details

### D3 Held modifiers: holding the stop hotkey cancels the dictation
Steps: wav diktat.wav, online. `combo ctrl shift d` (start), sleep 2, `hold 1500 ctrl shift d` (stop). Repeated with `hold 800` (same) and `hold 300` (fine).
Expected: either `Waited N ms for modifiers to be released` then `Dictate: Pasted`, or `ClipboardOnlyKeysHeld`; Notepad gets the text.
Actual: the stop press starts processing, then about 500 ms later (keyboard typematic delay) a second WM_HOTKEY arrives while the keys are still down and is treated as a press during Processing, which cancels. Nothing is pasted, Notepad stays empty. Code registers with MOD_NOREPEAT, so the repeat should not arrive; it does in the VM (possibly the app's own key-state handling or Parallels injecting repeated make codes; real hardware not verified).
Evidence (hold 1500):
```
16:47:30.693 INFO Hotkey Dictate in state Recording
16:47:30.701 INFO Overlay: Processing at {X=760,Y=912,Width=400,Height=62}, visible True
16:47:30.717 INFO Dictate: recorded 7.0 s
16:47:31.193 INFO Hotkey Dictate in state Processing
16:47:31.229 INFO Dictate: cancelled by user
```
Evidence (hold 800): `16:48:47.819 ... in state Recording` ... `16:48:48.380 INFO Hotkey Dictate in state Processing` / `16:48:48.614 INFO Dictate: cancelled by user`. Screenshot: ~/Downloads/wsw-test/e2e/D3.png (Notepad empty).
Why it matters: a doctor who holds the keys half a second too long loses the dictation. Needs a check on a real keyboard.

### G4 Glossary does not change online recognition
Steps: glossary [ramipril], wav glossar-goedde.wav. Baseline dictation: "Herr Görde kommt morgen ...". Add "goedde" via Ctrl+Shift+G (`Glossary add: Added (6 chars)`), dictate again three times.
Expected: "Goedde".
Actual: "Herr Görde", "Herr Görde", "Herr Gödde". Request size grew by 8 bytes, so the term is sent (`BuildTranscriptionBody` appends it). The term is stored lowercase ("goedde") as typed and the prompt says "exactly this spelling and capitalization", which may push the model away from the sentence-initial-name form; in the offline path the same word came out right ("Herr Goedde").
Evidence:
```
16:58:36.699 INFO Glossary add: Added (6 chars)
16:58:45.161 INFO Gemini gemini-3.1-flash-lite: HTTP 200, request 322587 bytes, answer 910 bytes
16:58:45.170 INFO Dictate: 95 chars raw, 95 chars after cleanup
```
Pasted text screenshot: ~/Downloads/wsw-test/e2e/G4.png ("Herr Görde kommt morgen zur Kontrolle. Wir erhöhen Ramipril auf 5 mg ..."). Model-quality issue rather than plumbing; promise "Ab dann wird es so geschrieben" is not kept online for this name.

### R4 Silence plus glossary: glossary words are pasted
Steps: settings glossary = [goedde, ramipril], online, wav stille.wav (peak amplitude 125 of 32768, mean 4: near-silent, not digital zero). Notepad, D hotkey twice.
Expected: nothing pasted (silence path or empty result).
Actual: Gemini answered with the glossary terms and 16 chars were pasted: "Goedde ramipril." The glossary header says to return empty on silence; the model ignored it. `AudioSilentException` was not triggered because the level is above the silence threshold.
Evidence:
```
16:50:18.026 INFO Dictate: recorded 2.2 s
16:50:19.800 INFO Gemini gemini-3.1-flash-lite: HTTP 200, request 114544 bytes, answer 829 bytes
16:50:19.879 INFO Dictate: 16 chars raw, 16 chars after cleanup
16:50:19.956 INFO Dictate: Pasted, 16 chars
```
Screenshot: ~/Downloads/wsw-test/e2e/R4.png. Real-world relevance: accidental recording in a quiet room with a medical glossary would paste drug names into the patient record. Note `DictationFidelityCheck.cs` exists; it is not applied to the dictate path (or did not trigger) here.
