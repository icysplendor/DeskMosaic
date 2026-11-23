#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd "$(dirname "$0")/.." && pwd)"
build_root="${project_root}/build"
dist_root="${project_root}/dist"
app_path="${build_root}/Build/Products/Release/DeskMosaic.app"

xcodebuild \
  -project "${project_root}/MACAssistant.xcodeproj" \
  -scheme MACAssistant \
  -configuration Release \
  -derivedDataPath "${build_root}" \
  ARCHS="arm64" \
  ONLY_ACTIVE_ARCH=NO \
  clean build

encoder_dir="${build_root}/recording-macos"
bash "${project_root}/scripts/build-recording-encoder.sh" macos "${encoder_dir}"
bash "${project_root}/scripts/bundle-macos-recording-encoder.sh" "${app_path}" - "${encoder_dir}"
bash "${project_root}/scripts/bundle-macos-translation-runtimes.sh" "${app_path}" -

lipo -info "${app_path}/Contents/MacOS/DeskMosaic"
codesign \
  --force \
  --options runtime \
  --timestamp=none \
  --entitlements "${project_root}/MACAssistant/MACAssistant.entitlements" \
  --sign - \
  "${app_path}"
codesign --verify --deep --strict --verbose=2 "${app_path}"
codesign --display --verbose=2 "${app_path}"

mkdir -p "${dist_root}"
"${project_root}/scripts/create-dmg.sh" \
  "${app_path}" \
  "${dist_root}/DeskMosaic-macos-arm64.dmg"
cp "${encoder_dir}/recording-encoder-source.zip" "${dist_root}/recording-encoder-source-macos.zip"
