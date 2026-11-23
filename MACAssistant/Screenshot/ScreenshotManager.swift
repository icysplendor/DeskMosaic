import AppKit
import CoreGraphics
import ScreenCaptureKit
import UniformTypeIdentifiers

@MainActor
final class ScreenshotManager {
    static let shared = ScreenshotManager()

    private var session: ScreenshotSession?
    private var isStarting = false
    private var ownsScreenshotCursor = false
    private var previousApplication: NSRunningApplication?

    func startCapture() {
        guard session == nil, !isStarting else { return }
        isStarting = true

        if !CGPreflightScreenCaptureAccess(), !CGRequestScreenCaptureAccess() {
            isStarting = false
            presentPermissionAlert()
            return
        }

        // Let a menu-triggered capture dismiss the menu before freezing the desktop.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.18) { [weak self] in
            self?.beginCapture()
        }
    }

    private func beginCapture() {
        defer { isStarting = false }

        let displays = NSScreen.screens.compactMap(CapturedDisplay.init(screen:))
        guard !displays.isEmpty else {
            presentFailureAlert("无法读取当前屏幕画面。")
            return
        }

        if NSWorkspace.shared.frontmostApplication?.processIdentifier != ProcessInfo.processInfo.processIdentifier {
            previousApplication = NSWorkspace.shared.frontmostApplication
        }

        let configuration = PersistentConfigurationStore.shared.configuration
        let annotationDefaults = ScreenshotAnnotationDefaults(
            color: configuration.screenshotAnnotationColor,
            strokeWidth: configuration.screenshotStrokeWidth,
            fontSize: configuration.screenshotFontSize,
            fontWeight: configuration.screenshotFontWeight
        )
        let captureSession = ScreenshotSession(
            displays: displays,
            windowCandidates: WindowCandidate.current(excluding: ProcessInfo.processInfo.processIdentifier),
            annotationDefaults: annotationDefaults,
            recordingSettings: configuration.regionRecording
        )
        captureSession.onCancel = { [weak self] in self?.closeCapture() }
        captureSession.onSave = { [weak self] in self?.closeCapture() }
        captureSession.onRecordingStarted = { [weak self] in
            guard let self else { return }
            self.releaseScreenshotCursor()
            self.previousApplication?.activate(options: [.activateIgnoringOtherApps])
            self.previousApplication = nil
        }
        captureSession.onFinish = { [weak self] image in
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            pasteboard.writeObjects([image])
            self?.closeCapture()
        }

        session = captureSession
        NSApp.activate(ignoringOtherApps: true)
        captureSession.present()
        NSCursor.crosshair.push()
        ownsScreenshotCursor = true
    }

    private func releaseScreenshotCursor() {
        if ownsScreenshotCursor {
            NSCursor.pop()
            ownsScreenshotCursor = false
        }
    }

    private func closeCapture() {
        releaseScreenshotCursor()
        session?.close()
        session = nil
        previousApplication?.activate(options: [.activateIgnoringOtherApps])
        previousApplication = nil
    }

    private func presentPermissionAlert() {
        let alert = NSAlert()
        alert.messageText = "需要屏幕录制权限"
        alert.informativeText = "请在“系统设置 → 隐私与安全性 → 屏幕与系统音频录制”中允许 DeskMosaic，然后重新截图。"
        alert.addButton(withTitle: "打开系统设置")
        alert.addButton(withTitle: "取消")
        if alert.runModal() == .alertFirstButtonReturn,
           let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }

    private func presentFailureAlert(_ message: String) {
        let alert = NSAlert()
        alert.messageText = "截图失败"
        alert.informativeText = message
        alert.runModal()
    }
}

// Bitmap contexts start in pixel coordinates. Image-representation size is metadata,
// not a transform: drawing a full point-sized screen into a thumbnail clips its
// corner and then magnifies that corner when the thumbnail is displayed.
enum ScreenshotBitmapRenderer {
    static func render(
        pointSize: NSSize,
        pixelWidth: Int,
        pixelHeight: Int,
        drawing: () -> Void
    ) -> NSImage? {
        guard pointSize.width > 0, pointSize.height > 0,
              pixelWidth > 0, pixelHeight > 0,
              let bitmap = NSBitmapImageRep(
                bitmapDataPlanes: nil,
                pixelsWide: pixelWidth,
                pixelsHigh: pixelHeight,
                bitsPerSample: 8,
                samplesPerPixel: 4,
                hasAlpha: true,
                isPlanar: false,
                colorSpaceName: .deviceRGB,
                bytesPerRow: 0,
                bitsPerPixel: 0
              ), let context = NSGraphicsContext(bitmapImageRep: bitmap) else { return nil }

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = context
        context.cgContext.scaleBy(
            x: CGFloat(pixelWidth) / pointSize.width,
            y: CGFloat(pixelHeight) / pointSize.height
        )
        drawing()
        NSGraphicsContext.restoreGraphicsState()

        // Set logical size after drawing; the explicit CTM above handles both
        // downsampled mosaic caches and Retina-resolution screenshot exports.
        bitmap.size = pointSize
        let output = NSImage(size: pointSize)
        output.addRepresentation(bitmap)
        return output
    }

    static func makeMosaicImage(image: CGImage, pointSize: NSSize) -> NSImage {
        let blockSize: CGFloat = 10
        return render(
            pointSize: pointSize,
            pixelWidth: max(1, Int(ceil(pointSize.width / blockSize))),
            pixelHeight: max(1, Int(ceil(pointSize.height / blockSize)))
        ) {
            let context = NSGraphicsContext.current!.cgContext
            context.interpolationQuality = .medium
            context.draw(image, in: NSRect(origin: .zero, size: pointSize))
        } ?? NSImage(cgImage: image, size: pointSize)
    }
}

enum ScreenshotBrushCursor {
    // One physical pixel per ring sample, matching Windows' monochrome XOR mask.
    static func path(pixelDiameter: CGFloat) -> CGPath {
        let radius = pixelDiameter / 2
        let extent = Int(ceil(radius + 1))
        let path = CGMutablePath()
        for y in -extent...extent {
            for x in -extent...extent {
                if (x != 0 || y != 0), abs(hypot(CGFloat(x), CGFloat(y)) - radius) <= 0.5 {
                    path.addRect(CGRect(x: CGFloat(x) - 0.5, y: CGFloat(y) - 0.5,
                                        width: 1, height: 1))
                }
            }
        }
        return path
    }

    static func draw(path: CGPath, center: NSPoint, backingScale: CGFloat, in context: CGContext) {
        context.saveGState()
        // Align the mask's cells to device pixels so edges never become a
        // partially blended color, and the hotspot's pixel remains untouched.
        context.translateBy(
            x: (floor(center.x * backingScale) + 0.5) / backingScale,
            y: (floor(center.y * backingScale) + 0.5) / backingScale
        )
        context.scaleBy(x: 1 / backingScale, y: 1 / backingScale)
        context.setShouldAntialias(false)
        // White in difference mode inverts each destination pixel separately.
        // Draw into the screenshot view itself, not a transparent sibling layer.
        context.setBlendMode(.difference)
        context.setFillColor(CGColor(gray: 1, alpha: 1))
        context.addPath(path)
        context.fillPath()
        context.restoreGState()
    }
}

private struct CapturedDisplay {
    let frame: NSRect
    let id: CGDirectDisplayID
    let image: CGImage
    let mosaicImage: NSImage

    init?(screen: NSScreen) {
        guard
            let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber,
            let image = CGDisplayCreateImage(CGDirectDisplayID(number.uint32Value))
        else { return nil }

        let frame = screen.frame
        self.frame = frame
        self.id = CGDirectDisplayID(number.uint32Value)
        self.image = image
        self.mosaicImage = ScreenshotBitmapRenderer.makeMosaicImage(image: image, pointSize: frame.size)
    }

    var pointImage: NSImage {
        NSImage(cgImage: image, size: frame.size)
    }

    var backingScale: CGFloat {
        max(CGFloat(image.width) / frame.width, CGFloat(image.height) / frame.height)
    }
}

private struct WindowCandidate {
    let frame: NSRect

    static func current(excluding processID: pid_t) -> [WindowCandidate] {
        guard let windowInfo = CGWindowListCopyWindowInfo(
            [.optionOnScreenOnly, .excludeDesktopElements],
            CGWindowID(0)
        ) as? [[String: Any]] else {
            return []
        }

        let mainHeight = NSScreen.screens.first(where: { $0.frame.origin == .zero })?.frame.height
            ?? NSScreen.main?.frame.height
            ?? 0

        // CGWindowList is ordered front-to-back, which is also the desired hit-test order.
        return windowInfo.compactMap { info in
            guard
                (info[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value != processID,
                (info[kCGWindowLayer as String] as? NSNumber)?.intValue == 0,
                ((info[kCGWindowAlpha as String] as? NSNumber)?.doubleValue ?? 1) > 0.01,
                let cgFrame = frame(from: info[kCGWindowBounds as String]),
                cgFrame.width >= 40,
                cgFrame.height >= 30
            else { return nil }

            // Quartz window coordinates start at the main display's top-left; AppKit uses
            // the main display's bottom-left. This conversion also handles displays above
            // or below the main display.
            let appKitFrame = NSRect(
                x: cgFrame.minX,
                y: mainHeight - cgFrame.maxY,
                width: cgFrame.width,
                height: cgFrame.height
            )
            return WindowCandidate(frame: appKitFrame)
        }
    }

    private static func frame(from value: Any?) -> CGRect? {
        guard
            let bounds = value as? [String: Any],
            let x = number(bounds["X"]),
            let y = number(bounds["Y"]),
            let width = number(bounds["Width"]),
            let height = number(bounds["Height"])
        else {
            return nil
        }
        return CGRect(x: x, y: y, width: width, height: height)
    }

    private static func number(_ value: Any?) -> CGFloat? {
        guard let number = value as? NSNumber else { return nil }
        return CGFloat(number.doubleValue)
    }
}

private enum ScreenshotTool: CaseIterable {
    case rectangle
    case arrow
    case pen
    case text
    case mosaic

    var title: String {
        switch self {
        case .rectangle: return "矩形"
        case .arrow: return "箭头"
        case .pen: return "画笔"
        case .text: return "文字"
        case .mosaic: return "马赛克"
        }
    }
}

private enum ScreenshotAnnotationShape {
    case rectangle(NSPoint, NSPoint)
    case arrow(NSPoint, NSPoint)
    case pen([NSPoint])
    case text(String, NSRect, CGFloat, NSFont.Weight)
    case mosaic([NSPoint], CGFloat)
}

private struct ScreenshotAnnotation {
    let shape: ScreenshotAnnotationShape
    let color: NSColor
    let strokeWidth: CGFloat

    var isMosaic: Bool {
        if case .mosaic = shape { return true }
        return false
    }
}

private struct ScreenshotAnnotationDefaults: Sendable {
    let color: String
    let strokeWidth: Double
    let fontSize: Double
    let fontWeight: String
}

private func annotationFontWeight(_ value: String) -> NSFont.Weight {
    switch value {
    case "Regular": return .regular
    case "SemiBold": return .semibold
    case "Bold": return .bold
    default: return .medium
    }
}

private extension NSColor {
    convenience init?(annotationHex value: String) {
        let hex = value.trimmingCharacters(in: CharacterSet(charactersIn: "#"))
        guard hex.count == 8, let raw = UInt32(hex, radix: 16) else { return nil }
        self.init(
            srgbRed: CGFloat((raw >> 16) & 0xFF) / 255,
            green: CGFloat((raw >> 8) & 0xFF) / 255,
            blue: CGFloat(raw & 0xFF) / 255,
            alpha: CGFloat((raw >> 24) & 0xFF) / 255
        )
    }
}

// TextKit supplies the same wrapping and glyph metrics as the native editor.
// Bounds stay in global AppKit points with their top edge at the click location.
enum ScreenshotTextLayout {
    static func layout(_ text: String, font: NSFont, width: CGFloat,
                       color: NSColor = .black) -> (NSTextStorage, NSLayoutManager, NSTextContainer) {
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineBreakMode = .byWordWrapping
        let storage = NSTextStorage(string: text, attributes: [
            .font: font, .foregroundColor: color, .paragraphStyle: paragraph
        ])
        let manager = NSLayoutManager()
        let container = NSTextContainer(containerSize: NSSize(width: max(1, width), height: CGFloat.greatestFiniteMagnitude))
        container.lineFragmentPadding = 0
        manager.addTextContainer(container)
        storage.addLayoutManager(manager)
        manager.ensureLayout(for: container)
        return (storage, manager, container)
    }

    static func contentHeight(_ text: String, font: NSFont, width: CGFloat) -> CGFloat {
        // Include an empty final line while typing, without changing saved text.
        let measured = text.isEmpty || text.hasSuffix("\n") ? text + " " : text
        let (storage, manager, container) = layout(measured, font: font, width: width)
        return withExtendedLifetime(storage) {
            ceil(max(manager.defaultLineHeight(for: font), manager.usedRect(for: container).height))
        }
    }

    static func editorBounds(_ text: String, font: NSFont, anchor: NSPoint, limits: NSRect) -> NSRect {
        let left = min(max(anchor.x, limits.minX), limits.maxX - min(80, limits.width))
        let (storage, manager, container) = layout(text.isEmpty ? "输入文字…" : text, font: font, width: 1_000_000)
        let naturalWidth = withExtendedLifetime(storage) { manager.usedRect(for: container).width }
        let width = min(limits.maxX - left, max(80, ceil(naturalWidth) + 2))
        let height = min(limits.height, contentHeight(text, font: font, width: width) + 2)
        let top = min(max(anchor.y, limits.minY + height), limits.maxY)
        return NSRect(x: left, y: top - height, width: width, height: height)
    }

    static func draw(_ text: String, in bounds: NSRect, font: NSFont, color: NSColor) {
        guard let graphics = NSGraphicsContext.current else { return }
        let (storage, manager, container) = layout(text, font: font, width: bounds.width, color: color)
        let context = graphics.cgContext
        NSGraphicsContext.saveGraphicsState()
        context.clip(to: bounds)
        context.translateBy(x: bounds.minX, y: bounds.maxY)
        context.scaleBy(x: 1, y: -1)
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
        withExtendedLifetime(storage) {
            let range = manager.glyphRange(for: container)
            manager.drawBackground(forGlyphRange: range, at: .zero)
            manager.drawGlyphs(forGlyphRange: range, at: .zero)
        }
        NSGraphicsContext.restoreGraphicsState()
    }
}

private enum ResizeHandle: CaseIterable {
    case topLeft, top, topRight, right, bottomRight, bottom, bottomLeft, left
}

private enum SelectionInteraction {
    case candidate(anchor: NSPoint, frame: NSRect)
    case selecting(anchor: NSPoint)
    case moving(origin: NSPoint, selection: NSRect)
    case resizing(handle: ResizeHandle, origin: NSPoint, selection: NSRect)
    case annotating(origin: NSPoint)
    case pen
    case movingAnnotation(index: Int, origin: NSPoint, annotation: ScreenshotAnnotation)
}

private enum ToolbarPicker: Equatable {
    case color
    case size
}

private enum SizeInputStage: Equatable {
    case width
    case height
}

private final class ScreenshotSession {
    let displays: [CapturedDisplay]
    let desktopFrame: NSRect
    let windowCandidates: [WindowCandidate]
    private let recordingSettings: RegionRecordingSettings

    var onFinish: ((NSImage) -> Void)?
    var onSave: (() -> Void)?
    var onCancel: (() -> Void)?
    var onRecordingStarted: (() -> Void)?

    private(set) var selection: NSRect?
    private(set) var hoverCandidate: NSRect?
    private(set) var activeTool: ScreenshotTool?
    private(set) var annotations: [ScreenshotAnnotation] = []
    private(set) var previewAnnotation: ScreenshotAnnotation?

    private var interaction: SelectionInteraction?
    private var penPoints: [NSPoint] = []
    private var textInputOrigin: NSPoint?
    private var textInputBuffer = ""
    private var textInputBounds = NSRect.zero
    private var textInputField: ScreenshotTextEditor?
    private var explicitOutputPixelSize: NSSize?
    private var selectedColor = NSColor.systemRed
    private var selectedStrokeWidth: CGFloat = 3
    private var selectedFontSize: CGFloat = 18
    private var selectedFontWeight: NSFont.Weight = .medium
    private var selectedMosaicBrushSize: CGFloat = 32
    private var showsMoreColors = false
    private var activePicker: ToolbarPicker?
    private var pickerSelectionIndex: Int?
    private var pickerAnchorOverride: NSRect?
    private var sizeInputStage: SizeInputStage?
    private var sizeInputBuffer = ""
    private var pendingInputWidth: Int?
    private var sizeInputError: String?
    private var isPresentingSavePanel = false
    private var isClosed = false
    private var isStartingRecording = false
    private var recording: RegionRecording?
    private var recordingTimer: Timer?
    private var recordingDisplays: [RecordingDisplay] = []
    private var recordingMosaics: [(rect: NSRect, image: NSImage)] = []
    private var locksSelection: Bool { isStartingRecording || recording != nil }
    private var keyEventMonitor: Any?
    private var rightMouseEventMonitor: Any?
    private var windows: [NSWindow] = []
    private var views: [ScreenshotOverlayView] = []
    private var recordingDrawingWindows: [NSWindow] = []
    private var recordingToolbarWindow: NSWindow?
    private var recordingPaletteWindow: NSWindow?
    private var recordingTextPreviousApp: NSRunningApplication?
    private var brushCursorFrame: NSRect?
    private var brushCursorPaths: [CGFloat: CGPath] = [:]
    private let transparentBrushCursor = NSCursor(
        image: NSImage(size: NSSize(width: 1, height: 1), flipped: false) { rect in
            NSGraphicsContext.current?.cgContext.clear(rect)
            return true
        }, hotSpot: .zero
    )
    private let quickColors: [NSColor] = [
        NSColor(srgbRed: 1, green: 0, blue: 0, alpha: 1),
        NSColor(srgbRed: 1, green: 242 / 255, blue: 0, alpha: 1),
        NSColor(srgbRed: 56 / 255, green: 189 / 255, blue: 248 / 255, alpha: 1),
        NSColor(srgbRed: 128 / 255, green: 128 / 255, blue: 128 / 255, alpha: 1)
    ]
    private let additionalColors: [NSColor] = [
        NSColor(srgbRed: 112 / 255, green: 130 / 255, blue: 56 / 255, alpha: 1),
        NSColor(srgbRed: 0, green: 0, blue: 0, alpha: 1),
        NSColor(srgbRed: 1, green: 1, blue: 1, alpha: 1),
        NSColor(srgbRed: 138 / 255, green: 43 / 255, blue: 226 / 255, alpha: 1),
        NSColor(srgbRed: 1, green: 140 / 255, blue: 0, alpha: 1)
    ]
    private let strokeWidths: [CGFloat] = [1, 2, 3, 5, 8, 12]
    private let fontSizes: [CGFloat] = [14, 18, 24, 32, 44, 60]
    private let mosaicBrushSizes: [CGFloat] = [12, 20, 32, 48, 72, 96]

    init(
        displays: [CapturedDisplay],
        windowCandidates: [WindowCandidate],
        annotationDefaults: ScreenshotAnnotationDefaults,
        recordingSettings: RegionRecordingSettings
    ) {
        self.displays = displays
        self.windowCandidates = windowCandidates
        self.recordingSettings = recordingSettings
        self.desktopFrame = displays.map(\.frame).dropFirst().reduce(displays[0].frame) { $0.union($1) }
        selectedColor = NSColor(annotationHex: annotationDefaults.color) ?? NSColor(srgbRed: 1, green: 0, blue: 0, alpha: 1)
        selectedStrokeWidth = CGFloat([1, 2, 3, 5, 8, 12].contains(annotationDefaults.strokeWidth) ? annotationDefaults.strokeWidth : 3)
        selectedFontSize = CGFloat([14, 18, 24, 32, 44, 60].contains(annotationDefaults.fontSize) ? annotationDefaults.fontSize : 18)
        selectedFontWeight = annotationFontWeight(annotationDefaults.fontWeight)
    }

    func present() {
        for display in displays {
            let view = ScreenshotOverlayView(frame: NSRect(origin: .zero, size: display.frame.size))
            view.session = self
            view.display = display

            let window = ScreenshotOverlayWindow(
                contentRect: display.frame,
                styleMask: .borderless,
                backing: .buffered,
                defer: false
            )
            window.level = .screenSaver
            window.isOpaque = true
            window.backgroundColor = .black
            window.hasShadow = false
            window.ignoresMouseEvents = false
            window.acceptsMouseMovedEvents = true
            window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            window.contentView = view
            window.setFrame(display.frame, display: false)
            window.orderFrontRegardless()

            windows.append(window)
            views.append(view)
        }

        if let window = window(at: NSEvent.mouseLocation) ?? windows.first,
           let view = window.contentView {
            window.makeKey()
            window.makeFirstResponder(view)
        }
        keyEventMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, !self.isPresentingSavePanel else { return event }
            if self.recording != nil, !self.isRecordingInput(event.window) { return event }
            if self.handleTextInputKey(event) {
                return nil
            }
            if self.textInputField != nil {
                return event
            }
            if event.keyCode == 53 {
                self.cancel()
                return nil
            }
            if
                event.modifierFlags.intersection(.deviceIndependentFlagsMask).contains(.command),
                event.charactersIgnoringModifiers?.lowercased() == "z"
            {
                self.undoLastAnnotation()
                return nil
            }
            if self.handleSizeInputKey(event) {
                return nil
            }
            return event
        }
        rightMouseEventMonitor = NSEvent.addLocalMonitorForEvents(
            matching: [.rightMouseDown, .rightMouseUp]
        ) { [weak self] event in
            guard let self, !self.isPresentingSavePanel else { return event }
            if self.recording != nil, !self.isRecordingInput(event.window) { return event }
            if event.type == .rightMouseUp {
                self.cancel()
            }
            return nil
        }
        updateHover(at: NSEvent.mouseLocation)
    }

    func close() {
        isClosed = true
        recordingTimer?.invalidate()
        recordingTimer = nil
        recording?.stop()
        recording = nil
        let sources = recordingDisplays
        recordingDisplays.removeAll()
        Task { for source in sources { await source.stop() } }
        if let keyEventMonitor {
            NSEvent.removeMonitor(keyEventMonitor)
            self.keyEventMonitor = nil
        }
        if let rightMouseEventMonitor {
            NSEvent.removeMonitor(rightMouseEventMonitor)
            self.rightMouseEventMonitor = nil
        }
        closeRecordingInputs()
        windows.forEach { $0.orderOut(nil) }
        windows.removeAll()
        views.removeAll()
    }

    func mouseMoved(to point: NSPoint) {
        if selection == nil, interaction == nil {
            updateHover(at: point)
        }
        updateCursor(at: point)
    }

    func recordingInputExited() {
        setBrushCursorFrame(nil)
    }

    func mouseDown(at point: NSPoint, clickCount: Int) {
        if recording == nil { focusWindow(at: point) }

        if textInputOrigin != nil {
            commitTextInput()
        }

        if !locksSelection, clickCount == 2, let selection, selection.contains(point) {
            finish()
            return
        }
        if handleMoreColorPaletteClick(at: point) {
            return
        }
        if handleToolbarClick(at: point) {
            return
        }
        showsMoreColors = false
        cancelSizeInput()

        guard let selection else {
            annotations.removeAll()
            explicitOutputPixelSize = nil
            if let candidate = hoverCandidate {
                self.selection = candidate
                interaction = .candidate(anchor: point, frame: candidate)
            } else {
                self.selection = NSRect(origin: point, size: .zero)
                interaction = .selecting(anchor: point)
            }
            redraw()
            return
        }

        if locksSelection && (!selection.contains(point)
            || activeTool == nil && annotationIndex(at: point) == nil) { return }

        if !locksSelection, let handle = hitHandle(at: point, selection: selection) {
            interaction = .resizing(handle: handle, origin: point, selection: selection)
            cursor(for: handle).set()
        } else if let index = annotationIndex(at: point) {
            interaction = .movingAnnotation(
                index: index,
                origin: point,
                annotation: annotations[index]
            )
            NSCursor.closedHand.set()
        } else if activeTool == nil {
            if selection.contains(point) {
                interaction = .moving(origin: point, selection: selection)
                NSCursor.closedHand.set()
            } else {
                annotations.removeAll()
                explicitOutputPixelSize = nil
                hoverCandidate = candidate(at: point)?.frame
                if let candidate = hoverCandidate {
                    self.selection = candidate
                    interaction = .candidate(anchor: point, frame: candidate)
                } else {
                    self.selection = NSRect(origin: point, size: .zero)
                    interaction = .selecting(anchor: point)
                }
            }
        } else if selection.contains(point) {
            if activeTool == .text {
                beginTextInput(at: point)
            } else if activeTool == .pen || activeTool == .mosaic {
                penPoints = [point]
                if activeTool == .mosaic {
                    previewAnnotation = ScreenshotAnnotation(
                        shape: .mosaic(penPoints, selectedMosaicBrushSize),
                        color: .clear,
                        strokeWidth: 0
                    )
                }
                interaction = .pen
            } else {
                interaction = .annotating(origin: point)
            }
        }
        updateCursor(at: point)
        redraw()
    }

    func mouseDragged(to point: NSPoint) {
        if activePicker != nil {
            updatePickerSelection(at: point)
            return
        }
        guard let interaction else { return }

        switch interaction {
        case let .candidate(anchor, frame):
            if hypot(point.x - anchor.x, point.y - anchor.y) >= 4 {
                annotations.removeAll()
                explicitOutputPixelSize = nil
                selection = normalizedRect(from: anchor, to: constrained(point))
                self.interaction = .selecting(anchor: anchor)
            } else {
                selection = frame
            }
        case let .selecting(anchor):
            explicitOutputPixelSize = nil
            selection = normalizedRect(from: anchor, to: constrained(point)).intersection(desktopFrame)
        case let .moving(origin, original):
            var moved = original.offsetBy(dx: point.x - origin.x, dy: point.y - origin.y)
            if moved.minX < desktopFrame.minX { moved.origin.x = desktopFrame.minX }
            if moved.minY < desktopFrame.minY { moved.origin.y = desktopFrame.minY }
            if moved.maxX > desktopFrame.maxX { moved.origin.x = desktopFrame.maxX - moved.width }
            if moved.maxY > desktopFrame.maxY { moved.origin.y = desktopFrame.maxY - moved.height }
            selection = moved
        case let .resizing(handle, origin, original):
            explicitOutputPixelSize = nil
            selection = resized(
                original,
                handle: handle,
                delta: NSPoint(x: point.x - origin.x, y: point.y - origin.y)
            ).intersection(desktopFrame)
        case let .annotating(origin):
            guard self.selection?.contains(point) == true else { return }
            switch activeTool {
            case .rectangle?:
                previewAnnotation = ScreenshotAnnotation(
                    shape: .rectangle(origin, point),
                    color: selectedAnnotationColor,
                    strokeWidth: selectedStrokeWidth
                )
            case .arrow?:
                previewAnnotation = ScreenshotAnnotation(
                    shape: .arrow(origin, point),
                    color: selectedAnnotationColor,
                    strokeWidth: selectedStrokeWidth
                )
            default:
                break
            }
        case .pen:
            guard self.selection?.contains(point) == true else {
                updateCursor(at: point)
                return
            }
            if let last = penPoints.last, hypot(point.x - last.x, point.y - last.y) < 0.5 { return }
            penPoints.append(point)
            if activeTool == .mosaic {
                previewAnnotation = ScreenshotAnnotation(
                    shape: .mosaic(penPoints, selectedMosaicBrushSize),
                    color: .clear,
                    strokeWidth: 0
                )
            } else {
                previewAnnotation = ScreenshotAnnotation(
                    shape: .pen(penPoints),
                    color: selectedAnnotationColor,
                    strokeWidth: selectedStrokeWidth
                )
            }
        case let .movingAnnotation(index, origin, annotation):
            guard annotations.indices.contains(index) else { return }
            annotations[index] = translated(
                annotation,
                dx: point.x - origin.x,
                dy: point.y - origin.y
            )
        }
        updateCursor(at: point)
        redraw()
    }

    func mouseUp() {
        if activePicker != nil {
            finishPickerSelection()
            return
        }
        if textInputOrigin == nil, let previewAnnotation {
            annotations.append(previewAnnotation)
        }
        if textInputOrigin == nil {
            previewAnnotation = nil
        }
        penPoints.removeAll()
        interaction = nil
        if !locksSelection, let selection, selection.width < 6 || selection.height < 6 {
            self.selection = nil
            explicitOutputPixelSize = nil
            updateHover(at: NSEvent.mouseLocation)
        }
        updateCursor(at: NSEvent.mouseLocation)
        redraw()
    }

    func keyDown(_ event: NSEvent) -> Bool {
        if handleTextInputKey(event) {
            return true
        }
        if event.keyCode == 53 {
            onCancel?()
            return true
        }
        if event.modifierFlags.contains(.command), event.charactersIgnoringModifiers == "z" {
            undoLastAnnotation()
            return true
        }
        if handleSizeInputKey(event) {
            return true
        }
        if event.keyCode == 36 || event.keyCode == 76 {
            finish()
            return true
        }
        return false
    }

    func cancel() {
        onCancel?()
    }

    private func undoLastAnnotation() {
        if !annotations.isEmpty {
            annotations.removeLast()
            redraw()
        }
    }

    private func beginTextInput(at point: NSPoint) {
        let input = recording == nil ? window(at: point) : recordingDrawingWindows.first { $0.frame.contains(point) }
        guard let window = input, let view = window.contentView, let selection else { return }
        let windowFrame = window.frame
        let limits = selection.intersection(windowFrame)
        guard limits.width > 0, limits.height > 0 else { return }
        textInputOrigin = point
        textInputBuffer = ""
        interaction = nil
        previewAnnotation = nil
        let font = NSFont.systemFont(ofSize: selectedFontSize, weight: selectedFontWeight)
        // A native editor belongs to one display, matching the Windows overlay.
        let field = ScreenshotTextEditor(frame: .zero, font: font, color: selectedColor)
        let resizeEditor: (String) -> Void = { [weak self, weak field] value in
            guard let self, let field else { return }
            self.textInputBuffer = value
            let frame = field.fitToSelection(anchor: NSPoint(x: point.x - windowFrame.minX, y: point.y - windowFrame.minY),
                                              limits: limits.offsetBy(dx: -windowFrame.minX, dy: -windowFrame.minY))
            self.textInputBounds = frame.offsetBy(dx: windowFrame.minX, dy: windowFrame.minY)
        }
        field.onChange = resizeEditor
        field.onCommit = { [weak self] in self?.commitTextInput() }
        field.onCancel = { [weak self] in self?.cancelTextInput() }
        view.addSubview(field)
        textInputField = field
        resizeEditor("")
        if recording != nil, let current = NSWorkspace.shared.frontmostApplication,
           current.processIdentifier != ProcessInfo.processInfo.processIdentifier {
            recordingTextPreviousApp = current
            NSApp.activate(ignoringOtherApps: true)
        }
        window.makeKey()
        window.makeFirstResponder(field.editor)
    }

    private func commitTextInput() {
        guard textInputOrigin != nil else { return }
        if let field = textInputField {
            field.flushChanges()
            textInputBuffer = field.editor.string
        }
        if !textInputBuffer.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            annotations.append(ScreenshotAnnotation(
                shape: .text(textInputBuffer, textInputBounds, selectedFontSize, selectedFontWeight),
                color: selectedColor,
                strokeWidth: 0
            ))
        }
        textInputOrigin = nil
        textInputBuffer = ""
        previewAnnotation = nil
        textInputField?.onChange = nil
        textInputField?.removeFromSuperview()
        textInputField = nil
        focusWindow(at: NSEvent.mouseLocation)
        redraw()
    }

    private func cancelTextInput() {
        textInputOrigin = nil
        textInputBuffer = ""
        previewAnnotation = nil
        textInputField?.onChange = nil
        textInputField?.removeFromSuperview()
        textInputField = nil
        focusWindow(at: NSEvent.mouseLocation)
        redraw()
    }

    private func handleTextInputKey(_ event: NSEvent) -> Bool {
        guard textInputOrigin != nil else { return false }
        // Escape belongs to the input method while a candidate is being composed.
        if textInputField?.editor.hasMarkedText() == true { return false }
        if event.keyCode == 53 {
            cancelTextInput()
            return true
        }
        return false
    }

    func draw(in view: ScreenshotOverlayView, dirtyRect: NSRect) {
        guard let display = view.display else { return }
        let localBounds = view.bounds
        if recording == nil {
            display.pointImage.draw(in: localBounds)
            NSColor.black.withAlphaComponent(0.48).setFill()
            NSBezierPath(rect: localBounds).fill()
        } else {
            NSGraphicsContext.current?.cgContext.clear(dirtyRect)
        }

        let effectiveSelection = selection ?? hoverCandidate
        guard let globalSelection = effectiveSelection else {
            if display.frame.contains(NSEvent.mouseLocation) {
                drawInstruction("移动鼠标选择窗口，或拖动选择任意区域", in: view)
            }
            return
        }

        let visibleGlobal = globalSelection.intersection(display.frame)
        if !visibleGlobal.isNull, visibleGlobal.width > 0, visibleGlobal.height > 0 {
            let localSelection = local(visibleGlobal, on: display)
            NSGraphicsContext.saveGraphicsState()
            NSBezierPath(rect: localSelection).addClip()
            if recording == nil { display.pointImage.draw(in: localBounds) }
            drawAnnotations(on: display)
            NSGraphicsContext.restoreGraphicsState()
        }

        let localGlobalSelection = local(globalSelection, on: display)
        NSColor.controlAccentColor.setStroke()
        let border = NSBezierPath(rect: localGlobalSelection)
        border.lineWidth = selection == nil ? 2 : 1.5
        border.stroke()

        guard selection != nil else { return }
        if !locksSelection { drawHandles(for: globalSelection, on: display) }
        drawToolbar(on: display)
        drawActivePicker(on: display)
        drawSizeLabel(globalSelection, on: display)
        // Cursor feedback is display-only; renderedSelection never calls this.
        if let brushCursorFrame,
           brushCursorFrame.insetBy(dx: -2, dy: -2).intersects(display.frame),
           let context = NSGraphicsContext.current?.cgContext {
            let pixelDiameter = brushCursorFrame.width * display.backingScale
            let path = brushCursorPaths[pixelDiameter] ?? ScreenshotBrushCursor.path(pixelDiameter: pixelDiameter)
            brushCursorPaths[pixelDiameter] = path
            ScreenshotBrushCursor.draw(
                path: path,
                center: local(NSPoint(x: brushCursorFrame.midX, y: brushCursorFrame.midY), on: display),
                backingScale: display.backingScale,
                in: context
            )
        }
    }

    private func updateHover(at point: NSPoint) {
        hoverCandidate = candidate(at: point)?.frame
        redraw()
    }

    private func candidate(at point: NSPoint) -> WindowCandidate? {
        windowCandidates.first { candidate in
            candidate.frame.contains(point)
                && displays.contains(where: { !$0.frame.intersection(candidate.frame).isNull })
        }
    }

    private func focusWindow(at point: NSPoint) {
        guard recording == nil, let window = window(at: point), let view = window.contentView else { return }
        window.makeKey()
        window.makeFirstResponder(view)
    }

    private func window(at point: NSPoint) -> NSWindow? {
        windows.first { $0.frame.contains(point) }
    }

    private func prepareRecordingInputs(selection: NSRect) {
        recordingDrawingWindows = displays.compactMap { display in
            let region = selection.intersection(display.frame)
            guard region.area > 0 else { return nil }
            return makeRecordingInput(frame: region)
        }
        recordingToolbarWindow = makeRecordingInput(frame: toolbarRect())
        recordingPaletteWindow = makeRecordingInput(frame: moreColorPaletteRect())
    }

    private func makeRecordingInput(frame: NSRect) -> NSWindow {
        let panel = ScreenshotRecordingInputPanel(contentRect: frame,
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        let view = ScreenshotOverlayView(frame: NSRect(origin: .zero, size: frame.size))
        view.session = self
        view.isRecordingInput = true
        view.autoresizingMask = [.width, .height]
        panel.level = .screenSaver
        panel.isOpaque = false
        panel.hasShadow = false
        panel.backgroundColor = .clear
        panel.ignoresMouseEvents = true
        panel.acceptsMouseMovedEvents = true
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.contentView = view
        panel.setFrame(frame, display: false)
        panel.orderFrontRegardless()
        return panel
    }

    private var recordingInputWindows: [NSWindow] {
        recordingDrawingWindows + [recordingToolbarWindow, recordingPaletteWindow].compactMap { $0 }
    }

    private func isRecordingInput(_ window: NSWindow?) -> Bool {
        guard let window else { return false }
        return recordingInputWindows.contains { $0 === window }
    }

    private func closeRecordingInputs() {
        recordingInputWindows.forEach { $0.orderOut(nil) }
        recordingDrawingWindows.removeAll()
        recordingToolbarWindow = nil
        recordingPaletteWindow = nil
        recordingTextPreviousApp = nil
    }

    private func updateRecordingInputs() {
        guard recording != nil else { return }
        for window in recordingDrawingWindows {
            window.ignoresMouseEvents = activeTool == nil
            window.contentView?.isHidden = activeTool == nil
        }
        let toolbar = toolbarRect()
        if let recordingToolbarWindow, recordingToolbarWindow.frame != toolbar {
            recordingToolbarWindow.setFrame(toolbar, display: true)
        }
        recordingToolbarWindow?.ignoresMouseEvents = false
        if showsMoreColors, let recordingPaletteWindow {
            let palette = moreColorPaletteRect()
            if recordingPaletteWindow.frame != palette { recordingPaletteWindow.setFrame(palette, display: true) }
        }
        recordingPaletteWindow?.ignoresMouseEvents = !showsMoreColors
        recordingPaletteWindow?.contentView?.isHidden = !showsMoreColors
        if activeTool == nil, let application = recordingTextPreviousApp {
            recordingTextPreviousApp = nil
            if NSApp.isActive { application.activate(options: [.activateIgnoringOtherApps]) }
        }
    }

    private func redraw() {
        updateRecordingInputs()
        views.forEach { $0.needsDisplay = true }
    }

    private func finish() {
        if locksSelection {
            cancel()
            return
        }
        guard let image = renderedSelection() else { return }
        onFinish?(image)
    }

    private func save() {
        guard !locksSelection else { return }
        guard
            let image = renderedSelection(),
            let bitmap = image.representations.compactMap({ $0 as? NSBitmapImageRep }).first,
            let data = bitmap.representation(using: .png, properties: [:])
        else {
            NSSound.beep()
            return
        }

        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH.mm.ss"
        let panel = NSSavePanel()
        panel.title = "保存截图"
        panel.nameFieldStringValue = "截图 \(formatter.string(from: Date())).png"
        panel.allowedContentTypes = [.png]
        panel.canCreateDirectories = true
        panel.isExtensionHidden = false

        isPresentingSavePanel = true
        windows.forEach { $0.orderOut(nil) }
        NSCursor.arrow.set()
        let response = panel.runModal()

        guard response == .OK, let url = panel.url else {
            isPresentingSavePanel = false
            restoreAfterSavePanel()
            return
        }
        do {
            try data.write(to: url, options: .atomic)
            isPresentingSavePanel = false
            onSave?()
        } catch {
            let alert = NSAlert(error: error)
            alert.messageText = "无法保存截图"
            alert.runModal()
            isPresentingSavePanel = false
            restoreAfterSavePanel()
        }
    }

    private func restoreAfterSavePanel() {
        windows.forEach { $0.orderFrontRegardless() }
        focusWindow(at: NSEvent.mouseLocation)
        updateCursor(at: NSEvent.mouseLocation)
        redraw()
    }

    private var outputScale: CGFloat {
        guard let selection else {
            return displays.map(\.backingScale).max() ?? 1
        }
        let intersectingDisplays = displays.filter {
            let intersection = $0.frame.intersection(selection)
            return !intersection.isNull && intersection.width > 0 && intersection.height > 0
        }
        return intersectingDisplays.map(\.backingScale).max() ?? 1
    }

    private func startRecording() {
        guard let selection, selection.width >= 2, selection.height >= 2 else { return }
        let settings = recordingSettings
        let captureDisplays = displays.filter { $0.frame.intersection(selection).area > 0 }
        prepareRecordingInputs(selection: selection)
        let overlays = Set((windows + recordingInputWindows).map { CGWindowID($0.windowNumber) })
        let pixels = outputPixelSize(for: selection)
        let dimensions = RegionRecording.outputDimensions(pixelWidth: pixels.width, pixelHeight: pixels.height,
                                                          maxHeight: settings.maxHeight)
        isStartingRecording = true
        interaction = nil
        activePicker = nil
        showsMoreColors = false

        Task { @MainActor in
            var sources: [RecordingDisplay] = []
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
                let excluded = content.windows.filter { overlays.contains($0.windowID) }
                for captured in captureDisplays {
                    guard !isClosed else { break }
                    guard let display = content.displays.first(where: { $0.displayID == captured.id }) else {
                        throw CocoaError(.fileReadUnknown, userInfo: [
                            NSLocalizedDescriptionKey: "无法读取录屏选区所在的显示器。"
                        ])
                    }
                    let rect = captured.frame.intersection(selection)
                    let sourceRect = CGRect(x: rect.minX - captured.frame.minX,
                                            y: captured.frame.maxY - rect.maxY,
                                            width: rect.width, height: rect.height)
                    let source = RecordingDisplay(rect: rect) { [weak self] error in
                        DispatchQueue.main.async {
                            guard let self, !self.isClosed else { return }
                            self.cancel()
                            Self.showRecordingResult(.failure(error))
                        }
                    }
                    let scaleX = min(captured.backingScale, CGFloat(dimensions.width) / selection.width)
                    let scaleY = min(captured.backingScale, CGFloat(dimensions.height) / selection.height)
                    try await source.start(display: display, excluding: excluded, sourceRect: sourceRect,
                                           width: max(2, Int((rect.width * scaleX).rounded())),
                                           height: max(2, Int((rect.height * scaleY).rounded())),
                                           fps: settings.frameRate)
                    sources.append(source)
                }
                guard !isClosed else {
                    for source in sources { await source.stop() }
                    return
                }

                let encoder = try RegionRecording(settings: settings, dimensions: dimensions) { [weak self] result in
                    DispatchQueue.main.async {
                        if let self, !self.isClosed { self.cancel() }
                        Self.showRecordingResult(result)
                    }
                }
                recordingDisplays = sources
                recording = encoder
                isStartingRecording = false
                activeTool = nil
                for window in windows {
                    window.ignoresMouseEvents = true
                    window.isOpaque = false
                    window.backgroundColor = .clear
                }
                onRecordingStarted?()
                let timer = Timer(timeInterval: 1 / Double(settings.frameRate), repeats: true) { [weak self] _ in
                    MainActor.assumeIsolated { self?.recordFrame() }
                }
                recordingTimer = timer
                RunLoop.main.add(timer, forMode: .common)
                redraw()
                recordFrame()
            } catch {
                for source in sources { await source.stop() }
                guard !isClosed else { return }
                isStartingRecording = false
                closeRecordingInputs()
                redraw()
                Self.showRecordingResult(.failure(error))
            }
        }
    }

    private func recordFrame() {
        guard let recording, let selection, recording.tryReserveFrame() else { return }
        let sources = recordingDisplays.compactMap { source -> (NSRect, CGImage)? in
            guard let image = source.image() else { return nil }
            return (source.rect, image)
        }
        guard !sources.isEmpty else {
            recording.releaseFrame()
            return
        }
        let useMosaic = annotations.contains(where: \.isMosaic) || previewAnnotation?.isMosaic == true
        let hadMosaic = !recordingMosaics.isEmpty
        recordingMosaics = useMosaic ? sources.map { rect, image in
            (rect, ScreenshotBitmapRenderer.makeMosaicImage(image: image, pointSize: rect.size))
        } : []
        if useMosaic || hadMosaic { views.forEach { $0.needsDisplay = true } }

        let rowBytes = recording.width * 4
        var frame = recording.takeFrameBuffer()
        let rendered = frame.withUnsafeMutableBytes { (bytes: UnsafeMutableRawBufferPointer) -> Bool in
            let bitmapInfo = CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.premultipliedFirst.rawValue
            guard let context = CGContext(data: bytes.baseAddress, width: recording.width, height: recording.height,
                                          bitsPerComponent: 8, bytesPerRow: rowBytes,
                                          space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
                                          bitmapInfo: bitmapInfo) else { return false }
            NSGraphicsContext.saveGraphicsState()
            defer { NSGraphicsContext.restoreGraphicsState() }
            NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
            context.setFillColor(NSColor.black.cgColor)
            context.fill(CGRect(x: 0, y: 0, width: recording.width, height: recording.height))
            context.scaleBy(x: CGFloat(recording.width) / selection.width,
                            y: CGFloat(recording.height) / selection.height)
            context.translateBy(x: -selection.minX, y: -selection.minY)
            context.interpolationQuality = .high
            for (rect, image) in sources { context.draw(image, in: rect) }
            drawAnnotations()
            if textInputOrigin != nil, !textInputBuffer.isEmpty, !textInputBounds.isEmpty {
                draw(ScreenshotAnnotation(shape: .text(textInputBuffer, textInputBounds, selectedFontSize, selectedFontWeight),
                                          color: selectedColor, strokeWidth: 0))
            }
            return true
        }
        if rendered {
            recording.submit(frame)
        } else {
            recording.releaseFrame(frame)
        }
    }

    @MainActor
    private static func showRecordingResult(_ result: Result<URL, Error>) {
        let settings = SettingsManager.shared
        let english = settings.language == "en-US"
        func t(_ chinese: String, _ translated: String) -> String { english ? translated : chinese }

        let alert = NSAlert()
        alert.window.appearance = NSAppearance(named: settings.theme == "Dark" ? .darkAqua : .aqua)
        NSApp.activate(ignoringOtherApps: true)
        switch result {
        case .success(let url):
            alert.messageText = t("区域录屏已保存", "Recording saved")
            if let checkmark = NSImage(systemSymbolName: "checkmark.circle.fill", accessibilityDescription: nil) {
                alert.icon = checkmark.withSymbolConfiguration(.init(paletteColors: [.systemGreen])) ?? checkmark
            }
            let folder = url.deletingLastPathComponent().path
            let details = NSView(frame: NSRect(x: 0, y: 0, width: 330, height: 45))
            let filename = NSTextField(labelWithString: url.lastPathComponent)
            filename.frame = NSRect(x: 0, y: 24, width: 330, height: 19)
            filename.font = .systemFont(ofSize: 13, weight: .semibold)
            filename.cell?.lineBreakMode = .byTruncatingMiddle
            filename.toolTip = url.path
            details.addSubview(filename)
            let directory = NSTextField(labelWithString: folder)
            directory.frame = NSRect(x: 0, y: 3, width: 330, height: 17)
            directory.font = .systemFont(ofSize: 12)
            directory.textColor = .secondaryLabelColor
            directory.cell?.lineBreakMode = .byTruncatingMiddle
            directory.toolTip = folder
            details.addSubview(directory)
            alert.accessoryView = details
            alert.addButton(withTitle: t("打开视频", "Open video"))
            alert.addButton(withTitle: t("在文件夹中显示", "Show in folder"))
            alert.addButton(withTitle: t("关闭", "Close")).keyEquivalent = "\u{1b}"
            let response = alert.runModal()
            if response == .alertFirstButtonReturn {
                if !NSWorkspace.shared.open(url) {
                    let failure = NSAlert()
                    failure.alertStyle = .warning
                    failure.messageText = t("无法打开录屏文件", "Could not open the recording")
                    failure.informativeText = url.path
                    failure.runModal()
                }
            } else if response == .alertSecondButtonReturn {
                NSWorkspace.shared.activateFileViewerSelecting([url])
            }
        case .failure(let error):
            alert.alertStyle = .warning
            alert.messageText = t("区域录屏失败", "Recording failed")
            alert.informativeText = error.localizedDescription
            alert.runModal()
        }
    }

    private func renderedSelection() -> NSImage? {
        guard let selection, selection.width >= 2, selection.height >= 2 else { return nil }
        let pixelSize = outputPixelSize(for: selection)
        let pixelWidth = pixelSize.width
        let pixelHeight = pixelSize.height
        return ScreenshotBitmapRenderer.render(
            pointSize: selection.size,
            pixelWidth: pixelWidth,
            pixelHeight: pixelHeight
        ) {
            NSColor.clear.setFill()
            NSBezierPath(rect: NSRect(origin: .zero, size: selection.size)).fill()

            for display in displays {
                let intersection = selection.intersection(display.frame)
                guard !intersection.isNull, intersection.width > 0, intersection.height > 0 else { continue }
                let source = NSRect(
                    x: intersection.minX - display.frame.minX,
                    y: intersection.minY - display.frame.minY,
                    width: intersection.width,
                    height: intersection.height
                )
                let destination = NSRect(
                    x: intersection.minX - selection.minX,
                    y: intersection.minY - selection.minY,
                    width: intersection.width,
                    height: intersection.height
                )
                display.pointImage.draw(in: destination, from: source, operation: .copy, fraction: 1)
            }

            let transform = NSAffineTransform()
            transform.translateX(by: -selection.minX, yBy: -selection.minY)
            transform.concat()
            drawAnnotations(includePreview: false)
        }
    }

    private func drawAnnotations(on display: CapturedDisplay) {
        let transform = NSAffineTransform()
        transform.translateX(by: -display.frame.minX, yBy: -display.frame.minY)
        transform.concat()
        drawAnnotations()
    }

    private func drawAnnotations(includePreview: Bool = true) {
        annotations.forEach(draw)
        if includePreview, let previewAnnotation {
            draw(previewAnnotation)
        }
    }

    private func draw(_ annotation: ScreenshotAnnotation) {
        annotation.color.setStroke()
        switch annotation.shape {
        case let .rectangle(start, end):
            let path = NSBezierPath(rect: normalizedRect(from: start, to: end))
            path.lineWidth = annotation.strokeWidth
            path.stroke()
        case let .arrow(start, end):
            let path = NSBezierPath()
            path.move(to: start)
            path.line(to: end)
            path.lineWidth = annotation.strokeWidth
            path.lineCapStyle = .round
            path.stroke()

            let angle = atan2(end.y - start.y, end.x - start.x)
            let arrowLength: CGFloat = 10 + annotation.strokeWidth * 1.5
            let head = NSBezierPath()
            head.move(to: end)
            head.line(to: NSPoint(
                x: end.x - arrowLength * cos(angle - .pi / 6),
                y: end.y - arrowLength * sin(angle - .pi / 6)
            ))
            head.move(to: end)
            head.line(to: NSPoint(
                x: end.x - arrowLength * cos(angle + .pi / 6),
                y: end.y - arrowLength * sin(angle + .pi / 6)
            ))
            head.lineWidth = annotation.strokeWidth
            head.lineCapStyle = .round
            head.stroke()
        case let .pen(points):
            guard let first = points.first else { return }
            let path = NSBezierPath()
            path.move(to: first)
            points.dropFirst().forEach { path.line(to: $0) }
            path.lineWidth = annotation.strokeWidth
            path.lineJoinStyle = .round
            path.lineCapStyle = .round
            path.stroke()
        case let .text(text, bounds, fontSize, fontWeight):
            ScreenshotTextLayout.draw(text, in: bounds,
                                      font: NSFont.systemFont(ofSize: fontSize, weight: fontWeight),
                                      color: annotation.color)
        case let .mosaic(points, brushSize):
            drawMosaic(points: points, brushSize: brushSize)
        }
    }

    private func drawMosaic(points: [NSPoint], brushSize: CGFloat) {
        guard let first = points.first, let graphics = NSGraphicsContext.current else { return }
        let context = graphics.cgContext
        context.saveGState()
        if points.count == 1 {
            context.addEllipse(in: NSRect(
                x: first.x - brushSize / 2,
                y: first.y - brushSize / 2,
                width: brushSize,
                height: brushSize
            ))
        } else {
            context.move(to: first)
            points.dropFirst().forEach { context.addLine(to: $0) }
            context.setLineWidth(brushSize)
            context.setLineCap(.round)
            context.setLineJoin(.round)
            context.replacePathWithStrokedPath()
        }
        context.clip()
        drawMosaicSource(in: mosaicBounds(points: points, brushSize: brushSize))
        context.restoreGState()
    }

    private func mosaicBounds(points: [NSPoint], brushSize: CGFloat) -> NSRect {
        guard !points.isEmpty else { return .zero }
        let minX = points.map(\.x).min() ?? 0
        let maxX = points.map(\.x).max() ?? 0
        let minY = points.map(\.y).min() ?? 0
        let maxY = points.map(\.y).max() ?? 0
        return NSRect(
            x: minX - brushSize / 2,
            y: minY - brushSize / 2,
            width: maxX - minX + brushSize,
            height: maxY - minY + brushSize
        )
    }

    private func drawMosaicSource(in rect: NSRect) {
        guard rect.width >= 1, rect.height >= 1 else { return }
        if recording != nil {
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current?.imageInterpolation = .none
            for mosaic in recordingMosaics where mosaic.rect.intersects(rect) {
                mosaic.image.draw(in: mosaic.rect, from: .zero, operation: .copy, fraction: 1)
            }
            NSGraphicsContext.restoreGraphicsState()
            return
        }
        for display in displays {
            guard !rect.intersection(display.frame).isNull else { continue }
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current?.imageInterpolation = .none
            display.mosaicImage.draw(
                in: display.frame,
                from: NSRect(origin: .zero, size: display.frame.size),
                operation: .copy,
                fraction: 1
            )
            NSGraphicsContext.restoreGraphicsState()
        }
    }

    private var selectedAnnotationColor: NSColor {
        selectedColor
    }

    private func annotationIndex(at point: NSPoint) -> Int? {
        annotations.indices.reversed().first {
            if case .mosaic = annotations[$0].shape, activeTool != nil { return false }
            return annotationHit(annotations[$0], at: point)
        }
    }

    private func annotationHit(_ annotation: ScreenshotAnnotation, at point: NSPoint) -> Bool {
        let tolerance = max(6, annotation.strokeWidth / 2 + 4)
        switch annotation.shape {
        case let .rectangle(start, end):
            let rect = normalizedRect(from: start, to: end)
            let nearHorizontal = point.x >= rect.minX - tolerance
                && point.x <= rect.maxX + tolerance
                && (abs(point.y - rect.minY) <= tolerance || abs(point.y - rect.maxY) <= tolerance)
            let nearVertical = point.y >= rect.minY - tolerance
                && point.y <= rect.maxY + tolerance
                && (abs(point.x - rect.minX) <= tolerance || abs(point.x - rect.maxX) <= tolerance)
            return nearHorizontal || nearVertical
        case let .arrow(start, end):
            if distance(from: point, toSegmentFrom: start, to: end) <= tolerance { return true }
            let angle = atan2(end.y - start.y, end.x - start.x)
            let arrowLength = 10 + annotation.strokeWidth * 1.5
            let first = NSPoint(
                x: end.x - arrowLength * cos(angle - .pi / 6),
                y: end.y - arrowLength * sin(angle - .pi / 6)
            )
            let second = NSPoint(
                x: end.x - arrowLength * cos(angle + .pi / 6),
                y: end.y - arrowLength * sin(angle + .pi / 6)
            )
            return distance(from: point, toSegmentFrom: end, to: first) <= tolerance
                || distance(from: point, toSegmentFrom: end, to: second) <= tolerance
        case let .pen(points):
            if points.count == 1 {
                return hypot(point.x - points[0].x, point.y - points[0].y) <= tolerance
            }
            return zip(points, points.dropFirst()).contains {
                distance(from: point, toSegmentFrom: $0.0, to: $0.1) <= tolerance
            }
        case let .text(_, bounds, _, _):
            return bounds.insetBy(dx: -4, dy: -4).contains(point)
        case let .mosaic(points, brushSize):
            let mosaicTolerance = brushSize / 2 + 4
            if points.count == 1, let first = points.first {
                return hypot(point.x - first.x, point.y - first.y) <= mosaicTolerance
            }
            return zip(points, points.dropFirst()).contains {
                distance(from: point, toSegmentFrom: $0.0, to: $0.1) <= mosaicTolerance
            }
        }
    }

    private func distance(
        from point: NSPoint,
        toSegmentFrom start: NSPoint,
        to end: NSPoint
    ) -> CGFloat {
        let dx = end.x - start.x
        let dy = end.y - start.y
        let lengthSquared = dx * dx + dy * dy
        guard lengthSquared > 0 else { return hypot(point.x - start.x, point.y - start.y) }
        let position = max(0, min(1,
            ((point.x - start.x) * dx + (point.y - start.y) * dy) / lengthSquared
        ))
        return hypot(
            point.x - (start.x + position * dx),
            point.y - (start.y + position * dy)
        )
    }

    private func translated(_ annotation: ScreenshotAnnotation, dx: CGFloat, dy: CGFloat) -> ScreenshotAnnotation {
        func moved(_ point: NSPoint) -> NSPoint {
            NSPoint(x: point.x + dx, y: point.y + dy)
        }
        let shape: ScreenshotAnnotationShape
        switch annotation.shape {
        case let .rectangle(start, end): shape = .rectangle(moved(start), moved(end))
        case let .arrow(start, end): shape = .arrow(moved(start), moved(end))
        case let .pen(points): shape = .pen(points.map(moved))
        case let .text(text, bounds, size, weight):
            shape = .text(text, bounds.offsetBy(dx: dx, dy: dy), size, weight)
        case let .mosaic(points, brushSize): shape = .mosaic(points.map(moved), brushSize)
        }
        return ScreenshotAnnotation(
            shape: shape,
            color: annotation.color,
            strokeWidth: annotation.strokeWidth
        )
    }

    private func drawHandles(for selection: NSRect, on display: CapturedDisplay) {
        NSColor.white.setFill()
        NSColor.controlAccentColor.setStroke()
        for handle in ResizeHandle.allCases {
            let point = handlePoint(handle, in: selection)
            guard display.frame.insetBy(dx: -5, dy: -5).contains(point) else { continue }
            let localPoint = local(point, on: display)
            let rect = NSRect(x: localPoint.x - 4, y: localPoint.y - 4, width: 8, height: 8)
            let path = NSBezierPath(ovalIn: rect)
            path.fill()
            path.stroke()
        }
    }

    private var toolbarTitles: [String] {
        ScreenshotTool.allCases.map(\.title)
            + ["", "", "像素", "撤销", "保存", "完成"] + (recording == nil ? [] : ["操作"])
            + [isStartingRecording ? "启动中" : recording == nil ? "录屏" : "停止"]
    }

    private var compactPaletteButtonIndex: Int {
        ScreenshotTool.allCases.count
    }

    private var sizeButtonIndex: Int {
        strokeSizeButtonIndex + 1
    }

    private var strokeSizeButtonIndex: Int {
        compactPaletteButtonIndex + 1
    }

    private var undoButtonIndex: Int {
        sizeButtonIndex + 1
    }

    private var saveButtonIndex: Int {
        undoButtonIndex + 1
    }

    private var finishButtonIndex: Int {
        saveButtonIndex + 1
    }

    private var desktopButtonIndex: Int { finishButtonIndex + 1 }
    private var recordButtonIndex: Int { desktopButtonIndex + (recording == nil ? 0 : 1) }

    private func toolbarButtonWidth(index: Int) -> CGFloat {
        if index == recordButtonIndex { return 50 }
        if index < ScreenshotTool.allCases.count, ScreenshotTool.allCases[index] == .mosaic {
            return 58
        }
        if index == compactPaletteButtonIndex {
            return 44
        }
        if index == strokeSizeButtonIndex {
            return 48
        }
        return 38
    }

    private var preferredToolbarWidth: CGFloat {
        toolbarTitles.indices.reduce(CGFloat.zero) {
            $0 + toolbarButtonWidth(index: $1)
        }
    }

    private func toolbarRect() -> NSRect {
        guard let selection else { return .zero }
        let width = preferredToolbarWidth
        let height: CGFloat = 38
        let targetDisplay = displays.max {
            $0.frame.intersection(selection).area < $1.frame.intersection(selection).area
        } ?? displays[0]
        let frame = targetDisplay.frame.insetBy(dx: 8, dy: 8)
        let actualWidth = min(width, frame.width)
        let x = min(
            max(selection.maxX - actualWidth, frame.minX),
            frame.maxX - actualWidth
        )
        let below = selection.minY - height - 8
        let y = below >= frame.minY
            ? below
            : min(selection.maxY + 8, frame.maxY - height)
        return NSRect(x: x, y: y, width: actualWidth, height: height)
    }

    private func toolbarButtonRect(index: Int) -> NSRect {
        let toolbar = toolbarRect()
        let scale = toolbar.width / preferredToolbarWidth
        let precedingWidth = toolbarTitles.indices.prefix(index).reduce(CGFloat.zero) {
            $0 + toolbarButtonWidth(index: $1)
        }
        let width = toolbarButtonWidth(index: index) * scale
        return NSRect(
            x: toolbar.minX + precedingWidth * scale,
            y: toolbar.minY,
            width: width,
            height: toolbar.height
        )
    }

    private func drawToolbar(on display: CapturedDisplay) {
        let globalToolbar = toolbarRect()
        guard display.frame.contains(NSPoint(x: globalToolbar.midX, y: globalToolbar.midY)) else { return }
        let toolbar = local(globalToolbar, on: display)
        NSColor.windowBackgroundColor.withAlphaComponent(0.97).setFill()
        NSBezierPath(roundedRect: toolbar, xRadius: 8, yRadius: 8).fill()
        NSColor.separatorColor.setStroke()
        NSBezierPath(roundedRect: toolbar, xRadius: 8, yRadius: 8).stroke()

        for (index, title) in toolbarTitles.enumerated() {
            let rect = local(toolbarButtonRect(index: index), on: display)
            if index < ScreenshotTool.allCases.count,
               ScreenshotTool.allCases[index] == activeTool {
                NSColor.controlAccentColor.withAlphaComponent(0.18).setFill()
                NSBezierPath(roundedRect: rect.insetBy(dx: 2, dy: 3), xRadius: 5, yRadius: 5).fill()
            } else if index == sizeButtonIndex && sizeInputStage != nil
                        || recording != nil && index == desktopButtonIndex && activeTool == nil {
                NSColor.controlAccentColor.withAlphaComponent(0.18).setFill()
                NSBezierPath(roundedRect: rect.insetBy(dx: 2, dy: 3), xRadius: 5, yRadius: 5).fill()
            }
            if index == compactPaletteButtonIndex {
                drawCompactPalette(in: rect, on: display)
                continue
            }
            if index == strokeSizeButtonIndex {
                drawStrokeSizeControl(in: rect)
                continue
            }
            let attributes: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: 12, weight: .medium),
                .foregroundColor: toolbarTitleColor(index: index)
            ]
            let displayedTitle: String
            if index < ScreenshotTool.allCases.count, ScreenshotTool.allCases[index] == .text {
                displayedTitle = "文字↕"
            } else if index < ScreenshotTool.allCases.count, ScreenshotTool.allCases[index] == .mosaic {
                displayedTitle = "马赛克↕"
            } else {
                displayedTitle = title
            }
            let size = displayedTitle.size(withAttributes: attributes)
            displayedTitle.draw(
                at: NSPoint(x: rect.midX - size.width / 2, y: rect.midY - size.height / 2),
                withAttributes: attributes
            )
        }
        if showsMoreColors {
            drawMoreColorPalette(on: display)
        }
    }

    private func toolbarTitleColor(index: Int) -> NSColor {
        if locksSelection, index == sizeButtonIndex || index == saveButtonIndex { return .disabledControlTextColor }
        if index == recordButtonIndex { return .systemRed }
        return index == finishButtonIndex ? .controlAccentColor : .labelColor
    }

    private func handleToolbarClick(at point: NSPoint) -> Bool {
        guard selection != nil, toolbarRect().contains(point) else { return false }
        for index in toolbarTitles.indices where toolbarButtonRect(index: index).contains(point) {
            if index != sizeButtonIndex {
                cancelSizeInput()
            }
            if index < ScreenshotTool.allCases.count {
                let tool = ScreenshotTool.allCases[index]
                if recording != nil && activeTool == tool {
                    activeTool = nil
                } else if tool == .text || tool == .mosaic {
                    activeTool = tool
                    beginPicker(.size, anchor: toolbarButtonRect(index: index))
                } else {
                    activeTool = activeTool == tool ? nil : tool
                }
                showsMoreColors = false
            } else if index == compactPaletteButtonIndex {
                beginPicker(.color)
            } else if index == strokeSizeButtonIndex {
                showsMoreColors = false
                beginPicker(.size)
            } else if index == sizeButtonIndex {
                showsMoreColors = false
                toggleSizeInput()
            } else if index == undoButtonIndex {
                undoLastAnnotation()
                showsMoreColors = false
            } else if index == saveButtonIndex {
                showsMoreColors = false
                save()
            } else if index == finishButtonIndex {
                finish()
            } else if index == recordButtonIndex {
                if locksSelection { cancel() } else { startRecording() }
            } else if index == desktopButtonIndex {
                activeTool = nil
                showsMoreColors = false
                setBrushCursorFrame(nil)
            }
            redraw()
            return true
        }
        return true
    }

    private func compactQuickColorRect(index: Int) -> NSRect {
        let button = toolbarButtonRect(index: compactPaletteButtonIndex)
        let cellSize: CGFloat = 13
        let column = CGFloat(index % 2)
        let row = CGFloat(index / 2)
        return NSRect(
            x: button.minX + 4 + column * cellSize,
            y: button.midY + (row == 0 ? 0 : -cellSize),
            width: cellSize,
            height: cellSize
        )
    }

    private func compactPaletteMoreRect() -> NSRect {
        let button = toolbarButtonRect(index: compactPaletteButtonIndex)
        return NSRect(
            x: button.minX + 33,
            y: button.minY + 4,
            width: button.width - 36,
            height: button.height - 8
        )
    }

    private func drawCompactPalette(in slot: NSRect, on display: CapturedDisplay) {
        let capsule = slot.insetBy(dx: 2, dy: 4)
        NSColor.controlBackgroundColor.withAlphaComponent(0.72).setFill()
        NSBezierPath(roundedRect: capsule, xRadius: 6, yRadius: 6).fill()
        (activePicker == .color ? NSColor.controlAccentColor : NSColor.separatorColor).setStroke()
        let border = NSBezierPath(roundedRect: capsule, xRadius: 6, yRadius: 6)
        border.lineWidth = activePicker == .color ? 1.5 : 1
        border.stroke()
        selectedColor.setFill()
        let currentColor = NSBezierPath(ovalIn: NSRect(
            x: slot.minX + 10,
            y: slot.midY - 7,
            width: 14,
            height: 14
        ))
        currentColor.fill()
        NSColor.separatorColor.setStroke()
        currentColor.stroke()
        let arrow = "↕"
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 13, weight: .semibold),
            .foregroundColor: NSColor.secondaryLabelColor
        ]
        let arrowSize = arrow.size(withAttributes: attributes)
        arrow.draw(
            at: NSPoint(x: slot.maxX - arrowSize.width - 10, y: slot.midY - arrowSize.height / 2),
            withAttributes: attributes
        )
    }

    private func drawStrokeSizeControl(in rect: NSRect) {
        let capsule = rect.insetBy(dx: 2, dy: 4)
        NSColor.controlBackgroundColor.withAlphaComponent(0.72).setFill()
        NSBezierPath(roundedRect: capsule, xRadius: 6, yRadius: 6).fill()
        (activePicker == .size ? NSColor.controlAccentColor : NSColor.separatorColor).setStroke()
        let border = NSBezierPath(roundedRect: capsule, xRadius: 6, yRadius: 6)
        border.lineWidth = activePicker == .size ? 1.5 : 1
        border.stroke()

        if activeTool == .text {
            let label = "Aa \(Int(selectedFontSize))"
            let attributes: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: 11, weight: .semibold),
                .foregroundColor: NSColor.labelColor
            ]
            let size = label.size(withAttributes: attributes)
            label.draw(
                at: NSPoint(x: rect.midX - size.width / 2, y: rect.midY - size.height / 2),
                withAttributes: attributes
            )
        } else if activeTool == .mosaic {
            let label = "● \(Int(selectedMosaicBrushSize))"
            let attributes: [NSAttributedString.Key: Any] = [
                .font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .semibold),
                .foregroundColor: NSColor.labelColor
            ]
            let size = label.size(withAttributes: attributes)
            label.draw(at: NSPoint(x: rect.midX - size.width / 2, y: rect.midY - size.height / 2), withAttributes: attributes)
        } else {
            selectedColor.setStroke()
            let sample = NSBezierPath()
            sample.move(to: NSPoint(x: rect.minX + 7, y: rect.midY))
            sample.line(to: NSPoint(x: rect.minX + 25, y: rect.midY))
            sample.lineWidth = min(selectedStrokeWidth, 9)
            sample.lineCapStyle = .round
            sample.stroke()
            let label = "\(Int(selectedStrokeWidth))"
            let attributes: [NSAttributedString.Key: Any] = [
                .font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .semibold),
                .foregroundColor: NSColor.labelColor
            ]
            let size = label.size(withAttributes: attributes)
            label.draw(
                at: NSPoint(x: rect.maxX - size.width - 7, y: rect.midY - size.height / 2),
                withAttributes: attributes
            )
        }
    }

    private var pickerColors: [NSColor] { quickColors + additionalColors }

    private var pickerSizeValues: [CGFloat] {
        Array((activeTool == .text ? fontSizes : activeTool == .mosaic ? mosaicBrushSizes : strokeWidths).reversed())
    }

    private func beginPicker(_ picker: ToolbarPicker, anchor: NSRect? = nil) {
        activePicker = picker
        pickerAnchorOverride = anchor
        switch picker {
        case .color:
            pickerSelectionIndex = 4
        case .size:
            let current = activeTool == .text ? selectedFontSize : activeTool == .mosaic ? selectedMosaicBrushSize : selectedStrokeWidth
            pickerSelectionIndex = pickerSizeValues.firstIndex(of: current) ?? 0
        }
        redraw()
    }

    private func pickerColor(at row: Int) -> NSColor {
        let colors = pickerColors
        let current = colors.firstIndex(of: selectedColor) ?? 0
        let offset = 4 - row
        return colors[(current + offset + colors.count) % colors.count]
    }

    private func pickerRowCount() -> Int {
        activePicker == .color ? pickerColors.count : pickerSizeValues.count
    }

    private func pickerCurrentRow() -> Int {
        switch activePicker {
        case .color: return 4
        case .size:
            let current = activeTool == .text ? selectedFontSize : activeTool == .mosaic ? selectedMosaicBrushSize : selectedStrokeWidth
            return pickerSizeValues.firstIndex(of: current) ?? 0
        case nil: return 0
        }
    }

    private func pickerOptionRect(row: Int) -> NSRect {
        let buttonIndex = activePicker == .color ? compactPaletteButtonIndex : strokeSizeButtonIndex
        let button = pickerAnchorOverride ?? toolbarButtonRect(index: buttonIndex)
        let rowHeight: CGFloat = 24
        let width: CGFloat = activePicker == .color ? 36 : 52
        let currentRow = pickerCurrentRow()
        var centerY = button.midY + CGFloat(currentRow - row) * rowHeight
        guard let display = displays.first(where: { $0.frame.contains(
            NSPoint(x: button.midX, y: button.midY)
        ) }) else { return .zero }
        let topCenter = button.midY + CGFloat(currentRow) * rowHeight
        let bottomCenter = button.midY - CGFloat(pickerRowCount() - 1 - currentRow) * rowHeight
        let upperOverflow = max(0, topCenter + rowHeight / 2 - (display.frame.maxY - 8))
        let lowerOverflow = max(0, display.frame.minY + 8 - (bottomCenter - rowHeight / 2))
        centerY += lowerOverflow - upperOverflow
        return NSRect(
            x: button.midX - width / 2,
            y: centerY - rowHeight / 2,
            width: width,
            height: rowHeight
        )
    }

    private func drawActivePicker(on display: CapturedDisplay) {
        guard activePicker != nil else { return }
        let rows = pickerRowCount()
        let rects = (0..<rows).map(pickerOptionRect)
        guard let first = rects.first, display.frame.contains(
            NSPoint(x: first.midX, y: first.midY)
        ) else { return }
        let rail = rects.dropFirst().reduce(first) { $0.union($1) }.insetBy(dx: -4, dy: -4)
        let localRail = local(rail, on: display)
        NSColor.windowBackgroundColor.withAlphaComponent(0.98).setFill()
        NSBezierPath(roundedRect: localRail, xRadius: 12, yRadius: 12).fill()
        NSColor.separatorColor.setStroke()
        NSBezierPath(roundedRect: localRail, xRadius: 12, yRadius: 12).stroke()

        for row in 0..<rows {
            let rect = local(rects[row], on: display).insetBy(dx: 2, dy: 2)
            if pickerSelectionIndex == row {
                NSColor.controlAccentColor.withAlphaComponent(0.18).setFill()
                NSBezierPath(roundedRect: rect, xRadius: 8, yRadius: 8).fill()
            }
            if activePicker == .color {
                let color = pickerColor(at: row)
                color.setFill()
                let swatch = NSBezierPath(ovalIn: NSRect(
                    x: rect.midX - 8, y: rect.midY - 8, width: 16, height: 16
                ))
                swatch.fill()
                NSColor.separatorColor.setStroke()
                swatch.stroke()
            } else {
                let value = pickerSizeValues[row]
                if activeTool == .text {
                    let label = "Aa \(Int(value))"
                    let attributes: [NSAttributedString.Key: Any] = [
                        .font: NSFont.systemFont(ofSize: 11, weight: .semibold),
                        .foregroundColor: NSColor.labelColor
                    ]
                    let size = label.size(withAttributes: attributes)
                    label.draw(at: NSPoint(
                        x: rect.midX - size.width / 2,
                        y: rect.midY - size.height / 2
                    ), withAttributes: attributes)
                } else if activeTool == .mosaic {
                    let label = "● \(Int(value))"
                    let attributes: [NSAttributedString.Key: Any] = [
                        .font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .semibold),
                        .foregroundColor: NSColor.labelColor
                    ]
                    let size = label.size(withAttributes: attributes)
                    label.draw(at: NSPoint(x: rect.midX - size.width / 2, y: rect.midY - size.height / 2), withAttributes: attributes)
                } else {
                    selectedColor.setStroke()
                    let sample = NSBezierPath()
                    sample.move(to: NSPoint(x: rect.minX + 8, y: rect.midY))
                    sample.line(to: NSPoint(x: rect.maxX - 22, y: rect.midY))
                    sample.lineWidth = min(value, 10)
                    sample.lineCapStyle = .round
                    sample.stroke()
                    let label = "\(Int(value))"
                    let attributes: [NSAttributedString.Key: Any] = [
                        .font: NSFont.monospacedDigitSystemFont(ofSize: 10, weight: .semibold),
                        .foregroundColor: NSColor.labelColor
                    ]
                    label.draw(at: NSPoint(x: rect.maxX - 17, y: rect.midY - 6), withAttributes: attributes)
                }
            }
        }
    }

    private func updatePickerSelection(at point: NSPoint) {
        guard activePicker != nil else { return }
        let rows = pickerRowCount()
        pickerSelectionIndex = (0..<rows).min {
            abs(pickerOptionRect(row: $0).midY - point.y)
                < abs(pickerOptionRect(row: $1).midY - point.y)
        }
        redraw()
    }

    private func finishPickerSelection() {
        guard let picker = activePicker, let row = pickerSelectionIndex else {
            activePicker = nil
            pickerAnchorOverride = nil
            return
        }
        switch picker {
        case .color:
            selectedColor = pickerColor(at: row)
        case .size:
            let value = pickerSizeValues[row]
            if activeTool == .text { selectedFontSize = value }
            else if activeTool == .mosaic { selectedMosaicBrushSize = value }
            else { selectedStrokeWidth = value }
        }
        activePicker = nil
        pickerSelectionIndex = nil
        pickerAnchorOverride = nil
        updateCursor(at: NSEvent.mouseLocation)
        redraw()
    }

    private func handleCompactPaletteClick(at point: NSPoint) {
        for index in quickColors.indices
        where compactQuickColorRect(index: index).contains(point) {
            selectedColor = quickColors[index]
            showsMoreColors = false
            return
        }
        if compactPaletteMoreRect().contains(point) {
            showsMoreColors.toggle()
        }
    }

    private func drawColorSwatch(_ color: NSColor, in slot: NSRect, selected: Bool) {
        let rect = NSRect(x: slot.midX - 7, y: slot.midY - 7, width: 14, height: 14)
        color.setFill()
        let swatch = NSBezierPath(ovalIn: rect)
        swatch.fill()
        (selected ? NSColor.controlAccentColor : NSColor.separatorColor).setStroke()
        swatch.lineWidth = selected ? 2.5 : 1
        swatch.stroke()
    }

    private func moreColorPaletteRect() -> NSRect {
        let toolbar = toolbarRect()
        let columns = 5
        let rows = (additionalColors.count + columns - 1) / columns
        let width = CGFloat(columns) * 22 + 4
        let height = CGFloat(rows) * 22 + 4
        guard let display = displays.first(where: { $0.frame.contains(
            NSPoint(x: toolbar.midX, y: toolbar.midY)
        ) }) else {
            return .zero
        }
        let frame = display.frame.insetBy(dx: 8, dy: 8)
        let paletteButton = toolbarButtonRect(index: compactPaletteButtonIndex)
        let x = min(max(paletteButton.midX - width / 2, frame.minX), frame.maxX - width)
        let above = toolbar.maxY + 6
        let y = above + height <= frame.maxY ? above : toolbar.minY - height - 6
        return NSRect(x: x, y: y, width: width, height: height)
    }

    private func moreColorSwatchRect(index: Int) -> NSRect {
        let palette = moreColorPaletteRect()
        let slotWidth: CGFloat = 22
        let columns = 5
        let column = CGFloat(index % columns)
        let row = CGFloat(index / columns)
        return NSRect(
            x: palette.minX + 6 + column * slotWidth,
            y: palette.maxY - 20 - row * slotWidth,
            width: 14,
            height: 14
        )
    }

    private func drawMoreColorPalette(on display: CapturedDisplay) {
        let globalPalette = moreColorPaletteRect()
        guard display.frame.contains(NSPoint(x: globalPalette.midX, y: globalPalette.midY)) else { return }
        let palette = local(globalPalette, on: display)
        NSColor.windowBackgroundColor.withAlphaComponent(0.98).setFill()
        NSBezierPath(roundedRect: palette, xRadius: 8, yRadius: 8).fill()
        NSColor.separatorColor.setStroke()
        NSBezierPath(roundedRect: palette, xRadius: 8, yRadius: 8).stroke()

        for (index, color) in additionalColors.enumerated() {
            let rect = local(moreColorSwatchRect(index: index), on: display)
            drawColorSwatch(color, in: rect, selected: selectedColor == color)
        }
    }

    private func handleMoreColorPaletteClick(at point: NSPoint) -> Bool {
        guard showsMoreColors, moreColorPaletteRect().contains(point) else { return false }
        for index in additionalColors.indices
        where moreColorSwatchRect(index: index).insetBy(dx: -4, dy: -4).contains(point) {
            selectedColor = additionalColors[index]
            showsMoreColors = false
            cancelSizeInput()
            redraw()
            return true
        }
        return true
    }

    private func toggleSizeInput() {
        guard !locksSelection else { return }
        if sizeInputStage != nil {
            cancelSizeInput()
        } else {
            sizeInputStage = .width
            sizeInputBuffer = ""
            pendingInputWidth = nil
            sizeInputError = nil
        }
        redraw()
    }

    private func cancelSizeInput() {
        sizeInputStage = nil
        sizeInputBuffer = ""
        pendingInputWidth = nil
        sizeInputError = nil
    }

    private func handleSizeInputKey(_ event: NSEvent) -> Bool {
        guard let stage = sizeInputStage else { return false }

        if event.keyCode == 51 {
            if sizeInputBuffer.isEmpty, stage == .height, let pendingInputWidth {
                sizeInputStage = .width
                sizeInputBuffer = String(pendingInputWidth)
                self.pendingInputWidth = nil
            } else if !sizeInputBuffer.isEmpty {
                sizeInputBuffer.removeLast()
            }
            sizeInputError = nil
            redraw()
            return true
        }

        if event.keyCode == 36 || event.keyCode == 76 {
            commitSizeInput(stage: stage)
            return true
        }

        let modifiers = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        guard !modifiers.contains(.command),
              !modifiers.contains(.control),
              !modifiers.contains(.option),
              let characters = event.charactersIgnoringModifiers,
              !characters.isEmpty,
              characters.unicodeScalars.allSatisfy({ $0.value >= 48 && $0.value <= 57 })
        else {
            return true
        }

        if sizeInputBuffer.count + characters.count <= 6 {
            sizeInputBuffer.append(contentsOf: characters)
        } else {
            NSSound.beep()
        }
        sizeInputError = nil
        redraw()
        return true
    }

    private func commitSizeInput(stage: SizeInputStage) {
        guard let value = Int(sizeInputBuffer), value > 0 else {
            sizeInputError = "请输入大于 0 的数字"
            NSSound.beep()
            redraw()
            return
        }

        let scale = outputScale
        let maximumWidth = max(1, Int((desktopFrame.width * scale).rounded()))
        let maximumHeight = max(1, Int((desktopFrame.height * scale).rounded()))
        switch stage {
        case .width:
            guard value <= maximumWidth else {
                sizeInputError = "宽度最大 \(maximumWidth) px"
                NSSound.beep()
                redraw()
                return
            }
            pendingInputWidth = value
            sizeInputStage = .height
            sizeInputBuffer = ""
            sizeInputError = nil
            redraw()
        case .height:
            guard let requestedWidth = pendingInputWidth else {
                sizeInputStage = .width
                sizeInputBuffer = ""
                redraw()
                return
            }
            guard value <= maximumHeight else {
                sizeInputError = "高度最大 \(maximumHeight) px"
                NSSound.beep()
                redraw()
                return
            }
            applySelectionSize(width: requestedWidth, height: value, scale: scale)
        }
    }

    private func applySelectionSize(width requestedWidth: Int, height requestedHeight: Int, scale: CGFloat) {
        guard let selection else {
            cancelSizeInput()
            return
        }
        let newWidth = CGFloat(requestedWidth) / scale
        let newHeight = CGFloat(requestedHeight) / scale
        var origin = NSPoint(x: selection.minX, y: selection.maxY - newHeight)
        origin.x = min(max(origin.x, desktopFrame.minX), desktopFrame.maxX - newWidth)
        origin.y = min(max(origin.y, desktopFrame.minY), desktopFrame.maxY - newHeight)
        self.selection = NSRect(
            x: origin.x,
            y: origin.y,
            width: newWidth,
            height: newHeight
        )
        explicitOutputPixelSize = NSSize(
            width: CGFloat(requestedWidth),
            height: CGFloat(requestedHeight)
        )
        cancelSizeInput()
        hoverCandidate = nil
        interaction = nil
        updateCursor(at: NSEvent.mouseLocation)
        redraw()
    }

    private func drawInstruction(_ text: String, in view: NSView) {
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 15, weight: .medium),
            .foregroundColor: NSColor.white
        ]
        let size = text.size(withAttributes: attributes)
        let background = NSRect(
            x: view.bounds.midX - size.width / 2 - 14,
            y: view.bounds.midY - size.height / 2 - 9,
            width: size.width + 28,
            height: size.height + 18
        )
        NSColor.black.withAlphaComponent(0.72).setFill()
        NSBezierPath(roundedRect: background, xRadius: 8, yRadius: 8).fill()
        text.draw(at: NSPoint(x: background.minX + 14, y: background.minY + 9), withAttributes: attributes)
    }

    private func drawSizeLabel(_ selection: NSRect, on display: CapturedDisplay) {
        let topLeft = NSPoint(
            x: selection.minX + min(1, selection.width / 2),
            y: selection.maxY - min(1, selection.height / 2)
        )
        let labelDisplay = displays.first(where: { $0.frame.contains(topLeft) })
            ?? displays.max {
                $0.frame.intersection(selection).area < $1.frame.intersection(selection).area
            }
        guard let labelDisplay, display.frame == labelDisplay.frame else { return }

        let pixelSize = outputPixelSize(for: selection)
        let text: String
        if let sizeInputStage {
            let input = sizeInputBuffer.isEmpty ? "…" : sizeInputBuffer
            switch sizeInputStage {
            case .width:
                text = "正在输入宽度：\(input) px  按回车继续"
            case .height:
                text = "宽度 \(pendingInputWidth ?? pixelSize.width) px · 正在输入高度：\(input) px  按回车应用"
            }
        } else if let recording {
            text = "录像 \(recording.width) × \(recording.height)"
        } else {
            text = "\(pixelSize.width) × \(pixelSize.height)"
        }
        let displayedText = sizeInputError.map { "\(text) · \($0)" } ?? text
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .medium),
            .foregroundColor: sizeInputError == nil ? NSColor.white : NSColor.systemYellow
        ]
        let size = displayedText.size(withAttributes: attributes)
        let labelWidth = min(size.width + 12, display.frame.width - 16)
        let globalRect = NSRect(
            x: min(
                max(selection.minX, display.frame.minX + 8),
                display.frame.maxX - labelWidth - 8
            ),
            y: min(selection.maxY + 6, display.frame.maxY - size.height - 8),
            width: labelWidth,
            height: size.height + 6
        )
        guard display.frame.contains(NSPoint(x: globalRect.midX, y: globalRect.midY)) else { return }
        let rect = local(globalRect, on: display)
        NSColor.black.withAlphaComponent(0.72).setFill()
        NSBezierPath(roundedRect: rect, xRadius: 4, yRadius: 4).fill()
        displayedText.draw(
            at: NSPoint(x: rect.minX + 6, y: rect.minY + 3),
            withAttributes: attributes
        )
    }

    private func outputPixelSize(for selection: NSRect) -> (width: Int, height: Int) {
        if let explicitOutputPixelSize {
            return (
                max(1, Int(explicitOutputPixelSize.width.rounded())),
                max(1, Int(explicitOutputPixelSize.height.rounded()))
            )
        }
        return (
            max(1, Int((selection.width * outputScale).rounded())),
            max(1, Int((selection.height * outputScale).rounded()))
        )
    }

    private func hitHandle(at point: NSPoint, selection: NSRect) -> ResizeHandle? {
        ResizeHandle.allCases.first {
            let center = handlePoint($0, in: selection)
            return NSRect(x: center.x - 8, y: center.y - 8, width: 16, height: 16).contains(point)
        }
    }

    private func updateCursor(at point: NSPoint) {
        if let interaction {
            switch interaction {
            case let .resizing(handle, _, _):
                setBrushCursorFrame(nil)
                cursor(for: handle).set()
                return
            case .moving:
                setBrushCursorFrame(nil)
                NSCursor.closedHand.set()
                return
            case .movingAnnotation:
                setBrushCursorFrame(nil)
                NSCursor.closedHand.set()
                return
            case .pen:
                if selection?.contains(point) == true {
                    showBrushCursor(at: point)
                } else {
                    setBrushCursorFrame(nil)
                    NSCursor.crosshair.set()
                }
                return
            default:
                break
            }
        }
        if activePicker != nil {
            NSCursor.pointingHand.set()
        } else if showsMoreColors, moreColorPaletteRect().contains(point) {
            NSCursor.pointingHand.set()
        } else if selection != nil, toolbarRect().contains(point) {
            NSCursor.pointingHand.set()
        } else if !locksSelection, let selection, let handle = hitHandle(at: point, selection: selection) {
            cursor(for: handle).set()
        } else if !locksSelection, let selection, activeTool == nil, selection.contains(point) {
            NSCursor.openHand.set()
        } else if annotationIndex(at: point) != nil {
            NSCursor.openHand.set()
        } else if let selection, activeTool == .text, selection.contains(point) {
            NSCursor.iBeam.set()
        } else if let selection, selection.contains(point), activeTool == .pen || activeTool == .mosaic {
            showBrushCursor(at: point)
            return
        } else {
            NSCursor.crosshair.set()
        }
        setBrushCursorFrame(nil)
    }

    private func showBrushCursor(at point: NSPoint) {
        let diameter = activeTool == .mosaic ? selectedMosaicBrushSize : selectedStrokeWidth
        transparentBrushCursor.set()
        setBrushCursorFrame(NSRect(x: point.x - diameter / 2, y: point.y - diameter / 2,
                                  width: diameter, height: diameter))
    }

    private func setBrushCursorFrame(_ frame: NSRect?) {
        guard frame != brushCursorFrame else { return }
        let dirtyFrames = [brushCursorFrame, frame].compactMap { $0 }
        brushCursorFrame = frame
        // Repaint only old/new cursor footprints; do not regenerate the mosaic
        // cache or allocate a new screen-sized bitmap for pointer movement.
        for view in views {
            guard let display = view.display else { continue }
            for rect in dirtyFrames {
                let dirty = rect.insetBy(dx: -2, dy: -2).intersection(display.frame)
                if !dirty.isNull { view.setNeedsDisplay(local(dirty, on: display)) }
            }
        }
    }

    private func cursor(for handle: ResizeHandle) -> NSCursor {
        switch handle {
        case .top:
            return .resizeUp
        case .bottom:
            return .resizeDown
        case .left, .topLeft, .bottomLeft:
            return .resizeLeft
        case .right, .topRight, .bottomRight:
            return .resizeRight
        }
    }

    private func handlePoint(_ handle: ResizeHandle, in rect: NSRect) -> NSPoint {
        switch handle {
        case .topLeft: return NSPoint(x: rect.minX, y: rect.maxY)
        case .top: return NSPoint(x: rect.midX, y: rect.maxY)
        case .topRight: return NSPoint(x: rect.maxX, y: rect.maxY)
        case .right: return NSPoint(x: rect.maxX, y: rect.midY)
        case .bottomRight: return NSPoint(x: rect.maxX, y: rect.minY)
        case .bottom: return NSPoint(x: rect.midX, y: rect.minY)
        case .bottomLeft: return NSPoint(x: rect.minX, y: rect.minY)
        case .left: return NSPoint(x: rect.minX, y: rect.midY)
        }
    }

    private func resized(_ rect: NSRect, handle: ResizeHandle, delta: NSPoint) -> NSRect {
        var left = rect.minX
        var right = rect.maxX
        var bottom = rect.minY
        var top = rect.maxY
        if [.topLeft, .left, .bottomLeft].contains(handle) { left += delta.x }
        if [.topRight, .right, .bottomRight].contains(handle) { right += delta.x }
        if [.topLeft, .top, .topRight].contains(handle) { top += delta.y }
        if [.bottomLeft, .bottom, .bottomRight].contains(handle) { bottom += delta.y }
        return normalizedRect(from: NSPoint(x: left, y: bottom), to: NSPoint(x: right, y: top))
    }

    private func normalizedRect(from start: NSPoint, to end: NSPoint) -> NSRect {
        NSRect(
            x: min(start.x, end.x),
            y: min(start.y, end.y),
            width: abs(end.x - start.x),
            height: abs(end.y - start.y)
        )
    }

    private func constrained(_ point: NSPoint) -> NSPoint {
        NSPoint(
            x: min(max(point.x, desktopFrame.minX), desktopFrame.maxX),
            y: min(max(point.y, desktopFrame.minY), desktopFrame.maxY)
        )
    }

    private func local(_ rect: NSRect, on display: CapturedDisplay) -> NSRect {
        rect.offsetBy(dx: -display.frame.minX, dy: -display.frame.minY)
    }

    private func local(_ point: NSPoint, on display: CapturedDisplay) -> NSPoint {
        NSPoint(x: point.x - display.frame.minX, y: point.y - display.frame.minY)
    }
}

private final class ScreenshotOverlayWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

private final class ScreenshotRecordingInputPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

private final class ScreenshotOverlayView: NSView {
    weak var session: ScreenshotSession?
    var display: CapturedDisplay?
    var isRecordingInput = false
    private var recordingTracking: NSTrackingArea?

    override var acceptsFirstResponder: Bool { true }
    override var isFlipped: Bool { false }

    // Screenshot windows may still be inactive when the user starts dragging,
    // especially on another display. Treat that activation click as the real
    // mouse-down so the first drag always establishes a selection anchor.
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool {
        true
    }

    override func draw(_ dirtyRect: NSRect) {
        if isRecordingInput {
            NSColor.black.withAlphaComponent(0.01).setFill()
            NSBezierPath(rect: dirtyRect).fill()
        } else {
            session?.draw(in: self, dirtyRect: dirtyRect)
        }
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        guard isRecordingInput else { return }
        if let recordingTracking { removeTrackingArea(recordingTracking) }
        let area = NSTrackingArea(rect: .zero, options: [.inVisibleRect, .activeAlways, .mouseEnteredAndExited],
                                  owner: self, userInfo: nil)
        addTrackingArea(area)
        recordingTracking = area
    }

    override func mouseExited(with event: NSEvent) {
        if isRecordingInput { session?.recordingInputExited() }
    }

    override func mouseMoved(with event: NSEvent) {
        session?.mouseMoved(to: NSEvent.mouseLocation)
    }

    override func mouseDown(with event: NSEvent) {
        session?.mouseDown(at: NSEvent.mouseLocation, clickCount: event.clickCount)
    }

    override func mouseDragged(with event: NSEvent) {
        session?.mouseDragged(to: NSEvent.mouseLocation)
    }

    override func mouseUp(with event: NSEvent) {
        session?.mouseUp()
    }

    override func rightMouseDown(with event: NSEvent) {
        // Consume the press but keep the overlay in place until the matching
        // release so the click cannot continue into the application below.
    }

    override func rightMouseUp(with event: NSEvent) {
        session?.cancel()
    }

    override func keyDown(with event: NSEvent) {
        if session?.keyDown(event) != true {
            super.keyDown(with: event)
        }
    }
}

final class ScreenshotTextEditor: NSScrollView, NSTextViewDelegate {
    // Use TextKit 1 explicitly, matching ScreenshotTextLayout's glyph engine.
    let editor = NSTextView(usingTextLayoutManager: false)
    var onChange: ((String) -> Void)?
    var onCommit: (() -> Void)?
    var onCancel: (() -> Void)?
    private let annotationFont: NSFont
    private var changeScheduled = false

    init(frame: NSRect, font: NSFont, color: NSColor) {
        annotationFont = font
        super.init(frame: frame)
        borderType = .noBorder
        drawsBackground = false
        hasVerticalScroller = true
        autohidesScrollers = true
        scrollerStyle = .overlay
        hasHorizontalScroller = false
        editor.delegate = self
        editor.isRichText = false
        editor.drawsBackground = false
        editor.font = font
        editor.textColor = color
        editor.insertionPointColor = color
        editor.textContainerInset = .zero
        editor.textContainer?.lineFragmentPadding = 0
        editor.textContainer?.widthTracksTextView = true
        editor.textContainer?.heightTracksTextView = false
        editor.textContainer?.containerSize = NSSize(width: max(1, frame.width),
                                                     height: CGFloat.greatestFiniteMagnitude)
        editor.isHorizontallyResizable = false
        editor.isVerticallyResizable = true
        editor.minSize = .zero
        editor.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineBreakMode = .byWordWrapping
        editor.defaultParagraphStyle = paragraph
        documentView = editor
    }

    required init?(coder: NSCoder) { return nil }

    @discardableResult
    func fitToSelection(anchor: NSPoint, limits: NSRect) -> NSRect {
        let proposed = ScreenshotTextLayout.editorBounds(editor.string, font: annotationFont,
                                                         anchor: anchor, limits: limits)
        // AppKit owns the marked text and its caret/underline attributes. Keep
        // the wrapping width fixed through composition, including long pinyin;
        // reflowing the container inside setMarkedText can hide committed text.
        let width = editor.hasMarkedText() && frame.width > 0 ? min(frame.width, limits.width) : proposed.width
        let initialHeight = min(limits.height, max(1, frame.height))
        let initialTop = min(max(anchor.y, limits.minY + initialHeight), limits.maxY)
        let initial = NSRect(x: proposed.minX, y: initialTop - initialHeight,
                             width: width, height: initialHeight)
        if frame != initial { frame = initial }
        let contentHeight = resizeText()
        let height = min(limits.height, contentHeight)
        let top = min(max(anchor.y, limits.minY + height), limits.maxY)
        let fitted = NSRect(x: proposed.minX, y: top - height, width: width, height: height)
        if frame != fitted { frame = fitted }
        resizeText()
        return fitted
    }

    @discardableResult
    func resizeText() -> CGFloat {
        tile()
        let width = contentSize.width
        guard width > 0, let manager = editor.layoutManager,
              let container = editor.textContainer else { return 0 }
        if editor.frame.width != width {
            editor.setFrameSize(NSSize(width: width, height: max(1, editor.frame.height)))
        }
        manager.ensureLayout(for: container)
        // Measure the existing attributed storage, including IME marked text.
        // editor.font can be nil during composition, and reconstructing a plain
        // string loses the input method's actual line and caret metrics.
        var height = max(manager.defaultLineHeight(for: annotationFont), manager.usedRect(for: container).maxY)
        if manager.extraLineFragmentTextContainer === container {
            height = max(height, manager.extraLineFragmentRect.maxY)
        }
        height = ceil(height) + 2
        let size = NSSize(width: width, height: max(contentSize.height, height))
        if editor.frame.size != size { editor.setFrameSize(size) }
        if height <= contentSize.height {
            // Native caret scrolling may have occurred before the viewport grew.
            // When everything fits, the committed prefix must remain visible.
            if contentView.bounds.origin != .zero {
                contentView.scroll(to: .zero)
                reflectScrolledClipView(contentView)
            }
        } else if !editor.hasMarkedText() {
            editor.scrollRangeToVisible(editor.selectedRange())
        }
        return height
    }

    private func scheduleChange() {
        guard !changeScheduled else { return }
        changeScheduled = true
        // Text/selection notifications can arrive inside an IME editing batch.
        // Coalesce them and adjust geometry only after AppKit finishes that batch.
        DispatchQueue.main.async { [weak self] in
            guard let self, self.changeScheduled else { return }
            self.flushChanges()
        }
    }

    func flushChanges() {
        changeScheduled = false
        onChange?(editor.string)
    }

    func textDidChange(_ notification: Notification) { scheduleChange() }

    func textViewDidChangeSelection(_ notification: Notification) { scheduleChange() }

    func textView(_ textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if textView.hasMarkedText() { return false }
        if commandSelector == #selector(NSResponder.insertNewline(_:)) {
            if NSEvent.modifierFlags.contains(.shift) { return false }
            onCommit?()
            return true
        }
        if commandSelector == #selector(NSResponder.cancelOperation(_:)) {
            onCancel?()
            return true
        }
        return false
    }
}

private extension NSRect {
    var area: CGFloat {
        isNull ? 0 : max(0, width) * max(0, height)
    }
}
