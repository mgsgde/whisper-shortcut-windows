#!/usr/bin/env bash
# Usage: tools/release.sh <version> <notes.md> [--publish]
# Builds and signs both Windows ZIPs (updater) and bare exes (download). Prints the gh command; runs it only with --publish.
set -euo pipefail

VERSION="${1:?usage: tools/release.sh <version> <notes.md> [--publish]}"
NOTES="${2:?usage: tools/release.sh <version> <notes.md> [--publish]}"
PUBLISH="${3:-}"
KEY="${KEY:-$HOME/.config/whispershortcut-windows-update/ecdsa-p256-private.pem}"

cd "$(dirname "$0")/.."

[ -f "$KEY" ] || { echo "Signing key missing: $KEY (without it installed apps refuse the update)" >&2; exit 1; }
[ -f "$NOTES" ] || { echo "Notes file missing: $NOTES" >&2; exit 1; }
CSPROJ_VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/WhisperShortcut/WhisperShortcut.csproj | head -1)
[ "$CSPROJ_VERSION" = "$VERSION" ] || { echo "csproj Version is $CSPROJ_VERSION, not $VERSION" >&2; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "Working tree is not clean" >&2; exit 1; }

dotnet test tests/WhisperShortcut.Tests

PUB="$(mktemp)"
openssl ec -in "$KEY" -pubout -out "$PUB" 2>/dev/null
mkdir -p dist
ZIPS=(); SIGS=(); EXES=(); EXESIGS=()
for rid in win-x64 win-arm64; do
  dotnet publish src/WhisperShortcut -c Release -r "$rid" --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "out/$rid"
  zip_path="$PWD/dist/WhisperShortcut-Windows-${rid#win-}.zip"
  stage="$(mktemp -d)"
  mkdir "$stage/WhisperShortcut"
  cp "out/$rid/WhisperShortcut.exe" "$stage/WhisperShortcut/WhisperShortcut.exe"
  cp dist/ANLEITUNG.txt "$stage/WhisperShortcut/ANLEITUNG.txt"
  rm -f "$zip_path"
  (cd "$stage" && zip -r -X "$zip_path" WhisperShortcut)
  rm -rf "$stage"
  openssl dgst -sha256 -sign "$KEY" -out "$zip_path.sig" "$zip_path"
  openssl dgst -sha256 -verify "$PUB" -signature "$zip_path.sig" "$zip_path"
  ZIPS+=("dist/$(basename "$zip_path")"); SIGS+=("dist/$(basename "$zip_path").sig")
  exe_path="$PWD/dist/WhisperShortcut-Windows-${rid#win-}.exe"
  cp "out/$rid/WhisperShortcut.exe" "$exe_path"
  openssl dgst -sha256 -sign "$KEY" -out "$exe_path.sig" "$exe_path"
  openssl dgst -sha256 -verify "$PUB" -signature "$exe_path.sig" "$exe_path"
  EXES+=("dist/$(basename "$exe_path")"); EXESIGS+=("dist/$(basename "$exe_path").sig")
done
rm -f "$PUB"

CMD=(gh release create "v$VERSION" -R mgsgde/whisper-shortcut-windows --title "Version $VERSION" --notes-file "$NOTES" "${ZIPS[@]}" "${SIGS[@]}" "${EXES[@]}" "${EXESIGS[@]}" dist/ANLEITUNG.txt)
if [ "$PUBLISH" = "--publish" ]; then
  "${CMD[@]}"
else
  echo "Not published. To publish:"
  printf '%q ' "${CMD[@]}"; echo
fi
