---
name: review-refactors
description: Hunt for the mid-to-long-term simplest, most elegant design — deep structural simplifications, not surface cleanups. Explicitly discounts "that's more code / more work to build" as an objection, so it argues for the best long-term shape even when it means large rework. Given no target it triages the repo itself (churn, defect density, size, crusted code, centrality) and picks the highest-payback hotspots. Produces a tiered, blast-radius-tagged proposal list; applies only what the user approves. Use when the user asks where the code could be built simpler/more elegantly/better, wants refactoring opportunities, wants technical debt reduced, or wants duplication removed across files. Scope is the WhisperShortcut Windows C# code (`src/`, `tests/`).
argument-hint: "[target: path, type or feature area]"
---

# Review Refactors

Port of the macOS repo's `/review-refactors` skill (`.agents/skills/review-refactors/SKILL.md` in
github.com/mgsgde/whisper-shortcut), adapted to C# / .NET 10 / WinForms.

Find where this codebase could be **fundamentally** simpler, more elegant, and better for the
mid-to-long term — then propose it, and apply what the user approves.

Target for this run: `$ARGUMENTS` (empty → no-target triage mode).

**How to invoke:**

- **`/review-refactors`** — no target: triage the repo (churn × size × defect density, minus what
  the ledger already covers) and pick the 2–3 highest-payback areas.
- **`/review-refactors <target>`** — a path, type, or feature area: sweep that plus its neighbors.

Both forms report first and edit nothing until the user approves. They reply `apply`,
`apply R4 and R6`, or `apply all`. Anything tagged **hot path / risk-sensitive** gets a scope
question before the rework.

The guiding rule: **"it's more code / more work to build" is NOT a valid argument against the
better design.** Implementation cost has collapsed; the only things that count are the _resulting_
design's clarity, safety, and maintainability. Argue for the best long-term shape even when the
rework is large.

This is still KISS — KISS is about the _resulting_ design being simple to reason about, not about the
diff being small. A 400-line change that collapses five divergent copies into one tested builder is
KISS. A 20-line change that adds an interface wrapping one concrete class is not.

**macOS parity still wins.** Windows rebuilds the Mac app (`AGENTS.md`). A refactor must not change
behaviour, defaults or wording that mirror the Mac app. If the Mac app solved the same structural
problem (e.g. it already has a `LiveMeetingSession`, a provider factory, a capability table), prefer
converging on its shape — read the Swift source the reference names before inventing a different one.

## The ledger — this command is meant to be run again and again

Refactoring a repo is a campaign, not one pass. This command is designed to be invoked repeatedly
over weeks, each run picking up where the last one stopped, so **`docs/refactor-ledger.md` is part
of the command, not an optional nicety**. Without it every run re-triages from zero, re-proposes
findings the user already rejected, and keeps re-reporting the same deferred items as if they were
new.

The ledger is the memory. It records, per finding: a stable ID, what it was, its blast radius, and
its **status** — `applied`, `in PR #<n>`, `deferred`, `rejected`, or `superseded` — plus which areas
have been swept and at which commit. It is long-lived: it is **not** deleted when a piece of work
finishes, because its value is precisely the accumulated history. Individual entries get their status
updated instead.

**`applied` means merged into `main`, with the merge commit.** While the change sits in an open PR the
status is `in PR #<n>`. Before you write `applied`, check `gh pr view <n> --json state` says `MERGED`.
(On the Mac, R71 stayed `applied` for two weeks while its PR never merged, so nobody looked again
until it was 360 commits behind main.)

Two rules make repeated runs additive rather than repetitive:

- **Read it before you measure anything** (Step 0a) — it changes what you triage.
- **Write it back before you finish** (Step 6) — a run that proposes findings and doesn't record
  them has thrown away the only thing that makes the next run cheaper.

If the file doesn't exist yet, this is run #1: create it in Step 6 with the template at the bottom
of this command.

## Step 0 — Args & scope

**Always read `docs/refactor-ledger.md` first**, before the triage or the target sweep. It tells
you what is already done, what the user turned down, and what is waiting. Then:

- **With a target**: a path, directory, type, or feature area — `src/WhisperShortcut.Core/Mcp/`,
  `SettingsForm.cs`, "the TTS path", "the provider clients". Sweep that area plus its neighbors and
  skip to Step 1. Check the ledger for entries covering that target and say up front what was already
  applied there, so the user isn't re-sold a finished refactor.
- **Without a target**: do **not** default to the working diff (that is `/code-review`'s job).
  Instead run the triage below and pick the targets yourself — the places where reducing technical
  debt buys the most.

**Never silently cap the sweep.** Whatever you pick, say what you read and what you skipped, so a
quiet week never reads as "nothing to improve".

### Step 0a — Collect the signals (no-target mode)

Five cheap measurements (Bash tool / Git Bash). Run them all before deciding anything; each one alone
is misleading. Exclude `bin/` and `obj/`.

```bash
# 1. Churn — where work actually happens. A refactor here pays back every week.
git log --since="6 months ago" --name-only --pretty=format: -- '*.cs' \
  | grep -v '^$' | sort | uniq -c | sort -rn | head -15

# 2. Defect density — where bugs keep landing. The strongest debt signal there is.
git log --since="12 months ago" -i --grep='fix\|bug\|revert\|regress' \
  --name-only --pretty=format: -- '*.cs' \
  | grep -v '^$' | sort | uniq -c | sort -rn | head -12

# 3. Size — god-file candidates.
find src -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -exec wc -l {} + \
  | sort -rn | sed -n '2,16p'

# 4. Crusted code — large files nobody has touched in months (oldest first).
find src -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | while read -r f; do
  n=$(wc -l < "$f"); [ "$n" -lt 250 ] && continue
  printf '%s  %5s  %s\n' "$(git log -1 --format=%ad --date=short -- "$f")" "$n" "$f"
done | sort | head -12

# 5. Centrality (tie-breaker only) — how many files reference the types a file declares.
types=$(grep -hoE '^\s*(public |internal |private )?(static |sealed |abstract |partial )*(class|struct|record|enum|interface) [A-Za-z_][A-Za-z0-9_]*' "$FILE" | awk '{print $NF}' | sort -u)
for t in $types; do
  echo "$(grep -rlw --include='*.cs' "$t" src | grep -v "^$FILE$" | wc -l) $t"
done
```

The app and Core share one namespace per project, so signal 5 is a rough proxy — use it to break
ties, never to rank. For "importance", trust the architecture-critical set instead: `TrayApp`
(menu, flows, state), `HotkeyService`, `ClipboardPaster`, `Settings` (persistence, defaults),
the provider clients (`GeminiClient`, `OpenAiClient`, `ChatClient`, `TtsClient`), `LiveDictation` /
`LiveMeetingSession` / `MeetingController` / `MeetingAudioCapture`, `OfflineEngine`, `McpService` +
`Mcp/`, the updater / `InstallState`.

### Step 0b — Rank into tiers

The classic hotspot is **high churn × large size** — a file that is both big and constantly edited
is where complexity costs real time. Layer defect density on top and sort into:

- **Tier A — hotspots.** Top-10 churn _and_ top-10 size, or top-5 defect density. Best payback;
  start here.
- **Tier B — crusted.** Large, central, and untouched for months. Either genuinely stable (leave it
  alone) or code people route around because it's scary. Decide which by reading it — if it is
  stable, say so and move on.
- **Tier C — the rest.** Mention only if something specific stands out.

Read the signals honestly, not mechanically:

- **High churn alone is not debt.** It often just means a feature is under active development
  (this repo is still catching up to the Mac app). It counts when paired with size, defect density,
  or visible duplication.
- **Staleness alone is never a finding.** Small, stable, untouched code is healthy code.
- **Size alone is weak.** A long file of flat declarations (a settings catalog, string tables) is not
  a hotspot. A long file of branching logic is. WinForms layout code is long by nature — judge it by
  repetition, not length.
- The signals are proxies for _where to look_. The finding still has to be a concrete structural
  problem you can point at with `file:line` — never report a ranking as if it were a finding.

### Step 0b2 — Subtract what the ledger already covers

The raw signals do not know about previous runs, so a file you refactored last week still ranks
high on churn and size. Reconcile before choosing targets:

- **A swept area is not a target again unless it moved.** For each ledger entry with a swept-at
  commit: `git diff --stat <swept-at-commit>..HEAD -- <path>`. No meaningful diff → deprioritize.
  Substantial new work → fair game again, and say so ("re-sweeping X, ~400 lines changed since").
- **`applied` entries mean the smell is gone.** If it has genuinely regressed — someone added a
  sixth copy — that is a *new* finding that references the old ID.
- **`rejected` entries are settled.** Raise one again only if the reason it was rejected has
  changed, and say explicitly what changed.
- **`deferred` entries are the backlog, and they outrank new discovery.** Carry them into Step 4
  ahead of anything newly found, unless the user asked for something specific.
- **Prefer an unswept neighborhood.** All else equal, pick areas no previous run has touched.

### Step 0c — Show the triage, then dive

Print a short table of the shortlist with the numbers behind it (churn / size / fix-count / last
touched) and say which 2–3 targets you picked and why. The user can redirect you before you spend
the effort. Then deep-read those targets and their neighbors.

Open with one line of continuity — e.g. "Run #4. Carrying 3 deferred findings; `TrayApp` /
provider clients already swept; picking up `SettingsForm` and the TTS path." On run #1 say that
instead: no ledger yet, starting fresh.

## Step 1 — Read for real

Read the target files **and their neighbors** — callers, callees, and sibling implementations of
the same concept. Most high-value refactors live in the _repetition across_ files. Grep for:

- the same helper defined more than once (HTTP retry/backoff, JSON extraction, error-message mapping,
  audio/WAV math, temp-file naming, DPI scaling, P/Invoke declarations);
- the same request/prompt/settings shape hand-assembled in several places;
- two methods that differ only in their data — per-provider or per-model near-clones, per-tab
  settings scaffolding in `SettingsForm` / `SettingsUi`, overlay windows (`RecordingOverlay`,
  `MeetingOverlay`, `ReadAloudOverlay`, `Toast`) that re-implement the same window chrome.

## Step 2 — Hunt these patterns

Rank candidates by **(long-term value × confidence) ÷ blast radius**. Look for:

1. **Duplicated logic that must change in lockstep** — especially anything security- or
   correctness-critical: API key storage, update signature checks, clipboard restore, hotkey
   registration, error mapping, cancellation. N hand-copies are a latent drift bug. Collapse to one
   source of truth. This is almost always the top-priority find.
2. **Clonal implementations / wrong altitude** — near-identical blocks differing only in literals or
   a key: per-provider request builders, per-model capability branches, per-tab settings rows,
   overlay windows. Drive them from one skeleton plus a data table (a capability record, a model
   table, a parameterized builder).
3. **Missing or leaky seams** — UI code (`*Form`, `TrayApp`, overlays) doing logic that belongs in
   `WhisperShortcut.Core` and is therefore untestable; Core reaching back into WinForms; a service
   mutating UI directly instead of going through one owner. Extract the seam into Core; keep the
   _variable_ part local and share only the _mechanical_ part.
4. **Primitive obsession / stringly-typed** — model IDs, provider names, hotkey specs or settings
   keys passed as raw `string`s; a shape passed as five loose arguments when a small `record` or
   `enum` would make illegal states unrepresentable.
5. **Dispatch ladders & god-conditionals** — long `if`/`switch` chains selecting a handler where the
   same selector is duplicated in several files. Replace with one table or one factory.
6. **Thread-affinity sprawl** — `Invoke`/`BeginInvoke`/`SynchronizationContext` hops scattered across
   call sites instead of one marshalling point; `async void` outside event handlers; fire-and-forget
   tasks with swallowed exceptions.
7. **Inconsistency with the repo's own conventions** — this file does X the hard way while a shared
   helper already exists (`Log`, `SettingsUi` builders, existing Core helpers). Adopt the existing
   pattern instead of inventing a parallel one.

Explicitly **do not** reject a candidate because the fix touches a lot of code or is "hard". Do
weigh _blast radius_ — that governs how you roll it out and whether you ask first, not whether the
better design is worth proposing.

## Step 3 — Write each finding

For every candidate capture:

- **ID** — stable, never reused: `R<n>` continuing from the highest number in the ledger. Never
  renumber existing entries.
- **Current shape** — what's there now, with `file:line`.
- **Proposed shape** — the target design, concretely: a signature, a type sketch, a small diff.
- **Why it's better long-term** — single source of truth, testability, fewer places to change,
  illegal states unrepresentable. Not "shorter" for its own sake.
- **Depends on** — other IDs that should land first.
- **Blast radius** — exactly one of:
  - **self-contained** — one file or one feature; safe to apply once approved;
  - **cross-file / shared helper** — apply, but name the ripple and the call sites touched;
  - **hot path / risk-sensitive** — `TrayApp` state/flows, `HotkeyService` (low-level keyboard
    hook), `ClipboardPaster`, the audio/recording/meeting pipeline, `Settings` persistence and
    defaults (existing users' settings files must still load), API key storage, the updater and
    signature verification, or a widely used type. Must be behavior-preserving, and scope has to be
    confirmed with the user first.
- **Effort** — `S` / `M` / `L`, stated but explicitly _not_ used as an argument against the design.

## Step 4 — Present, then decide scope

Report first. **Do not edit files in the reporting pass.** Show the prioritized, tiered list (most
structural first) with a clear recommendation on what you'd do and in what order.

Present the backlog and the new findings as **one** list, each with its ID. Mark carried-over items
(`R4 (deferred, run #2)`). A previously deferred finding whose blast radius has since shrunk —
because a dependency landed — is usually the best next move; say so when it applies.

Then:

- The user replies **"apply"**, **"apply 1 and 3"**, **"apply all"**, or refers to an earlier ID
  (**"apply R4"**) → do the work.
- For anything tagged **hot path / risk-sensitive**, use `AskUserQuestion` to confirm scope _before_
  the rework, and present the blast-radius trade-off honestly.

Never do a silent large refactor of shared or risk-sensitive code without surfacing its reach first.

## Step 5 — Apply: behavior-preserving and verified

Refactors must not change behavior. For each applied change:

1. **Work on a branch**, never directly on `main` (`AGENTS.md` → Releases: branch → PR → merge).
2. **Typing follows the model tiering in `AGENTS.md`**: you (Opus) write the plan/brief from
   `docs/grok-brief-template.md`; `grok-build -f <brief>` types it, falling back to an `Agent` with
   `model: "sonnet"`. UI layout changes are the exception (Opus itself). Review the diff yourself.
3. Keep the _observable_ output identical. When collapsing duplicates, compare the generated
   artifact (request JSON, prompt text, formatted error/German UI string, settings file) against the
   original — ideally in a test — don't eyeball it.
4. Build and test; this is the authoritative check, and the verbatim summary goes in the report:
   ```bash
   dotnet build src/WhisperShortcut
   dotnet test tests/WhisperShortcut.Tests
   ```
5. New shared helpers on non-trivial logic deserve a test in `tests/WhisperShortcut.Tests` — it
   doubles as the equivalence lock against the old copies. Moving logic from WinForms into Core is
   often what makes it testable at all.
6. **Do not start the app from this session** (it would run against a redirected AppData copy).
   Anything the user sees — tray menu, settings window, overlays, hotkeys, paste — needs a real check
   on the Windows PC: list exactly what to click and what should happen, and ask Magnus to run it.
7. Keep comments and naming at the density of the surrounding code. Document _why_ a seam exists,
   never what the line does. User-facing text stays German.
8. Commit only the files you touched. Never tag or release from this command — releases follow
   `AGENTS.md` once the PR is merged and verified.

## Step 6 — Report, then update the ledger

Summarize:

- what was unified or simplified, with before → after (files touched, copies removed, net line delta);
- how equivalence was verified (build/test summary, which tests lock it, what still needs a check on
  the real PC);
- what was deferred and why. Be explicit; a deferred finding that isn't named is a finding that's lost.

**Then write `docs/refactor-ledger.md` — every run, including runs where nothing was applied.**
Record:

- every finding from this run, with its ID and final status — `applied` (merge commit),
  `in PR #<n>`, `working tree`, `deferred`, or `rejected` (with the reason in the user's terms);
- status changes to entries from earlier runs — `deferred` → `applied`; moot → `superseded` naming
  the replacing ID; `in PR #<n>` → `applied` once `gh pr view <n> --json state` says `MERGED`;
- the areas swept this run, each with `git rev-parse --short HEAD` at sweep time;
- areas deliberately **not** swept yet, so the next run has an obvious starting point.

Keep entries one or two lines each. This is an index for future runs, not a second copy of the report.

### Ledger template (run #1 creates this)

```markdown
# Refactor Ledger

Running record for `/review-refactors`. Each run reads this before triaging and updates it at the
end. Long-lived: entries change status, they are not deleted.

## Findings

| ID  | Finding                                     | Blast radius | Status   | Run | Notes |
| --- | ------------------------------------------- | ------------ | -------- | --- | ----- |
| R1  | …                                           | hot path     | deferred | 1   |       |

## Swept areas

| Area              | Last swept | At commit | Notes |
| ----------------- | ---------- | --------- | ----- |
| `TrayApp.cs`      | run 1      | abc1234   |       |

## Not yet swept

- …
```

## Anti-patterns for this command

Do not propose:

- an abstraction for hypothetical future flexibility that unifies nothing that exists today;
- an interface or class whose only job is to forward to another type;
- splitting a clear linear method into many tiny helpers that each get one caller;
- renames or reformatting dressed up as refactoring;
- "fixing" a deliberate macOS-parity behaviour or one of the deliberate Windows differences listed in
  `docs/macos-referenz.md` §8.

And across runs, do not:

- re-triage from scratch and re-propose findings the ledger already records as `applied` or
  `rejected`;
- renumber IDs, or reuse the ID of a closed finding for something new;
- finish a run without writing the ledger back;
- keep re-sweeping the same top-of-the-churn-list files while whole areas stay untouched.

The test is always: **after this change, are there fewer places where the same decision has to be
made correctly?** If not, it isn't a refactor worth proposing here.
