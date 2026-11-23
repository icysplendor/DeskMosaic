#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "Usage: $0 <application.app> <output.dmg>" >&2
  exit 2
fi

app_path="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
output_path="$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"
script_root="$(cd "$(dirname "$0")" && pwd)"
source "${script_root}/detach-dmg.sh"
volume_name="DeskMosaic"
work_root="$(mktemp -d "${TMPDIR:-/tmp}/macassistant-dmg.XXXXXX")"
staging_root="${work_root}/staging"
readwrite_dmg="${work_root}/installer-rw.dmg"
device_path=""
mount_point=""

cleanup() {
  if [ -n "${device_path}" ]; then
    hdiutil detach "${device_path}" -force >/dev/null 2>&1 || true
  fi
  rm -rf "${work_root}" >/dev/null 2>&1 || true
}
trap cleanup EXIT

mkdir -p "${staging_root}/.background"
ditto "${app_path}" "${staging_root}/DeskMosaic.app"
ln -s /Applications "${staging_root}/Applications"
xcrun swift "${script_root}/make-dmg-background.swift" \
  "${staging_root}/.background/installer-background.png"

hdiutil create \
  -volname "${volume_name}" \
  -srcfolder "${staging_root}" \
  -fs HFS+ \
  -format UDRW \
  -ov \
  "${readwrite_dmg}"

attach_output="$(hdiutil attach \
  -readwrite \
  -noverify \
  -noautoopen \
  "${readwrite_dmg}")"
echo "${attach_output}"

device_path="${attach_output%%[[:space:]]*}"
mount_point="${attach_output##*$'\t'}"

if [ ! -d "${mount_point}" ]; then
  echo "Unable to determine the mounted DMG path from hdiutil output" >&2
  exit 1
fi

volume_name="$(basename "${mount_point}")"
background_path="${mount_point}/.background/installer-background.png"

if [ ! -f "${background_path}" ]; then
  echo "DMG background image is missing after mount: ${background_path}" >&2
  exit 1
fi

# Finder needs time to discover newly attached volumes on headless CI runners.
sleep 5

layout_succeeded=false
for attempt in 1 2 3; do
  if osascript - "${volume_name}" "${background_path}" <<'APPLESCRIPT'
on run argv
  set volumeName to item 1 of argv
  set backgroundPath to item 2 of argv
  set backgroundImage to (POSIX file backgroundPath) as alias
  tell application "Finder"
    tell disk volumeName
      open
      set dmgWindow to container window
      set current view of dmgWindow to icon view
      set toolbar visible of dmgWindow to false
      set statusbar visible of dmgWindow to false
      set pathbar visible of dmgWindow to false
      set bounds of dmgWindow to {180, 120, 720, 500}
      set viewOptions to icon view options of dmgWindow
      set arrangement of viewOptions to not arranged
      set icon size of viewOptions to 96
      set text size of viewOptions to 13
      set background picture of viewOptions to backgroundImage
      set position of item "DeskMosaic.app" to {140, 190}
      set position of item "Applications" to {400, 190}
      update without registering applications
      delay 3
      close dmgWindow
    end tell
  end tell
end run
APPLESCRIPT
  then
    layout_succeeded=true
    break
  fi
  echo "Finder layout attempt ${attempt} failed; retrying..." >&2
  sleep 3
done

if [ "${layout_succeeded}" != true ]; then
  echo "Unable to create the Finder drag-to-Applications layout" >&2
  exit 1
fi

if [ ! -f "${mount_point}/.DS_Store" ]; then
  echo "Finder did not persist the DMG window layout" >&2
  exit 1
fi

detach_dmg "${device_path}"
device_path=""

hdiutil convert \
  "${readwrite_dmg}" \
  -format UDZO \
  -imagekey zlib-level=9 \
  -ov \
  -o "${output_path}"

hdiutil verify "${output_path}"
