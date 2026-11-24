#!/usr/bin/env bash
set -euo pipefail

scripts="$(cd "$(dirname "$0")" && pwd)"
app_path="${1:?Usage: bundle-macos-translation-runtimes.sh APP_PATH SIGNING_IDENTITY}"
identity="${2:?Set signing identity to - for ad-hoc signing}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
helpers="$app_path/Contents/Helpers"
libraries="$app_path/Contents/Frameworks"
licenses="$app_path/Contents/Resources/TranslationLicenses"
mkdir -p "$helpers" "$libraries" "$licenses"
resources="$app_path/Contents/Resources"

bash "$scripts/fetch-translation-runtimes.sh" macos "$work"
cp "$work/silero_vad.onnx" "$resources/"
cp "$work/silero-vad-LICENSE.txt" "$licenses/"
tar -xzf "$work/llama-mac.tar.gz" -C "$work"
cp -P "$work/llama-b12561/"*.dylib "$libraries/"
cp "$work/llama-b12561/llama-server" "$helpers/"
cp "$work/llama-b12561/LICENSE" "$licenses/llama.cpp.txt"
# The upstream binary locates its libraries beside itself. App bundles keep dylibs in Frameworks.
install_name_tool -rpath @loader_path @loader_path/../Frameworks "$helpers/llama-server"

mkdir -p "$work/include/sherpa-onnx/c-api"
cp "$work/c-api.h" "$work/include/sherpa-onnx/c-api/"
tar -xjf "$work/sherpa-mac.tar.bz2" -C "$work"
sherpa="$work/sherpa-onnx-v2.0.2-osx-arm64-shared-no-tts-lib/lib"
cp "$sherpa/libsherpa-onnx-c-api.dylib" "$sherpa/libonnxruntime.dylib" "$libraries/"
install_name_tool -change @rpath/libonnxruntime.dylib @loader_path/libonnxruntime.dylib \
  "$libraries/libsherpa-onnx-c-api.dylib"
tar -xzf "$work/nemo-mac-metal.tar.gz" -C "$work"
nemo="$work/nemo-speech-0.3.0-macos-aarch64-metal"
diarization="$libraries/Diarization"
mkdir -p "$diarization"
cp -P "$nemo/lib/"libggml*.dylib "$nemo/lib/"libllama*.dylib \
  "$nemo/lib/"libnemo_speech_{asr*,speechlm,text_normalization}.dylib "$diarization/"
cp -R "$nemo/share/licenses/nemo-speech" "$licenses/nemo-speech"

cc -arch arm64 -mmacosx-version-min=13.3 -O2 -I "$work/include" -I "$nemo/include" \
  "$scripts/translation/streaming-asr.c" -o "$helpers/streaming-asr"
cp "$work/streaming-license.txt" "$licenses/sherpa-onnx.txt"
unzip -p "$work/ort-mac.zip" '*/LICENSE' > "$licenses/onnxruntime-LICENSE.txt"
unzip -p "$work/ort-mac.zip" '*/ThirdPartyNotices.txt' > "$licenses/onnxruntime-ThirdPartyNotices.txt"

timestamp=--timestamp
if [[ "$identity" == "-" || "${DESKMOSAIC_MAC_LOCAL_SIGNING:-0}" == 1 ]]; then timestamp=--timestamp=none; fi
for component in "$libraries/"*.dylib "$diarization/"*.dylib "$helpers/llama-server" "$helpers/streaming-asr"; do
  if [[ ! -L "$component" ]]; then
    signing_options=(--force --options runtime "$timestamp" --sign "$identity")
    if [[ ( "$identity" == "-" || "${DESKMOSAIC_MAC_LOCAL_SIGNING:-0}" == 1 ) && ( "$component" == "$helpers/llama-server" || "$component" == "$helpers/streaming-asr" ) ]]; then
      # Local signatures have no Apple-issued Team ID for library validation.
      signing_options+=(--entitlements "$scripts/macos-adhoc-translation.entitlements")
    fi
    codesign "${signing_options[@]}" "$component"
  fi
done

"$helpers/llama-server" --help >/dev/null 2>&1

"$helpers/streaming-asr" --check "$libraries/libsherpa-onnx-c-api.dylib"
"$helpers/streaming-asr" --check-diarization "$diarization/libnemo_speech_asr_c.dylib"
