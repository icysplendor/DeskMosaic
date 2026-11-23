import CoreGraphics
import Foundation

struct DisplayMode: Identifiable, Hashable {
    let id: Int32
    let width: Int
    let height: Int
    let pixelWidth: Int
    let pixelHeight: Int
    let refreshRate: Double
    let ioFlags: UInt32
    let isUsableForDesktopGUI: Bool
    let cgMode: CGDisplayMode

    var isHiDPI: Bool {
        pixelWidth > width || pixelHeight > height
    }

    var label: String {
        label(relativeTo: nil)
    }

    func label(relativeTo nativeResolution: DisplayResolution?) -> String {
        var attributes: [String] = []
        if isHiDPI {
            attributes.append("HiDPI")
        }
        if let nativeResolution {
            if nativeResolution.width == width, nativeResolution.height == height {
                attributes.append("原生")
            } else if nativeResolution.matchesAspectRatio(width: width, height: height) {
                attributes.append("等比例")
            } else {
                attributes.append("非等比例")
            }
        }

        let suffix = attributes.isEmpty ? "" : " · \(attributes.joined(separator: " · "))"
        return "\(width) × \(height) @ \(formattedRefreshRate)Hz\(suffix)"
    }

    private var formattedRefreshRate: String {
        let effectiveRate = refreshRate > 0 ? refreshRate : 60
        if effectiveRate.rounded() == effectiveRate {
            return String(Int(effectiveRate))
        }
        return String(format: "%.2f", effectiveRate)
            .replacingOccurrences(of: #"0+$"#, with: "", options: .regularExpression)
            .replacingOccurrences(of: #"\.$"#, with: "", options: .regularExpression)
    }

    init(_ mode: CGDisplayMode, fallbackRefreshRate: Double = 60) {
        id = mode.ioDisplayModeID
        width = mode.width
        height = mode.height
        pixelWidth = mode.pixelWidth
        pixelHeight = mode.pixelHeight
        refreshRate = mode.refreshRate > 0 ? mode.refreshRate : fallbackRefreshRate
        ioFlags = mode.ioFlags
        isUsableForDesktopGUI = mode.isUsableForDesktopGUI()
        cgMode = mode
    }

    static func == (lhs: DisplayMode, rhs: DisplayMode) -> Bool {
        lhs.id == rhs.id
    }

    func hash(into hasher: inout Hasher) {
        hasher.combine(id)
    }
}

struct DisplayResolution: Hashable {
    let width: Int
    let height: Int

    var label: String {
        "\(width) × \(height)"
    }

    func matchesAspectRatio(width candidateWidth: Int, height candidateHeight: Int) -> Bool {
        guard width > 0, height > 0, candidateWidth > 0, candidateHeight > 0 else {
            return false
        }
        let nativeAspectRatio = Double(width) / Double(height)
        let candidateAspectRatio = Double(candidateWidth) / Double(candidateHeight)
        return abs(candidateAspectRatio - nativeAspectRatio) / nativeAspectRatio < 0.005
    }
}

struct ConnectedDisplay: Identifiable {
    let id: CGDirectDisplayID
    let name: String
    let stableIdentifier: String
    var currentModeID: Int32?
    let nativeResolution: DisplayResolution?
    let modes: [DisplayMode]

    func shortcutIdentifier(for mode: DisplayMode) -> String {
        let roundedRate = Int((mode.refreshRate * 100).rounded())
        return [
            stableIdentifier,
            "\(mode.width)x\(mode.height)",
            "\(mode.pixelWidth)x\(mode.pixelHeight)",
            "\(roundedRate)"
        ].joined(separator: "|")
    }
}
