# Tester 2 results (areas P and S), 2026-10-27, Windows 11 ARM VM

Settings backup: `~/Downloads/wsw-test/settings.backup2.json`, restored at the end (verified identical). App restarted with `tools/vm.sh start`.
Evidence screenshots: `~/Downloads/wsw-test/e2e/`. Log: `~/Downloads/wsw-test/wsw.log`.

| ID | Result | Note |
|---|---|---|
| P1 | PASS | Selection 30 chars, `Prompt: answer 87 chars`, `Pasted`, clipboard sentinel restored. Overlay (P1_rec.png) shows the five neutral defaults although the saved settings.json held the legacy medical list: migration on load works. |
| P2 | PASS | No selection: offer text (Heizkörper/Montage/Dienstag), no `*`/`#`, clipboard restored. |
| P3 | PASS | `Prompt: bare edit command without selection, refused`, `Notify: Bitte erst Text markieren`, nothing pasted. (Side note: PromptAsync still fires the parallel Gemini answer call before the refusal, one wasted call.) |
| P4 | PASS | `Quick action 3`, transcript 111 chars, selection 0, answer 76 chars. |
| P5 | PASS | Ctrl+1 with 34-char selection, pasted 35 chars. The "near-silent" wav yields a 16-char transcript consisting of the glossary words (tester 1 R4) and is appended to the instruction. Warning card flagged "Satz", which was correct: `vm.sh type` garbled the selection text. |
| P6 | PASS | Idle Ctrl+1/Ctrl+2 during Dictate produced no `Quick action` line; dictation pasted normally. |
| P7 | PASS | Third press during Processing: `Hotkey Prompt in state Processing`, `Prompt: cancelled by user`, no paste. |
| P8 | PASS | `1 word(s) in the reply are not in the dictation, warning shown`, card "Nicht in deinem Diktat – Meyer" visible at 5 s (P8_5s.png), gone at 25 s (P8_25s.png), text still pasted. |
| S1 | BLOCKED | Auto key check with the real key: `Gemini key check: Ok`. Invalid-key case not verifiable: `vm.sh combo ctrl a` / `type` did not clear the field, so the field held the real key plus garbage and the check returned `NoConnection` (twice, ~10 s each). Inconclusive, not an app bug claim. Needs a harness that can clear the edit (UIA ValuePattern set). |
| S2 | PASS | Auto key check `Ok` on open, label "✓ Schlüssel funktioniert", again `Ok` after Prüfen. |
| S3 | BLOCKED | Not run: prlctl exec channel became unstable (hangs of 120 s, `PrlJob_GetRetCode: Invalid argument`); real mouse clicks on nav items/radios did not register in the VM. |
| S4 | PASS | Edit glossary, Esc: dialog "Änderungen speichern?" (S4_dialog.png). Cancel keeps window and edit; No closes with no `Settings saved` line. Captions are English (Yes/No/Cancel) because VM OS locale is English (note only). |
| S5 | PASS | Esc without changes closes silently. |
| S6 | BLOCKED | Not run, same channel problems as S3. |
| S7 | PASS | With the settings window active, Ctrl+Shift+D logs nothing; after switching to Notepad `Hotkey registered` lines appear and Ctrl+Shift+D gives `Hotkey Dictate in state Idle`. |
| S8 | PASS | Schnellanweisungen shows the five neutral defaults (migration). "Vorschläge wiederherstellen" restores them; Speichern logs `Settings saved (… quick actions 5 …)`. |

Saboteurs: #7 (double Ctrl+1) PASS: one `Quick action` line, one paste. #2/#3 (P during D recording and D during P recording) PASS: the running recording is stopped, mode does not switch, no quick action pasted into a dictation.

Counts: 13 PASS, 0 FAIL, 3 BLOCKED (S1, S3, S6).

## Observations (no FAIL, but worth a look)

1. **Hotkeys silently dead after closing settings, in UIA-driven flows (probably harness artefact, app is fragile).**
   - Steps: start with `WSW_ARGS=--settings` (or tray-open), move focus with a PowerShell/UIA process (`uia invoke Speichern`, or SetFocus), close the window (Esc or Speichern).
   - Expected: Ctrl+Shift+D/P/G work after close.
   - Actual: log shows two groups of `Hotkey registered: Ctrl+Shift+D/P/G`, but a separate process calling `RegisterHotKey` for the same keys succeeds (`registerOK=True`, i.e. the app does not hold them); pressing them does nothing.
   - Alive cases: tray-open + `AppActivate` + Esc, and a pure-keyboard Save with no PowerShell in between (probe `registerOK=False err=1409`, i.e. registered).
   - Likely cause: `ShowSettings` has `Activated += if (_state == Idle) _hotkeys.UnregisterAll()`, not guarded by `_settingsForm != null`; a late `Activated` after `FormClosed` re-registered unregisters everything again. Not proven for real mouse use (clicks were not usable in the VM).
   - Suggested hardening: guard the Activated handler with `_settingsForm != null && !_settingsForm.IsDisposed`, or re-register in a `BeginInvoke` after close.
2. Stale copy: Schnellanweisungen page hint still says "Beispiel: Befund diktieren, Strg+1, fertig gegliedert." although defaults are now neutral.
3. No `Overlay: Hidden` is logged, so overlay hide timing can only be verified by screenshot.
4. Harness: `vm.sh type` garbles text (dropped/duplicated keys) and rejects uppercase ("unknown key: A"); `vm.sh combo ctrl a` was unreliable.
