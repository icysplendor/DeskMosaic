import AppKit
import Combine

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, ObservableObject {
    @Published private var quitPreparation: Task<Void, Never>? = nil
    private var quitApproved = false

    var isPreparingToQuit: Bool { quitPreparation != nil }

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
        guard !isRunningUnitTests, !quitApproved else { return .terminateNow }
        guard !isPreparingToQuit else { return .terminateCancel }
        let notebook = NotebookWindowController.shared
        let translation = TranslationWindowController.shared
        guard notebook.needsFlushBeforeQuit || translation.running else { return .terminateNow }

        // Returning terminateLater enters AppKit's modal run loop. Keep the
        // normal event loop available to WebKit and audio while saving.
        quitPreparation = Task {
            let deadline = Task {
                do { try await Task.sleep(nanoseconds: 50_000_000_000) }
                catch { return }
                guard let quitPreparation else { return }
                quitPreparation.cancel()
                self.quitPreparation = nil
                showQuitFailure("等待保存超时，应用仍在运行。请检查笔记和录音后重试。",
                                "Saving timed out. Check your notes and recording before quitting again.")
            }
            defer { deadline.cancel() }

            let recordingError = await translation.stopAndWait()
            guard !Task.isCancelled else { return }
            if let recordingError {
                deadline.cancel()
                quitPreparation = nil
                showQuitFailure(recordingError, recordingError)
                return
            }
            do {
                try await notebook.flushBeforeQuit()
            } catch {
                guard !Task.isCancelled else { return }
                deadline.cancel()
                quitPreparation = nil
                notebook.show()
                showQuitFailure("笔记尚未保存。请处理后再退出。\n" + error.localizedDescription,
                                "Notes could not be saved. Resolve the error before quitting.\n" + error.localizedDescription)
                return
            }
            guard !Task.isCancelled else { return }
            deadline.cancel()
            quitPreparation = nil
            guard !translation.running else {
                showQuitFailure("录写/翻译仍在运行，请停止后再退出。",
                                "Transcription or translation is running. Stop it before quitting.")
                return
            }
            quitApproved = true
            sender.terminate(nil)
            quitApproved = false
        }
        return .terminateCancel
    }

    private func showQuitFailure(_ chinese: String, _ english: String) {
        let isEnglish = SettingsManager.shared.language == "en-US"
        let alert = NSAlert()
        alert.messageText = isEnglish ? "Could not quit DeskMosaic" : "DeskMosaic 未能退出"
        alert.informativeText = isEnglish ? english : chinese
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
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
