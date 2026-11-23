import AppKit
import Combine
import CoreGraphics
import Foundation

@MainActor
final class DisplayManager: ObservableObject {
    static let shared = DisplayManager()

    @Published private(set) var displays: [ConnectedDisplay] = []
    @Published var lastError: String?

    private var refreshTask: Task<Void, Never>?
    private var isApplyingDisplayConfiguration = false
    private var shouldRestoreOnNextRefresh = false

    private init() {
        let context = UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque())
        CGDisplayRegisterReconfigurationCallback(displayReconfigurationCallback, context)
    }

    func refresh(restoreSavedModes: Bool = false) {
        var count: UInt32 = 0
        guard CGGetOnlineDisplayList(0, nil, &count) == .success, count > 0 else {
            displays = []
            return
        }

        var ids = Array(repeating: CGDirectDisplayID(), count: Int(count))
        guard CGGetOnlineDisplayList(count, &ids, &count) == .success else {
            displays = []
            return
        }

        var screenDetails: [CGDirectDisplayID: ScreenDetails] = [:]
        for screen in NSScreen.screens {
            guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else {
                continue
            }
            let displayID = CGDirectDisplayID(number.uint32Value)
            screenDetails[displayID] = ScreenDetails(
                name: screen.localizedName,
                refreshRate: Double(screen.maximumFramesPerSecond)
            )
        }

        var connectedDisplays: [ConnectedDisplay] = []
        for displayID in ids.prefix(Int(count)) {
            var allModes = (CGDisplayCopyAllDisplayModes(displayID, nil) as? [CGDisplayMode]) ?? []
            let detectionOptions = [
                kCGDisplayShowDuplicateLowResolutionModes as String: true
            ] as CFDictionary
            let detectionModes =
                (CGDisplayCopyAllDisplayModes(displayID, detectionOptions) as? [CGDisplayMode])
                ?? allModes
            let currentCGMode = CGDisplayCopyDisplayMode(displayID)
            let currentModeID = currentCGMode?.ioDisplayModeID
            if let currentCGMode,
               !allModes.contains(where: { $0.ioDisplayModeID == currentCGMode.ioDisplayModeID }) {
                allModes.append(currentCGMode)
            }
            let isBuiltIn = CGDisplayIsBuiltin(displayID) != 0

            let fallbackRate = screenDetails[displayID]?.refreshRate ?? 60
            var candidates: [DisplayMode] = []
            for cgMode in allModes {
                candidates.append(DisplayMode(cgMode, fallbackRefreshRate: fallbackRate))
            }
            let nativeResolution = nativeResolution(
                from: detectionModes,
                physicalSize: CGDisplayScreenSize(displayID)
            )
            let modes = filteredModes(candidates, currentModeID: currentModeID)

            let fallbackName = isBuiltIn ? "内置显示器" : "外接显示器"
            let stableIdentifier = [
                String(CGDisplayVendorNumber(displayID)),
                String(CGDisplayModelNumber(displayID)),
                String(CGDisplaySerialNumber(displayID)),
                String(CGDisplayUnitNumber(displayID))
            ].joined(separator: "-")
            connectedDisplays.append(
                ConnectedDisplay(
                    id: displayID,
                    name: screenDetails[displayID]?.name ?? fallbackName,
                    stableIdentifier: stableIdentifier,
                    currentModeID: currentModeID,
                    nativeResolution: nativeResolution,
                    modes: modes
                )
            )
        }
        displays = connectedDisplays
        GlobalShortcutManager.shared.reload()
        if restoreSavedModes {
            restorePreferredDisplayModeIfNeeded()
        }
    }

    func setMode(_ mode: DisplayMode, for display: ConnectedDisplay) {
        let result = applyPermanently(mode, to: display)
        if result == .success {
            lastError = nil
            let preference = DisplayModePreference(mode: mode)
            PersistentConfigurationStore.shared.update {
                $0.preferredDisplayModes[display.stableIdentifier] = preference
            }
            updateCurrentMode(mode.id, for: display.id)
            scheduleRefresh()
        } else {
            lastError = "无法切换 \(display.name) 的显示模式（错误 \(result.rawValue)）"
            presentError(lastError!)
        }
    }

    private func applyPermanently(_ mode: DisplayMode, to display: ConnectedDisplay) -> CGError {
        var configuration: CGDisplayConfigRef?
        let beginResult = CGBeginDisplayConfiguration(&configuration)
        guard beginResult == .success, let configuration else { return beginResult }

        let configureResult = CGConfigureDisplayWithDisplayMode(
            configuration,
            display.id,
            mode.cgMode,
            nil
        )
        guard configureResult == .success else {
            _ = CGCancelDisplayConfiguration(configuration)
            return configureResult
        }
        return CGCompleteDisplayConfiguration(configuration, .permanently)
    }

    private func restorePreferredDisplayModeIfNeeded() {
        guard !isApplyingDisplayConfiguration else { return }
        let preferences = PersistentConfigurationStore.shared.configuration.preferredDisplayModes

        for display in displays {
            guard
                let preference = preferences[display.stableIdentifier],
                let preferredMode =
                    display.modes.first(where: preference.matches)
                    ?? display.modes.first(where: preference.matchesGeometry),
                display.currentModeID != preferredMode.id
            else {
                continue
            }

            isApplyingDisplayConfiguration = true
            let result = applyPermanently(preferredMode, to: display)
            isApplyingDisplayConfiguration = false
            if result == .success {
                updateCurrentMode(preferredMode.id, for: display.id)
                scheduleRefresh()
            } else {
                lastError = "无法恢复 \(display.name) 的已保存显示模式（错误 \(result.rawValue)）"
            }
            // Apply one mode per callback cycle so macOS can finish rebuilding its
            // display list before another display is configured.
            break
        }
    }

    private func updateCurrentMode(_ modeID: Int32, for displayID: CGDirectDisplayID) {
        guard let index = displays.firstIndex(where: { $0.id == displayID }) else { return }
        displays[index].currentModeID = modeID
    }

    private func scheduleRefresh(restoreSavedModes: Bool = false) {
        shouldRestoreOnNextRefresh = shouldRestoreOnNextRefresh || restoreSavedModes
        refreshTask?.cancel()
        refreshTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 350_000_000)
            guard !Task.isCancelled else { return }
            guard let self else { return }
            let shouldRestore = shouldRestoreOnNextRefresh
            shouldRestoreOnNextRefresh = false
            refresh(restoreSavedModes: shouldRestore)
        }
    }

    private func presentError(_ message: String) {
        let alert = NSAlert()
        alert.messageText = "分辨率切换失败"
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.runModal()
    }

    private func filteredModes(
        _ candidates: [DisplayMode],
        currentModeID: Int32?
    ) -> [DisplayMode] {
        var orderedCandidates: [DisplayMode] = []
        if let currentModeID, let current = candidates.first(where: { $0.id == currentModeID }) {
            orderedCandidates.append(current)
        }
        orderedCandidates.append(
            contentsOf: candidates
                .filter { $0.id != currentModeID }
                .sorted {
                    ($0.pixelWidth * $0.pixelHeight) > ($1.pixelWidth * $1.pixelHeight)
                }
        )

        var identities = Set<DisplayModeIdentity>()
        var uniqueModes: [DisplayMode] = []
        for mode in orderedCandidates {
            let identity = DisplayModeIdentity(mode)
            let isCurrent = mode.id == currentModeID
            let shouldShow = mode.isUsableForDesktopGUI
            guard (shouldShow || isCurrent), identities.insert(identity).inserted else {
                continue
            }
            uniqueModes.append(mode)
        }

        return uniqueModes.sorted { left, right in
            if left.id == currentModeID { return true }
            if right.id == currentModeID { return false }
            if left.width != right.width { return left.width > right.width }
            if left.height != right.height { return left.height > right.height }
            return left.refreshRate > right.refreshRate
        }
    }

    private func nativeResolution(
        from modes: [CGDisplayMode],
        physicalSize: CGSize
    ) -> DisplayResolution? {
        let oneXModes = modes.filter {
            $0.width == $0.pixelWidth && $0.height == $0.pixelHeight
        }
        let nativeModes = oneXModes.filter {
            $0.ioFlags & displayModeNativeFlag != 0
        }
        let fallbackModes = oneXModes.filter {
            $0.isUsableForDesktopGUI()
        }
        let physicalAspectRatio: Double?
        if physicalSize.width > 0, physicalSize.height > 0 {
            physicalAspectRatio = Double(physicalSize.width / physicalSize.height)
        } else {
            physicalAspectRatio = nil
        }
        let proportionalFallbackModes = fallbackModes.filter { mode in
            guard let physicalAspectRatio else { return true }
            let modeAspectRatio = Double(mode.pixelWidth) / Double(mode.pixelHeight)
            return abs(modeAspectRatio - physicalAspectRatio) / physicalAspectRatio < 0.02
        }
        let fallbackPool =
            proportionalFallbackModes.isEmpty ? fallbackModes : proportionalFallbackModes
        let bestMode = (nativeModes.isEmpty ? fallbackPool : nativeModes).max {
            ($0.pixelWidth * $0.pixelHeight) < ($1.pixelWidth * $1.pixelHeight)
        }

        guard let bestMode else { return nil }
        return DisplayResolution(width: bestMode.pixelWidth, height: bestMode.pixelHeight)
    }

    fileprivate func displayConfigurationDidChange(flags: CGDisplayChangeSummaryFlags) {
        guard !flags.contains(.beginConfigurationFlag) else { return }
        scheduleRefresh(restoreSavedModes: flags.contains(.addFlag))
    }
}

// IOKit/IOGraphicsTypes.h: kDisplayModeNativeFlag.
private let displayModeNativeFlag: UInt32 = 0x02000000

private struct ScreenDetails {
    let name: String
    let refreshRate: Double
}

private struct DisplayModeIdentity: Hashable {
    let width: Int
    let height: Int
    let refreshRate: Double
    let isHiDPI: Bool

    init(_ mode: DisplayMode) {
        width = mode.width
        height = mode.height
        refreshRate = mode.refreshRate.rounded(toPlaces: 2)
        isHiDPI = mode.isHiDPI
    }
}

private let displayReconfigurationCallback: CGDisplayReconfigurationCallBack = { _, flags, userInfo in
    guard let userInfo else { return }
    let manager = Unmanaged<DisplayManager>.fromOpaque(userInfo).takeUnretainedValue()
    Task { @MainActor in
        manager.displayConfigurationDidChange(flags: flags)
    }
}

private extension Double {
    func rounded(toPlaces places: Int) -> Double {
        let divisor = pow(10, Double(places))
        return (self * divisor).rounded() / divisor
    }
}
