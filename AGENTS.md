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

## Releases: the agent owns them

Magnus, 2026-10-28: „dass du hier dafür Sorge trägst zentral, dass alles deployed wird bezüglich
Windows“. Whoever works in this repo makes sure every finished change reaches a GitHub release; Magnus
does not have to ask for it. No separate „Soll ich releasen?“: merged and verified means released.

1. Branch → PR → merge to `main` (tests green, check on the Windows PC for anything users see).
2. Bump `<Version>` in `src/WhisperShortcut/WhisperShortcut.csproj` (semver; fixes = patch), commit
   „Version x.y.z“ on `main`, write German release notes (what changes for the user, short).
3. `tools/release.sh x.y.z <notes.md> --publish` from a clean `main`: tests, both exes and ZIPs (x64, arm64),
   ECDSA signatures with `~/.config/whispershortcut-windows-update/ecdsa-p256-private.pem`. Never
   publish a ZIP without its `.sig`; never lose or commit that key.
4. Verify: `gh release view vx.y.z` lists both exes, both ZIPs, all four `.sig`, `ANLEITUNG.txt` (the exe is the
   download, the ZIP is what installed updaters fetch); on the Windows PC the
   previous version finds and installs the update.
5. Report the release link to Magnus. Testers (e.g. Christoph) get it through the in-app update.

## Model tiering: Opus 5.5 thinks, cheap hands type

Same split as sabaki.dance and the macOS app; the full wording and the daily cap live in
`~/.claude/CLAUDE.md`. Magnus, 2026-10-02: „Opus 5.5 fürs Planen und fürs Evaluieren und für die UI …
stupide Tätigkeiten wie Typing: Grok oder Sonnet“.

| Job | Who |
|---|---|
| Plan, spec, review, judge, talk to Magnus | Opus 5.5 session |
| UI: settings window layout, tray menu, copy, states | Opus 5.5 session itself, checked with screenshots in a Claude Code session on the Windows PC |
| Typing from a finished plan (C#, tests, scripts, csproj) | 1. Grok 4.7: `grok-build -f <brief>` · 2. exit ≠ 0 or 75 (cap): `Agent` with `model: "sonnet"`, same brief, no asking |

- **Enforced:** the user-level hook `~/.claude/hooks/typing-guard.py` refuses Edit/Write/sed/heredoc
  on code files in this repo from the main session; subagents pass. For UI work or Magnus's
  „mach das selbst“ the deny message names the 3-hour override; say in the report that it was used.
- `grok-build` (on PATH, `~/.local/bin`) is sabaki's `scripts/grok-build.sh`: 16 runs a day (40 until 2026-11-02) across all
  repos, `grok-build --status` shows the count, log in `~/.cache/sabaki/grok-runs.tsv`.
- Briefs from `docs/grok-brief-template.md`. Review gate is the diff plus the verbatim
  `dotnet test` summary, not a glance.
