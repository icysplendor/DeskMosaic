import AppKit
import Foundation

guard CommandLine.arguments.count == 2 else {
    fputs("Usage: make-dmg-background.swift <output.png>\n", stderr)
    exit(2)
}

let canvasSize = NSSize(width: 540, height: 380)
let image = NSImage(size: canvasSize)

image.lockFocus()

NSColor(calibratedWhite: 0.965, alpha: 1).setFill()
NSBezierPath(rect: NSRect(origin: .zero, size: canvasSize)).fill()

let titleStyle = NSMutableParagraphStyle()
titleStyle.alignment = .center

let titleAttributes: [NSAttributedString.Key: Any] = [
    .font: NSFont.systemFont(ofSize: 22, weight: .semibold),
    .foregroundColor: NSColor(calibratedWhite: 0.12, alpha: 1),
    .paragraphStyle: titleStyle
]

let subtitleAttributes: [NSAttributedString.Key: Any] = [
    .font: NSFont.systemFont(ofSize: 13, weight: .regular),
    .foregroundColor: NSColor(calibratedWhite: 0.42, alpha: 1),
    .paragraphStyle: titleStyle
]

("安装 DeskMosaic" as NSString).draw(
    in: NSRect(x: 40, y: 322, width: 460, height: 32),
    withAttributes: titleAttributes
)
("将左侧应用拖到右侧 Applications 文件夹" as NSString).draw(
    in: NSRect(x: 40, y: 294, width: 460, height: 22),
    withAttributes: subtitleAttributes
)

let arrow = NSBezierPath()
arrow.lineWidth = 5
arrow.lineCapStyle = .round
arrow.lineJoinStyle = .round
arrow.move(to: NSPoint(x: 218, y: 190))
arrow.line(to: NSPoint(x: 318, y: 190))
arrow.move(to: NSPoint(x: 298, y: 208))
arrow.line(to: NSPoint(x: 318, y: 190))
arrow.line(to: NSPoint(x: 298, y: 172))
NSColor(calibratedRed: 0.16, green: 0.48, blue: 0.93, alpha: 0.9).setStroke()
arrow.stroke()

image.unlockFocus()

guard
    let tiff = image.tiffRepresentation,
    let bitmap = NSBitmapImageRep(data: tiff),
    let png = bitmap.representation(using: .png, properties: [:])
else {
    fputs("Unable to render DMG background\n", stderr)
    exit(1)
}

try png.write(to: URL(fileURLWithPath: CommandLine.arguments[1]), options: .atomic)
