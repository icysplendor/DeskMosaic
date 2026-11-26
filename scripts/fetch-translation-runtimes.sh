#!/usr/bin/env bash
set -euo pipefail

# Fetch executable runtimes and the small microphone detector. Speech and translation models are selected by the user.
destination="${2:?Usage: fetch-translation-runtimes.sh windows|macos OUTPUT_DIR}"
mkdir -p "$destination"

fetch() {
  local url="$1" file="$destination/$2" digest="$3"
  if [[ -f "$file" ]] && checksum "$digest" "$file" >/dev/null; then
    return
  fi
  curl -fLsS --retry 3 --connect-timeout 15 --max-time 180 "$url" -o "$file.partial"
  checksum "$digest" "$file.partial"
  mv -f "$file.partial" "$file"
}

checksum() {
  local expected="$1" file="$2" actual
  if command -v sha256sum >/dev/null 2>&1; then
    actual="$(sha256sum < "$file")"
  else
    actual="$(shasum -a 256 < "$file")"
  fi
  if [[ "${actual%% *}" != "$expected" ]]; then
    echo "Checksum mismatch: $file" >&2
    return 1
  fi
}

sherpa=https://github.com/k2-fsa/sherpa-onnx/releases/download/v2.0.2
ort=https://github.com/csukuangfj/onnxruntime-libs/releases/download/v1.28.2
nemo=https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v0.3.0
fetch https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/v2.0.2/sherpa-onnx/c-api/c-api.h c-api.h \
  1f738de63a206328253ff471edda96b011fa1bd6f2a028f82967a7761abc2edd
fetch https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/v2.0.2/LICENSE streaming-license.txt \
  cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30

silero=https://raw.githubusercontent.com/snakers4/silero-vad/v5.0
fetch "$silero/files/silero_vad.onnx" silero_vad.onnx \
  6b99cbfd39246b6706f98ec13c7c50c6b299181f2474fa05cbc8046acc274396
fetch "$silero/LICENSE" silero-vad-LICENSE.txt \
  2e63e9a38b6e8fc0c7bc37ce174caca1862870856c6daf5697cfb785e925520b

moss=https://github.com/handy-computer/transcribe.cpp/releases/download/v0.4.2
fetch https://raw.githubusercontent.com/handy-computer/transcribe.cpp/v0.4.2/include/transcribe.h transcribe.h \
  5c8267936370f40d911866df8617cb320dda6dba951e5ba89287645988105dbf

case "${1:-}" in
  windows)
    fetch "$moss/transcribe-native-0.4.2-windows-x86_64-cpu-vulkan.tar.gz" moss-windows.tar.gz \
      6094102cf57b404b050707ceb258b12ce647b92e801b5d46077214e5b030c8aa
    fetch "$nemo/nemo-speech-0.3.0-windows-x86_64-cpu.zip" nemo-win-cpu.zip \
      4ed9740e361e4653f56c3dce7d1e81883a1c8b831c1bd7d01567fff1130b5b05
    fetch "$nemo/nemo-speech-0.3.0-windows-x86_64-vulkan.zip" nemo-win-vulkan.zip \
      72b3b8b41bfe2be0320eff4e9d310594e4f99deefcde4a157799a039656963ce
    fetch "$sherpa/sherpa-onnx-v2.0.2-win-x64-shared-MT-Release-no-tts-lib.tar.bz2" sherpa-win.tar.bz2 \
      982d5365be66590f319964a07a7eeb7b66743f640056bee1d07e4753d8defa70
    fetch "$ort/onnxruntime-win-x64-MT-Release-1.28.2.tar.bz2" ort-win.tar.bz2 \
      cd7f757f7a38ce14238bc9198bee197a9430b42ec3ecadea3408dd2e832eb02e
    fetch https://github.com/ggml-org/llama.cpp/releases/download/b12561/llama-b12561-bin-win-cpu-x64.zip llama.zip \
      c62488af06fe63351a72ceb219f504c8bf3b5389fa784edc97b29dc7bac756b0
    fetch https://github.com/ggml-org/llama.cpp/releases/download/b12561/llama-b12561-bin-win-vulkan-x64.zip llama-vulkan.zip \
      6a2dea33a38419bb53cee4bca082a034fe90f620910771be9e0fb2c7943993ad
    fetch https://raw.githubusercontent.com/ggml-org/llama.cpp/b12561/LICENSE llama-license.txt \
      94f29bbed6a22c35b992c5c6ebf0e7c92f13b836b90f36f461c9cf2f0f1d010d
    cp "$destination/llama-license.txt" "$destination/llama-vulkan-license.txt"
    ;;
  macos)
    fetch "$moss/transcribe-native-0.4.2-macos-arm64-metal.tar.gz" moss-macos.tar.gz \
      75ea3a705f3edec09942fdbb1b2b5509d7ecd75bda55158a47a3ab1e2d1105b3
    fetch "$nemo/nemo-speech-0.3.0-macos-aarch64-metal.tar.gz" nemo-mac-metal.tar.gz \
      2b972ed8b5dba07492212acf8cb75cbf34ecb141198ffa1997febe84b7c4d9ed
    fetch "$sherpa/sherpa-onnx-v2.0.2-osx-arm64-shared-no-tts-lib.tar.bz2" sherpa-mac.tar.bz2 \
      451f9fd778b32213ea9b590a2e1fef85c4c1b607c5f1b310f93a40b22fde85ae
    fetch "$ort/onnxruntime-osx-arm64-1.28.2.zip" ort-mac.zip \
      d9e5c0c79929e201f5b8eb095e6809a91ce78be9866a1bca0dfab7e20b40ae40
    fetch https://github.com/ggml-org/llama.cpp/releases/download/b12561/llama-b12561-bin-macos-arm64.tar.gz llama-mac.tar.gz \
      29fe18aaf319cb8b3d3b2f89b4558d87c29e793ab503166a28e39119b5e9bd85
    ;;
  *)
    echo "Expected windows or macos" >&2
    exit 2
    ;;
esac
