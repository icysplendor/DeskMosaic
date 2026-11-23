import XCTest
@testable import MACAssistant

final class AudioConfigurationTests: XCTestCase {
    func testSchemaOneConfigurationGetsSafeAudioDefaults() throws {
        let data = try XCTUnwrap(#"{"schemaVersion":1}"#.data(using: .utf8))
        let configuration = try JSONDecoder().decode(AppConfiguration.self, from: data)

        XCTAssertEqual(configuration.schemaVersion, 1)
        XCTAssertTrue(configuration.outputAudioFavorites.isEmpty)
        XCTAssertTrue(configuration.inputAudioFavorites.isEmpty)
        XCTAssertTrue(configuration.lockOutputDevice)
        XCTAssertTrue(configuration.lockInputDevice)
        XCTAssertEqual(configuration.theme, "Light")
        XCTAssertEqual(configuration.language, "zh-CN")
        XCTAssertTrue(configuration.silentStart)
        XCTAssertEqual(configuration.screenshotAnnotationColor, "#FFFF0000")
        XCTAssertEqual(configuration.screenshotStrokeWidth, 3)
        XCTAssertEqual(configuration.screenshotFontSize, 18)
        XCTAssertEqual(configuration.screenshotFontWeight, "Medium")
        XCTAssertTrue(configuration.audioEnabled)
        XCTAssertTrue(configuration.audioProfiles.isEmpty)
        XCTAssertNil(configuration.notebookLastShortcut)
        XCTAssertNil(configuration.notebookQuickShortcut)
        XCTAssertNil(configuration.activeAudioProfileID)
        XCTAssertNil(configuration.nextAudioProfileShortcut)
        XCTAssertNil(configuration.previousAudioProfileShortcut)
    }

    func testAudioPreferencesRoundTrip() throws {
        var configuration = AppConfiguration()
        configuration.audioEnabled = false
        configuration.outputAudioFavorites = ["output-1"]
        configuration.inputAudioFavorites = ["input-1"]
        configuration.outputAudioVolumes = ["output-1": 0.42]
        configuration.preferredOutputDeviceID = "output-1"
        configuration.lockInputDevice = false
        configuration.theme = "Dark"
        configuration.language = "en-US"
        configuration.screenshotAnnotationColor = "#FF38BDF8"
        configuration.screenshotStrokeWidth = 8
        configuration.screenshotFontSize = 32
        configuration.screenshotFontWeight = "Bold"
        let profile = AudioConfigurationProfile(
            id: "desk",
            name: "桌面",
            outputDeviceID: "output-1",
            inputDeviceID: "input-1"
        )
        configuration.audioProfiles = [profile]
        configuration.activeAudioProfileID = profile.id
        configuration.nextAudioProfileShortcut = KeyboardShortcut(keyCode: 124, modifiers: 256)
        configuration.previousAudioProfileShortcut = KeyboardShortcut(keyCode: 123, modifiers: 256)
        configuration.notebookLastShortcut = KeyboardShortcut(keyCode: 45, modifiers: 256)
        configuration.notebookQuickShortcut = KeyboardShortcut(keyCode: 12, modifiers: 256)

        let decoded = try JSONDecoder().decode(
            AppConfiguration.self,
            from: JSONEncoder().encode(configuration)
        )

        XCTAssertEqual(decoded, configuration)
    }

    func testLegacyPreferredDevicesBecomeProfileAndHotkeysMigrate() throws {
        let data = Data(#"""
        {
          "schemaVersion": 3,
          "preferredOutputDeviceID": "legacy-output",
          "preferredInputDeviceID": "legacy-input",
          "nextOutputShortcut": { "keyCode": 124, "modifiers": 256 },
          "nextInputShortcut": { "keyCode": 123, "modifiers": 256 }
        }
        """#.utf8)

        let configuration = try JSONDecoder().decode(AppConfiguration.self, from: data)

        XCTAssertEqual(configuration.audioProfiles.count, 1)
        XCTAssertEqual(configuration.audioProfiles[0].outputDeviceID, "legacy-output")
        XCTAssertEqual(configuration.audioProfiles[0].inputDeviceID, "legacy-input")
        XCTAssertEqual(configuration.activeAudioProfileID, "migrated-default-profile")
        XCTAssertEqual(configuration.nextAudioProfileShortcut?.keyCode, 124)
        XCTAssertEqual(configuration.previousAudioProfileShortcut?.keyCode, 123)
    }
}
