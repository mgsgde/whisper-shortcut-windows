#!/usr/bin/env bash
# Usage: tools/release.sh [--yes]
#
# Cuts a release the way the macOS app does (scripts/create-release.sh there): checks, then creates the tag
# v<csproj Version> on HEAD and pushes it. The push starts .github/workflows/release.yml, which waits for green
# CI on that commit, builds and signs both exes and ZIPs in CI and publishes. Nothing is built or signed here,
# so this needs neither the signing key nor zip/openssl, only git and gh.
#
# Before running: commit the version bump (<Version> in src/WhisperShortcut/WhisperShortcut.csproj) and the
# German notes in .github/RELEASE_NOTES.md, push the branch, and let CI go green on HEAD.
# HEAD may be main or a hotfix branch release/x.y.z (e.g. release/0.7.14 off v0.7.13; CI runs on its pushes).
#
# Keep it a draft to install and test first: gh variable set RELEASE_AS_DRAFT --body true (before pushing),
# then gh release edit vX.Y.Z --draft=false --latest, and gh variable delete RELEASE_AS_DRAFT.
set -euo pipefail

YES=false
case "${1:-}" in
  --yes) YES=true ;;
  "") ;;
  -h|--help) sed -n '2,14p' "$0"; exit 0 ;;
  *) echo "Unknown option: $1 (usage: tools/release.sh [--yes])" >&2; exit 1 ;;
esac

cd "$(dirname "$0")/.."
REPO=mgsgde/whisper-shortcut-windows

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/WhisperShortcut/WhisperShortcut.csproj | head -1)
TAG="v$VERSION"
SHA=$(git rev-parse HEAD)
NOTES=.github/RELEASE_NOTES.md

fail() { echo "Not released: $*" >&2; exit 1; }

[ -z "$(git status --porcelain)" ] || fail "working tree is not clean"
git fetch -q origin
[ -n "$(git branch -r --contains "$SHA")" ] || fail "HEAD $SHA is not pushed to any branch on origin"
git rev-parse -q --verify "refs/tags/$TAG" >/dev/null && fail "tag $TAG already exists locally"
[ -z "$(git ls-remote --tags origin "refs/tags/$TAG")" ] || fail "tag $TAG already exists on GitHub"
[ -s "$NOTES" ] || fail "$NOTES is missing or empty"
PREV=$(git describe --tags --abbrev=0 --match 'v[0-9]*' "$SHA" 2>/dev/null || true)
if [ -n "$PREV" ] && git diff --quiet "$PREV" "$SHA" -- "$NOTES"; then
  fail "$NOTES is unchanged since $PREV; write the notes for $VERSION"
fi

# release.yml checks this too; checking here means a red or running CI never leaves a tag behind.
# Push runs only: a pull_request run may skip the dictation smoke check (no secret) and still be green.
CI=$(gh api "repos/$REPO/actions/workflows/ci.yml/runs?head_sha=$SHA&event=push&per_page=20" \
  --jq '[.workflow_runs[] | "\(.status) \(.conclusion)"] | join(",")')
case ",$CI," in
  *",completed success,"*) ;;
  *) fail "CI is not green on $SHA (${CI:-no run}). Wait for it, re-run a flaky job, or push a hotfix as release/x.y.z so CI runs on push." ;;
esac

echo "Release $TAG"
echo "  commit: $(git log -1 --format='%h %s' "$SHA")"
echo "  since:  ${PREV:-(first tag)}"
echo "  notes:"; sed 's/^/    /' "$NOTES"
if [ "$YES" != true ]; then
  read -r -p "Tag and push $TAG? (y/N) " reply
  [[ "$reply" =~ ^[Yy]$ ]] || { echo "Cancelled, nothing tagged."; exit 0; }
fi

# Annotated tag on the exact commit; the push is the release.
git tag -a "$TAG" -m "Version $VERSION" "$SHA"
if ! git push origin "refs/tags/$TAG"; then
  git tag -d "$TAG" >/dev/null
  fail "pushing the tag failed; the local tag is removed again"
fi
echo "Pushed $TAG. The release workflow runs now:"
echo "  gh run list -R $REPO --workflow release.yml --limit 1"
echo "  gh run watch -R $REPO \$(gh run list -R $REPO --workflow release.yml --limit 1 --json databaseId --jq '.[0].databaseId')"
echo "Done when: gh release view $TAG -R $REPO shows 9 assets and it is not a draft (unless RELEASE_AS_DRAFT is set)."
