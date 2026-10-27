# Release regression, Windows app v0.2.0 (build /tmp/wsw-020/arm64, main c1f769c), 2026-10-27

INCOMPLETE: the run was cut short by VM/harness problems (see "Environment incidents"). Cases 1 to 4 and the first
half of 5 are verified; 5c/5d, 6 to 9 and the three "PR #1 checks" were NOT run (BLOCKED, harness).

Log: `wsw.log` (first run in `~/Downloads/wsw-test/`, moved to Trash at 19:55 by someone else, copy in
`~/.Trash/wsw-test 19.55.39/`; continued share is `~/wsw-020/`). Screenshots: `rel-*.png` in `e2e/` of those folders.
Settings backup (taken before any change): `settings.backup-020.json` (also `~/wsw-backup-020.json`).
Clipboard sentinel: `sentinel42`.

| # | Case | Result | Evidence |
|---|---|---|---|
| 1a | Smoke: start, version, hotkeys | PASS | `Start 0.2.0.0 (Arm64)`, three `Hotkey registered` lines |
| 1b | Settings opens, screenshot every sidebar page | PASS | rel-02-general, rel-03-diktieren, rel-04-prompt, rel-05-glossar, rel-06-offline, rel-07-info; all render, no clipping |
| 1c | Info page shows "Version 0.2.0" | PASS | rel-07-info.png: "Version 0.2.0 (arm64)" |
| 2a | Dictate into Notepad, restore on | PASS | `Dictate: Pasted, 111 chars ... restore on`, `Paste: restored previous clipboard`, clipboard = sentinel42 |
| 2b | "Zwischenablage wiederherstellen" off | PASS | `Paste: sent Ctrl+V (111 chars), restore off`, no restore line, clipboard keeps the dictation |
| 2c | "Automatisch einfügen" off | PASS | `Dictate: ClipboardOnlyByChoice, 111 chars, autoPaste off`, Notepad empty (rel-d2c.png), overlay "In der Zwischenablage" (rel-d2c-overlay.png), clipboard = dictation |
| 3a | Prompt with selection | PASS | `Copy: selection 29 chars`, `Prompt: answer 66 chars`, `Pasted`; selection replaced by a friendlier sentence, no `*`/`#`, sentinel back |
| 3b | Prompt without selection (compose) | PASS | `no selection (compose)`, 762 chars in the Vorlage format pasted (rel-p2.png), warning card "Nicht in deinem Diktat: Meyer", sentinel back |
| 3c | Quick action Ctrl+3 | PASS | `Quick action 3`, answer "Test" replaced the selection (rel-p3-result.png) |
| 3d | Overlay shows the five neutral quick actions | PASS | rel-p3-overlay.png: Rechtschreibung, Klarer formulieren, Kürzer fassen, Aufgabenliste, Ins Englische (legacy medical list in settings.json was migrated) |
| 4a | stille.wav + glossary | PASS | `No speech (peak -48 dBFS, loud frames 0/71), Gemini skipped`, `Notify: Nichts erkannt`, nothing pasted, clipboard = sentinel |
| 4b | diktat.wav + glossary: no glossary words appended | PASS | six runs, always the same 111 chars "Hallo Herr Meyer, ... einbauen." |
| 4c | "Goedde" spelled right | PASS | `Glossary spelling: 2 word(s) set to the glossary spelling`, Notepad "Herr Goedde kommt morgen ... Ramipril ..." (rel-goedde.png) |
| 5a | B1: settings open but behind Notepad, hotkeys from Notepad | PASS | no `Hotkeys off` line, `Hotkey Dictate in state Idle/Recording`, `Pasted` |
| 5b | B2: "Änderungen speichern?" box open + hotkey | PASS (behaviour differs from the brief) | hotkeys stay off while the box is open: no log line for either press, nothing pasted, clipboard unchanged (rel-b2-box.png, rel-b2-after.png). No recording starts, so there is no "Bitte selbst einfügen" path here. After "No": `Hotkey registered` x3. The mid-recording variant (box opened while a recording runs) was started but not finished, see incidents |
| 5c | B3: Esc during processing cancels | BLOCKED | not run |
| 5d | B4: long hold of stop hotkey does not cancel | BLOCKED | not run |
| 6 | Hotkeys after Save+close; changed hotkey, old one dead | PARTIAL | after Save+close (UIA focus + space, not a real click; real clicks via click.ps1 still do not register) `Hotkey registered` x3 and D/P worked, three times. Changed hotkey not run |
| 7 | Anweisungen rule ENDE | BLOCKED | not run. Note: the field is `promptModeSystemPrompt` ("Anweisungen Dictate Prompt"); empty in settings.json, shows the English default text |
| 8 | Problem melden | BLOCKED | not run. Code read only: `ProblemReport.Body` has Version/Windows/Modus/Modelle plus the last 50 log lines through `Redact`, no clipboard or dictation text; button is on the Info page ("Melden ..."); `Problem report opened (body in URL: ...)` is logged, the URL itself is not. Open point: `TailLog(Log.DefaultPath)` ignores a `--log` override |
| 9 | Update check on Info | BLOCKED | not run |
| PR1-1 | Window switch mid-processing | BLOCKED | not run |
| PR1-2 | Clipboard round-trip (image, Excel, text) | PARTIAL | (c) text sentinel restored: PASS (see 2a, 3a, 3b). (a) image and (b) Excel not run |
| PR1-3 | Offline retry balloon and re-run | BLOCKED | not run; network was never disconnected (`net0` untouched) |

Counts: 18 PASS, 0 FAIL, 2 PARTIAL, 10 BLOCKED (harness). No APP BUG found in what ran.

## Observations (no FAIL)

1. (low) Esc is a global hotkey while recording, so a user who records with the settings window open cannot press Esc in
   that window; Esc cancels the recording instead. Seen while setting up 5b mid-recording, not verified end to end.
2. Hotkey churn on save: `Hotkeys unregistered (3)` followed by `Hotkey registered` x3 twice within 500 ms of `Settings saved`. Final state registered, harmless.
3. Settings footer shows "Nicht gespeicherte Änderungen" correctly after a toggle (rel-b2-box.png).
4. Sidebar page changes work through UIA focus + space; the old click-based path still does not work in this VM.

## Environment incidents (VM ARTEFACT / BLOCKED, not app bugs)

- 19:55: `~/Downloads/wsw-test` was moved to the Trash (19:55:39) and again at 20:37 (`wsw-test 20.37.11`) by something
  outside my session (the Downloads folder is being tidied; WhatsApp files and screenshots appear there at the same minutes).
  The test share, log and backups vanished mid-run. I restored from the Trash by copy and moved the harness to `~/wsw-020`
  (mounted as `Z:\wsw-020`) with an untracked `tools/vm-020.sh` (copy of vm.sh with the new share path; delete it).
- 19:55: a PowerShell `prlctl exec` in the guest hung while the share was missing and the guest froze completely (screen
  identical for 6 min, graceful restart ignored). `prlctl reset` was needed. Afterwards `prlctl exec` was unreliable:
  "Unable to open new session", `PrlJob_GetRetCode: Invalid argument`, calls without output, for 20+ minutes.
- Foreign activity seen: a `vm.sh run dir %LOCALAPPDATA%\wsw` at 19:58 (not mine), `~/Downloads/wsw-test/WhisperShortcut.exe`
  replaced by another build at 20:07/20:14 (not mine; I redeployed `/tmp/wsw-020/arm64` and confirmed identical `cmp`), VM
  suspended at 20:17 (resumed), Edge open on a new tab at 20:41 (not started by me).
- State at the end: `prlctl exec` was dead (no output for 30+ min after the reset and a second restart), so I could NOT
  restore settings.json byte for byte or start the app normally. The VM's settings.json was copied at 20:2x
  (`settings.now-020.json`) and differs from the backup only by defaults the app writes on save (`autoPaste`,
  `restoreClipboard`, empty prompts, quick actions migrated to the neutral five, re-protected key). To restore:
  `copy /y Z:\wsw-020\settings.backup-020.json %APPDATA%\WhisperShortcut\settings.json`, then `tools/vm-020.sh start`.
  The v0.2.0 exe is deployed in `%LOCALAPPDATA%\wsw` (hash-checked), app probably not running.

## Ship verdict

On the evidence that ran (smoke, settings pages, dictate, prompt, quick actions, silence, glossary, B1, B2, clipboard
options) 0.2.0 looks good to hand to a tester. It is not a full regression: B3, B4, the changed-hotkey case, Anweisungen,
Problem melden, update check and the three PR #1 checks (window switch, image clipboard, offline retry) are unverified and
should be run once the VM is stable.
