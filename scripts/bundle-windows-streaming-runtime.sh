#!/usr/bin/env bash
set -euo pipefail

scripts="$(cd "$(dirname "$0")" && pwd)"
destination="${1:?Usage: bundle-windows-streaming-runtime.sh OUTPUT_DIR}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mkdir -p "$work/include/sherpa-onnx/c-api" "$work/package"
cp "$destination/c-api.h" "$work/include/sherpa-onnx/c-api/"
cp "$destination/transcribe.h" "$work/include/"
tar -xjf "$destination/sherpa-win.tar.bz2" -C "$work"
tar -xjf "$destination/ort-win.tar.bz2" -C "$work"
sherpa="$work/sherpa-onnx-v2.0.2-win-x64-shared-MT-Release-no-tts-lib/lib"
ort="$work/onnxruntime-win-x64-MT-Release-1.28.2"
cp "$sherpa/"{sherpa-onnx-c-api,onnxruntime,onnxruntime_providers_shared}.dll "$work/package/"
cp "$ort/LICENSE" "$work/package/onnxruntime-LICENSE.txt"
cp "$ort/ThirdPartyNotices.txt" "$work/package/onnxruntime-ThirdPartyNotices.txt"
for backend in cpu vulkan; do
  python -m zipfile -e "$destination/nemo-win-$backend.zip" "$work"
  nemo="$work/nemo-speech-0.3.0-windows-x86_64-$backend"
  mkdir -p "$work/package/diarization-$backend"
  if [[ -f "$nemo/bin/vcomp140.dll" ]]; then cp "$nemo/bin/vcomp140.dll" "$work/package/diarization-$backend/"; fi
  cp "$nemo/bin/"{nemo_speech_asr,nemo_speech_asr_c,nemo_speech_speechlm,llama,llama-common,concrt140}.dll \
    "$nemo/bin/"ggml*.dll "$nemo/bin/"msvcp*.dll "$nemo/bin/"vcruntime*.dll \
    "$work/package/diarization-$backend/"
done
cp -R "$nemo/share/licenses/nemo-speech" "$work/package/nemo-speech-licenses"
tar -xzf "$destination/moss-windows.tar.gz" -C "$work"
moss="$work/transcribe-native-windows-x86_64-cpu-vulkan"
mkdir -p "$work/package/moss"
cp "$moss/"*.dll "$work/package/moss/"
cp "$nemo/bin/"msvcp*.dll "$nemo/bin/"vcruntime*.dll "$work/package/moss/"
cp -R "$moss/licenses" "$work/package/transcribe.cpp-licenses"
python -m ziglang cc -target x86_64-windows-gnu -O2 -s -I "$work/include" -I "$nemo/include" \
  "$scripts/translation/streaming-asr.c" -o "$work/package/streaming-asr.exe"
cp "$destination/"{silero_vad.onnx,silero-vad-LICENSE.txt} "$work/package/"
python -m zipfile -c "$destination/streaming.zip" "$work/package/"*
