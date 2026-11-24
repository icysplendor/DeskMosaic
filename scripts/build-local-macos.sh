#!/usr/bin/env bash
set -euo pipefail

[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || {
  echo "Build on an Apple Silicon Mac with Xcode selected." >&2
  exit 1
}
identity="${DESKMOSAIC_MAC_SIGN_IDENTITY:?Set this to your persistent Code Signing keychain identity}"
[[ "$identity" != "-" ]] || { echo "A certificate-backed signing identity is required." >&2; exit 1; }
xcodebuild -version
command -v pkg-config >/dev/null || { echo "Install pkg-config on the Mac." >&2; exit 1; }
output="${1:-build/local-macos}"
mkdir -p "$output"
output="$(cd "$output" && pwd)"
project="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project"

xcodebuild -quiet -project MACAssistant.xcodeproj -scheme MACAssistant -configuration Release \
  -derivedDataPath "$output/derived" ARCHS=arm64 ONLY_ACTIVE_ARCH=NO CODE_SIGNING_ALLOWED=NO build
app="$output/derived/Build/Products/Release/DeskMosaic.app"
export DESKMOSAIC_MAC_LOCAL_SIGNING=1
bash scripts/build-recording-encoder.sh macos "$output/encoder"
bash scripts/bundle-macos-translation-runtimes.sh "$app" "$identity"
bash scripts/bundle-macos-recording-encoder.sh "$app" "$identity" "$output/encoder"
codesign --force --options runtime --timestamp=none --entitlements MACAssistant/MACAssistant.entitlements \
  --sign "$identity" "$app"
codesign --verify --deep --strict "$app"
pin="$output/signing-requirement.txt"
requirement="$(codesign -d -r- "$app" 2>&1 | sed -n 's/^designated => //p')"
[[ "$requirement" == *"certificate "* || "$requirement" == *"anchor "* ]] || {
  echo "The app's designated requirement does not identify a certificate." >&2
  exit 1
}
if [[ -f "$pin" ]]; then
  codesign --verify -R "=$(cat "$pin")" "$app"
fi
if [[ -n "${DESKMOSAIC_MAC_REFERENCE_APP:-}" ]]; then
  reference="$(codesign -d -r- "$DESKMOSAIC_MAC_REFERENCE_APP" 2>&1 | sed -n 's/^designated => //p')"
  [[ -n "$reference" ]] || { echo "The reference app has no designated requirement." >&2; exit 1; }
  codesign --verify -R "=$reference" "$app"
fi
work="$(mktemp -d "$output/.dmg.XXXXXX")"
trap 'rm -rf "$work"' EXIT
ditto "$app" "$work/DeskMosaic.app"
ln -s /Applications "$work/Applications"
hdiutil create -quiet -ov -volname "DeskMosaic" -srcfolder "$work" -format UDZO "$output/DeskMosaic-macos-arm64-local.dmg"
cp "$output/encoder/recording-encoder-source.zip" "$output/recording-encoder-source-macos.zip"
if [[ ! -f "$pin" ]]; then printf '%s\n' "$requirement" >"$pin"; fi
printf '%s\n' "$app" "$output/DeskMosaic-macos-arm64-local.dmg"
