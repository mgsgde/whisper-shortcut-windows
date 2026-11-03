You are the build hand for one task in the WhisperShortcut macOS app (Swift, Xcode, Swift Testing).
Working directory: /Users/mgsgde/wt/mac-shared-cases (git worktree, branch test/shared-cases). Stay inside it.

HARD RULES
- Never push, tag, or publish a release.
- Do not touch app code (WhisperShortcut/), UI, or user-facing copy. Tests and test data only.
- Do not run scripts/rebuild-and-restart.sh (it relaunches the user's app). Use the test command below.
- Do not edit WhisperShortcut.xcodeproj/project.pbxproj. The test folder is a file-system synchronized
  group, new files in WhisperShortcutTests/ are picked up automatically.
- Stage explicit paths only; never stash/reset.
- If the spec is wrong against the code: stop, commit nothing, explain.
- Commit message: conventional, English, ends with
  Co-Authored-By: Grok 4.7 via Cursor <noreply@cursor.com>

CONTEXT
Mac and Windows apps will share test cases as JSON data. This is the pilot for one function:
DictationFidelityCheck.unsupportedWords(in: String, sources: [String]) -> [String]
(WhisperShortcut/DictationFidelityCheck.swift). The existing tests are in
WhisperShortcutTests/DictationFidelityCheckTests.swift (13 @Test). The Windows app (C#) will read the
same JSON file later.

FILES
- new: WhisperShortcutTests/SharedCases/fidelity.unsupportedWords.json
- new: WhisperShortcutTests/SharedCasesTests.swift
- read only: WhisperShortcutTests/DictationFidelityCheckTests.swift, WhisperShortcut/DictationFidelityCheck.swift
- maybe: WhisperShortcut-Hermetic.xctestplan (only if the new suite would otherwise not run in --hermetic)
DO NOT OPEN: build/, DerivedData, anything outside WhisperShortcutTests/ and the files above.

TASK
1. Create the JSON file with exactly this shape (2-space indent, UTF-8, real umlauts, no \u escapes):
   {
     "contract": "fidelity.unsupportedWords",
     "cases": [
       {
         "id": "faithful-reorder-with-headings",
         "note": "short English sentence what the case pins",
         "input": { "output": "...", "sources": ["..."] },
         "expect": { ... },
         "windows": "ok"
       }
     ]
   }
   "expect" uses exactly one or two of these keys:
     "isEmpty": true            -> result must be empty
     "equals": ["a","b"]        -> result must equal this list exactly, same order
     "contains": ["a"]          -> every listed word is in the result
     "notContains": ["b"]       -> no listed word is in the result
   ("contains" and "notContains" may appear together.) "windows" is always "ok" for now.
   Convert EVERY #expect in DictationFidelityCheckTests.swift into cases, one case per call of
   check(...)/unsupportedWords with its own stable kebab-case id. Copy strings verbatim (the shared
   transcript constant gets inlined into each case that uses it). Keep the Swift file's intent: where it
   loops over words to expect contained / not contained, use contains / notContains.
   Do not delete or change DictationFidelityCheckTests.swift.
2. Create SharedCasesTests.swift:
   - Decodable structs for the file (contract, cases, id, note, input as [String: JSONValue] or a
     small enum that handles string / string array / number / bool, expect, windows, optional mac).
   - Load the file: first via Bundle(for: SharedCasesBundleToken.self) (a private final class token)
     url(forResource: "fidelity.unsupportedWords", withExtension: "json") searching also subdirectory
     "SharedCases"; if not found, fall back to URL(fileURLWithPath: #filePath) sibling
     "SharedCases/<name>.json". Fail with a clear message if neither exists.
   - A dispatcher `func run(contract: String, input: ...) -> SharedResult` with a switch on the contract
     name; "fidelity.unsupportedWords" calls DictationFidelityCheck.unsupportedWords(in:sources:).
     Unknown contract -> Issue.record("no dispatcher entry for <name>").
   - One parameterized @Test(arguments:) over the cases, display name including the case id, asserting
     each expect key. Skip (return early with a comment) only if the case has "mac": "offen".
   - Also a @Test that every case id in the file is unique.
   - Match the style of the existing test files (@Suite, @testable import WhisperShortcut_AppStore,
     two-space indent).
3. Check whether WhisperShortcut-Hermetic.xctestplan would exclude the new suite. Only if so, add it.
4. Commit: test: shared JSON cases for the dictation fidelity check

TESTS (exit code via file, never pipe the exit code):
  bash scripts/run-tests.sh --hermetic > /tmp/mac-t.log 2>&1; echo "exit=$?"
  grep -E "SharedCases|Shared cases|Test run with|passed|failed|error:" /tmp/mac-t.log | tail -40
Both the new suite and DictationFidelityCheckTests must pass. If an unrelated test fails, report it
verbatim and do not try to fix it.

FINAL OUTPUT: files changed, number of cases in the JSON, commit sha, the test summary lines verbatim.
