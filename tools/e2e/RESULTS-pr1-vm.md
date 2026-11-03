# PR #1 VM checks on v0.2.0 (origin/main f00fe3b, win-arm64), 2026-10-27 ~21:00

Share: `~/Documents/wsw-test` (Z:\Documents\wsw-test). The VM only sees Desktop/Documents/Downloads, so `~/wsw-020` is not visible, and Downloads is being tidied.
Harness note: every PowerShell `prlctl exec` opens a Windows Terminal window that takes the foreground. Set the clipboard BEFORE focusing the paste target.

| Check | Result | Evidence |
|---|---|---|
| Baseline dictate into Notepad, restore on | PASS | `Pasted, 111 chars`, `restored previous clipboard`, clipboard = sentinel42 |
| Window switch, target still open | PASS | `foreground changed, brought the target window back`, pasted into Notepad, sentinel back |
| Window switch, target gone (a transient console was the target) | PASS | `target window is gone, leaving text on the clipboard`, `ClipboardOnlyTargetChanged`, balloon "Bitte selbst einfügen", clipboard = dictation |
| Image 1920x1080 on clipboard | PASS | image restored 1920x1080, pixel colour identical |
| Image 3840x2400 (4K wallpaper) | **FAIL** | `Clipboard snapshot above 64 MB, keeping text only`: the image is lost and the clipboard keeps the dictation. Likely cause: CF_DIB + synthesized CF_DIBV5 (+ CF_BITMAP) are each counted at ~37 MB. Fix idea: when CF_DIBV5 is present skip CF_DIB (Windows re-synthesizes it), or raise the cap |
| Excel round-trip | NOT RUN | no Excel in the VM |
| Offline retry balloon and re-run | NOT RUN | usage limit; net0 never disconnected |

Side observation: lang.wav (334 s) gives only 447 chars over 4 chunks. This is worth checking separately: is lang.wav mostly silence, or is text being lost per chunk?

## 2026-10-28: retest of the 4K fix (PR #8), blocked
`prlctl exec` answers once after a `prlctl reset`, then every call hangs or fails with `PrlJob_GetRetCode: Invalid argument`, even `cmd /c echo ok`. `prlctl restart` hangs too, but `send-key-event` and `capture` still work. The 4K retest and the offline retry test were not run. The Parallels Tools exec service in the guest needs fixing first: restart Parallels Desktop, or reinstall the Tools.

## 2026-10-28 14:25: retest after `prlctl suspend` + `resume` (exec working again), build of PR #8
| Check | Result | Evidence |
|---|---|---|
| Image 3840x2400 with the fix | **PASS** | `Paste: restored previous clipboard`, then `GetImage()` = 3840x2400 (before the fix: lost) |
| Offline: retries and message | PASS | network attempts 1-5 with 1.5/3/6/12 s backoff, `Dictate: Gemini Network`, toast "Gemini-Fehler … Kein Netz … Hier klicken, um es noch einmal zu versuchen.", nothing pasted into Notepad |
| Click on the toast re-runs the recording | NOT VERIFIED | a synthetic click (click.ps1, mouse_event) on the toast triggered nothing. The same helper did not register clicks in this VM before (Martin's run), and the Win key cannot be sent with `prlctl send-key-event`. Needs one real click by hand |

Harness: `prlctl suspend` + `prlctl resume` revives a dead exec channel without a reboot. PowerShell `-EncodedCommand` fails with "Invalid argument"; `powershell -File Z:\Documents\wsw-test\x.ps1` works. The channel still dies again after a few PowerShell calls.
