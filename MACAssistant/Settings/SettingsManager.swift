import Combine
import Foundation
import ServiceManagement
import SwiftUI

@MainActor
final class SettingsManager: ObservableObject {
    static let shared = SettingsManager()

    @Published private(set) var launchesAtLogin = false
    @Published var loginItemError: String?
    @Published private(set) var notebookLastShortcut: KeyboardShortcut? = nil
    @Published private(set) var notebookQuickShortcut: KeyboardShortcut? = nil
    @Published private(set) var translationShortcut: KeyboardShortcut?
    @Published private(set) var translationOptions = TranslationOptions()
    @Published private(set) var screenshotShortcut: KeyboardShortcut? = nil
    @Published private(set) var screenshotAnnotationColor = "#FFFF0000"
    @Published private(set) var screenshotStrokeWidth = 3.0
    @Published private(set) var screenshotFontSize = 18.0
    @Published private(set) var screenshotFontWeight = "Medium"
    @Published private(set) var regionRecording = RegionRecordingSettings()
    @Published private(set) var mouseToggleShortcut: KeyboardShortcut? = nil
    @Published private(set) var displayShortcuts: [String: KeyboardShortcut] = [:]
    @Published private(set) var nextAudioProfileShortcut: KeyboardShortcut? = nil
    @Published private(set) var previousAudioProfileShortcut: KeyboardShortcut? = nil
    @Published private(set) var theme = "Light"
    @Published private(set) var language = "zh-CN"
    @Published private(set) var silentStart = true
    @Published var shortcutError: String?

    private let configurationStore = PersistentConfigurationStore.shared

    private init() {
        let configuration = configurationStore.configuration
        notebookLastShortcut = configuration.notebookLastShortcut
        notebookQuickShortcut = configuration.notebookQuickShortcut
        translationShortcut = configuration.translationShortcut
        translationOptions = configuration.translationOptions
        screenshotShortcut = configuration.screenshotShortcut
        screenshotAnnotationColor = Self.screenshotColors.contains(configuration.screenshotAnnotationColor) ? configuration.screenshotAnnotationColor : Self.screenshotColors[0]
        screenshotStrokeWidth = [1, 2, 3, 5, 8, 12].contains(configuration.screenshotStrokeWidth) ? configuration.screenshotStrokeWidth : 3
        screenshotFontSize = [14, 18, 24, 32, 44, 60].contains(configuration.screenshotFontSize) ? configuration.screenshotFontSize : 18
        screenshotFontWeight = ["Regular", "Medium", "SemiBold", "Bold"].contains(configuration.screenshotFontWeight) ? configuration.screenshotFontWeight : "Medium"
        regionRecording = configuration.regionRecording
        mouseToggleShortcut = configuration.mouseToggleShortcut
        displayShortcuts = configuration.displayShortcuts
        nextAudioProfileShortcut = configuration.nextAudioProfileShortcut
        previousAudioProfileShortcut = configuration.previousAudioProfileShortcut
        theme = configuration.theme
        language = configuration.language
        silentStart = configuration.silentStart
        refresh()
    }

    func refresh() {
        launchesAtLogin = SMAppService.mainApp.status == .enabled
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            loginItemError = nil
        } catch {
            loginItemError = error.localizedDescription
        }
        refresh()
    }

    func setNotebookLastShortcut(_ shortcut: KeyboardShortcut?) {
        guard validate(shortcut, excluding: notebookLastShortcut) else { return }
        notebookLastShortcut = shortcut
        configurationStore.update { $0.notebookLastShortcut = shortcut }
        shortcutError = nil
        GlobalShortcutManager.shared.reload()
    }
    func setNotebookQuickShortcut(_ shortcut: KeyboardShortcut?) {
        guard validate(shortcut, excluding: notebookQuickShortcut) else { return }
        notebookQuickShortcut = shortcut
        configurationStore.update { $0.notebookQuickShortcut = shortcut }
        shortcutError = nil
        GlobalShortcutManager.shared.reload()
    }
    func setTranslationShortcut(_ shortcut: KeyboardShortcut?) {
        guard validate(shortcut, excluding: translationShortcut) else { return }
        translationShortcut = shortcut
        configurationStore.update { $0.translationShortcut = shortcut }
        shortcutError = nil
        GlobalShortcutManager.shared.reload()
    }

    func translationBinding<Value>(_ key: WritableKeyPath<TranslationOptions, Value>) -> Binding<Value> {
        Binding(get: { self.translationOptions[keyPath: key] }, set: { value in
            var options = self.translationOptions
            options[keyPath: key] = value
            self.setTranslationOptions(options)
        })
    }

    func setTranslationOptions(_ options: TranslationOptions) {
        guard options != translationOptions else { return }
        translationOptions = options
        configurationStore.update { $0.translationOptions = options }
    }

    func setScreenshotShortcut(_ shortcut: KeyboardShortcut?) {
        guard validate(shortcut, excluding: screenshotShortcut) else { return }
        screenshotShortcut = shortcut
        configurationStore.update { $0.screenshotShortcut = shortcut }
        shortcutError = nil
        GlobalShortcutManager.shared.reload()
    }

    func setScreenshotAnnotationColor(_ value: String) {
        screenshotAnnotationColor = Self.screenshotColors.contains(value) ? value : Self.screenshotColors[0]
        configurationStore.update { $0.screenshotAnnotationColor = screenshotAnnotationColor }
    }

    private static let screenshotColors = [
        "#FFFF0000", "#FFFFF200", "#FF38BDF8", "#FF808080", "#FF708238",
        "#FF000000", "#FFFFFFFF", "#FF8A2BE2", "#FFFF8C00"
    ]

    func setScreenshotStrokeWidth(_ value: Double) {
        screenshotStrokeWidth = [1, 2, 3, 5, 8, 12].contains(value) ? value : 3
        configurationStore.update { $0.screenshotStrokeWidth = screenshotStrokeWidth }
    }

    func setScreenshotFontSize(_ value: Double) {
        screenshotFontSize = [14, 18, 24, 32, 44, 60].contains(value) ? value : 18
        configurationStore.update { $0.screenshotFontSize = screenshotFontSize }
    }

    func setRegionRecording<Value>(_ keyPath: WritableKeyPath<RegionRecordingSettings, Value>, _ value: Value) {
        regionRecording[keyPath: keyPath] = value
        configurationStore.update { $0.regionRecording = regionRecording }
    }

    func setScreenshotFontWeight(_ value: String) {
        screenshotFontWeight = ["Regular", "Medium", "SemiBold", "Bold"].contains(value) ? value : "Medium"
        configurationStore.update { $0.screenshotFontWeight = screenshotFontWeight }
    }

    func setMouseToggleShortcut(_ shortcut: KeyboardShortcut?) {
        guard validate(shortcut, excluding: mouseToggleShortcut) else { return }
        mouseToggleShortcut = shortcut
        configurationStore.update { $0.mouseToggleShortcut = shortcut }
        shortcutError = nil
        GlobalShortcutManager.shared.reload()
    }

    func setDisplayShortcut(_ shortcut: KeyboardShortcut?, actionID: String) {
        guard validate(shortcut, excluding: displayShortcuts[actionID]) else { return }
        if let shortcut {
            displayShortcuts[actionID] = shortcut
        } else {
            displayShortcuts.removeValue(forKey: actionID)
        }
        let updatedShortcuts = displayShortcuts
        configurationStore.update { $0.displayShortcuts = updatedShortcuts }
        shortcutError = nil
        GlobalShortcutManager.shared.reload()
    }

    func setNextAudioProfileShortcut(_ shortcut: KeyboardShortcut?) {
        guard validate(shortcut, excluding: nextAudioProfileShortcut) else { return }
        nextAudioProfileShortcut = shortcut
        configurationStore.update { $0.nextAudioProfileShortcut = shortcut }
        shortcutError = nil
        GlobalShortcutManager.shared.reload()
    }

    func setPreviousAudioProfileShortcut(_ shortcut: KeyboardShortcut?) {
        guard validate(shortcut, excluding: previousAudioProfileShortcut) else { return }
        previousAudioProfileShortcut = shortcut
        configurationStore.update { $0.previousAudioProfileShortcut = shortcut }
        shortcutError = nil
        GlobalShortcutManager.shared.reload()
    }

    func setTheme(_ value: String) {
        theme = value == "Dark" ? "Dark" : "Light"
        configurationStore.update { $0.theme = theme }
    }

    func setLanguage(_ value: String) {
        language = value == "en-US" ? "en-US" : "zh-CN"
        configurationStore.update { $0.language = language }
    }

    func setSilentStart(_ enabled: Bool) {
        silentStart = enabled
        configurationStore.update { $0.silentStart = enabled }
    }

    private func validate(_ shortcut: KeyboardShortcut?, excluding oldValue: KeyboardShortcut?) -> Bool {
        guard let shortcut, shortcut != oldValue else { return true }
        let assigned = [notebookLastShortcut, notebookQuickShortcut, translationShortcut, screenshotShortcut, mouseToggleShortcut, nextAudioProfileShortcut, previousAudioProfileShortcut]
            .compactMap { $0 } + Array(displayShortcuts.values)
        guard !assigned.contains(shortcut) else {
            shortcutError = "该快捷键已被 DeskMosaic中的其他操作使用。"
            return false
        }
        return true
    }

}
