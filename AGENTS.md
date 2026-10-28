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
- `grok-build` (on PATH, `~/.local/bin`) is sabaki's `scripts/grok-build.sh`: 8 runs a day across all
  repos, `grok-build --status` shows the count, log in `~/.cache/sabaki/grok-runs.tsv`.
- Briefs from `docs/grok-brief-template.md`. Review gate is the diff plus the verbatim
  `dotnet test` summary, not a glance.
