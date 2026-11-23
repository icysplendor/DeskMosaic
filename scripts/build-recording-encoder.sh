#!/usr/bin/env bash
set -euo pipefail

platform="${1:?Usage: build-recording-encoder.sh macos|windows|linux OUTPUT_DIR [SOURCE_CACHE]}"
output="${2:?Set the output directory}"
mkdir -p "$output"
output="$(cd "$output" && pwd)"
cache="${3:-$output/sources}"
mkdir -p "$cache"
cache="$(cd "$cache" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

ffmpeg_version=7.1.5
x264_revision=31e19f92f00c7003fa115047ce50978bc98c3a0d
ffmpeg_archive="ffmpeg-$ffmpeg_version.tar.xz"
x264_archive="x264-$x264_revision.tar.gz"
script="$(cd "$(dirname "$0")" && pwd)/$(basename "$0")"

checksum() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum; else shasum -a 256; fi
}

fetch() {
  local url="$1" archive="$2" digest="$3" actual
  if [[ ! -f "$cache/$archive" ]]; then
    curl -fLsS --retry 3 --connect-timeout 15 --max-time 180 "$url" -o "$work/$archive"
    actual="$(checksum < "$work/$archive")"
    [[ "${actual%% *}" == "$digest" ]] || { echo "Checksum mismatch: $archive" >&2; return 1; }
    mv "$work/$archive" "$cache/$archive"
  fi
  actual="$(checksum < "$cache/$archive")"
  [[ "${actual%% *}" == "$digest" ]] || { echo "Checksum mismatch: $cache/$archive" >&2; return 1; }
}

fetch "https://ffmpeg.org/releases/$ffmpeg_archive" "$ffmpeg_archive" \
  de668509caf9e35e3cd162473441fdb29538c6d96ed080292b3cf9e6fc5d558f
fetch "https://codeload.github.com/mirror/x264/tar.gz/$x264_revision" "$x264_archive" \
  d053c9d86988d6bc78237ca5205865c5ddf99c98ef4cd9927eec8f6d388f6dd9
tar -xf "$cache/$ffmpeg_archive" -C "$work"
tar -xf "$cache/$x264_archive" -C "$work"

prefix="$work/install"
x264="$work/x264-$x264_revision"
ffmpeg="$work/ffmpeg-$ffmpeg_version"
jobs="${RECORDING_ENCODER_JOBS:-4}"
binary=ffmpeg
x264_flags=()
ffmpeg_flags=()
case "$platform" in
  macos)
    export MACOSX_DEPLOYMENT_TARGET=13.0
    x264_flags+=(--host=aarch64-apple-darwin)
    ffmpeg_flags+=(--arch=aarch64 --target-os=darwin)
    ;;
  windows)
    binary=ffmpeg.exe
    x264_flags+=(--host=x86_64-w64-mingw32)
    ffmpeg_flags+=(--arch=x86_64 --target-os=mingw32 --extra-ldflags=-static)
    if [[ "$(uname -s)" == Linux ]]; then
      x264_flags+=(--cross-prefix=x86_64-w64-mingw32-)
      ffmpeg_flags+=(--cross-prefix=x86_64-w64-mingw32- --enable-cross-compile --pkg-config=pkg-config)
    else
      # The UCRT64 pkg-config executable consumes Windows paths from x264.pc.
      prefix="$(cygpath -am "$prefix")"
    fi
    ;;
  linux) ;;
  *) echo "Expected macos, windows, or linux" >&2; exit 2 ;;
esac

(
  cd "$x264"
  ./configure --prefix="$prefix" --enable-static --enable-pic --disable-cli --bit-depth=8 "${x264_flags[@]}"
  make -j "$jobs"
  make install-lib-static
)

export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
unset PKG_CONFIG_PATH
(
  cd "$ffmpeg"
  # Keep only the raw BGRA -> H.264 -> fragmented MP4 pipeline used by the app.
  ./configure --disable-autodetect --disable-network --disable-doc --disable-debug \
    --disable-ffplay --disable-ffprobe --disable-avdevice --disable-everything \
    --enable-gpl --enable-libx264 --pkg-config-flags=--static \
    --enable-protocol=pipe --enable-demuxer=rawvideo --enable-decoder=rawvideo \
    --enable-filter=scale --enable-encoder=libx264 --enable-muxer=mp4 "${ffmpeg_flags[@]}"
  make -j "$jobs" "$binary"
)
install -m 755 "$ffmpeg/$binary" "$output/$binary"

{
  printf 'FFmpeg %s with x264 %s\n\nFFmpeg license\n\n' "$ffmpeg_version" "$x264_revision"
  cat "$ffmpeg/COPYING.GPLv2"
  printf '\n\nx264 license\n\n'
  cat "$x264/COPYING"
} > "$output/ffmpeg-license.txt"

# Ship the complete corresponding sources and the script used to build the binary.
python3 - "$output" "$script" "$cache/$ffmpeg_archive" "$cache/$x264_archive" <<'PYTHON'
from pathlib import Path
import sys
from zipfile import ZipFile

output, *sources = map(Path, sys.argv[1:])
with ZipFile(output / "recording-encoder-source.zip", "w") as archive:
    for source in [*sources, output / "ffmpeg-license.txt"]:
        archive.write(source, source.name)
PYTHON
