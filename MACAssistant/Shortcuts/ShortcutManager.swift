import AppKit
import Carbon
import SwiftUI

struct KeyboardShortcut: Codable, Hashable {
    let keyCode: UInt32
    let modifiers: UInt32

    var displayText: String {
        var parts: [String] = []
        if modifiers & UInt32(controlKey) != 0 { parts.append("⌃") }
        if modifiers & UInt32(optionKey) != 0 { parts.append("⌥") }
        if modifiers & UInt32(shiftKey) != 0 { parts.append("⇧") }
        if modifiers & UInt32(cmdKey) != 0 { parts.append("⌘") }
        parts.append(Self.keyName(for: keyCode))
        return parts.joined()
    }

    static func from(event: NSEvent) -> KeyboardShortcut {
        var carbonModifiers: UInt32 = 0
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        if flags.contains(.control) { carbonModifiers |= UInt32(controlKey) }
        if flags.contains(.option) { carbonModifiers |= UInt32(optionKey) }
        if flags.contains(.shift) { carbonModifiers |= UInt32(shiftKey) }
        if flags.contains(.command) { carbonModifiers |= UInt32(cmdKey) }
        return KeyboardShortcut(keyCode: UInt32(event.keyCode), modifiers: carbonModifiers)
    }

    private static func keyName(for keyCode: UInt32) -> String {
        let names: [UInt32: String] = [
            0: "A", 1: "S", 2: "D", 3: "F", 4: "H", 5: "G", 6: "Z", 7: "X",
            8: "C", 9: "V", 11: "B", 12: "Q", 13: "W", 14: "E", 15: "R",
            16: "Y", 17: "T", 18: "1", 19: "2", 20: "3", 21: "4", 22: "6",
            23: "5", 24: "=", 25: "9", 26: "7", 27: "-", 28: "8", 29: "0",
            30: "]", 31: "O", 32: "U", 33: "[", 34: "I", 35: "P", 37: "L",
            38: "J", 39: "'", 40: "K", 41: ";", 42: "\\", 43: ",", 44: "/",
            45: "N", 46: "M", 47: ".", 49: "Space", 50: "`", 53: "Esc",
            96: "F5", 97: "F6", 98: "F7", 99: "F3", 100: "F8", 101: "F9",
            103: "F11", 109: "F10", 111: "F12", 115: "Home", 116: "Page Up",
            117: "Delete", 119: "End", 121: "Page Down", 123: "←", 124: "→",
            125: "↓", 126: "↑"
        ]
        return names[keyCode] ?? "Key \(keyCode)"
    }
}

struct ShortcutRecorder: NSViewRepresentable {
    @Binding var shortcut: KeyboardShortcut?

    func makeNSView(context: Context) -> ShortcutRecorderNSView {
        let view = ShortcutRecorderNSView()
        view.onChange = { shortcut = $0 }
        view.shortcut = shortcut
        return view
    }

    func updateNSView(_ nsView: ShortcutRecorderNSView, context: Context) {
        nsView.shortcut = shortcut
        nsView.needsDisplay = true
    }
}

final class ShortcutRecorderNSView: NSView {
    var shortcut: KeyboardShortcut?
    var onChange: ((KeyboardShortcut?) -> Void)?
    private var isRecording = false

    override var acceptsFirstResponder: Bool { true }
    override var intrinsicContentSize: NSSize { NSSize(width: 145, height: 28) }

    override func mouseDown(with event: NSEvent) {
        isRecording = true
        window?.makeFirstResponder(self)
        GlobalShortcutManager.shared.isRecordingShortcut = true
        needsDisplay = true
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            finishRecording()
            return
        }
        if event.keyCode == 51 || event.keyCode == 117 {
            shortcut = nil
            onChange?(nil)
            finishRecording()
            return
        }

        let recorded = KeyboardShortcut.from(event: event)
        guard recorded.modifiers != 0 else {
            NSSound.beep()
            return
        }
        shortcut = recorded
        onChange?(recorded)
        finishRecording()
    }

    override func resignFirstResponder() -> Bool {
        finishRecording()
        return super.resignFirstResponder()
    }

    override func draw(_ dirtyRect: NSRect) {
        let path = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 6, yRadius: 6)
        (isRecording ? NSColor.controlAccentColor.withAlphaComponent(0.14) : NSColor.controlBackgroundColor)
            .setFill()
        path.fill()
        (isRecording ? NSColor.controlAccentColor : NSColor.separatorColor).setStroke()
        path.lineWidth = 1
        path.stroke()

        let text = isRecording ? "请按快捷键…" : (shortcut?.displayText ?? "未设置")
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 12, weight: shortcut == nil ? .regular : .medium),
            .foregroundColor: shortcut == nil ? NSColor.secondaryLabelColor : NSColor.labelColor
        ]
        let size = text.size(withAttributes: attributes)
        text.draw(
            at: NSPoint(x: (bounds.width - size.width) / 2, y: (bounds.height - size.height) / 2),
            withAttributes: attributes
        )
    }

    private func finishRecording() {
        isRecording = false
        GlobalShortcutManager.shared.isRecordingShortcut = false
        needsDisplay = true
    }
}

@MainActor
final class GlobalShortcutManager {
    static let shared = GlobalShortcutManager()

    var isRecordingShortcut = false {
        didSet { reload() }
    }

    private var eventHandler: EventHandlerRef?
    private var registrations: [EventHotKeyRef] = []
    private var actions: [UInt32: () -> Void] = [:]

    private init() {
        var eventType = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard),
            eventKind: UInt32(kEventHotKeyPressed)
        )
        InstallEventHandler(
            GetApplicationEventTarget(),
            globalHotKeyHandler,
            1,
            &eventType,
            Unmanaged.passUnretained(self).toOpaque(),
            &eventHandler
        )
    }

    func reload() {
        registrations.forEach { UnregisterEventHotKey($0) }
        registrations.removeAll()
        actions.removeAll()
        guard !isRecordingShortcut else { return }

        let settings = SettingsManager.shared
        register(settings.screenshotShortcut) {
            ScreenshotManager.shared.startCapture()
        }
        register(settings.notebookLastShortcut) { NotebookWindowController.shared.show(intent: "last") }
        register(settings.notebookQuickShortcut) { NotebookWindowController.shared.show(intent: "quick") }
        register(settings.translationShortcut) { TranslationWindowController.shared.show() }
        register(settings.mouseToggleShortcut) {
            let mouse = MouseScrollManager.shared
            mouse.setBehavior(mouse.behavior == .normal ? .reversed : .normal)
        }
        if AudioManager.shared.isEnabled {
            register(settings.nextAudioProfileShortcut) {
                AudioManager.shared.cycleProfile(direction: 1)
            }
            register(settings.previousAudioProfileShortcut) {
                AudioManager.shared.cycleProfile(direction: -1)
            }
        }

        for display in DisplayManager.shared.displays {
            for mode in display.modes {
                let actionID = display.shortcutIdentifier(for: mode)
                guard let shortcut = settings.displayShortcuts[actionID] else { continue }
                register(shortcut) {
                    guard
                        let currentDisplay = DisplayManager.shared.displays.first(where: { $0.id == display.id }),
                        let currentMode = currentDisplay.modes.first(where: { $0.id == mode.id })
                    else { return }
                    DisplayManager.shared.setMode(currentMode, for: currentDisplay)
                }
            }
        }
    }

    fileprivate func perform(id: UInt32) {
        actions[id]?()
    }

    private func register(_ shortcut: KeyboardShortcut?, action: @escaping () -> Void) {
        guard let shortcut else { return }
        let id = UInt32(actions.count + 1)
        let hotKeyID = EventHotKeyID(signature: 0x4D_41_43_41, id: id)
        var reference: EventHotKeyRef?
        let status = RegisterEventHotKey(
            shortcut.keyCode,
            shortcut.modifiers,
            hotKeyID,
            GetApplicationEventTarget(),
            0,
            &reference
        )
        if status == noErr, let reference {
            registrations.append(reference)
            actions[id] = action
        }
    }
}

private let globalHotKeyHandler: EventHandlerUPP = { _, event, userData in
    guard let event, let userData else { return OSStatus(eventNotHandledErr) }
    var hotKeyID = EventHotKeyID(signature: 0, id: 0)
    let status = GetEventParameter(
        event,
        EventParamName(kEventParamDirectObject),
        EventParamType(typeEventHotKeyID),
        nil,
        MemoryLayout<EventHotKeyID>.size,
        nil,
        &hotKeyID
    )
    guard status == noErr else { return status }
    let manager = Unmanaged<GlobalShortcutManager>.fromOpaque(userData).takeUnretainedValue()
    let actionID = hotKeyID.id
    Task { @MainActor in
        manager.perform(id: actionID)
    }
    return noErr
}
