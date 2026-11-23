# AGENTS.md

Guidance for AI coding agents (Claude Code, Cursor, …) in this repo. `CLAUDE.md` is a symlink to it.

WhisperShortcut for Windows: C# / .NET 10, WinForms, tray app. `src/WhisperShortcut` (app),
`src/WhisperShortcut.Core` (logic), `tests/WhisperShortcut.Tests`. User-facing text is German.

## The macOS app is the reference

Windows rebuilds the macOS app, so know the original before building „wie auf dem Mac“:

- **How the Mac app is built and behaves** (menu, shortcuts, flows, messages, every settings tab, with
  screenshots): `docs/macos-referenz.md`. **What Windows still lacks:** `docs/macos-paritaet.md`.
- **The Mac source is the truth**, not the docs: `~/whisper-shortcut-private/whisper-shortcut/WhisperShortcut/`
  (public: github.com/mgsgde/whisper-shortcut). Before porting a feature, read the Swift file the reference
  names and match its behaviour, defaults and wording (German strings: `Localizable.xcstrings`). Never
  change the Mac repo from here.
- Deliberate Windows differences (German UI, Strg/Alt keys, tray, the places Windows is better) are listed
  in `docs/macos-referenz.md` §8; keep them.

## Build and test (on the Mac)

```bash
dotnet test tests/WhisperShortcut.Tests
dotnet publish src/WhisperShortcut -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

Real-Windows checks run on Magnus's Windows PC, by a Claude Code session running there (build,
start, hotkeys, PowerShell screenshots, UI Automation via `tools/uia.ps1` / `tools/click.ps1`, log).
The Parallels VM and `tools/vm.sh` are gone (subscription ended, 2026-11-02). Test plans and
results: `tools/e2e/` (older results were taken in that VM).

**Release gates since 0.7.13** (shipped with all tests green, yet Gemini dictation gave one word and every
Parakeet download failed):

- CI runs the real Silero model on real speech (`SileroRealSpeechTests`, WAVs from `tools/e2e/make-speech-wav.ps1`)
  and, as `Dictation smoke (Windows)`, the published exe end to end (`tools/e2e/dictation-smoke.ps1`).
  The release workflow refuses a commit whose CI (push run, smoke check included) is not green. The Gemini key for it is the repository
  secret `GEMINI_API_KEY` (`gh secret set GEMINI_API_KEY`); never put a key in the repo.
- `Offline catalog upstream` (daily) compares every `OfflineCatalog` file with its host. Pin new URLs to a
  revision or release tag, never `/resolve/main/`.
- Manual tests on the Windows PC: dictate only after `SPEECH-GATE: Silero VAD ready` is in the log, and test once
  with the offline folder deleted (details: `tools/e2e/CASES.md` → Conventions).

## Merging and releasing are automatic

Same pipeline as the macOS app (`mgsgde/whisper-shortcut`, AGENTS.md „Merging and releasing are automatic“,
owner ruling 2026-11-20), here as GitHub Actions so nothing depends on the PC being on. Magnus, 2026-11-23: the
Mac flow in full for Windows. **No session merges or releases by hand**, and there is no release session to hand
over to. Your job ends with a green PR. (Replaces the 2026-10-28 rule „the agent owns releases“: the pipeline
owns them now.)

1. **You:** branch, commit, push, open the PR (German title and body; say what you tested, with the verbatim
   `dotnet test` summary). Anything users see: label `needs-device-test` until it was checked on the Windows PC.
   Needs Magnus's word first (user-visible behaviour he has not seen, a changed default): label `hold` with his
   open question in the body. Must not merge for another reason: `hold` or a draft. No stacked PRs.
2. **Merge train** (`.github/workflows/merge-train.yml`, every 20 min): merges every green, conflict-free,
   non-draft PR of the owner without `hold` / `needs-device-test` / `merge-train-failed`, as a merge commit,
   branches not updated first. While CI on `main` is red it merges nothing and opens one issue that mentions
   Magnus. A red or conflicting PR gets `merge-train-failed` plus a comment; fix it and remove the label. PRs that
   change `.github/workflows/` are never merged by the train (the Actions token may not): Magnus merges those.
3. **Nightly release** (`.github/workflows/release-nightly.yml`, 02:17): if `main` changed since the newest tag
   and its CI is green (unit, UI, real Silero audio, and the dictation smoke check through the published exe),
   it writes German notes into `.github/RELEASE_NOTES.md` (Gemini, from the commits), bumps the patch version
   in the csproj, commits „Version x.y.z“ on `main`, tags it, runs CI on the tag and calls `release.yml`, which
   publishes it as a **prerelease = channel Latest**.
4. **Stable on Tuesday** (same workflow, 08:00): the newest prerelease published at least 6 days ago whose CI
   was green becomes Stable (prerelease flag cleared, `releases/latest`). The app's update channel (Settings →
   Updates → Update-Kanal) defaults to Stable; copies older than that setting read `releases/latest` = Stable too.
   No downgrade, no deleted assets: roll back by shipping the good code with a higher version.
5. **Monday list** (`merge-train.yml`, Mondays 08:30): PRs open 3 days or more go into one issue to Magnus with
   their reason; after 14 days they are closed, the branch is kept.

Notifications are GitHub issues that mention `@mgsgde` (GitHub mails them), not a mail script.

**Dev instance on Magnus's PC** (the Mac's „WhisperShortcut Dev“ and `dev-daily.sh`): `tools/dev/README.md`. A Task
Scheduler task installs every green `main` build as a separate „WhisperShortcut Dev“ (own data, shared offline models,
deleting them is locked in Dev). Magnus sets the task up himself; never start either app from Claude.

### Stop any of it

| What | Hold | Resume |
|---|---|---|
| Merge train | `gh variable set MERGE_TRAIN_HOLD --body "Grund"` | `gh variable delete MERGE_TRAIN_HOLD` |
| Nightly release | `gh variable set RELEASE_HOLD --body "Grund"` | `gh variable delete RELEASE_HOLD` |
| Stable promotion | `gh variable set STABLE_HOLD --body "Grund"` | `gh variable delete STABLE_HOLD` |
| Keep new releases drafts | `gh variable set RELEASE_AS_DRAFT --body true` | `gh variable delete RELEASE_AS_DRAFT` |
| A whole workflow | `gh workflow disable merge-train.yml` / `release-nightly.yml` | `gh workflow enable …` |
| One PR | label `hold` or `needs-device-test`, or a draft | remove it |

### Release mechanics (`release.yml`)

- Starts from a `v*` tag push, from `release-nightly.yml` (workflow_call), or by hand:
  `gh workflow run release.yml -f tag=vx.y.z` (re-drive a failed run; `draft=true` keeps it a draft,
  `stable=true` publishes straight to Stable). It never creates or moves a tag.
- Gate: the tag exists, csproj `<Version>` = tag, notes not empty, and the whole `CI` workflow green on the tagged
  commit in a push or dispatch run (a PR run may skip the smoke check without its secret). Commits pushed by
  Actions start no push run, so the train and the nightly job dispatch CI themselves (`ci.yml` has
  `workflow_dispatch`).
- Builds both exes and ZIPs (x64, arm64), signs them and checks every signature against the public key in
  `UpdateSignature.cs`, uploads everything to a draft, downloads it back and verifies it, and only then publishes.
  A published release's assets are never replaced.
- **Signing key:** secret `UPDATE_SIGNING_KEY` in the GitHub environment `release`, which GitHub hands only to
  runs on `main` or a `v*` tag; pull-request runs never see it. It is the PEM of
  `~/.config/whispershortcut-windows-update/ecdsa-p256-private.pem` (the PC keeps the original as backup). Never
  commit or print it; losing it means installed copies can never auto-update again.
- **Dry run:** a PR touching `release.yml`, `tools/release.sh`, `dist/ANLEITUNG.txt` or `UpdateSignature.cs` runs
  the whole release against a throwaway draft (throwaway key, no tag) and deletes it; a PR touching
  `release-nightly.yml` or `tools/automation/release-nightly.sh` runs prepare and promote dry (real Gemini notes,
  nothing pushed).

### Hotfix

- Now, from `main`: merge the fix (Magnus merges by hand if the train is held), then
  `gh workflow run release-nightly.yml -f action=release -f force=true`; to Stable at once afterwards:
  `gh workflow run release-nightly.yml -f action=promote -f tag=vx.y.z`. CI must still be green; there is no
  „skip tests“.
- Off an older version: branch `release/x.y.z` from the old tag, bump `<Version>`, replace
  `.github/RELEASE_NOTES.md`, push (CI runs on pushes to `release/**`), then `tools/release.sh` there: it checks
  and tags HEAD; `release.yml` publishes to Latest. Only a higher version ever becomes Stable.
- **Abort:** cancel the run (`gh run cancel <id>`) and check `gh release view vx.y.z`: publishing is the last step,
  so a cancelled run leaves at most a draft. Say „not published“ only after that check.

## Model tiering: Opus 5.5 thinks, cheap hands type

Same split as sabaki.dance and the macOS app; the full wording and the daily cap live in
`~/.claude/CLAUDE.md`. Magnus, 2026-10-02: „Opus 5.5 fürs Planen und fürs Evaluieren und für die UI …
stupide Tätigkeiten wie Typing: Grok oder Sonnet“.

- **Fable 5.2 only for the hard thinking, on Magnus's word or an accepted proposal** (2026-11-13, replaces „no Fable"): „entweder ich sage Opus 5.5 … für das und das möchte ich, dass du Fable 5.2 nutzt, weil es wichtig ist, oder Opus 5.5 soll mir den Vorschlag machen und ich akzeptiere". Opus proposes in one line and waits for the yes (important UI component or screen, hard-to-reverse decision, strategy, a problem it failed at twice; never tweaks), then one `Agent` subagent with `model: "fable"` as consultant on a full brief; Opus builds. Never switch a running session to Fable. Full wording: `~/.claude/CLAUDE.md` → Models.

| Job | Who |
|---|---|
| Plan, spec, review, judge, talk to Magnus | Opus 5.5 session |
| UI: settings window layout, tray menu, copy, states | Opus 5.5 session itself, checked with screenshots in a Claude Code session on the Windows PC |
| Typing from a finished plan (C#, tests, scripts, csproj) | 1. Grok 4.7: `grok-build -f <brief>` · 2. exit ≠ 0 or 75 (cap): `Agent` with `model: "sonnet"`, same brief, no asking |

- **Enforced:** the user-level hook `~/.claude/hooks/typing-guard.py` refuses Edit/Write/sed/heredoc
  on code files in this repo from the main session; subagents pass. For UI work or Magnus's
  „mach das selbst“ the deny message names the 3-hour override; say in the report that it was used.
- `grok-build` (on PATH, `~/.local/bin`) is sabaki's `scripts/grok-build.sh`: 22 runs a day across all
  repos, `grok-build --status` shows the count, log in `~/.cache/sabaki/grok-runs.tsv`.
- Briefs from `docs/grok-brief-template.md`. Review gate is the diff plus the verbatim
  `dotnet test` summary, not a glance.
