You are the build hand for one task in whisper-shortcut-windows (C# / .NET 10, WinForms).
Working directory: /Users/mgsgde/wt/win-shared-cases (git worktree, branch test/shared-cases). Stay inside it,
except reading the one source folder named below.

HARD RULES
- Never push, tag, or publish a release.
- Do not touch src/ (no app code, no UI, no German copy). Tests, test data, tools/ and docs only.
- Stage explicit paths only; never stash/reset.
- If the spec is wrong against the code: stop, commit nothing, explain.
- Commit message: conventional, English, ends with
  Co-Authored-By: Grok 4.7 via Cursor <noreply@cursor.com>

CONTEXT
Mac and Windows share test cases as JSON. The macOS app repo is the source of truth; Windows keeps a
copy so its tests run without the private Mac repo. Plan: docs/gemeinsame-testfaelle.md (read the
sections "Format", "Wo die Dateien liegen", "Ausführung"). Pilot contract: fidelity.unsupportedWords,
implemented here by DictationFidelityCheck.UnsupportedWords(string output, IEnumerable<string?> sources)
in src/WhisperShortcut.Core/DictationFidelityCheck.cs.
The Mac side already exists at
  /Users/mgsgde/wt/mac-shared-cases/WhisperShortcutTests/SharedCases/fidelity.unsupportedWords.json
(read it first to see the exact shape; "expect" keys: isEmpty, equals (exact list, same order),
contains, notContains; per case "windows": ok | offen | abweichend, optional "mac").
The normal location of the Mac source folder on this machine is
  /Users/mgsgde/whisper-shortcut-private/whisper-shortcut/WhisperShortcutTests/SharedCases

FILES
- new: tools/sync-cases.sh
- new: tests/shared-cases/fidelity.unsupportedWords.json (created by the script, not by hand)
- new: tests/WhisperShortcut.Tests/SharedCasesTests.cs
- edit: tests/WhisperShortcut.Tests/WhisperShortcut.Tests.csproj (copy tests/shared-cases/*.json to output)
- edit: tests/WhisperShortcut.Tests/TestSupport.cs (add the Mac SharedCases path constant next to SwiftDir)
DO NOT OPEN: src/WhisperShortcut/ (the WinForms app), bin/, obj/.

TASK
1. tools/sync-cases.sh [source-dir]: bash, set -euo pipefail, default source dir is the normal Mac
   location above. Copies every *.json from there to tests/shared-cases/ (creates the folder), deletes
   json files in tests/shared-cases/ that no longer exist in the source, prints what changed. Exits 1
   with a clear message if the source dir does not exist. chmod +x.
2. Run it once with the worktree path: tools/sync-cases.sh /Users/mgsgde/wt/mac-shared-cases/WhisperShortcutTests/SharedCases
3. csproj: <None Include="..\shared-cases\*.json" LinkBase="shared-cases" CopyToOutputDirectory="PreserveNewest" />
4. SharedCasesTests.cs (namespace WhisperShortcut.Tests, style like the other test files, System.Text.Json):
   - Load all cases of a contract from AppContext.BaseDirectory/shared-cases/<contract>.json.
   - A dispatcher: switch on contract name; "fidelity.unsupportedWords" calls
     DictationFidelityCheck.UnsupportedWords(input.output, input.sources). Unknown contract throws
     with "no dispatcher entry for <name>".
   - [Theory] with MemberData yielding one row per case (case id as the first argument so the test name
     shows it). Asserts every expect key present. If "windows" is "offen" or "abweichend", the case is
     skipped: xunit 2.9 has no dynamic skip, so instead return early and write the reason through
     ITestOutputHelper; do NOT add a new package.
   - [Fact] every json file in shared-cases/ has a dispatcher entry and unique case ids.
   - [Fact] copy is current: if the Mac source folder (constant from TestSupport.cs) exists, every
     *.json there is byte-identical to the file in tests/shared-cases/ (find the repo's tests/shared-cases
     by walking up from AppContext.BaseDirectory like Paths.TestAudio does), and there are no extra files.
     If the source folder does not exist, return early (the Mac repo is not on this machine).
     Message on failure: "shared cases out of date, run tools/sync-cases.sh".
   - [Fact] a summary that writes counts per contract (ok / offen / abweichend) to ITestOutputHelper.
5. Keep tests/WhisperShortcut.Tests/DictationFidelityCheckTests.cs unchanged.
6. In docs/gemeinsame-testfaelle.md change nothing except: under "## Schritte", at the end of step 1,
   append the sentence "Umgesetzt 31.10.2026 (Branch test/shared-cases in beiden Repos)."
7. Commit (include docs/gemeinsame-testfaelle.md, it is new on this branch):
   test: shared JSON cases with the macOS app, pilot fidelity check

TESTS:
  dotnet build tests/WhisperShortcut.Tests > /tmp/b.log 2>&1; echo "exit=$?"; grep -E " error |Error" /tmp/b.log | head -30
  dotnet test tests/WhisperShortcut.Tests > /tmp/t.log 2>&1; echo "exit=$?"; grep -E "Passed!|Failed!|Failed |error" /tmp/t.log | head -40
  dotnet test tests/WhisperShortcut.Tests --filter "FullyQualifiedName~SharedCases" -v n > /tmp/s.log 2>&1; echo "exit=$?"; grep -cE "Passed .*SharedCases" /tmp/s.log
Note: the copy-is-current test will skip here, because the Mac main checkout does not yet contain the
folder. Also prove it bites: temporarily run it against the worktree by setting the constant locally,
change one char in the copy, see it fail, revert both, and report that you did.

FINAL OUTPUT: files changed, commit sha, the summary lines of each test command verbatim.
