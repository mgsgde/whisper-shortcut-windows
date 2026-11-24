# Refactor Ledger — WhisperShortcut for Windows

Running record of refactor reviews (same method as the macOS app's `/review-refactors`: the better
long-term design wins, "more work" is no argument). Each run reads this first and updates it at the
end. Entries change status, they are not deleted. IDs are stable and never reused.

Status values: `applied` · `deferred` · `rejected` · `superseded`.

## Findings

| ID  | Finding | Blast radius | Status | Run | Notes |
| --- | ------- | ------------ | ------ | --- | ----- |
| W1  | **Bug:** settings.json temp file renamed without flush; a crash can leave it empty | self-contained | applied (#89) | 1 | flush before rename; `settings.json.prev` fallback |
| W2  | **Bug:** API key that fails to decrypt becomes "" and the next save wipes the blob | self-contained | applied (#89) | 1 | undecryptable blob kept per key |
| W3  | **Bug:** staged update exe could be swapped between signature check and UAC install | self-contained | applied (#89) | 1 | held open with FileShare.Read until the installer exits |
| W4  | **Bug:** meeting worker copies the glossary off the UI thread while the hotkey adds to it | cross-file | applied (#91) | 1 | copy-on-write list, `Settings.TryAddGlossaryTerm`; kept `List<string>` type, contract by convention |
| W5  | **Bug:** screenshot during Prompt/Template recording cancels the late-copy guard | cross-file | applied (#91) | 1 | `LateCopyWatch` per take; snip's own clipboard write ignored |
| W6  | **Bug:** settings drop-downs save the hovered item (hand list missed Read Aloud + meeting combos) | self-contained | applied (#89) | 1 | every `FluentCombo` on the form |
| W7  | **Bug:** failed settings save not reported | cross-file | applied (#89) | 1 | `Settings.TrySave` with rollback; tour, window, tray |
| W8  | **Bugs:** quit during paste loses clipboard restore; last live insertion forgotten before a refused start; drift measurement not cancelled; malformed 200 reply = "no speech" | cross-file | applied (#89) | 1 | W8a–d |
| W9  | Processing pipeline inside `TrayApp`, reads shared `_take!`; two result paths | hot path | applied (#95) | 1 | `Core.DictationPipeline` → `TakeResult` with one Stop path; TrayApp −295 lines |
| W10 | `Mode` + `RecordingKindName` translated into each other, ~52 branches; ~15 ownerless recording fields | hot path | in PR #97 | 1 | `TakeProfile` table, `RecordingController`, `KeptAudio` (one expiry timer). `StartRecording` stays in TrayApp |
| W11 | One setting = 6 edits in 4 files, default written 3×; tester report misses settings | cross-file | applied (#96) | 1 | descriptor per `Fields` row; bindings by key; catalog ids/titles + report derived. Catalog option/validation logic for model/engine entries still hand-written |
| W12 | Shortcuts: 3 properties + 2 JSON fields + 2 rows each, ~12 edits to add one | cross-file | applied (#94) | 1 | `Dictionary<HotkeyAction, HotkeyBinding>` + `HotkeySlots.All`; JSON unchanged; `get_configuration` lists all 10 |
| W13 | Gemini-or-OpenAI decision repeated; two cloned `SendAsync` retry loops; everything named "Gemini" | cross-file | applied (#92) | 1 | `CloudProvider` table, `CloudHttp.SendWithRetryAsync`, `CloudException`; dead `gemini-3.1-pro` entries removed |
| W14 | Repo slug written several times; `PromptsEqualSwiftSource` matched by line numbers (failing) | self-contained | applied (#89) | 1 | `AppIdentity.GitHubRepo`; match by Swift symbol |
| W15 | **Bug:** a replaced llama-server killed by the old host; old host not disposed | self-contained | applied (#90) | 1 | |
| W16 | **Bug:** local meeting summaries not sized to the context window | self-contained | applied (#90) | 1 | `LocalLlm.ContextTokens` drives `-c` and clipping |
| W17 | **Bug:** meeting audio deleted when the pending WAV cannot be saved | self-contained | applied (#90) | 1 | |
| W18 | Meeting audio written on the capture thread | hot path | applied (#90) | 1 | bounded queue (waits, never drops), writer task owns chunker/partial/WAVs |
| W19 | **Bug:** deleting the streaming chat re-saves it | self-contained | applied (#90) | 1 | |
| W20 | **Bug:** TTS fires every chunk request at once | self-contained | applied (#90) | 1 | ≤ 3 in flight |
| W21 | **Bug:** meeting shutdown not idempotent | self-contained | applied (#90) | 1 | |
| W22 | Two Parakeet load paths; delete without the lock | self-contained | applied (#90) | 1 | |
| W23 | Two `MeetingStore` instances (controller + chat) | cross-file | applied (#90) | 1 | one store, chat listens for changes |
| W24 | One ToolTip per attachment chip | self-contained | applied (#90) | 1 | |
| W25 | **Bug:** download marker written after the zip is deleted | self-contained | applied (#90) | 1 | |

## Swept areas

| Area | Last swept | At commit | Notes |
| ---- | ---------- | --------- | ----- |
| `TrayApp`, `Settings*`, `SettingsForm*`, `UpdateInstaller`, `DpapiProtector`, `ClipboardPaster`, `GeminiClient`/`OpenAiClient`/`CloudApi`, `Mcp*` | run 1 | `523a520` | W1–W14 |
| `ChatWindow*`, `MeetingController`, `MeetingStore`, `LiveMeetingSession`, `ReadAloud*`, `TtsClient`, `Core/Offline/**`, `OfflineEngine` | run 1 | `523a520` | W15–W25 |

## Not yet swept

- `tools/` (release, VM scripts), `.github/workflows/`, the UiTests project.

## Run log

- Run 1 (2026-11-24, base `523a520`): two Opus sweeps, W1–W25, Magnus: "fix all". Bugs first (#89, #90, #91), then refactors W13 → W12 → W9 → W11 → W10 one at a time (each touches TrayApp or Settings). Typed by Grok 4.7 (#89, #90) and Sonnet 5.5 after Grok's daily cap (#91–#97), reviewed by Opus 5.5. Bug fixes released as v0.7.18. Lesson: batch A silently left out W4/W5; check every ID of a batch against the PR before closing it.
