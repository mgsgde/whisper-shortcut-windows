# Refactor Ledger

Running record for `/review-refactors` (`.claude/commands/review-refactors.md`). Each run reads this
before triaging and updates it at the end. Long-lived: entries change status, they are not deleted.

## Findings

| ID  | Finding | Blast radius | Status | Run | Notes |
| --- | ------- | ------------ | ------ | --- | ----- |
| R1  | One run spread over ~20 `TrayApp` fields with 7 hand-written teardowns that disagree → one `Take` (kind, phase, cts, selection, live, `Dispose`); `FailedRun` becomes its replay snapshot | hot path | deferred | 1 | L. Base for R2, R5. Fixes: run-end `finally` misses `CancelLateWatch` (TrayApp.cs:1528ff vs 683ff); checkpoint stores `mode.ToString()`, crash-recovered Template/Live takes come back as Dictate (303, 885) |
| R2  | `ProcessAsync` / `ProcessLiveAsync` clone one skeleton; error ladder repeated in TrayApp, ReadAloudController, ChatWindow → `ProcessTakeAsync(take, body)` + Core `RunOutcomes.ForFailure/ForPaste` | hot path (skeleton), Core table self-contained | deferred | 1 | M. `RunOutcomes` can land before R1 |
| R3  | Engine dispatch (`Local`/Gemini/OpenAI) switched in 6+ places (TrayApp, Meetings, ReadAloud, Welcome, MCP); `OfflineEngine` outside the provider interfaces; `speech is OpenAiClient` checks; glossary-backstop rule decided per caller and inconsistent → `EngineSet` / client pair + Core `TranscriptionPolicy` | hot path | deferred | 1 | M–L. Merged from TrayApp and provider sweeps. ReadAloudController ctor params `gemini`/`openAi` are dead (overwritten at :42-43) |
| R4  | Hotkeys listed in ~15 places (Settings, Settings.Features, McpSettingsCatalog, SettingsForm, TrayApp), clash check written 3 ways → Core `HotkeySlots` table + one `Check` | hot path | deferred | 1 | M–L. Live drift: Diktieren/Prompt boxes and MCP rows ignore `DictateHotkeyEnabled`/`PromptHotkeyEnabled` (SettingsForm.cs:176-177, McpSettingsCatalog.cs:309-310) |
| R5  | "May I touch the clipboard?" answered from different flags at 5+ entry points → `ClipboardPaster.TryAcquire(ClipboardUser)` lease | hot path | deferred | 1 | M. Easier after R1 |
| R6  | Three "settings changed" follow-up lists already drifted (Saved, AfterTourSaved, AfterMcpApply) → one `ApplySettingsChange(SettingsDelta)` | cross-file | applied (PR #54, 4ee5617) | 1 | S. Core SettingsDelta + TrayApp.ApplySettingsChange; tour/MCP gained missing steps, each gated on its change |
| R7  | Each setting in 4 lists × 4 SettingsForm partials (Snapshot / before / apply / restore); whole-window write-back needs 5 workarounds → `Binding<T>` with baseline, apply only edited, `Pull` for outside changes; Core `Settings.Capture/Restore` | hot path | deferred | 1 | L. Simpler after R4, R9, R10a |
| R8  | Choice tokens and German labels (engine, punctuation, …) duplicated in Settings, MCP and form, labels already differ → `ChoiceSet<T>` like `ClipboardAfterPasteModes` | cross-file | applied (PR #54, 4ee5617) | 1 | Core Choices (Engine, Punctuation, DictateMode, AfterPaste); ClipboardAfterPasteModes removed; MCP engine label now "Gemini (Google)" |
| R9  | Provider + Gemini model + OpenAI model triple exists 5× (dictate, prompt, meeting ×2, chat) → `ModelSelection` value type, flat JSON keys stay | hot path (Load fallbacks) | deferred | 1 | M |
| R10 | Settings field = property + Load + ToDto + Dto, missing-field defaults hidden in Load. a) reflection round-trip + Fingerprint test now; b) descriptor rows later | a) test only, b) hot path | a) applied (PR #54, 4ee5617); b) deferred | 1 | a) SettingsRoundTripTests, all properties round-trip, no bug found; b) L |
| R11 | Base URL, auth header, non-2xx mapping, German timeout/network/no-key texts copied across Gemini/OpenAi/Chat/Tts clients (no-key text in 4 versions) → `CloudApi` profile + `CloudHttp.SendAsync` | hot path (provider contract) | deferred | 1 | M. Base for R12, R14 |
| R12 | Retryability decided 4 ways, 4 retry loops → `Retryable`/`RetryAfter` on the exception + one `HttpRetry.RunAsync` with per-caller policy | hot path | deferred | 1 | M, after R11. Behaviour bug: TTS retries Gemini daily-quota 429 (TtsClient.cs:299-305); TTS/Chat ignore RetryInfo/Retry-After — fix deliberately |
| R13 | Per-model facts (minimal thinking ×3 encodings, effort, keywords, speed ranks) spread over 4–5 files → `ModelInfo` capability table, `GeminiModels.Spec(id)` | cross-file, Core only | applied (PR #54, 4ee5617) | 1 | ModelInfo speed ranks, GeminiModels.RejectsMinimalThinking (union) + BuiltInThinking; OpenAI effort untouched |
| R14 | Three near-identical SSE streaming methods (Chat ×2, TTS) → `IdleWatchdog` + `Sse.DataPayloads` | self-contained | deferred | 1 | M, after R11 |
| R15 | ClipboardPaster bookkeeping by hand: "user's original" picked 4× (Paste, BeginLive, CopySelection, Snip), own-write sequence stamped in 4 writers (PutImage deliberately not), late-watch baseline moved 3×, pending-restore 3 fields cleared in 6 places → `PendingRestore` object + one `OwnWrite` funnel + `UsersClipboard()` | hot path | deferred | 2 | M. Fix history c7a2c12, 1f4ac31, 3a6d347 are all this bookkeeping. After R16 |
| R16 | Win32 clipboard memory I/O copied: UTF-16→HGLOBAL ×2 (WriteTextCore, RenderLazyText), HGLOBAL→bytes ×2 with caps 64 MB vs 16 MB, history/cloud markers ×2; WriteTextCore lacks GlobalLock null check; KeepOnClipboard ≡ CopyToClipboard → static `ClipboardMemory` | self-contained | in PR #57 | 2 | S |
| R17 | Paste / live piece / copy each re-query the foreground 3–5× (race between checks); live refusal ladder not in Core → `ForegroundProbe` snapshot + `PasteTargetPolicy.DecideLive` | hot path | deferred | 2 | M. Live path must never activate a window |
| R18 | No-activate overlay chrome (ctor, CreateParams, MA_NOACTIVATE, `S`) ×4, DwmSetWindowAttribute declared 3×, rounded path ×4 with different edge guards → `OverlayForm` base + `Theme.Rounded` only | cross-file | in PR #57 | 2 | S |
| R19 | Overlay placement open-coded ×4 (26/30/16 px, primary vs active screen); Toast avoids only RecordingOverlay → toast lands on ReadAloud card → `OverlayPlacement.BottomCenter(avoid…)` | cross-file | deferred | 2 | S–M. Overlap fix is a deliberate behaviour change |
| R20 | Silence rule ×3 (3 s / 5 s / 3 s; Meeting uses sampled level, Recording true peak), meter smoothing 0.8 vs 0.82, `Format(TimeSpan)` ×3 → Core `LevelMeter` | cross-file | in PR #57 | 2 | S. Check Mac values before unifying thresholds |
| R21 | MeetingOverlay / ReadAloudOverlay clones (colours, fonts, drag-to-move, spinner); fixed fonts not rebuilt on DPI change (unverified), 20/10 fps repaint while idle → `OverlayCardStyle` + `DragPlacement` | cross-file | deferred | 2 | M. After R18 |
| R22 | Toast / RecordingOverlay fade+slide, settle-skip and card fill near-identical → `LayeredCardAnimator` in LayeredWindow | cross-file | deferred | 2 | M, lowest priority |

## Swept areas

| Area | Last swept | At commit | Notes |
| ---- | ---------- | --------- | ----- |
| `TrayApp.cs` (+ partials, ReadAloudController, ChatWindow neighbours) | run 1 | 862c05c | R1–R6. Healthy: hotkey policies in Core, RightCtrl bridge, Live/Meeting Core types, MeetingController, Toast funnel |
| Settings complex (`SettingsForm*`, `SettingsUi`, `Settings*`, `McpSettingsCatalog`) | run 1 | 862c05c | R4, R7–R10. Healthy: SettingsUi, McpSetting, Fingerprint, atomic save, ModelPicker |
| Provider clients (`GeminiClient`, `OpenAiClient`, `ChatClient`, `Chat`, `TtsClient`) | run 1 | 862c05c | R3, R11–R14. Healthy: HttpRetry constants, NetworkPolicy, Utf8JsonWriter builders, PcmStreamBatcher, PlaybackOrderGate |
| `ClipboardPaster.cs` | run 2 | d4585cd | R15–R17. Healthy: Core policies (PasteTargetPolicy, ClipboardPasteGuard, SelectionCopyPolicy, SnipWatch, LiveTextTyping), lazy rendering |
| Overlays (`RecordingOverlay`, `Toast`, `MeetingOverlay`, `ReadAloudOverlay`, `LayeredWindow`) | run 2 | d4585cd | R18–R22. Healthy: LayeredWindow, no async void, UI marshalling via injected SynchronizationContext |

## Not yet swept

- `HotkeyService.cs`, `MicAudioSource.cs`, `NativeMethods.cs` (duplicate P/Invoke e.g. FocusedTextContext.cs:106)
- `WelcomeForm.cs`, `ChatWindow.cs` itself
- Meeting stack (`MeetingStore`, `MeetingController`, `MeetingAudioCapture`, `LiveMeetingSession`)
- `Mcp/` beyond `McpSettingsCatalog`, `McpService`
- `OfflineEngine.cs`, `TextCleanup.cs` / `GlossaryPrompt.cs`
