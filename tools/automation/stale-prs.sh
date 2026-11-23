#!/usr/bin/env bash
# Monday list of open PRs, ported from the macOS repo (scripts/merge-train/stale-prs.sh).
# Runs in GitHub Actions (.github/workflows/merge-train.yml, Mondays), independent of MERGE_TRAIN_HOLD.
#
# Every open PR against main (drafts included): open >= 3 days -> listed with its reason; open >= 14 days ->
# closed with a comment, branch kept. The list goes into one issue that mentions the owner, so GitHub mails
# it (the Mac sends a mail); last week's list issue is closed. Nothing 3 days old -> no issue.
#
# Env: GH_TOKEN, REPO; DRY_RUN=true prints the issue and the closes instead of doing them.
set -uo pipefail

REPO="${REPO:?REPO=owner/name}"
OWNER="${REPO%%/*}"
DRY="${DRY_RUN:-false}"
TITLE_PREFIX="Offene PRs:"
FAIL_PREFIX="Der Merge-Zug hat bei diesem PR angehalten:"
now=$(date -u +%s)

act() { if [ "$DRY" = true ]; then echo "DRY: $*"; else "$@"; fi; }

reason() {  # number -> one-line reason, first match wins (same order as the Mac)
  gh pr view "$1" -R "$REPO" --json labels,isDraft,mergeable,statusCheckRollup,author,headRefName,comments --jq '
    "'"$OWNER"'" as $owner | "'"$FAIL_PREFIX"'" as $fp
    | [.labels[].name] as $l
    | [.statusCheckRollup[] | select(((.conclusion // .state // "") | ascii_upcase) | test("^(FAILURE|TIMED_OUT|ACTION_REQUIRED|STARTUP_FAILURE|ERROR)$")) | (.name // .context)] as $red
    | [.statusCheckRollup[] | select((.status // "COMPLETED") != "COMPLETED" or ((.state // "") | test("PENDING|EXPECTED")))] as $pending
    | if ($l | index("needs-device-test")) then "wartet auf Test am Windows-PC (needs-device-test)"
      elif ($l | index("hold")) then "hold"
      elif ($l | index("merge-train-failed")) then
        ("Merge-Zug angehalten: " + ([.comments[] | select(.body | startswith($fp)) | .body][-1] // "" | ltrimstr($fp) | split("\n")[0] | ltrimstr(" ")))
      elif .isDraft then "Entwurf"
      elif .mergeable == "CONFLICTING" then "Konflikt mit main"
      elif ($red | length) > 0 then ("CI rot: " + ($red | join(", ")))
      elif ($pending | length) > 0 or (.statusCheckRollup | length) == 0 then "CI läuft oder nicht gestartet"
      elif .author.login != $owner and (($l | index("merge-train")) | not) then "fremder Autor ohne Label merge-train"
      elif (.headRefName | startswith("implementer/")) then "implementer/-Branch"
      else "grün, wartet auf den Merge-Zug" end' 2>/dev/null || echo "(nicht lesbar)"
}

listed=(); closed=()
while IFS='|' read -r n created url title; do
  [ -n "$n" ] || continue
  age=$(( (now - $(date -u -d "$created" +%s)) / 86400 ))
  [ "$age" -ge 3 ] || continue
  r=$(reason "$n")
  line="#$n ($age T) $title — $r — $url"
  if [ "$age" -ge 14 ]; then
    act gh pr close "$n" -R "$REPO" --comment "Nach 14 Tagen automatisch geschlossen (Grund: $r). Der Branch bleibt; diesen PR wieder öffnen oder einen neuen daraus machen, wenn es weitergeht." >/dev/null
    closed+=("$line")
  else
    listed+=("$line")
  fi
done < <(gh pr list -R "$REPO" --base main --state open --limit 100 --json number,createdAt,url,title \
  --jq 'sort_by(.createdAt)[] | "\(.number)|\(.createdAt)|\(.url)|\(.title | gsub("\\|"; "/"))"')

old=$(gh issue list -R "$REPO" --state open --search "\"$TITLE_PREFIX\" in:title" --json number,title \
  --jq ".[] | select(.title | startswith(\"$TITLE_PREFIX\")) | .number" 2>/dev/null || true)
for n in $old; do act gh issue close "$n" -R "$REPO" --comment "Abgelöst durch die Liste dieser Woche." >/dev/null; done

total=$(( ${#listed[@]} + ${#closed[@]} ))
if [ "$total" -eq 0 ]; then
  echo "No PR open 3 days or longer."
  exit 0
fi
title="$TITLE_PREFIX $total älter als 3 Tage"
[ "${#closed[@]}" -gt 0 ] && title="$title, ${#closed[@]} geschlossen"
body="@$OWNER Wochenliste der offenen PRs (Montag)."
if [ "${#closed[@]}" -gt 0 ]; then
  body+=$'\n\n**Automatisch geschlossen (14 Tage):**\n'
  for l in "${closed[@]}"; do body+="- $l"$'\n'; done
fi
if [ "${#listed[@]}" -gt 0 ]; then
  body+=$'\n\n**Offen seit 3 Tagen oder länger:**\n'
  for l in "${listed[@]}"; do body+="- $l"$'\n'; done
fi
body+=$'\n'"Nicht mergen lassen: Label \`hold\` oder \`needs-device-test\`. Alles andere, was konfliktfrei und grün ist, merged der Merge-Zug."
if [ "$DRY" = true ]; then
  echo "DRY: issue \"$title\""; echo "$body"
else
  gh issue create -R "$REPO" --title "$title" --body "$body"
fi
