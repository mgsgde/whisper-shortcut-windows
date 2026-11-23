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
  `tools/release.sh` refuses a commit whose smoke check is not green. The Gemini key for it is the repository
  secret `GEMINI_API_KEY` (`gh secret set GEMINI_API_KEY`); never put a key in the repo.
- `Offline catalog upstream` (daily) compares every `OfflineCatalog` file with its host. Pin new URLs to a
  revision or release tag, never `/resolve/main/`.
- Manual tests on the Windows PC: dictate only after `SPEECH-GATE: Silero VAD ready` is in the log, and test once
  with the offline folder deleted (details: `tools/e2e/CASES.md` → Conventions).

## Releases: the agent owns them

Magnus, 2026-10-28: „dass du hier dafür Sorge trägst zentral, dass alles deployed wird bezüglich
Windows“. Whoever works in this repo makes sure every finished change reaches a GitHub release; Magnus
does not have to ask for it. No separate „Soll ich releasen?“: merged and verified means released.

Releases run in CI, the same way as the macOS app (`mgsgde/whisper-shortcut`, `release.yml`): a pushed
`v*` tag is the release. Since 2026-11-23, after an evening where a tag landed on the default branch, a session
published although it said it had aborted, and an unreviewed 0.7.14 had to be deleted.

1. Branch → PR → merge to `main` (tests green, check on the Windows PC for anything users see).
2. Bump `<Version>` in `src/WhisperShortcut/WhisperShortcut.csproj` (semver; fixes = patch) and replace
   `.github/RELEASE_NOTES.md` with the German notes (what changes for the user, short); commit both as
   „Version x.y.z“, push, wait for CI green on that commit. Coordinate with other sessions first: one release
   at a time, and say in their notes folder which version you are cutting.
3. `tools/release.sh` (Git Bash; asks before tagging, `--yes` skips): refuses a dirty tree, an unpushed HEAD, an
   existing tag, unchanged notes or a CI that is not green on HEAD; then tags HEAD (annotated, explicit commit)
   and pushes the tag. Nothing is built or signed on the PC.
4. `.github/workflows/release.yml` takes over: waits for the whole `CI` workflow green on the tagged commit in a
   push run (a PR run may skip the smoke check without its secret), so every gate in `ci.yml`, including the dictation smoke check, blocks a release; builds both exes and
   ZIPs (x64, arm64), signs them with the repository secret `UPDATE_SIGNING_KEY` and checks every signature
   against the public key in `UpdateSignature.cs`, uploads to a draft, downloads the assets back and verifies
   them, and only then publishes. Only a higher version becomes Latest (what `UpdateCheck` reads), so a
   hotfix on an older line never hijacks the update feed. A published release's assets are never replaced:
   ship a higher version.
5. Verify: `gh run watch` on the release run until green; `gh release view vx.y.z` lists both exes, both ZIPs,
   all four `.sig`, `ANLEITUNG.txt`; on the Windows PC the previous version finds and installs the update.
6. Report the release link to Magnus. Testers (e.g. Christoph) get it through the in-app update.

- **Draft first** (to install it on the PC before users get it): `gh variable set RELEASE_AS_DRAFT --body true`
  before pushing the tag, or dispatch the workflow with `draft=true`; publish with
  `gh release edit vx.y.z --draft=false --latest`, then `gh variable delete RELEASE_AS_DRAFT`.
- **Re-drive a tag** whose run failed (flaky CI re-run, outage): Actions → Release → Run workflow,
  `tag=vx.y.z` (or `gh workflow run release.yml -f tag=vx.y.z`). An existing draft gets its assets replaced.
- **Hotfix off an older version:** branch `release/x.y.z` from the old tag, bump, notes, push (CI runs on
  pushes to `release/**`), then `tools/release.sh` on that branch. The tag points at that branch's commit, never at `main`.
- **Abort:** before the tag is pushed nothing has happened. After it, cancel the run
  (`gh run cancel <id>`) and check `gh release view vx.y.z`: the workflow publishes only in its last step, so a
  cancelled run leaves at most a draft. Say "not published" only after that check.
- **Dry run:** every PR touching `release.yml`, `tools/release.sh`, `dist/ANLEITUNG.txt` or `UpdateSignature.cs`
  runs the whole pipeline against a throwaway draft (`dryrun-<version>-<run>`, no tag) and deletes it.
- **Signing key:** `UPDATE_SIGNING_KEY` holds the PEM of `~/.config/whispershortcut-windows-update/ecdsa-p256-private.pem`
  (the PC keeps the original as backup). Never commit it, never print it; losing it means installed copies can
  never auto-update again. Not ported from the Mac: nightly auto-release and the Latest/Stable split (the Windows
  updater reads only `releases/latest`).

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
