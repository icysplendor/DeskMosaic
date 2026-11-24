#!/usr/bin/env bash
set -euo pipefail

# Run inside the image built from scripts/windows-local.Dockerfile.
output="${1:?Usage: bash scripts/build-local-windows.sh OUTPUT_DIR [DOWNLOAD_CACHE]}"
mkdir -p "$output"
output="$(cd "$output" && pwd)"
cache="${2:-$output/cache}"
mkdir -p "$cache"
cache="$(cd "$cache" && pwd)"
project="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
export DOTNET_CLI_HOME="$work/dotnet" NUGET_PACKAGES="$cache/nuget"
export ZIG_GLOBAL_CACHE_DIR="$cache/zig" ZIG_LOCAL_CACHE_DIR="$work/zig"

# The checkout can be mounted read-only. Keep MSBuild intermediates out of it.
tar -C "$project" --exclude=.git --exclude=build --exclude=dist --exclude=bin --exclude=obj -cf - . | tar -C "$work" -xf -
cd "$work"
mkdir -p build/translation-windows build/recording-windows
bash scripts/fetch-translation-runtimes.sh windows "$cache/translation-windows"
cp "$cache/translation-windows/"* build/translation-windows/
bash scripts/bundle-windows-streaming-runtime.sh "$work/build/translation-windows"
bash scripts/build-recording-encoder.sh windows build/recording-windows "$cache/encoder"
dotnet publish WinAssistant/WinAssistant.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:RequireBundledTranslation=true -p:RequireBundledRecordingEncoder=true -o "$work/publish"
install -m 755 "$work/publish/DeskMosaic.exe" "$output/DeskMosaic.exe"
zip -j -q "$work/DeskMosaic-windows-x64-local.zip" "$output/DeskMosaic.exe"
unzip -t "$work/DeskMosaic-windows-x64-local.zip"
cp "$work/DeskMosaic-windows-x64-local.zip" "$output/"
cp build/recording-windows/recording-encoder-source.zip "$output/recording-encoder-source-windows.zip"
