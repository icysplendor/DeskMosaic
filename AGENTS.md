# Repository agent instructions

## Git workflow

- This project uses the `main` branch. Do not create or switch branches unless the user explicitly requests it.
- After completing and validating any repository update, automatically stage the files that belong to the current task, create a concise commit, and push the current `main` branch to `origin`.
- GitHub Actions is manual-only. Keep workflows limited to `workflow_dispatch`, and never dispatch, rerun, or otherwise start a GitHub Actions workflow after a push unless the user explicitly asks for that run.
- Do not wait for a separate commit or push request unless the user explicitly asks not to commit or not to push.
- Never include unrelated user changes in a commit. If unrelated or ambiguous changes are present, preserve them and stage only the task-related paths; ask the user before proceeding only when the scopes cannot be separated safely.
- If commit or push is blocked by authentication, permissions, conflicts, failed checks, or remote changes, report the exact blocker and keep the local work intact.

## Cross-platform screenshot parity

- The macOS screenshot implementation lives in `MACAssistant/Screenshot/`; the Windows implementation lives in `WinAssistant/Screenshot/`.
- Any change to screenshot capture, multi-monitor behavior, smart window selection, selection resizing, toolbar layout, annotation tools, color selection, exact pixel-size input, undo, cancel/finish gestures, clipboard output, screenshot shortcuts, or related user-facing documentation must be reviewed for both implementations in the same task.
- Keep the two platforms behaviorally equivalent unless an operating-system API makes that impossible. When a platform-specific difference is unavoidable, document it in both the root README and the affected platform code instead of silently allowing the implementations to drift.
- Screenshot-related validation must include both the macOS Xcode target and the Windows `WinAssistant/WinAssistant.csproj` build whenever the available environment supports them. CI changes must preserve both DMG and EXE artifacts.

## Unified product and audio parity

- This repository is one cross-platform assistant, not separate screenshot and audio products. Existing screenshot, display, and mouse behavior must remain available when audio features change.
- The macOS audio implementation lives in `MACAssistant/Audio/`; the Windows implementation lives in `WinAssistant/Audio/`. The shared behavioral contract is documented in `spec.md`.
- Any change to audio device discovery, paired output/input profiles, profile switching, volume control or memory, default-device lock recovery, audio hotkeys, tray/menu-bar state, theme/language, startup behavior, configuration migration, or related documentation must be reviewed for both platforms in the same task.
- Keep device identity stable by using Core Audio device UIDs on macOS and MMDevice endpoint IDs on Windows. Never persist transient list indexes or display names as identity.
- UI code must call the platform audio state/service layer instead of invoking Core Audio or COM directly. System API calls belong in `MACAssistant/Audio/` or `WinAssistant/Audio/Core/`.
- Preserve recoverable configuration writes. macOS uses the primary JSON, backup JSON, and UserDefaults mirror; Windows uses the primary and backup JSON files. Schema additions must decode missing fields with safe defaults.
- Audio validation must include the macOS Xcode target and Windows `WinAssistant/WinAssistant.csproj` build whenever supported. Manual device testing should cover built-in, USB/Bluetooth, disconnected preferred devices, external default-device changes, volume restoration, and hotkey collisions.
- A platform API difference must be recorded in `spec.md` and the root README; do not silently remove the matching user-visible behavior.

## Cross-platform visual parity

- The Windows Audio Control Center source is the canonical visual baseline; `docs/Unified-Control-Center-UI.png` is supporting reference only. Preserve its dashboard hierarchy, color tokens, fixed typography scale, 8px cards, compact rows, and size-to-content tray flyout on both platforms. The audio page manages paired output+input profiles instead of separate favorite-device lists. Add screenshot and platform-specific tools as matching page tabs rather than inventing a separate sidebar or visual system.
- The settings window uses an application-drawn 52px title bar on both platforms instead of visible system title-bar chrome. Its shell follows the compact PulseDock/notification reference: real application icon and name at the leading edge, centered page context with an inline running indicator, a compact light/dark switch, a single bottom divider, and platform-appropriate window controls. Preserve drag/double-click behavior and resize support when changing it; do not replace the application icon with a generic colored glyph.
- UI redesigns must cover the unified product as a whole: overview, audio, screenshot, hotkeys, general settings, and the macOS display/mouse pages. Never replace or hide an existing product capability merely to make an audio-centric layout simpler.
- Keep output and input selectors and compact current-device controls behaviorally and visually symmetric. UI code must consume the existing manager/state layer; visual work must not duplicate device identity, profile persistence, or system API logic in views.

## Local desktop builds

- Build the macOS Apple Silicon app on an Apple Silicon Mac with Xcode selected and pkg-config installed. The Linux Docker host can cross-build Windows but does not run the native Xcode, codesign, and hdiutil pipeline. The same macOS script can run inside a macOS virtual machine on an Apple Silicon host.
- For local Mac packages, keep one certificate-backed Code Signing identity in the Mac's keychain. Set DESKMOSAIC_MAC_SIGN_IDENTITY to that exact identity and run bash scripts/build-local-macos.sh. The script uses it for the application, helpers and dylibs and verifies subsequent packages in the same output directory against the original app’s signing requirement. Set DESKMOSAIC_MAC_REFERENCE_APP to a previous copy signed with the same identity when testing upgrade compatibility. Confirm screen recording, microphone and Accessibility permissions by launching both versions on a Mac. Keep private keys out of the repository and build images. Public Developer ID signing and notarization remain in the GitHub workflow.
- For a local Windows x64 package, build and reuse the Windows builder image. Run these commands from the checkout root. The output directory also retains checked download inputs for subsequent builds.

  ```sh
  docker build -f scripts/windows-local.Dockerfile -t deskmosaic-windows-builder:net8 scripts
  mkdir -p build/local-windows
  docker run --rm --platform linux/amd64 --user "$(id -u):$(id -g)" \
    -v "$PWD:/src:ro" -v "$PWD/build/local-windows:/output" \
    deskmosaic-windows-builder:net8 bash /src/scripts/build-local-windows.sh /output
  ```
