import ApplicationServices
import AppKit
import Combine
import Foundation

@MainActor
final class MouseScrollManager: ObservableObject {
    static let shared = MouseScrollManager()

    @Published private(set) var behavior: MouseScrollBehavior = .systemDefault
    @Published private(set) var hasAccessibilityPermission = false
    @Published private(set) var isRunning = false
    @Published private(set) var startupError: String?

    private let eventTap = EventTapManager()
    private var savedBehavior: MouseScrollBehavior

    private init() {
        let stored = PersistentConfigurationStore.shared.configuration.mouseBehavior
        savedBehavior = MouseScrollBehavior(rawValue: stored) ?? .normal
    }

    @discardableResult
    func setBehavior(_ requested: MouseScrollBehavior) -> Bool {
        guard applyBehavior(requested) else { return false }
        if savedBehavior != requested {
            savedBehavior = requested
            PersistentConfigurationStore.shared.update {
                $0.mouseBehavior = requested.rawValue
            }
        }
        return true
    }

    func stop() {
        eventTap.stop()
        isRunning = false
        if behavior != .systemDefault { behavior = .systemDefault }
    }

    func refreshPermissionStatus() {
        _ = applyBehavior(savedBehavior)
    }

    func openAccessibilitySettings() {
        refreshPermissionStatus()
        guard !hasAccessibilityPermission,
              let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
        else { return }
        NSWorkspace.shared.open(url)
    }

    private func applyBehavior(_ requested: MouseScrollBehavior) -> Bool {
        hasAccessibilityPermission = AXIsProcessTrusted()
        if requested == .systemDefault {
            stop()
            startupError = nil
            return true
        }
        guard hasAccessibilityPermission else {
            stop()
            startupError = permissionRecoveryMessage
            return false
        }
        guard eventTap.start() else {
            stop()
            startupError = "辅助功能权限已开启，但无法创建滚轮监听器。请移除辅助功能列表中的旧“DeskMosaic”，重新添加当前应用后再试。"
            return false
        }
        eventTap.behavior = requested
        isRunning = true
        if behavior != requested { behavior = requested }
        startupError = nil
        return true
    }

    private var permissionRecoveryMessage: String {
        "需要辅助功能权限。如果系统设置中已经开启，请先删除旧“DeskMosaic”条目，再通过“+”添加“应用程序”中的当前版本。CI 的临时签名会使 macOS 将更新后的应用视为新程序。"
    }
}
