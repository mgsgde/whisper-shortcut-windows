#!/usr/bin/env bash
# Nightly release and Tuesday Stable promotion, ported from the macOS repo (scripts/release-nightly-job.sh,
# scripts/stable-promote-job.sh, scripts/promote-stable.sh). Runs in .github/workflows/release-nightly.yml.
#
#   release-nightly.sh prepare   gates -> version -> German notes (Gemini) -> commit "Version X" on main -> tag
#                                -> dispatch CI on the tag. Prints tag=vX to $GITHUB_OUTPUT; release.yml (called
#                                next by the workflow) waits for that CI, builds, signs, publishes to Latest.
#   release-nightly.sh promote [vX]   Stable: clear the prerelease flag of the newest prerelease published at
#                                least 6 days ago (the Mac's "frozen last Tuesday, promoted this Tuesday"), if it
#                                is newer than Stable and CI was green on its commit. With vX: that one, no age rule.
#
# Gates of prepare (as on the Mac): hold (repo variable RELEASE_HOLD), main tip has a green CI run (push or
# dispatch; that run includes the dictation smoke check from #75, the live test), and something besides
# "Version x.y.z" commits landed since the newest v* tag. FORCE=true skips only "nothing new" (hotfix).
# The version: if the csproj is already above the newest tag, that version; otherwise the patch number + 1.
#
# Env: GH_TOKEN, REPO, GEMINI_API_KEY (notes), RELEASE_HOLD / STABLE_HOLD, FORCE, DRY_RUN=true (no push/tag/edit).
set -euo pipefail

REPO="${REPO:?REPO=owner/name}"
DRY="${DRY_RUN:-false}"
CSPROJ=src/WhisperShortcut/WhisperShortcut.csproj
NOTES=.github/RELEASE_NOTES.md
MODEL="${NOTES_MODEL:-gemini-3.8-flash}"

out() { echo "$1" >> "${GITHUB_OUTPUT:-/dev/null}"; echo "output: $1"; }
act() { if [ "$DRY" = true ]; then echo "DRY: $*"; else "$@"; fi; }
ci_green() {  # sha -> success if a push/dispatch CI run on it succeeded
  gh api "repos/$REPO/actions/workflows/ci.yml/runs?head_sha=$1&per_page=30" \
    --jq '[.workflow_runs[] | select((.event == "push" or .event == "workflow_dispatch") and .conclusion == "success")] | length' \
    | grep -qv '^0$'
}
vgt() { [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -1)" = "$1" ]; }

prepare() {
  if [ -n "${RELEASE_HOLD:-}" ]; then
    echo "::notice::Nightly release on hold (repo variable RELEASE_HOLD): $RELEASE_HOLD"; out "tag="; return 0
  fi
  git fetch -q --tags origin main
  local tip last csv next commits
  tip=$(git rev-parse origin/main)
  last=$(git tag -l 'v[0-9]*' --sort=-v:refname | head -1)
  echo "main $tip, newest tag ${last:-none}"
  if ! ci_green "$tip"; then
    echo "::notice::CI is not green on main ${tip:0:8} (yet); nothing released tonight."; out "tag="; return 0
  fi
  commits=$(git log --no-merges --format='- %s%n%w(0,2,2)%b' "${last:+$last..}$tip" | grep -v '^- Version [0-9]' | sed '/^\s*$/d' | head -400 || true)
  if [ -z "$commits" ] && [ "${FORCE:-false}" != true ]; then
    echo "::notice::Nothing new on main since $last."; out "tag="; return 0
  fi
  csv=$(git show "$tip:$CSPROJ" | sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' | head -1)
  if vgt "$csv" "${last#v}"; then next="$csv"
  else
    local base="${last#v}"; vgt "$csv" "$base" && base="$csv"
    next="${base%.*}.$(( ${base##*.} + 1 ))"
  fi
  [ -z "$(git ls-remote --tags origin "refs/tags/v$next")" ] || { echo "::error::Tag v$next already exists"; exit 1; }
  echo "Next version $next"

  git checkout -q --detach "$tip"
  write_notes "$last" "v$next" "$commits"
  sed -i "s:<Version>$csv</Version>:<Version>$next</Version>:" "$CSPROJ"
  grep -q "<Version>$next</Version>" "$CSPROJ" || { echo "::error::Could not set <Version> in $CSPROJ"; exit 1; }
  git add "$CSPROJ" "$NOTES"
  git -c user.name="github-actions[bot]" -c user.email="41898282+github-actions[bot]@users.noreply.github.com" \
    commit -q -m "Version $next"
  git show --stat HEAD | head -8

  # Push to main; if main moved, rebase the two-file bump onto it (the tagged commit then gets its own CI run).
  local i
  for i in 1 2 3; do
    if act git push -q origin HEAD:refs/heads/main; then break; fi
    [ "$i" = 3 ] && { echo "::error::Could not push the version commit to main"; exit 1; }
    git fetch -q origin main && git rebase -q origin/main
  done
  local rel; rel=$(git rev-parse HEAD)
  act git -c user.name="github-actions[bot]" -c user.email="41898282+github-actions[bot]@users.noreply.github.com" \
    tag -a "v$next" -m "Version $next" "$rel"
  act git push -q origin "refs/tags/v$next"
  # A GITHUB_TOKEN push starts no workflow: run CI on the tag ourselves; release.yml waits for it.
  act gh workflow run ci.yml -R "$REPO" --ref "v$next"
  if [ "$DRY" = true ]; then echo "DRY: would release v$next"; out "tag="; else out "tag=v$next"; fi
}

write_notes() {  # last_tag new_tag commits
  local last="$1" new="$2" commits="$3" prev prompt text
  prev=$(cat "$NOTES" 2>/dev/null || true)
  prompt="Du schreibst die Release-Notes für Version ${new#v} von WhisperShortcut für Windows (Diktier-App).
Leser sind Nutzerinnen und Nutzer, keine Entwickler. Schreib auf Deutsch, kurz, im Stil der vorigen Notes (Beispiel unten):
eine Einleitungszeile, dann Stichpunkte \"- **Kurzer Titel:** ein bis zwei Sätze, was sich für Nutzer ändert\".
Lass alles weg, was nur Entwickler betrifft (Tests, CI, Skripte, Refactoring, Doku). Keine Commit-Hashes, PR-Nummern,
Dateinamen oder Platzhalter. Erfinde nichts, was nicht in den Änderungen steht. Ist nichts davon für Nutzer sichtbar,
schreib genau: \"Fehlerbehebungen und interne Verbesserungen.\" Gib nur den Markdown-Text aus, ohne Codeblock.

Vorige Release-Notes (nur als Stilbeispiel):
$prev

Änderungen seit ${last:-dem Anfang}:
$commits"
  if [ -z "${GEMINI_API_KEY:-}" ] && [ "$DRY" = true ]; then
    printf '(Probelauf ohne GEMINI_API_KEY: hier stünden die Notes.)
' > "$NOTES"; return 0
  fi
  [ -n "${GEMINI_API_KEY:-}" ] || { echo "::error::GEMINI_API_KEY missing: cannot write release notes"; exit 1; }
  text=$(python3 - "$prompt" "$MODEL" <<'PY'
import json, os, sys, urllib.request
prompt, model = sys.argv[1], sys.argv[2]
req = urllib.request.Request(
    f"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent",
    data=json.dumps({"contents": [{"parts": [{"text": prompt}]}], "generationConfig": {"temperature": 0.2}}).encode(),
    headers={"Content-Type": "application/json", "x-goog-api-key": os.environ["GEMINI_API_KEY"]})
with urllib.request.urlopen(req, timeout=120) as r:
    data = json.load(r)
print("".join(p.get("text", "") for p in data["candidates"][0]["content"]["parts"]).strip())
PY
)
  text=$(sed -e '/^```/d' <<<"$text")
  if [ -z "$text" ] || [ "${#text}" -gt 4000 ] || grep -qiE 'USER/REPO|your-repo|TODO|\[.*hier.*\]' <<<"$text"; then
    echo "::error::Release notes from $MODEL failed validation:"; echo "$text"; exit 1
  fi
  printf '%s\n\nAlle Änderungen: https://github.com/%s/compare/%s...%s\n' "$text" "$REPO" "${last:-v0.0.0}" "$new" > "$NOTES"
  echo "Notes:"; cat "$NOTES"
}

promote() {
  local want="${1:-}" tag published sha stable
  if [ -n "${STABLE_HOLD:-}" ] && [ -z "$want" ]; then
    echo "::notice::Stable promotion on hold (repo variable STABLE_HOLD): $STABLE_HOLD"; return 0
  fi
  stable=$(gh api "repos/$REPO/releases/latest" --jq .tag_name 2>/dev/null || echo v0)
  if [ -n "$want" ]; then
    tag="$want"
  else
    local cutoff; cutoff=$(date -u -d '6 days ago' +%Y-%m-%dT%H:%M:%SZ)
    tag=$(gh api "repos/$REPO/releases?per_page=50" --jq "[.[] | select(.prerelease and (.draft | not) and .published_at <= \"$cutoff\")] | sort_by(.published_at) | last | .tag_name // empty")
  fi
  [ -n "$tag" ] || { echo "No prerelease old enough to promote (Stable is $stable)."; return 0; }
  vgt "${tag#v}" "${stable#v}" || { echo "$tag is not newer than Stable $stable; nothing to do."; return 0; }
  read -r published sha < <(gh api "repos/$REPO/releases/tags/$tag" --jq '"\(.published_at) \(.target_commitish)"')
  sha=$(gh api "repos/$REPO/commits/refs/tags/$tag" --jq .sha)
  ci_green "$sha" || { echo "::error::CI was not green on $tag ($sha); not promoting."; exit 1; }
  echo "Promoting $tag (published $published) over Stable $stable"
  act gh release edit "$tag" -R "$REPO" --prerelease=false --latest
  [ "$DRY" = true ] || [ "$(gh api "repos/$REPO/releases/latest" --jq .tag_name)" = "$tag" ] \
    || { echo "::error::releases/latest is not $tag after the edit"; exit 1; }
  out "promoted=$tag"
}

case "${1:-}" in
  prepare) prepare ;;
  promote) promote "${2:-}" ;;
  *) echo "usage: $0 prepare | promote [vX.Y.Z]" >&2; exit 2 ;;
esac
