#!/usr/bin/env bash
set -euo pipefail

app="${1:?Usage: bundle-macos-recording-encoder.sh APP_PATH SIGNING_IDENTITY ENCODER_DIR}"
identity="${2:?Set signing identity to - for ad-hoc signing}"
encoder="${3:?Set the recording encoder build directory}"

mkdir -p "$app/Contents/Helpers" "$app/Contents/Resources/RecordingLicenses"
helper="$app/Contents/Helpers/ffmpeg"
install -m 755 "$encoder/ffmpeg" "$helper"
install -m 644 "$encoder/ffmpeg-license.txt" "$app/Contents/Resources/RecordingLicenses/"
lipo "$helper" -verify_arch arm64

timestamp=--timestamp
if [[ "$identity" == "-" ]]; then timestamp=--timestamp=none; fi
codesign --force --options runtime "$timestamp" --sign "$identity" "$helper"
