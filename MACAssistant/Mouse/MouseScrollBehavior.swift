import Foundation

enum MouseScrollBehavior: String, CaseIterable, Identifiable {
    case systemDefault
    case normal
    case reversed

    var id: String { rawValue }

    var title: String {
        switch self {
        case .systemDefault: return "系统默认"
        case .normal: return "正常滚动"
        case .reversed: return "反向滚动"
        }
    }
}

enum ScrollInputSource: Equatable {
    case mouse
    case trackpad
}

struct ScrollSourceClassifier {
    private(set) var lastTouchTimestamp: UInt64 = 0
    private(set) var touchingFingers = 0
    private(set) var lastSource: ScrollInputSource = .mouse

    mutating func recordGesture(touchCount: Int, timestamp: UInt64) {
        guard touchCount >= 2 else { return }
        lastTouchTimestamp = timestamp
        touchingFingers = max(touchingFingers, touchCount)
    }

    mutating func classify(
        isContinuous: Bool,
        hasMomentumPhase: Bool,
        timestamp: UInt64
    ) -> ScrollInputSource {
        let elapsed = timestamp >= lastTouchTimestamp ? timestamp - lastTouchTimestamp : .max
        let fingers = touchingFingers
        touchingFingers = 0

        let source: ScrollInputSource
        if !isContinuous {
            source = .mouse
        } else if fingers >= 2 && elapsed < 222_000_000 {
            source = .trackpad
        } else if !hasMomentumPhase && elapsed > 333_000_000 {
            source = .mouse
        } else {
            source = lastSource
        }

        lastSource = source
        return source
    }
}
