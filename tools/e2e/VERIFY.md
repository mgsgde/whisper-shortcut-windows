# Verification of tester findings, 2026-10-27

Verifier: Opus 5.5, code reading on `main` (47b5bec) plus one scratch probe. No VM was used.
Line numbers refer to `main` at 47b5bec.

| Rank | ID | Verdict | One line |
|---|---|---|---|
| 1 | R4 | **CONFIRMED** (but the tester's repro took a different route than a real user) | No speech gate before Gemini and no glossary-echo check after it. The tester's WAV skipped the existing 1 % gate only because `FileAudioSource` has none. The customer's "appended" symptom is the trailing echo, which nothing on `main` strips. |
| 2 | D3 | **LIKELY ENV ARTEFACT** as reproduced, **design fragile** (fix anyway) | Any hotkey press during Processing cancels at once. In the VM a second WM_HOTKEY arrives despite MOD_NOREPEAT. Real hardware is unverified. |
| 3 | Obs. 4 (S, hotkeys dead after settings close) | **LIKELY** (robustness bug, trigger only seen under UIA automation) | Hotkey on/off is driven by three unordered events. The `Activated` lambda can silently unregister everything, and nothing re-registers after the form is gone. |
| 4 | G4 | **NOT A PLUMBING BUG** (model limitation, same design as macOS) | The term reaches Gemini, but the prompt says "if, and only if, you clearly hear". For a near-homophone the model is never sure, so it keeps the common spelling. A deterministic post-correction is the fix. |

---

## 1. R4: silence plus glossary pastes glossary words (HIGH)

### Verdict
CONFIRMED as an app weakness. The repro in RESULTS-tester1 is partly a harness artefact. The customer symptom is real and has a second cause.

### What actually happened in the test
- `stille.wav`: 2.16 s, peak 125/32768 (-48.4 dBFS), RMS 6.6 (-73.9 dBFS). Max 30 ms frame RMS is -63 dBFS. That is digital near-silence.
- `MicAudioSource.StopAsync` (`src/WhisperShortcut/MicAudioSource.cs:131-133`) throws `AudioSilentException` when `peak < 328` (1 %). 125 < 328, so **a real microphone with this signal would never have reached Gemini**. The tester's note "the level is above the silence threshold" is wrong.
- The test ran with `--test-wav`, which uses `FileAudioSource` (`src/WhisperShortcut.Core/AudioSource.cs:27-39`). It returns the bytes with **no level check at all**. That is why the call went out.

### Why real users still hit it (root causes)
1. **The only gate catches a blocked device, not a quiet room** (`MicAudioSource.cs:131-133`). One mouse click, chair creak or breath above 1 % peak (-40 dBFS) lets a speechless recording through to Gemini. Its message ("Mikrofon liefert Stille … Datenschutz → Mikrofon") is also wrong for a quiet but working mic.
2. **No post-check of the transcript against the glossary** on `main`. `DictateAsync` (`src/WhisperShortcut/TrayApp.cs:431-445`) runs only `TextCleanup.ForDictation` and `DiscardImplausible` (`src/WhisperShortcut.Core/TextCleanup.cs:80-85`, an upper bound only). `DictationFidelityCheck` is a Dictate-Prompt reply-vs-source warning (`TrayApp.cs:483`, `517`). It never runs on plain dictation, and it could not tell an echoed glossary word from a spoken one.
3. **The "appended" symptom the customer reports is the trailing echo**: real speech followed by a run of glossary terms ("…zur Kontrolle. Goedde, Ramipril."). The macOS app has seen exactly this ("…gesperrt werden. Joschka SumUp lemniscus", `TextProcessingUtility.swift`, `strippingTrailingGlossaryEcho` doc comment). It also shows up on **long dictations**. Above 2.5 min, `ChunkedTranscription` (`TrayApp.cs:439-440`) sends ~2 min pieces. A near-silent last piece comes back as glossary words and is joined with a space onto the real text.
4. **The prompt is not the problem.** `Prompts.GlossaryHintHeader` (`src/WhisperShortcut.Core/Prompts.cs:62`) is already the macOS anti-echo wording ("spelling reference only, NOT content … never append these terms"). Gemini Flash-Lite ignored it here. Wording alone will not fix this.

### Evidence: scratch probe
Scratch console app against the **unmerged** port in `.claude/worktrees/agent-a8720142836eb89dd` (commit aedb6e8, "Port glossary prompt, echo gates…"). It adds `TextCleanup.DiscardGlossaryEcho` / `StripTrailingGlossaryEcho`, which are **not wired** into `GeminiClient` or `TrayApp`:
```
ForDictation: 'Goedde ramipril.'
DiscardImplausible: 'Goedde ramipril.'                 <- main: passes
DiscardGlossaryEcho (port): 'Goedde ramipril.'         <- port: passes too (16 chars >= 2.16 s * 3)
StripTrailingGlossaryEcho (port): 'Goedde ramipril.'   <- no sentence boundary, unchanged
Strip appended (port): 'Herr Goedde kommt morgen zur Kontrolle.'  <- catches the customer's case
```
So the port fixes the "appended" case once it is wired in. It does **not** fix R4. macOS would not catch R4 by text either: its `discardingGlossaryEchoTranscript` has the same 3 chars/s floor. On the Mac, R4 is stopped earlier by the **local level gate**.

### What macOS does for dictation
- Local silence gate: `AudioRecorder.swift:36` `silenceThresholdDB = -45` on AVAudioRecorder `averagePower` (short-window RMS). `MenuBarController.swift:3254-3280`: if the whole recording stayed below -45 dB, it **skips the cloud call** ("Skipping API call — recording was silent"). This applies to cloud transcription and prompt, not offline (offline was gated once and dropped real low-gain recordings).
- Digital zero (≤ -100 dB for ≥ 2 s) gets a separate "no microphone input" error (`MenuBarController.swift:48-52, 3233-3252`).
- On the Gemini response: `discardingImplausibleTranscript` and `discardingGlossaryEchoTranscript` (`GeminiTranscriptionCall.swift:91-100`). On an echo it retries once **without the glossary**, then falls back to another cloud model (`SpeechService.swift:2957-2975`).
- On every transcript: `strippingTrailingGlossaryEcho` (`SpeechService.swift:422-430`), for dictation and meeting chunks alike.

### Measured levels of the test WAVs (30 ms frames)
| file | max frame RMS | frames > -45 dBFS |
|---|---|---|
| stille.wav | -63.0 dBFS | 0 of 71 |
| glossar-goedde.wav | -12.5 dBFS | 186 of 211 |
| diktat.wav | -12.5 dBFS | 205 of 232 |
| anweisung-freundlicher.wav | -13.0 dBFS | 40 of 42 |
| angebot-ohne-markierung.wav | -12.9 dBFS | 196 of 218 |

The macOS threshold of -45 dBFS leaves more than 30 dB of margin on both sides here. It also fits the overlay's 1 % PeakSoFar warning (`RecordingOverlay.cs:31-32, 330`): 1 % peak is -40 dBFS, and speech frames above -45 dBFS RMS always peak above that.

### Fix spec
**(a) Speech gate (Core, pure, unit-testable).**
- New `src/WhisperShortcut.Core/SpeechGate.cs`: `public static SpeechLevel Analyze(byte[] wav)` returns `(float PeakDbfs, int LoudFrames, int Frames)`. It uses `Wav.ReadMonoFloat`, 30 ms frames and frame RMS. `LoudFrames` counts frames with RMS > -45 dBFS (linear 0.005623). Add `public static bool HasSpeech(SpeechLevel l) => l.LoudFrames >= 5` (≥ 150 ms above -45 dBFS, so one click does not count). On `InvalidDataException`, return "has speech" (fail open).
- `TrayApp.DictateAsync` (`TrayApp.cs:431`): **online only** (not offline, not `_fakeGemini`). Before the Gemini call, run `Analyze(wav)`. When `!HasSpeech`, log `Dictate: no speech (peak X dBFS, loud frames N/M), Gemini skipped`, notify `"Nichts gehört" / "In der Aufnahme war keine Sprache zu hören."` (Info) and return "".
- The same applies per chunk: in the delegate at `TrayApp.cs:439-440` and `533-534`, wrap it as `(chunk, c) => SpeechGate.HasSpeech(SpeechGate.Analyze(chunk)) ? _gemini.TranscribeAsync(chunk, …, c) : Task.FromResult("")`. A silent tail chunk then never reaches Gemini, which also saves cost.
- `PromptAsync` (`TrayApp.cs:447`) online: when there is no speech and no quick action, do not call Gemini at all. Notify "Nichts gehört" and return "". `QuickActionAsync`: when there is no speech, set `transcript = ""` without calling Gemini. That keeps P5 clean (the tester saw the echo words appended to the instruction).
- `MicAudioSource.StopAsync` (`MicAudioSource.cs:131-133`): keep `AudioSilentException` (blocked-mic message) only for **digital** silence. Lower the bound to `peak < 33` (about -60 dBFS, which is what a privacy-blocked device delivers: zeros). Anything quieter but nonzero goes to the speech gate and gets "Nichts gehört" instead of the wrong privacy hint.
- Tests (`tests/WhisperShortcut.Tests/SpeechGateTests.cs`): `stille.wav` → no speech. The four speech WAVs → speech. A synthetic WAV with one 20 ms click at 0.5 FS in silence → no speech. 200 ms of a -30 dBFS sine → speech. Non-16-bit input → speech (fail open).

**(b) Prompt.** Already the macOS anti-echo wording; leave it, with one optional change. Move the term list into its own delimited block (`Reference vocabulary (not spoken content):\n<terms>…</terms>`) and keep the audio part last, as now (`GeminiClient.cs:136-144`). Do not expect this to fix R4. Only ship it if a 10-run eval with `stille.wav` + glossary shows fewer echoes than before.

**(c) Deterministic post-check.** Wire in and extend the port from aedb6e8:
- Merge `GlossaryPrompt` and the `TextCleanup` gates from `worktree-agent-a8720142836eb89dd`.
- `GeminiClient.TranscribeAsync`: needs the chunk duration (`Wav.DurationSeconds(wav)`). Apply `DiscardGlossaryEcho(text, seconds, GlossaryPrompt.EchoTerms(glossary))`. On a discard, retry once with an **empty glossary** (macOS parity, `SpeechService.swift:2962`).
- New rule `TextCleanup.IsPureGlossaryList(text, terms)`: true when every word is a glossary word, at least **2 distinct terms** appear, and their term indices never decrease (glossary order). Apply it with the discard regardless of length. "Goedde ramipril." gives indices 0,1 and is discarded. A single spoken "Ramipril." survives (one term), and "Ramipril und Goedde" survives (a non-glossary word, reverse order).
- `TrayApp.DictateAsync`: after `ChunkedTranscription`, run `StripTrailingGlossaryEcho(raw, terms)` before `ForDictation`. Do the same on the instruction transcript in `PromptAsync` (`TrayApp.cs:470`) and `QuickActionAsync`. Log `Glossary echo stripped: "<tail>"`.
- Tests: R4 text → "" (pure list). "Herr Goedde kommt morgen zur Kontrolle. Goedde, Ramipril." → head only. "Ramipril." → unchanged. "Ramipril 5 mg." → unchanged. A chunk join "Text eins. Goedde ramipril" → stripped.

---

## 2. D3: holding the stop hotkey cancels the dictation

### Verdict
LIKELY ENV ARTEFACT for the trigger. The design is still fragile and worth hardening.

### Root cause in code
- Registration does use MOD_NOREPEAT: `src/WhisperShortcut/HotkeyService.cs:79` `RegisterHotKey(Handle, id, hotkey.Modifiers | MOD_NOREPEAT, …)`. Nothing in the stop-to-Gemini window injects input or re-registers the D hotkey. `SetState(Processing)` → `SyncSessionHotkeys` (`TrayApp.cs:693-708`) only unregisters Esc and Ctrl+1–5. `SendInput` is used only at paste time (`ClipboardPaster.cs:207`), and paste had not started 500 ms after stop (Gemini answers take ~1.7 s).
- `OnHotkey` (`TrayApp.cs:154-169`) treats **any** WM_HOTKEY during Processing as cancel (`case State.Processing: _cts?.Cancel();`, line 165-167), with no time or key-up condition. The same holds for Recording → stop (line 162-164): holding the *start* press would stop the recording after the typematic delay ("Aufnahme zu kurz").

### Why it is probably the VM
- The extra WM_HOTKEY arrives once, after about 500-560 ms (the typematic delay), and **not** every ~33 ms. A genuine auto-repeat that slipped past MOD_NOREPEAT would come as a stream, and the log would show a new recording starting right after the cancel (state is Idle by then). A single re-press at the typematic delay looks like Parallels synthesising a break+make for a key held via `prlctl send-key-event press` (`tools/vm.sh:46-47, 92-96`; each key is a separate `prlctl` call). With a break+make, Windows legitimately sees a new press. MOD_NOREPEAT cannot suppress that, because it only suppresses events whose key was already down.
- On a physical keyboard, Windows' own typematic repeats are flagged as repeats and MOD_NOREPEAT drops them. This is the standard behaviour, but nobody has verified it on real hardware for this app.

### Why fix it anyway
A press that arrives within a fraction of a second of the stop press is never a deliberate cancel: key bounce on worn office keyboards, a KVM, RDP or Citrix session (common in practices) re-sending keys, or this VM. Losing a 3-minute dictation to it is far worse than ignoring a cancel for half a second. macOS has the same "press during processing cancels" semantics (`MenuBarController.swift` `.cancelProcessing`), but Carbon hotkeys there do not repeat.

### Fix spec
- New `src/WhisperShortcut.Core/HotkeyPressGate.cs` (pure, clock and key-state injectable):
  - `void Armed(long nowMs)` is called when a hotkey press changes the state (Idle→Recording, Recording→Processing). It stores `_armedAt` and sets `_released = false`.
  - `void KeyUp()` sets `_released = true`.
  - `bool Accept(long nowMs) => _released && nowMs - _armedAt >= 300`.
- `TrayApp`: on every hotkey press that changes the state (`StartRecording`, `StopAndProcessAsync` entry), call `Armed` and start a 30 ms `System.Windows.Forms.Timer`. The timer polls `GetAsyncKeyState((int)hotkey.Key) & 0x8000` for the key of the hotkey that was pressed. When it reads 0, call `KeyUp()` and stop the timer (stop it after 10 s regardless).
- `OnHotkey` (`TrayApp.cs:154-169`): in `case State.Recording` and `case State.Processing`, first `if (!_pressGate.Accept(now)) { Core.Log.Info($"Hotkey {mode} in state {_state} ignored: {(released ? "within 300 ms" : "key still held")}"); return; }`.
- This keeps the "triple press" saboteur (#1) and P7 passing: a deliberate third press comes after a key-up.
- Tests (`HotkeyPressGateTests.cs`): press at t0 with no key-up → Accept false at t0+500, t0+1500. Key-up at t0+100 → Accept false at t0+200 and true at t0+400. Re-arm resets.
- Real-hardware check to add to CASES.md D3: hold Ctrl+Shift+D for 1.5 s on a physical keyboard (or over RDP) and look for exactly one `Hotkey Dictate in state Recording` and no `ignored` line.

---

## 3. Tester 2 observation: hotkeys dead after the settings window closes

### Verdict
LIKELY. This is a real robustness bug. The exact trigger has only been seen under UIA/PowerShell automation, and real mouse use worked (tester 1). Assistive and automation tools that drive windows the same way do exist at customer sites (Dragon, screen readers, password managers).

### The code
`src/WhisperShortcut/TrayApp.cs:771-780`:
```csharp
_settingsForm.Activated += (_, _) => { if (_state == State.Idle) _hotkeys.UnregisterAll(); };
_settingsForm.Deactivate += (_, _) => RegisterHotkeys(notify: false);
_settingsForm.FormClosed += (_, _) => { _settingsForm = null; RegisterHotkeys(); … };
```
plus `SettingsForm.cs:350` `Activated += SyncOfflineFromTray` (harmless: it only touches the form's radio buttons and `_snapshot`, not hotkeys).

### Why only this path can produce the observed log
- `HotkeyService.UnregisterAll` (`HotkeyService.cs:100-105`) **logs nothing**. Every other unregister path either re-registers and logs (`RegisterHotkeys`, `TrayApp.cs:127-152`) or is `Quit`.
- The log shows two `Hotkey registered` groups after `Settings saved` (FormClosed plus the Deactivate fired during the window's destruction). An external probe could then register the same keys (`registerOK=True`), so the app no longer held them. The only code that can drop them without a log line is the `Activated` lambda. Therefore an `Activated` must have run **after** the last `RegisterHotkeys`.
- Once that happens, nothing re-arms: `FormClosed` already ran, `_settingsForm` is null, and the closed form never raises `Deactivate` again. Hotkeys stay dead until restart. There is no log line and no tray hint.

### Sequence that leaves hotkeys unregistered
1. Settings open, not the foreground window (started via `--settings` from `prlctl`, so Windows' foreground lock denies `Activate()` at `TrayApp.cs:782`). Hotkeys are registered.
2. An external UIA client (PowerShell `uia invoke Speichern` / SetFocus) asks to activate the window. Cross-process activation is delivered **asynchronously** to the UI thread, while the UIA Invoke itself arrives as a sent COM call, which is processed first.
3. Invoke → `Save()` → `TrySave()` → `Close()` → `FormClosed` → `RegisterHotkeys()` (group 1). During teardown `Deactivate` → `RegisterHotkeys(false)` (group 2).
4. The queued activation is then processed for the form object. `Activated` fires with `_state == Idle` → `UnregisterAll()`. This is silent and final.

Step 4 (WinForms raising `Activated` on a form whose `FormClosed` has already run) cannot be verified without Windows. It is the only explanation consistent with the log plus the probe. The same lambda has a mirror bug: a form activated while not Idle is never unregistered, so after processing ends the open settings window has live global hotkeys. The HotkeyBox then cannot record Ctrl+Shift+D, because the press starts a dictation.

### Fix spec
Replace the three event-driven toggles with one idempotent reconciler, so event order no longer matters:
- `TrayApp`: field `bool _globalHotkeysOn`. Method:
  ```csharp
  private void SyncGlobalHotkeys(bool notify = false)
  {
      var f = _settingsForm;
      bool settingsInFront = f is { IsDisposed: false, Visible: true } && Form.ActiveForm == f;
      bool wanted = !(settingsInFront && _state == State.Idle);
      if (wanted == _globalHotkeysOn) return;
      if (wanted) RegisterHotkeys(notify); else { _hotkeys.UnregisterAll(); Core.Log.Info("Hotkeys off: settings window in front"); }
      _globalHotkeysOn = wanted;
  }
  ```
  `RegisterHotkeys` sets `_globalHotkeysOn = true`.
- `ShowSettings` (`TrayApp.cs:771-780`): `Activated` and `Deactivate` both call `SyncGlobalHotkeys()`. `FormClosed`: `_settingsForm = null;` then `_ui.Post(_ => SyncGlobalHotkeys(notify: true), null)`, so it runs after any trailing activation messages, plus the current Prewarm/GlossaryChanged calls.
- Also call `SyncGlobalHotkeys()` at the end of `SetState` (`TrayApp.cs:710-726`), so leaving Processing with the settings window in front switches them off, and entering Idle after close guarantees them on.
- `HotkeyService.UnregisterAll`: log `Hotkeys unregistered (N)` so the next occurrence is visible in the log.
- Tests: the reconciler depends on WinForms state. Extract the decision `static bool WantGlobalHotkeys(bool settingsAlive, bool settingsActive, State state)` into a testable helper and table-test it. E2E (CASES S7 extension): open settings via `--settings`, close via `uia invoke Speichern`, press Ctrl+Shift+D, expect `Hotkey Dictate in state Idle`.

---

## 4. G4: glossary term "Goedde" ignored online

### Verdict
NOT A PLUMBING BUG. It is a model limitation, with the same prompt design as macOS. Worth a deterministic fix because of the promise "Ab dann wird es so geschrieben".

### How the glossary reaches Gemini
`GeminiClient.BuildTranscriptionBody` (`src/WhisperShortcut.Core/GeminiClient.cs:127-131`): the transcription prompt + `\n\n` + `Prompts.GlossaryHintHeader` + `string.Join(", ", terms)` go into **user content**, as the first text part before the audio. There is no `systemInstruction` and no `temperature`. This matches macOS exactly: `SpeechService.appendGlossaryHint` (`SpeechService.swift:124-145`) and `GeminiTranscriptionCall.swift:60-66` also send instruction text + inline audio in `contents`. Placement therefore does not explain a difference.

### Why the model writes "Görde" / "Gödde"
1. The hint says "**If, and only if, you clearly hear one of them**" (`Prompts.cs:62`). macOS documents exactly this failure: for a near-homophone, a Flash-tier model "is never confident, so it falls back on whichever word carries the stronger language prior" (`SpeechService.swift:147-160`). macOS solves it with a separate **tie-breaker block** for `Term (not "Wrong")` glossary lines, plus a post-correction. Windows `main` has neither. The unmerged port aedb6e8 adds both, but its `CorrectRejectedSpellings` rewrites **multi-word** rejected forms only (macOS parity), so "Görde" → "Goedde" would still rely on the prompt.
2. The term is stored as typed, **all lowercase** ("goedde", from the hotkey on a lowercase selection), and the hint demands "exactly this spelling **and capitalization**". For a surname after "Herr", that contradicts German grammar, and the model resolves it by trusting neither.
3. Offline works because Parakeet hotwords (`OfflineEngine.cs:246-262`, `ParakeetHotwords.FromGlossary`) bias the decoder acoustically. They do not depend on the model "being sure".

### Fix spec (in order of value)
1. **Deterministic name post-correction** (new `GlossaryPrompt.ApplyTermSpellings(text, terms)`, run after Gemini in `DictateAsync`, before `ForDictation`, and on chunk joins):
   - German transliteration fold for matching: lowercase, `ä→ae ö→oe ü→ue ß→ss`, strip other diacritics. "Gödde" → `goedde` == term → replace. The current `GlossaryPrompt.Fold` only strips diacritics ("godde") and would miss this.
   - Optional fuzzy step, guarded: a transcript word at Levenshtein distance 1 from a term on the folded forms, only when the term is ≥ 6 letters, the transcript word is **capitalized and not sentence-initial**, and the word is not itself another glossary term. That catches "Görde" (`goerde` vs `goedde`). Log every replacement (`Glossary spelling: "Görde" → "Goedde"`). Ship the exact-fold step first, and the fuzzy step only with the tests below.
   - Casing: if the term is all lowercase and the replaced word was capitalized, capitalize the term's first letter.
2. **Glossary-line annotations** (merge aedb6e8): the user can write `Goedde (nicht "Görde", "Gödde")`. The tie-breaker block goes into the prompt. Extend `CorrectRejectedSpellings` to **single-word** rejected forms when the rejected form is not a common word (it is user-asserted, so this is lower risk than the fuzzy match).
3. **Capitalization wording**: when building the hint, add "(capitalize as normal for the language when the listed spelling is all lowercase)", or capitalize the term's first letter at hotkey-add time when the selection was one lowercase word.
4. Not recommended as a fix by itself: moving the hint to `systemInstruction`. It is untested on either platform. Only consider it if an eval (10 runs each, `glossar-goedde.wav` with glossary [goedde] and [Goedde]) shows a gain.
- Tests: "Herr Gödde kommt" + [goedde] → "Herr Goedde kommt". "Herr Görde kommt" + [goedde] → "Herr Goedde kommt" (fuzzy). "Die Görde" + [] → unchanged. "Gerade" + [Goedde] → unchanged (distance > 1). Sentence-initial "Görde ist…" → unchanged under the fuzzy rule.

---

## Side notes
- `dotnet test` was not run. No code was changed on `main`. The scratch probe is in the session scratchpad, not the repo.
- An unmerged branch `worktree-agent-a8720142836eb89dd` (aedb6e8) already holds the glossary parsing and echo-gate library code for R4(c) and G4(2). It still needs wiring into `GeminiClient`/`TrayApp` and the extra pure-list rule above.
- Harness: `--test-wav` (`FileAudioSource`) bypasses the mic level gate. With the speech gate in (a) placed in `TrayApp` rather than `MicAudioSource`, file-driven tests exercise the same path as the microphone.
