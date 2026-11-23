#!/usr/bin/env bash
set -euo pipefail
source "$(cd "$(dirname "$0")" && pwd)/detach-dmg.sh"

# Exercise the real retry helper without mounting disks or waiting on macOS.
for scenario in immediate retry force failure; do
  (
    calls=0
    waits=0
    flushes=0
    forced=false
    sync() { flushes=$((flushes + 1)); }
    sleep() { test "$1" = 2; waits=$((waits + 1)); }
    hdiutil() {
      test "$1" = detach && test "$2" = /dev/disk66 || return 99
      calls=$((calls + 1))
      if [ "$#" = 3 ]; then
        test "$3" = -force && test "$calls" = 6 || return 99
        forced=true
        if [ "$scenario" = failure ]; then return 16; fi
        return 0
      fi
      test "$#" = 2 || return 99
      case "$scenario" in
        immediate) return 0 ;;
        retry) if [ "$calls" = 3 ]; then return 0; fi ;;
      esac
      return 16
    }
    status=0
    detach_dmg /dev/disk66 >/dev/null 2>&1 || status=$?
    test "$flushes" = 1
    case "$scenario" in
      immediate) test "$status" = 0 && test "$calls" = 1 && test "$waits" = 0 && test "$forced" = false ;;
      retry) test "$status" = 0 && test "$calls" = 3 && test "$waits" = 2 && test "$forced" = false ;;
      force) test "$status" = 0 && test "$calls" = 6 && test "$waits" = 4 && test "$forced" = true ;;
      failure) test "$status" = 16 && test "$calls" = 6 && test "$waits" = 4 && test "$forced" = true ;;
    esac
    echo "PASS DMG detach: ${scenario}"
  )
done
