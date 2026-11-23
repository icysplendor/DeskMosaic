import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        // The XCTest bundle is hosted by the application binary. Starting hardware
        // discovery, Carbon hotkeys, or long-lived observers in that host can keep
        // a headless CI test session alive before XCTest begins executing cases.
        guard !isRunningUnitTests else { return }

        DisplayManager.shared.refresh(restoreSavedModes: true)
        GlobalShortcutManager.shared.reload()
        AudioManager.shared.start()
        NotebookWindowController.shared.start()
        // Permission guidance remains available in the menu and settings.
        MouseScrollManager.shared.refreshPermissionStatus()
        if !PersistentConfigurationStore.shared.configuration.silentStart {
            SettingsWindowController.shared.show()
        }
        NSWorkspace.shared.notificationCenter.addObserver(
            self,
            selector: #selector(workspaceDidWake),
            name: NSWorkspace.didWakeNotification,
            object: nil
        )
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard !isRunningUnitTests else { return .terminateNow }
        Task { @MainActor in
            do {
                try await NotebookWindowController.shared.flushBeforeQuit()
                await TranslationWindowController.shared.stopAndWait()
                sender.reply(toApplicationShouldTerminate: true)
            } catch {
                NotebookWindowController.shared.show()
                let alert = NSAlert()
                alert.messageText = "笔记尚未成功保存"
                alert.informativeText = "已保留窗口，请处理保存错误后再退出。\n" + error.localizedDescription
                alert.runModal()
                sender.reply(toApplicationShouldTerminate: false)
            }
        }
        return .terminateLater
    }

    func applicationWillTerminate(_ notification: Notification) {
        NSWorkspace.shared.notificationCenter.removeObserver(self)
        TranslationWindowController.shared.stop()
        MouseScrollManager.shared.stop()
        AudioManager.shared.stop()
    }

    @objc private func workspaceDidWake(_ notification: Notification) {
        Task { @MainActor in
            MouseScrollManager.shared.stop()
            MouseScrollManager.shared.refreshPermissionStatus()
            DisplayManager.shared.refresh(restoreSavedModes: true)
            AudioManager.shared.refresh()
            NotebookWindowController.shared.wake()
        }
    }

    func applicationDidBecomeActive(_ notification: Notification) {
        guard !isRunningUnitTests else { return }
        MouseScrollManager.shared.refreshPermissionStatus()
        DisplayManager.shared.refresh()
        AudioManager.shared.refresh()
    }

    private var isRunningUnitTests: Bool {
        let environment = ProcessInfo.processInfo.environment
        return environment["XCTestConfigurationFilePath"] != nil
            || environment["XCTestBundlePath"] != nil
            || environment["XCInjectBundleInto"] != nil
            || NSClassFromString("XCTestCase") != nil
    }
}
