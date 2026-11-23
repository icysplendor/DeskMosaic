import AppKit
import XCTest
@testable import MACAssistant

final class ScrollSourceClassifierTests: XCTestCase {
    func testDiscreteWheelIsAlwaysMouse() {
        var classifier = ScrollSourceClassifier()
        classifier.recordGesture(touchCount: 2, timestamp: 900_000_000)

        let source = classifier.classify(
            isContinuous: false,
            hasMomentumPhase: false,
            timestamp: 1_000_000_000
        )

        XCTAssertEqual(source, .mouse)
    }

    func testRecentTwoFingerGestureIsTrackpad() {
        var classifier = ScrollSourceClassifier()
        classifier.recordGesture(touchCount: 2, timestamp: 900_000_000)

        let source = classifier.classify(
            isContinuous: true,
            hasMomentumPhase: false,
            timestamp: 1_000_000_000
        )

        XCTAssertEqual(source, .trackpad)
    }

    func testMomentumKeepsPreviousTrackpadSource() {
        var classifier = ScrollSourceClassifier()
        classifier.recordGesture(touchCount: 3, timestamp: 900_000_000)
        XCTAssertEqual(
            classifier.classify(
                isContinuous: true,
                hasMomentumPhase: false,
                timestamp: 1_000_000_000
            ),
            .trackpad
        )

        XCTAssertEqual(
            classifier.classify(
                isContinuous: true,
                hasMomentumPhase: true,
                timestamp: 1_500_000_000
            ),
            .trackpad
        )
    }

    func testContinuousInputBecomesMouseAfterTouchTimeout() {
        var classifier = ScrollSourceClassifier()
        classifier.recordGesture(touchCount: 2, timestamp: 100_000_000)

        let source = classifier.classify(
            isContinuous: true,
            hasMomentumPhase: false,
            timestamp: 500_000_001
        )

        XCTAssertEqual(source, .mouse)
    }

    func testSingleFingerGestureDoesNotMarkTrackpadScrolling() {
        var classifier = ScrollSourceClassifier()
        classifier.recordGesture(touchCount: 1, timestamp: 900_000_000)

        let source = classifier.classify(
            isContinuous: true,
            hasMomentumPhase: false,
            timestamp: 1_000_000_000
        )

        XCTAssertEqual(source, .mouse)
    }

    func testTimestampUnderflowFallsBackSafely() {
        var classifier = ScrollSourceClassifier()
        classifier.recordGesture(touchCount: 2, timestamp: 1_000)

        let source = classifier.classify(
            isContinuous: true,
            hasMomentumPhase: false,
            timestamp: 999
        )

        XCTAssertEqual(source, .mouse)
    }

    func testDisplayResolutionRecognizesProportionalModes() {
        let nativeResolution = DisplayResolution(width: 2560, height: 1664)

        XCTAssertTrue(nativeResolution.matchesAspectRatio(width: 1280, height: 832))
        XCTAssertTrue(nativeResolution.matchesAspectRatio(width: 1710, height: 1112))
    }

    func testDisplayResolutionRejectsDifferentAspectRatio() {
        let nativeResolution = DisplayResolution(width: 2560, height: 1664)

        XCTAssertFalse(nativeResolution.matchesAspectRatio(width: 2560, height: 1600))
        XCTAssertFalse(nativeResolution.matchesAspectRatio(width: 0, height: 0))
    }

    func testKeyboardShortcutCodableRoundTrip() throws {
        let shortcut = KeyboardShortcut(keyCode: 1, modifiers: 0)

        let encoded = try JSONEncoder().encode(shortcut)
        let decoded = try JSONDecoder().decode(KeyboardShortcut.self, from: encoded)

        XCTAssertEqual(decoded, shortcut)
        XCTAssertEqual(decoded.displayText, "S")
    }

    func testAppConfigurationCodableRoundTrip() throws {
        var configuration = AppConfiguration()
        configuration.mouseBehavior = MouseScrollBehavior.reversed.rawValue
        configuration.screenshotShortcut = KeyboardShortcut(keyCode: 1, modifiers: 256)
        configuration.displayShortcuts["display|mode"] = KeyboardShortcut(keyCode: 2, modifiers: 512)

        let encoded = try JSONEncoder().encode(configuration)
        let decoded = try JSONDecoder().decode(AppConfiguration.self, from: encoded)

        XCTAssertEqual(decoded, configuration)
        XCTAssertEqual(decoded.schemaVersion, AppConfiguration.currentSchemaVersion)
    }

    func testAppConfigurationRecoversMissingFields() throws {
        let data = Data(#"{"schemaVersion":1}"#.utf8)
        let decoded = try JSONDecoder().decode(AppConfiguration.self, from: data)

        XCTAssertEqual(decoded.mouseBehavior, MouseScrollBehavior.normal.rawValue)
        XCTAssertNil(decoded.screenshotShortcut)
        XCTAssertTrue(decoded.displayShortcuts.isEmpty)
        XCTAssertTrue(decoded.preferredDisplayModes.isEmpty)
    }

    func testScreenshotTextStartsBelowInsertionPoint() {
        let font = NSFont.systemFont(ofSize: 18, weight: .medium)
        let insertionPoint = NSPoint(x: 120, y: 240)
        let bounds = ScreenshotTextLayout.editorBounds("Hello", font: font, anchor: insertionPoint,
                                                       limits: NSRect(x: 0, y: 0, width: 500, height: 500))
        XCTAssertEqual(bounds.minX, insertionPoint.x, accuracy: 0.001)
        XCTAssertEqual(bounds.maxY, insertionPoint.y, accuracy: 0.001)
        XCTAssertLessThan(bounds.minY, insertionPoint.y)
    }
}
