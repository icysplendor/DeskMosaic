#!/usr/bin/env bash

# Finder and metadata services can briefly retain the image after its window
# closes. Call only for an image owned by this packaging process, after writes
# have finished. Never convert an image until detach actually succeeds.
detach_dmg() {
  local device="$1"
  local attempt
  sync
  for attempt in 1 2 3 4 5; do
    if hdiutil detach "${device}"; then
      return 0
    fi
    if [ "${attempt}" -lt 5 ]; then
      echo "DMG is still busy; retrying detach (${attempt}/5)..." >&2
      sleep 2
    fi
  done
  echo "DMG remains busy after retries; forcing detach of ${device}..." >&2
  hdiutil detach "${device}" -force
}
