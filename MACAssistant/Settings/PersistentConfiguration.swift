import Foundation

struct DisplayModePreference: Codable, Equatable {
    let width: Int
    let height: Int
    let pixelWidth: Int
    let pixelHeight: Int
    let refreshRateHundredths: Int

    init(mode: DisplayMode) {
        width = mode.width
        height = mode.height
        pixelWidth = mode.pixelWidth
        pixelHeight = mode.pixelHeight
        refreshRateHundredths = Int((mode.refreshRate * 100).rounded())
    }

    func matches(_ mode: DisplayMode) -> Bool {
        matchesGeometry(mode)
            && abs(refreshRateHundredths - Int((mode.refreshRate * 100).rounded())) <= 1
    }

    func matchesGeometry(_ mode: DisplayMode) -> Bool {
        width == mode.width
            && height == mode.height
            && pixelWidth == mode.pixelWidth
            && pixelHeight == mode.pixelHeight
    }
}

struct AudioConfigurationProfile: Codable, Equatable, Identifiable {
    var id: String
    var name: String
    var outputDeviceID: String
    var inputDeviceID: String

    init(
        id: String = UUID().uuidString,
        name: String,
        outputDeviceID: String,
        inputDeviceID: String
    ) {
        self.id = id
        self.name = name
        self.outputDeviceID = outputDeviceID
        self.inputDeviceID = inputDeviceID
    }
}

struct AppConfiguration: Codable, Equatable {
    static let currentSchemaVersion = 5

    var schemaVersion = AppConfiguration.currentSchemaVersion
    var mouseBehavior = MouseScrollBehavior.normal.rawValue
    var notebookLastShortcut: KeyboardShortcut?
    var notebookQuickShortcut: KeyboardShortcut?
    var translationShortcut: KeyboardShortcut?
    var translationOptions = TranslationOptions()
    var screenshotShortcut: KeyboardShortcut?
    var screenshotAnnotationColor = "#FFFF0000"
    var screenshotStrokeWidth = 3.0
    var screenshotFontSize = 18.0
    var screenshotFontWeight = "Medium"
    var regionRecording = RegionRecordingSettings()
    var audioEnabled = true
    var mouseToggleShortcut: KeyboardShortcut?
    var displayShortcuts: [String: KeyboardShortcut] = [:]
    var preferredDisplayModes: [String: DisplayModePreference] = [:]
    var outputAudioFavorites: [String] = []
    var inputAudioFavorites: [String] = []
    var outputAudioVolumes: [String: Double] = [:]
    var inputAudioVolumes: [String: Double] = [:]
    var preferredOutputDeviceID: String?
    var preferredInputDeviceID: String?
    var lockOutputDevice = true
    var lockInputDevice = true
    var nextOutputShortcut: KeyboardShortcut?
    var nextInputShortcut: KeyboardShortcut?
    var audioProfiles: [AudioConfigurationProfile] = []
    var activeAudioProfileID: String?
    var nextAudioProfileShortcut: KeyboardShortcut?
    var previousAudioProfileShortcut: KeyboardShortcut?
    var theme = "Light"
    var language = "zh-CN"
    var silentStart = true

    private enum CodingKeys: String, CodingKey {
        case schemaVersion
        case mouseBehavior
        case notebookLastShortcut
        case notebookQuickShortcut
        case translationShortcut
        case translationOptions
        case screenshotShortcut
        case screenshotAnnotationColor
        case screenshotStrokeWidth
        case screenshotFontSize
        case screenshotFontWeight
        case regionRecording
        case audioEnabled
        case mouseToggleShortcut
        case displayShortcuts
        case preferredDisplayModes
        case outputAudioFavorites
        case inputAudioFavorites
        case outputAudioVolumes
        case inputAudioVolumes
        case preferredOutputDeviceID
        case preferredInputDeviceID
        case lockOutputDevice
        case lockInputDevice
        case nextOutputShortcut
        case nextInputShortcut
        case audioProfiles
        case activeAudioProfileID
        case nextAudioProfileShortcut
        case previousAudioProfileShortcut
        case theme
        case language
        case silentStart
    }

    init() {}

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        schemaVersion = try container.decodeIfPresent(Int.self, forKey: .schemaVersion) ?? 1
        mouseBehavior =
            try container.decodeIfPresent(String.self, forKey: .mouseBehavior)
            ?? MouseScrollBehavior.normal.rawValue
        notebookLastShortcut = try container.decodeIfPresent(KeyboardShortcut.self, forKey: .notebookLastShortcut)
        notebookQuickShortcut = try container.decodeIfPresent(KeyboardShortcut.self, forKey: .notebookQuickShortcut)
        translationShortcut = try container.decodeIfPresent(KeyboardShortcut.self, forKey: .translationShortcut)
        translationOptions = try container.decodeIfPresent(TranslationOptions.self, forKey: .translationOptions) ?? TranslationOptions()
        screenshotShortcut = try container.decodeIfPresent(
            KeyboardShortcut.self,
            forKey: .screenshotShortcut
        )
        let decodedScreenshotColor = try container.decodeIfPresent(String.self, forKey: .screenshotAnnotationColor)
        let allowedScreenshotColors = [
            "#FFFF0000", "#FFFFF200", "#FF38BDF8", "#FF808080", "#FF708238",
            "#FF000000", "#FFFFFFFF", "#FF8A2BE2", "#FFFF8C00"
        ]
        if let decodedScreenshotColor, allowedScreenshotColors.contains(decodedScreenshotColor) {
            screenshotAnnotationColor = decodedScreenshotColor
        } else {
            screenshotAnnotationColor = allowedScreenshotColors[0]
        }
        screenshotStrokeWidth = try container.decodeIfPresent(Double.self, forKey: .screenshotStrokeWidth) ?? 3
        screenshotFontSize = try container.decodeIfPresent(Double.self, forKey: .screenshotFontSize) ?? 18
        screenshotFontWeight = try container.decodeIfPresent(String.self, forKey: .screenshotFontWeight) ?? "Medium"
        regionRecording = try container.decodeIfPresent(RegionRecordingSettings.self, forKey: .regionRecording) ?? RegionRecordingSettings()
        audioEnabled = try container.decodeIfPresent(Bool.self, forKey: .audioEnabled) ?? true
        mouseToggleShortcut = try container.decodeIfPresent(
            KeyboardShortcut.self,
            forKey: .mouseToggleShortcut
        )
        displayShortcuts =
            try container.decodeIfPresent(
                [String: KeyboardShortcut].self,
                forKey: .displayShortcuts
            ) ?? [:]
        preferredDisplayModes =
            try container.decodeIfPresent(
                [String: DisplayModePreference].self,
                forKey: .preferredDisplayModes
            ) ?? [:]
        outputAudioFavorites = try container.decodeIfPresent([String].self, forKey: .outputAudioFavorites) ?? []
        inputAudioFavorites = try container.decodeIfPresent([String].self, forKey: .inputAudioFavorites) ?? []
        outputAudioVolumes = try container.decodeIfPresent([String: Double].self, forKey: .outputAudioVolumes) ?? [:]
        inputAudioVolumes = try container.decodeIfPresent([String: Double].self, forKey: .inputAudioVolumes) ?? [:]
        preferredOutputDeviceID = try container.decodeIfPresent(String.self, forKey: .preferredOutputDeviceID)
        preferredInputDeviceID = try container.decodeIfPresent(String.self, forKey: .preferredInputDeviceID)
        lockOutputDevice = try container.decodeIfPresent(Bool.self, forKey: .lockOutputDevice) ?? true
        lockInputDevice = try container.decodeIfPresent(Bool.self, forKey: .lockInputDevice) ?? true
        nextOutputShortcut = try container.decodeIfPresent(KeyboardShortcut.self, forKey: .nextOutputShortcut)
        nextInputShortcut = try container.decodeIfPresent(KeyboardShortcut.self, forKey: .nextInputShortcut)
        audioProfiles = try container.decodeIfPresent([AudioConfigurationProfile].self, forKey: .audioProfiles) ?? []
        activeAudioProfileID = try container.decodeIfPresent(String.self, forKey: .activeAudioProfileID)
        nextAudioProfileShortcut = try container.decodeIfPresent(KeyboardShortcut.self, forKey: .nextAudioProfileShortcut) ?? nextOutputShortcut
        previousAudioProfileShortcut = try container.decodeIfPresent(KeyboardShortcut.self, forKey: .previousAudioProfileShortcut) ?? nextInputShortcut
        if audioProfiles.isEmpty,
           let preferredOutputDeviceID,
           let preferredInputDeviceID {
            let migrated = AudioConfigurationProfile(
                id: "migrated-default-profile",
                name: "默认配置",
                outputDeviceID: preferredOutputDeviceID,
                inputDeviceID: preferredInputDeviceID
            )
            audioProfiles = [migrated]
            activeAudioProfileID = migrated.id
        }
        theme = try container.decodeIfPresent(String.self, forKey: .theme) ?? "Light"
        language = try container.decodeIfPresent(String.self, forKey: .language) ?? "zh-CN"
        silentStart = try container.decodeIfPresent(Bool.self, forKey: .silentStart) ?? true
    }
}

@MainActor
final class PersistentConfigurationStore {
    static let shared = PersistentConfigurationStore()

    private(set) var configuration: AppConfiguration

    private let fileManager: FileManager
    private let encoder: JSONEncoder
    private let decoder: JSONDecoder
    private let mirrorKey: String
    private let primaryURL: URL?
    private let backupURL: URL?

    private init() {
        let fileManager = FileManager.default
        let encoder = JSONEncoder()
        let decoder = JSONDecoder()
        let mirrorKey = "configuration.v1.mirror"
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]

        self.fileManager = fileManager
        self.encoder = encoder
        self.decoder = decoder
        self.mirrorKey = mirrorKey

        let resolvedPrimaryURL: URL?
        let resolvedBackupURL: URL?
        if let applicationSupport = try? fileManager.url(
            for: .applicationSupportDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: true
        ) {
            let directory = applicationSupport.appendingPathComponent("MACAssistant", isDirectory: true)
            try? fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
            resolvedPrimaryURL = directory.appendingPathComponent("configuration-v1.json")
            resolvedBackupURL = directory.appendingPathComponent("configuration-v1.backup.json")
        } else {
            resolvedPrimaryURL = nil
            resolvedBackupURL = nil
        }
        primaryURL = resolvedPrimaryURL
        backupURL = resolvedBackupURL

        let loaded = Self.loadConfiguration(
            decoder: decoder,
            primaryURL: resolvedPrimaryURL,
            backupURL: resolvedBackupURL,
            mirrorKey: mirrorKey
        )
        configuration = loaded ?? Self.migrateLegacyConfiguration()
        configuration.schemaVersion = AppConfiguration.currentSchemaVersion

        // Repair a missing/corrupt primary file and create all redundant copies.
        persist()
    }

    func update(_ change: (inout AppConfiguration) -> Void) {
        var updated = configuration
        change(&updated)
        updated.schemaVersion = AppConfiguration.currentSchemaVersion
        guard updated != configuration else { return }
        configuration = updated
        persist()
    }

    private func persist() {
        guard let data = try? encoder.encode(configuration) else { return }
        UserDefaults.standard.set(data, forKey: mirrorKey)

        guard let primaryURL else { return }
        if
            let backupURL,
            fileManager.fileExists(atPath: primaryURL.path),
            let currentData = try? Data(contentsOf: primaryURL),
            (try? decoder.decode(AppConfiguration.self, from: currentData)) != nil
        {
            try? currentData.write(to: backupURL, options: .atomic)
        }
        try? data.write(to: primaryURL, options: .atomic)
    }

    private static func loadConfiguration(
        decoder: JSONDecoder,
        primaryURL: URL?,
        backupURL: URL?,
        mirrorKey: String
    ) -> AppConfiguration? {
        for url in [primaryURL, backupURL].compactMap({ $0 }) {
            guard
                let data = try? Data(contentsOf: url),
                let configuration = try? decoder.decode(AppConfiguration.self, from: data),
                configuration.schemaVersion <= AppConfiguration.currentSchemaVersion
            else {
                continue
            }
            return configuration
        }

        guard
            let data = UserDefaults.standard.data(forKey: mirrorKey),
            let configuration = try? decoder.decode(AppConfiguration.self, from: data),
            configuration.schemaVersion <= AppConfiguration.currentSchemaVersion
        else {
            return nil
        }
        return configuration
    }

    private static func migrateLegacyConfiguration() -> AppConfiguration {
        let defaults = UserDefaults.standard
        let decoder = JSONDecoder()
        var configuration = AppConfiguration()

        if let rawBehavior = defaults.string(forKey: "mouseScrollBehavior"),
           MouseScrollBehavior(rawValue: rawBehavior) != nil {
            configuration.mouseBehavior = rawBehavior
        }
        if let data = defaults.data(forKey: "shortcut.screenshot") {
            configuration.screenshotShortcut = try? decoder.decode(KeyboardShortcut.self, from: data)
        }
        if let data = defaults.data(forKey: "shortcut.mouseToggle") {
            configuration.mouseToggleShortcut = try? decoder.decode(KeyboardShortcut.self, from: data)
        }
        if let data = defaults.data(forKey: "shortcut.displayModes") {
            configuration.displayShortcuts =
                (try? decoder.decode([String: KeyboardShortcut].self, from: data)) ?? [:]
        }
        return configuration
    }
}
