# Grok brief template (whisper-shortcut-windows)

Adapted from sabaki's `docs/grok-brief-template.md`; the cost rules there hold here too: one bounded
task per brief, trivial edits bundled, runs under ~15 min, after a wrong run a sharper brief instead
of the same one. Run from the repo or worktree with `grok-build -f <brief-file>`. Exit 75 means the
daily cap is reached: give the same brief to an `Agent` subagent with `model: "sonnet"`.

Do not tell Grok to read `AGENTS.md`; cursor-agent loads it already. Quote the one rule that matters.

**Skeleton:**

```
You are the build hand for one task in whisper-shortcut-windows (C# / .NET 10, WinForms).
Working directory: <repo or worktree path> (branch <branch>). Stay inside it.

HARD RULES
- Never push, tag, or publish a release.
- Do not touch UI layout or user-facing German copy unless the task lists it.
- Stage explicit paths only; never stash/reset.
- If the spec is wrong against the code: stop, commit nothing, explain.
- Commit message: conventional, English, ends with
  Co-Authored-By: Grok 4.7 via Cursor <noreply@cursor.com>

FILES: <exact paths>          DO NOT OPEN: <paths>
TASK: <numbered steps>
TESTS:
  dotnet build src/WhisperShortcut > /tmp/b.log 2>&1; echo "exit=$?"; grep -E " error |Warn|Error" /tmp/b.log | head -30
  dotnet test tests/WhisperShortcut.Tests > /tmp/t.log 2>&1; echo "exit=$?"; grep -E "Passed!|Failed!|Failed |error" /tmp/t.log | head -40
FINAL OUTPUT: files changed, commit sha, the summary lines of each test command verbatim.
```

For a change that needs real Windows, the brief adds the `tools/vm.sh` steps (`deploy`, `start`,
`combo`, `shot`, `log`) and asks for the log lines verbatim; Opus looks at the screenshots.
