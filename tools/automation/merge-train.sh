#!/usr/bin/env bash
# Merge train, ported from the macOS repo (mgsgde/whisper-shortcut, scripts/merge-train/train.sh).
# Runs in GitHub Actions every 20 minutes (.github/workflows/merge-train.yml), not on a PC.
#
# Each tick:
#   1. Hold: repo variable MERGE_TRAIN_HOLD non-empty -> nothing happens (its text is the reason).
#   2. Brake: the newest finished CI run on main (push or dispatch; cancelled/skipped ignored) must be green.
#      Red -> merge nothing, open ONE issue per red commit that mentions the owner (GitHub mails him).
#      Green again -> that issue is closed.
#   3. Every open PR against main, lowest number first. It merges (merge commit, --match-head-commit, branch
#      deleted) every PR that is: by the repo owner or Dependabot (others only with label merge-train), not a
#      draft, without hold / needs-device-test / merge-train-failed, conflict-free, and with every check green.
#      Branches are never updated first. No per-tick limit.
#   4. A PR that is red, conflicting, or changes .github/workflows/ (the Actions token may not merge those)
#      gets label merge-train-failed and one comment with the reason; the train ignores it until the label is
#      removed. Pending, missing or only-cancelled checks: retried next tick. Only exactly "green" merges.
#   5. After a merge it dispatches CI on main: merges made with GITHUB_TOKEN start no push run.
#
# Env: GH_TOKEN, REPO (owner/name); DRY_RUN=true decides and prints but changes nothing; MERGE_TRAIN_HOLD.
# Needs only gh (its --jq), no separate jq.
set -uo pipefail

REPO="${REPO:?REPO=owner/name}"
OWNER="${REPO%%/*}"
DRY="${DRY_RUN:-false}"
FAIL_PREFIX="Der Merge-Zug hat bei diesem PR angehalten:"

act() { if [ "$DRY" = true ]; then echo "  DRY: $*"; else "$@"; fi; }

if [ -n "${MERGE_TRAIN_HOLD:-}" ]; then
  echo "::notice::Merge train on hold (repo variable MERGE_TRAIN_HOLD): $MERGE_TRAIN_HOLD"
  exit 0
fi

if [ "$DRY" != true ]; then
  while IFS='|' read -r name color desc; do
    gh label create "$name" -R "$REPO" --color "$color" --description "$desc" >/dev/null 2>&1 || true
  done <<'EOF'
hold|b60205|Merge-Zug: nicht mergen, wartet auf Magnus
needs-device-test|fbca04|Merge-Zug: wartet auf einen Test am Windows-PC
merge-train|0e8a16|Merge-Zug: auch diesen PR mergen (fremder Autor)
merge-train-failed|d93f0b|Merge-Zug hat angehalten; Grund im Kommentar, Label entfernen zum Wiederholen
merge-train-paused|5319e7|Merge-Zug pausiert, weil main rot ist
EOF
fi

# --- 2. Brake: main must be green -------------------------------------------------------------------------
read -r concl sha url < <(gh api "repos/$REPO/actions/workflows/ci.yml/runs?branch=main&status=completed&per_page=30" \
  --jq '[.workflow_runs[] | select((.event == "push" or .event == "workflow_dispatch")
          and (.conclusion == "success" or .conclusion == "failure" or .conclusion == "timed_out"))][0]
        | "\(.conclusion) \(.head_sha) \(.html_url)"' 2>/dev/null || echo "unknown - -")
echo "main CI: $concl ${sha:0:8} $url"
paused=$(gh issue list -R "$REPO" --label merge-train-paused --state open --json number,title --jq '.[] | "\(.number) \(.title)"' 2>/dev/null || true)
if [ "$concl" = failure ] || [ "$concl" = timed_out ]; then
  if grep -q "${sha:0:8}" <<<"$paused"; then
    echo "main is red at ${sha:0:8}; owner already told. Nothing merged."
    exit 0
  fi
  green=$(gh api "repos/$REPO/actions/workflows/ci.yml/runs?branch=main&status=success&per_page=1" --jq '.workflow_runs[0].head_sha' 2>/dev/null || true)
  commits=""
  [ -n "$green" ] && commits=$(gh api "repos/$REPO/compare/$green...$sha" \
    --jq '.commits[] | "- \(.sha[0:7]) \(.commit.message | split("\n")[0])"' 2>/dev/null || true)
  body="@$OWNER CI auf \`main\` ist rot bei \`${sha:0:8}\`: $url

Commits seit dem letzten grünen Lauf (\`${green:0:8}\`):
${commits:-(nicht ermittelbar)}

Der Merge-Zug merged nichts, bis \`main\` wieder grün ist (flaky Job: im Lauf „Re-run failed jobs“). Dieses Issue schließt er dann selbst."
  while read -r n _; do [ -n "$n" ] && act gh issue close "$n" -R "$REPO" --comment "Abgelöst: main ist jetzt bei ${sha:0:8} rot."; done <<<"$paused"
  act gh issue create -R "$REPO" --label merge-train-paused --title "Merge-Zug pausiert: main rot bei ${sha:0:8}" --body "$body"
  echo "main is red at ${sha:0:8}. Nothing merged."
  exit 0
fi
if [ "$concl" = success ]; then
  while read -r n _; do [ -n "$n" ] && act gh issue close "$n" -R "$REPO" --comment "main ist wieder grün (${sha:0:8}); der Merge-Zug fährt weiter."; done <<<"$paused"
fi

# --- 3. Pull requests ---------------------------------------------------------------------------------------
fail_pr() {  # number reason
  echo "  #$1 FAILED: $2"
  act gh pr edit "$1" -R "$REPO" --add-label merge-train-failed >/dev/null
  act gh pr comment "$1" -R "$REPO" --body "$FAIL_PREFIX $2

Bitte beheben und das Label \`merge-train-failed\` entfernen; dann versucht er es beim nächsten Lauf (alle 20 min) wieder."
}

merged=0
# '|' and not a tab: read collapses consecutive tabs, so an empty label list would shift the fields.
prs=$(gh pr list -R "$REPO" --base main --state open --limit 100 \
  --json number,isDraft,author,labels,headRefName \
  --jq 'sort_by(.number)[] | "\(.number)|\(.isDraft)|\(.author.login)|\([.labels[].name] | join(","))|\(.headRefName)"')
while IFS='|' read -r n draft author labels branch; do
  [ -n "$n" ] || continue
  echo "#$n by $author on $branch [$labels]"
  [ "$draft" = true ] && { echo "  skip: draft"; continue; }
  case ",$labels," in *,hold,*|*,needs-device-test,*|*,merge-train-failed,*) echo "  skip: label"; continue ;; esac
  trusted=false
  case "$author" in "$OWNER"|app/dependabot|dependabot\[bot\]) trusted=true ;; esac
  case "$branch" in implementer/*) trusted=false ;; esac
  case ",$labels," in *,merge-train,*) trusted=true ;; esac
  [ "$trusted" = true ] || { echo "  skip: author $author without label merge-train"; continue; }

  # One line: mergeable|head|checks|workflow files|url. Checks: red = a failed check; pending = unfinished,
  # none at all, or only cancelled ones (concurrency cancels, runner outages); otherwise green.
  info=$(gh pr view "$n" -R "$REPO" --json mergeable,headRefOid,statusCheckRollup,files,url --jq '
    [.statusCheckRollup[] | (.conclusion // .state // "") | ascii_upcase] as $c
    | [.statusCheckRollup[] | select((.status // "COMPLETED") != "COMPLETED" or ((.state // "") | test("PENDING|EXPECTED")))] as $p
    | (if ($c | length) == 0 then "pending"
       elif any($c[]; test("^(FAILURE|TIMED_OUT|ACTION_REQUIRED|STARTUP_FAILURE|ERROR)$")) then "red"
       elif ($p | length) > 0 then "pending"
       elif any($c[]; . == "CANCELLED") then "pending"
       else "green" end) as $checks
    | "\(.mergeable)|\(.headRefOid)|\($checks)|\([.files[].path | select(startswith(".github/workflows/"))] | join(" "))|\(.url)"') \
    || { echo "  skip: could not read"; continue; }
  IFS='|' read -r mergeable head checks workflows url <<<"$info"
  echo "  mergeable=$mergeable checks=$checks${workflows:+ workflows=$workflows}"

  if [ "$mergeable" = CONFLICTING ]; then
    if [ "$merged" -gt 0 ]; then echo "  skip: conflicts after this tick's merges, next tick"
    else fail_pr "$n" "Konflikt mit \`main\`. Branch auf \`main\` rebasen und pushen."; fi
    continue
  fi
  case "$checks" in
    green) ;;
    red) fail_pr "$n" "CI ist rot: $url/checks"; continue ;;
    *) echo "  skip: checks pending"; continue ;;
  esac
  [ "$mergeable" = MERGEABLE ] || { echo "  skip: mergeable=$mergeable, next tick"; continue; }
  if [ -n "$workflows" ]; then
    fail_pr "$n" "ändert \`.github/workflows/\` ($workflows). Das Actions-Token darf solche PRs nicht mergen; Magnus merged sie von Hand: \`gh pr merge $n --merge --delete-branch\`."
    continue
  fi
  if out=$(act gh pr merge "$n" -R "$REPO" --merge --match-head-commit "$head" --delete-branch 2>&1); then
    merged=$((merged + 1))
    echo "  merged"
    act gh pr comment "$n" -R "$REPO" --body "Vom Merge-Zug nach \`main\` gemerged (Kopf \`${head:0:8}\`)." >/dev/null
  elif [ "$merged" -gt 0 ]; then
    echo "  skip: merge failed after earlier merges, next tick: $(head -3 <<<"$out")"
  else
    fail_pr "$n" "Merge fehlgeschlagen: $(head -3 <<<"$out")"
  fi
done <<<"$prs"

# --- 5. CI on the new main -------------------------------------------------------------------------------
if [ "$merged" -gt 0 ]; then
  act gh workflow run ci.yml -R "$REPO" --ref main
  echo "Merged $merged PR(s); CI dispatched on main."
else
  echo "Nothing merged."
fi
