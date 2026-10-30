# AGENTS.md

Guidance for AI coding agents (Claude Code, Cursor, …) in this repo. `CLAUDE.md` is a symlink to it.

WhisperShortcut for Windows: C# / .NET 10, WinForms, tray app. `src/WhisperShortcut` (app),
`src/WhisperShortcut.Core` (logic), `tests/WhisperShortcut.Tests`. Feature parity with the macOS app:
`docs/macos-paritaet.md`. User-facing text is German.

## Build and test (on the Mac)

```bash
dotnet test tests/WhisperShortcut.Tests
dotnet publish src/WhisperShortcut -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

Real-Windows checks run in the Parallels VM through `tools/vm.sh` (deploy, start, hotkeys,
screenshots, UI Automation, log). Test plans and results: `tools/e2e/`.

## Releases: the agent owns them

Magnus, 2026-10-28: „dass du hier dafür Sorge trägst zentral, dass alles deployed wird bezüglich
Windows“. Whoever works in this repo makes sure every finished change reaches a GitHub release; Magnus
does not have to ask for it. No separate „Soll ich releasen?“: merged and verified means released.

1. Branch → PR → merge to `main` (tests green, VM check for anything users see).
2. Bump `<Version>` in `src/WhisperShortcut/WhisperShortcut.csproj` (semver; fixes = patch), commit
   „Version x.y.z“ on `main`, write German release notes (what changes for the user, short).
3. `tools/release.sh x.y.z <notes.md> --publish` from a clean `main`: tests, both exes and ZIPs (x64, arm64),
   ECDSA signatures with `~/.config/whispershortcut-windows-update/ecdsa-p256-private.pem`. Never
   publish a ZIP without its `.sig`; never lose or commit that key.
4. Verify: `gh release view vx.y.z` lists both exes, both ZIPs, all four `.sig`, `ANLEITUNG.txt` (the exe is the
   download, the ZIP is what installed updaters fetch); in the VM the
   previous version finds and installs the update (`tools/vm.sh`).
5. Report the release link to Magnus. Testers (e.g. Christoph) get it through the in-app update.

## Model tiering: Opus 5.5 thinks, cheap hands type

Same split as sabaki.dance and the macOS app; the full wording and the daily cap live in
`~/.claude/CLAUDE.md`. Magnus, 2026-10-02: „Opus 5.5 fürs Planen und fürs Evaluieren und für die UI …
stupide Tätigkeiten wie Typing: Grok oder Sonnet“.

| Job | Who |
|---|---|
| Plan, spec, review, judge, talk to Magnus | Opus 5.5 session |
| UI: settings window layout, tray menu, copy, states | Opus 5.5 session itself, checked with `tools/vm.sh shot` |
| Typing from a finished plan (C#, tests, scripts, csproj) | 1. Grok 4.7: `grok-build -f <brief>` · 2. exit ≠ 0 or 75 (cap): `Agent` with `model: "sonnet"`, same brief, no asking |

- **Enforced:** the user-level hook `~/.claude/hooks/typing-guard.py` refuses Edit/Write/sed/heredoc
  on code files in this repo from the main session; subagents pass. For UI work or Magnus's
  „mach das selbst“ the deny message names the 3-hour override; say in the report that it was used.
- `grok-build` (on PATH, `~/.local/bin`) is sabaki's `scripts/grok-build.sh`: 16 runs a day (40 until 2026-11-02) across all
  repos, `grok-build --status` shows the count, log in `~/.cache/sabaki/grok-runs.tsv`.
- Briefs from `docs/grok-brief-template.md`. Review gate is the diff plus the verbatim
  `dotnet test` summary, not a glance.
