import AppKit
import XCTest
@testable import MACAssistant

final class ScreenshotRenderingTests: XCTestCase {
    @MainActor
    func testTextWrapsAndNativeEditorRetainsAllInput() throws {
        let font = NSFont.systemFont(ofSize: 18, weight: .medium)
        let limits = NSRect(x: -300, y: 100, width: 240, height: 500)
        let anchor = NSPoint(x: -290, y: 590)
        for text in ["First line\nSecond line\nThird line",
                     String(repeating: "自动换行测试 English words 😀 ", count: 20),
                     "https://example.com/" + String(repeating: "x", count: 300)] {
            let bounds = ScreenshotTextLayout.editorBounds(text, font: font, anchor: anchor, limits: limits)
            XCTAssertTrue(limits.contains(bounds))
            XCTAssertGreaterThan(bounds.height, 40)
            let editor = ScreenshotTextEditor(frame: bounds, font: font, color: .red)
            editor.editor.string = text
            editor.resizeText()
            let manager = try XCTUnwrap(editor.editor.layoutManager)
            let container = try XCTUnwrap(editor.editor.textContainer)
            manager.ensureLayout(for: container)
            let actualHeight = manager.usedRect(for: container).height
            XCTAssertGreaterThan(actualHeight, 40)
            XCTAssertEqual(ceil(actualHeight),
                           ScreenshotTextLayout.contentHeight(text, font: font, width: bounds.width), accuracy: 1)
            XCTAssertEqual(editor.editor.string, text, "Never silently truncate long input")
        }
        let one = ScreenshotTextLayout.editorBounds("One", font: font, anchor: anchor, limits: limits)
        let two = ScreenshotTextLayout.editorBounds("One\n", font: font, anchor: anchor, limits: limits)
        XCTAssertGreaterThan(two.height, one.height, "Keep final empty caret row visible")
        for size in [NSSize(width: 30, height: 10), NSSize(width: 200, height: 100)] {
            let small = NSRect(origin: NSPoint(x: -80, y: 250), size: size)
            let bounds = ScreenshotTextLayout.editorBounds(String(repeating: "W", count: 600), font: font,
                anchor: NSPoint(x: small.maxX, y: small.minY), limits: small)
            XCTAssertTrue(small.contains(bounds), "Editor must stay within small/edge selections")
        }
    }

    @MainActor
    func testMarkedTextKeepsCommittedPrefixVisibleUntilChineseIsConfirmed() async throws {
        let limits = NSRect(x: 20, y: 20, width: 280, height: 300)
        // Start close to the bottom edge as well as crossing a line boundary.
        let anchor = NSPoint(x: 30, y: 55)
        let host = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 340, height: 350),
                            styleMask: .borderless, backing: .buffered, defer: false)
        host.isReleasedWhenClosed = false
        let root = NSView(frame: NSRect(x: 0, y: 0, width: 340, height: 350))
        host.contentView = root
        let field = ScreenshotTextEditor(frame: .zero, font: NSFont.systemFont(ofSize: 18), color: .red)
        root.addSubview(field)
        field.fitToSelection(anchor: anchor, limits: limits)
        host.orderFront(nil)
        host.makeFirstResponder(field.editor)
        defer {
            field.onChange = nil
            host.close()
        }
        var changes = 0
        var confirmations = 0
        var cancellations = 0
        field.onChange = { [weak field] _ in
            field?.fitToSelection(anchor: anchor, limits: limits)
            changes += 1
        }
        field.onCommit = { confirmations += 1 }
        field.onCancel = { cancellations += 1 }
        let prefix = "已经输入十个左右的文字"
        let insertion = NSRange(location: NSNotFound, length: 0)
        field.editor.insertText(prefix, replacementRange: insertion)
        await waitForAppKitInputUpdates()
        XCTAssertEqual(field.editor.string, prefix)
        let committedWidth = field.frame.width

        for pinyin in ["zhongwenshurufa", "zhongwenshurufaceshi"] {
            let previousChanges = changes
            // IMEs supply attributed marked text; a larger composition font must
            // not suppress resizing or replace the existing committed prefix.
            let marked = NSAttributedString(string: pinyin, attributes: [
                .font: NSFont.systemFont(ofSize: 28), .underlineStyle: NSUnderlineStyle.single.rawValue
            ])
            field.editor.setMarkedText(marked, selectedRange: NSRange(location: pinyin.utf16.count, length: 0),
                                       replacementRange: insertion)
            XCTAssertEqual(changes, previousChanges, "Do not resize inside the native IME edit batch")
            await waitForAppKitInputUpdates()
            XCTAssertGreaterThan(changes, previousChanges, "Marked-text updates must request a new layout")
            XCTAssertTrue(field.editor.hasMarkedText())
            XCTAssertEqual(field.editor.string, prefix + pinyin)
            XCTAssertEqual(field.frame.width, committedWidth, accuracy: 0.01)
            XCTAssertTrue(limits.contains(field.frame))
            let manager = try XCTUnwrap(field.editor.layoutManager)
            let container = try XCTUnwrap(field.editor.textContainer)
            manager.ensureLayout(for: container)
            XCTAssertGreaterThanOrEqual(field.contentSize.height + 0.01, manager.usedRect(for: container).maxY)
            XCTAssertEqual(field.contentView.bounds.origin.x, 0, accuracy: 0.01)
            XCTAssertEqual(field.contentView.bounds.origin.y, 0, accuracy: 0.01)
            let visible = manager.glyphRange(forBoundingRect: field.editor.visibleRect, in: container)
            XCTAssertEqual(visible.location, 0, "The already typed first line must stay visible")
            XCTAssertGreaterThanOrEqual(NSMaxRange(visible), manager.numberOfGlyphs)
            XCTAssertFalse(field.textView(field.editor, doCommandBy: #selector(NSResponder.insertNewline(_:))))
            XCTAssertFalse(field.textView(field.editor, doCommandBy: #selector(NSResponder.cancelOperation(_:))))
            XCTAssertEqual(confirmations, 0)
            XCTAssertEqual(cancellations, 0)
        }
        field.editor.insertText("中文", replacementRange: insertion)
        await waitForAppKitInputUpdates()
        XCTAssertFalse(field.editor.hasMarkedText())
        XCTAssertEqual(field.editor.string, prefix + "中文")
        XCTAssertTrue(limits.contains(field.frame))
        XCTAssertEqual(field.contentView.bounds.origin, .zero)

        // A cancelled composition restores the same committed text.
        field.editor.setMarkedText("quxiao", selectedRange: NSRange(location: 6, length: 0),
                                   replacementRange: insertion)
        await waitForAppKitInputUpdates()
        field.editor.insertText("", replacementRange: field.editor.markedRange())
        await waitForAppKitInputUpdates()
        XCTAssertEqual(field.editor.string, prefix + "中文")
        XCTAssertFalse(field.editor.hasMarkedText())
        XCTAssertEqual(field.contentView.bounds.origin, .zero)

        // Confirmation can occur before the queued change notification runs.
        field.editor.insertText("完成", replacementRange: insertion)
        field.flushChanges()
        XCTAssertEqual(field.editor.string, prefix + "中文完成")
        XCTAssertTrue(limits.contains(field.frame))
        XCTAssertEqual(field.contentView.bounds.origin, .zero)
        XCTAssertTrue(field.textView(field.editor, doCommandBy: #selector(NSResponder.insertNewline(_:))))
        XCTAssertEqual(confirmations, 1)
    }

    @MainActor
    private func waitForAppKitInputUpdates() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            DispatchQueue.main.async { continuation.resume() }
        }
    }

    func testMultilineTextExportsAllRowsAtNormalAndRetinaResolution() throws {
        let bounds = NSRect(x: 15, y: 10, width: 180, height: 90)
        for scale in [1, 2] {
            let image = try XCTUnwrap(ScreenshotBitmapRenderer.render(
                pointSize: NSSize(width: 220, height: 120), pixelWidth: 220 * scale, pixelHeight: 120 * scale
            ) {
                NSColor.white.setFill()
                NSBezierPath(rect: NSRect(x: 0, y: 0, width: 220, height: 120)).fill()
                ScreenshotTextLayout.draw("MMMM\nMMMM\nMMMM", in: bounds,
                                          font: NSFont.systemFont(ofSize: 18), color: .black)
            })
            let bitmap = try representation(of: image)
            var bands = 0
            var previousHasInk = false
            for y in 0..<bitmap.pixelsHigh {
                var hasInk = false
                for x in 0..<bitmap.pixelsWide {
                    let color = try XCTUnwrap(bitmap.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB))
                    if color.redComponent < 0.5 {
                        hasInk = true
                        // Raster rows run down from the top; keep all glyphs in the text box.
                        XCTAssertTrue(bounds.insetBy(dx: -1, dy: -1).contains(NSPoint(
                            x: CGFloat(x) / CGFloat(scale), y: 120 - CGFloat(y) / CGFloat(scale))))
                    }
                }
                if hasInk && !previousHasInk { bands += 1 }
                previousHasInk = hasInk
            }
            XCTAssertEqual(bands, 3, "Final image must contain all three lines")
        }
    }

    private let size = NSSize(width: 317, height: 193)
    private let colors: [NSColor] = [.red, .green, .blue, .yellow]

    func testBrushCursorHasHollowHotspotAndMatchesPhysicalDiameter() {
        for scale in [CGFloat(1), 2] {
            for diameter in [CGFloat(1), 2, 3, 5, 8, 12, 20, 32, 48, 72, 96] {
                let path = ScreenshotBrushCursor.path(pixelDiameter: diameter * scale)
                XCTAssertFalse(path.contains(.zero), "Cursor center must stay transparent")
                // Rasterization rounds the diameter to the nearest physical pixel.
                XCTAssertEqual(path.boundingBoxOfPath.width - 1, diameter * scale, accuracy: 1.01)
                XCTAssertEqual(path.boundingBoxOfPath.height - 1, diameter * scale, accuracy: 1.01)
            }
        }
    }

    func testBrushCursorInvertsRingPixelsSeparatelyAndLeavesCenterUntouched() throws {
        for scale in [1, 2] {
            let center = NSPoint(x: 16 + 0.5 / CGFloat(scale), y: 16 + 0.5 / CGFloat(scale))
            let left = NSColor(deviceRed: 0.25, green: 0.5, blue: 0.75, alpha: 1)
            let right = NSColor(deviceRed: 0.8, green: 0.2, blue: 0.4, alpha: 1)
            let image = try XCTUnwrap(ScreenshotBitmapRenderer.render(
                pointSize: NSSize(width: 32, height: 32),
                pixelWidth: 32 * scale, pixelHeight: 32 * scale
            ) {
                let context = NSGraphicsContext.current!.cgContext
                context.setFillColor(left.cgColor)
                context.fill(NSRect(x: 0, y: 0, width: 16, height: 32))
                context.setFillColor(right.cgColor)
                context.fill(NSRect(x: 16, y: 0, width: 16, height: 32))
                ScreenshotBrushCursor.draw(
                    path: ScreenshotBrushCursor.path(pixelDiameter: CGFloat(12 * scale)),
                    center: center, backingScale: CGFloat(scale), in: context
                )
            })
            let bitmap = try representation(of: image)
            let x = 16 * scale, y = 32 * scale - 1 - 16 * scale
            // Invert different RGB values under the same ring independently,
            // instead of using one color sampled at the center.
            assertColor(bitmap, x: x - 6 * scale, y: y,
                        expected: NSColor(deviceRed: 0.75, green: 0.5, blue: 0.25, alpha: 1))
            assertColor(bitmap, x: x + 6 * scale, y: y,
                        expected: NSColor(deviceRed: 0.2, green: 0.8, blue: 0.6, alpha: 1))
            assertColor(bitmap, x: x, y: y, expected: right)
            assertColor(bitmap, x: 0, y: 0, expected: left)
        }
    }

    func testMosaicSamplesWholeScreenInsteadOfMagnifyingItsCorner() throws {
        for scale in [1, 2] {
            let source = try makeSource(scale: scale)
            let image = ScreenshotBitmapRenderer.makeMosaicImage(image: source, pointSize: size)
            let bitmap = try representation(of: image)
            XCTAssertEqual(image.size, size)
            XCTAssertEqual(bitmap.pixelsWide, 32)
            XCTAssertEqual(bitmap.pixelsHigh, 20)
            for row in 0..<2 {
                for column in 0..<2 {
                    assertColor(
                        bitmap, x: (column * 2 + 1) * bitmap.pixelsWide / 4,
                        y: (row * 2 + 1) * bitmap.pixelsHigh / 4,
                        expected: colors[row * 2 + column]
                    )
                }
            }
        }
    }

    func testBitmapDrawingFillsOutputAtOneAndTwoTimesResolution() throws {
        for scale in [1, 2] {
            let image = try XCTUnwrap(ScreenshotBitmapRenderer.render(
                pointSize: size,
                pixelWidth: Int(size.width) * scale,
                pixelHeight: Int(size.height) * scale
            ) {
                let context = NSGraphicsContext.current!.cgContext
                context.setFillColor(NSColor.red.cgColor)
                context.fill(NSRect(origin: .zero, size: size))
                context.setFillColor(NSColor.blue.cgColor)
                context.fill(NSRect(x: size.width / 2, y: 0,
                                    width: size.width / 2, height: size.height))
            })
            let bitmap = try representation(of: image)
            XCTAssertEqual(image.size, size)
            XCTAssertEqual(bitmap.pixelsWide, Int(size.width) * scale)
            XCTAssertEqual(bitmap.pixelsHigh, Int(size.height) * scale)
            // Check both ends of both axes, including the Retina pixels that
            // were left empty when only image-representation size was changed.
            for y in [1, bitmap.pixelsHigh - 2] {
                assertColor(bitmap, x: 1, y: y, expected: .red)
                assertColor(bitmap, x: bitmap.pixelsWide - 2, y: y, expected: .blue)
            }
        }
    }

    func testLargeAndSmallBrushesUseSameSourceCoordinatesInCroppedRetinaExport() throws {
        let mosaic = ScreenshotBitmapRenderer.makeMosaicImage(image: try makeSource(scale: 2), pointSize: size)
        // A display to the left and above the primary screen. Export a cropped
        // region of its upper-right quadrant, not the origin of the desktop.
        let frame = NSRect(x: -317, y: 137, width: size.width, height: size.height)
        let center = NSPoint(x: frame.minX + size.width * 0.75, y: frame.minY + size.height * 0.75)
        let selection = NSRect(x: center.x - 50, y: center.y - 50, width: 100, height: 100)
        for brushSize in [CGFloat(12), 20, 32, 48, 72, 96] {
            let image = try XCTUnwrap(ScreenshotBitmapRenderer.render(
                pointSize: selection.size, pixelWidth: 200, pixelHeight: 200
            ) {
                NSColor.white.setFill()
                NSBezierPath(rect: NSRect(origin: .zero, size: selection.size)).fill()
                let context = NSGraphicsContext.current!.cgContext
                context.translateBy(x: -selection.minX, y: -selection.minY)
                context.addEllipse(in: NSRect(x: center.x - brushSize / 2,
                                             y: center.y - brushSize / 2,
                                             width: brushSize, height: brushSize))
                context.clip()
                NSGraphicsContext.current!.imageInterpolation = .none
                mosaic.draw(in: frame, from: NSRect(origin: .zero, size: size),
                            operation: .copy, fraction: 1)
            })
            let bitmap = try representation(of: image)
            assertColor(bitmap, x: 100, y: 100, expected: .green)
            // Only the brush footprint changes, not the content's scale or position.
            assertColor(bitmap, x: 2, y: 2, expected: .white)
        }
    }

    private func makeSource(scale: Int) throws -> CGImage {
        let width = Int(size.width) * scale
        let height = Int(size.height) * scale
        let palette: [[UInt8]] = [
            [255, 0, 0, 255], [0, 255, 0, 255],
            [0, 0, 255, 255], [255, 255, 0, 255]
        ]
        var pixels: [UInt8] = []
        pixels.reserveCapacity(width * height * 4)
        // Write explicit RGBA bytes. NSBitmapImageRep.setColor does not accept
        // every NSColor color-space model (including the system named colors
        // on the macOS runner), leaving invalid pixels and flooding the log.
        // Keep this source independent of the renderer under test.
        for y in 0..<height {
            for x in 0..<width {
                let row = y < height / 2 ? 0 : 1
                let column = x < width / 2 ? 0 : 1
                pixels.append(contentsOf: palette[row * 2 + column])
            }
        }
        let provider = try XCTUnwrap(CGDataProvider(data: Data(pixels) as CFData))
        return try XCTUnwrap(CGImage(
            width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32,
            bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue)
                .union(.byteOrder32Big),
            provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent
        ))
    }

    private func representation(of image: NSImage) throws -> NSBitmapImageRep {
        try XCTUnwrap(image.representations.compactMap { $0 as? NSBitmapImageRep }.first)
    }

    private func assertColor(
        _ bitmap: NSBitmapImageRep, x: Int, y: Int, expected: NSColor,
        file: StaticString = #filePath, line: UInt = #line
    ) {
        guard let actual = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB),
              let expected = expected.usingColorSpace(.deviceRGB) else {
            XCTFail("Missing RGB color at \(x), \(y)", file: file, line: line)
            return
        }
        XCTAssertEqual(actual.redComponent, expected.redComponent, accuracy: 0.03, file: file, line: line)
        XCTAssertEqual(actual.greenComponent, expected.greenComponent, accuracy: 0.03, file: file, line: line)
        XCTAssertEqual(actual.blueComponent, expected.blueComponent, accuracy: 0.03, file: file, line: line)
        XCTAssertEqual(actual.alphaComponent, 1, accuracy: 0.03, file: file, line: line)
    }
}
