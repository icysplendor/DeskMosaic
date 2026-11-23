import AudioToolbox
import Combine
import CoreAudio
import Foundation

enum AudioDeviceKind: String, Codable, CaseIterable, Hashable {
    case output
    case input
}

struct AudioDeviceInfo: Identifiable, Equatable {
    let id: String
    let objectID: AudioDeviceID
    let name: String
    let kind: AudioDeviceKind
    let isDefault: Bool
    let volume: Double
    let isMuted: Bool
}

@MainActor
final class AudioManager: ObservableObject {
    static let shared = AudioManager()

    @Published private(set) var outputs: [AudioDeviceInfo] = []
    @Published private(set) var inputs: [AudioDeviceInfo] = []
    @Published private(set) var lastError: String?
    @Published private(set) var isEnabled: Bool

    private let configurationStore = PersistentConfigurationStore.shared
    private let coreAudioObserver = CoreAudioObserver()
    private var refreshTimer: Timer?
    private var topologyRefreshTimer: Timer?
    private var volumePersistenceTimer: Timer?
    private var pendingOutputVolumes: [String: Double] = [:]
    private var pendingInputVolumes: [String: Double] = [:]
    private var startupRefreshesRemaining = 0
    private var isRefreshing = false

    private init() {
        isEnabled = configurationStore.configuration.audioEnabled
    }

    var currentOutput: AudioDeviceInfo? { outputs.first(where: \.isDefault) }
    var currentInput: AudioDeviceInfo? { inputs.first(where: \.isDefault) }
    var profiles: [AudioConfigurationProfile] { configurationStore.configuration.audioProfiles }
    var activeProfileID: String? { configurationStore.configuration.activeAudioProfileID }

    func start() {
        guard isEnabled else { return }
        coreAudioObserver.start { [weak self] event in
            Task { @MainActor in
                self?.handleCoreAudioEvent(event)
            }
        }
        refresh()
        refreshTimer?.invalidate()
        startupRefreshesRemaining = 40
        scheduleNextRefresh(after: 0.75)
    }

    func stop() {
        refreshTimer?.invalidate()
        refreshTimer = nil
        topologyRefreshTimer?.invalidate()
        topologyRefreshTimer = nil
        volumePersistenceTimer?.invalidate()
        volumePersistenceTimer = nil
        flushPendingVolumes()
        coreAudioObserver.stop()
        startupRefreshesRemaining = 0
    }

    func setEnabled(_ enabled: Bool) {
        guard enabled != isEnabled else { return }
        configurationStore.update { $0.audioEnabled = enabled }
        isEnabled = enabled
        if enabled {
            start()
        } else {
            stop()
            outputs = []
            inputs = []
            lastError = nil
        }
        GlobalShortcutManager.shared.reload()
    }

    private func scheduleNextRefresh(after interval: TimeInterval) {
        refreshTimer = Timer.scheduledTimer(withTimeInterval: interval, repeats: false) { _ in
            Task { @MainActor in
                guard AudioManager.shared.refreshTimer != nil else { return }
                AudioManager.shared.refresh()
                if AudioManager.shared.startupRefreshesRemaining > 0 {
                    AudioManager.shared.startupRefreshesRemaining -= 1
                }
                let fallbackInterval = AudioManager.shared.coreAudioObserver.needsFrequentPolling ? 5.0 : 30.0
                let nextInterval = AudioManager.shared.startupRefreshesRemaining > 0 ? 0.75 : fallbackInterval
                AudioManager.shared.scheduleNextRefresh(after: nextInterval)
            }
        }
    }

    func refresh() {
        guard isEnabled else { return }
        guard !isRefreshing else { return }
        isRefreshing = true
        defer { isRefreshing = false }

        do {
            var nextOutputs = try CoreAudioController.enumerate(.output)
            var nextInputs = try CoreAudioController.enumerate(.input)
            let configuration = configurationStore.configuration

            if configuration.lockOutputDevice,
               let preferred = configuration.preferredOutputDeviceID,
               nextOutputs.contains(where: { $0.id == preferred }),
               nextOutputs.first(where: \.isDefault)?.id != preferred {
                try CoreAudioController.setDefaultDevice(uid: preferred, kind: .output)
                nextOutputs = try CoreAudioController.enumerate(.output)
            }
            if configuration.lockInputDevice,
               let preferred = configuration.preferredInputDeviceID,
               nextInputs.contains(where: { $0.id == preferred }),
               nextInputs.first(where: \.isDefault)?.id != preferred {
                try CoreAudioController.setDefaultDevice(uid: preferred, kind: .input)
                nextInputs = try CoreAudioController.enumerate(.input)
            }

            if outputs != nextOutputs { outputs = nextOutputs }
            if inputs != nextInputs { inputs = nextInputs }
            coreAudioObserver.observe(devices: nextOutputs + nextInputs)
            rememberInitialDefaultsAndCurrentVolumes()
            if lastError != nil { lastError = nil }
        } catch {
            lastError = error.localizedDescription
            AppFileLogger.log("Audio refresh failed: \(error)")
        }
    }

    func setLock(_ kind: AudioDeviceKind, enabled: Bool) {
        guard isEnabled else { return }
        let current = kind == .output ? currentOutput : currentInput
        configurationStore.update { configuration in
            if kind == .output {
                configuration.lockOutputDevice = enabled
                if enabled { configuration.preferredOutputDeviceID = current?.id }
            } else {
                configuration.lockInputDevice = enabled
                if enabled { configuration.preferredInputDeviceID = current?.id }
            }
        }
        objectWillChange.send()
    }

    func setVolume(_ volume: Double, for device: AudioDeviceInfo) {
        guard isEnabled else { return }
        let normalized = min(max(volume, 0), 1)
        do {
            try CoreAudioController.setVolume(normalized, device: device)
            discardPendingVolume(for: device)
            configurationStore.update { configuration in
                if device.kind == .output { configuration.outputAudioVolumes[device.id] = normalized }
                else { configuration.inputAudioVolumes[device.id] = normalized }
            }
            replaceVolume(for: device, volume: normalized)
            lastError = nil
        } catch {
            lastError = error.localizedDescription
            AppFileLogger.log("Audio volume update failed: \(error)")
        }
    }

    func addCurrentProfile() {
        guard isEnabled else { return }
        guard let output = currentOutput, let input = currentInput else {
            lastError = "需要同时存在输出和输入设备才能保存配置组。"
            return
        }
        let profile = AudioConfigurationProfile(
            name: "配置 \(profiles.count + 1)",
            outputDeviceID: output.id,
            inputDeviceID: input.id
        )
        configurationStore.update { configuration in
            configuration.audioProfiles.append(profile)
            configuration.activeAudioProfileID = profile.id
            configuration.preferredOutputDeviceID = output.id
            configuration.preferredInputDeviceID = input.id
        }
        objectWillChange.send()
        GlobalShortcutManager.shared.reload()
    }

    func updateProfile(
        id: String,
        name: String,
        outputDeviceID: String,
        inputDeviceID: String
    ) {
        guard !outputDeviceID.isEmpty, !inputDeviceID.isEmpty else { return }
        configurationStore.update { configuration in
            guard let index = configuration.audioProfiles.firstIndex(where: { $0.id == id }) else { return }
            configuration.audioProfiles[index].name = name
            configuration.audioProfiles[index].outputDeviceID = outputDeviceID
            configuration.audioProfiles[index].inputDeviceID = inputDeviceID
            if configuration.activeAudioProfileID == id {
                configuration.preferredOutputDeviceID = outputDeviceID
                configuration.preferredInputDeviceID = inputDeviceID
            }
        }
        objectWillChange.send()
    }

    func deleteProfile(id: String) {
        configurationStore.update { configuration in
            configuration.audioProfiles.removeAll(where: { $0.id == id })
            if configuration.activeAudioProfileID == id {
                configuration.activeAudioProfileID = nil
            }
        }
        objectWillChange.send()
        GlobalShortcutManager.shared.reload()
    }

    func activateProfile(id: String) {
        guard isEnabled else { return }
        guard let profile = profiles.first(where: { $0.id == id }) else { return }
        guard
            let output = outputs.first(where: { $0.id == profile.outputDeviceID }),
            let input = inputs.first(where: { $0.id == profile.inputDeviceID })
        else {
            lastError = "配置组中的输出或输入设备当前未连接。"
            return
        }

        rememberCurrentVolume(for: .output)
        rememberCurrentVolume(for: .input)
        do {
            configurationStore.update { configuration in
                configuration.activeAudioProfileID = profile.id
                configuration.preferredOutputDeviceID = profile.outputDeviceID
                configuration.preferredInputDeviceID = profile.inputDeviceID
            }
            if currentOutput?.id != output.id {
                try CoreAudioController.setDefaultDevice(uid: output.id, kind: .output)
            }
            if currentInput?.id != input.id {
                try CoreAudioController.setDefaultDevice(uid: input.id, kind: .input)
            }
            if let volume = configurationStore.configuration.outputAudioVolumes[output.id] {
                try CoreAudioController.setVolume(volume, device: output)
            }
            if let volume = configurationStore.configuration.inputAudioVolumes[input.id] {
                try CoreAudioController.setVolume(volume, device: input)
            }
            refresh()
            lastError = nil
        } catch {
            lastError = error.localizedDescription
            AppFileLogger.log("Audio profile activation failed: \(error)")
        }
    }

    func cycleProfile(direction: Int) {
        guard isEnabled else { return }
        let available = profiles.filter { profile in
            outputs.contains(where: { $0.id == profile.outputDeviceID })
                && inputs.contains(where: { $0.id == profile.inputDeviceID })
        }
        guard !available.isEmpty else {
            lastError = "没有输出和输入设备都在线的配置组。"
            return
        }
        let currentIndex = available.firstIndex(where: {
                $0.outputDeviceID == currentOutput?.id && $0.inputDeviceID == currentInput?.id
            }) ?? available.firstIndex(where: { $0.id == activeProfileID })
        let offset = direction < 0 ? -1 : 1
        let nextIndex = currentIndex.map { ($0 + offset + available.count) % available.count }
            ?? (offset < 0 ? available.count - 1 : 0)
        activateProfile(id: available[nextIndex].id)
    }

    func deviceName(id: String, kind: AudioDeviceKind) -> String? {
        (kind == .output ? outputs : inputs).first(where: { $0.id == id })?.name
    }

    private func rememberInitialDefaultsAndCurrentVolumes() {
        let output = currentOutput
        let input = currentInput
        configurationStore.update { configuration in
            if configuration.preferredOutputDeviceID == nil { configuration.preferredOutputDeviceID = output?.id }
            if configuration.preferredInputDeviceID == nil { configuration.preferredInputDeviceID = input?.id }
            let matchingProfileID = configuration.audioProfiles.first(where: {
                $0.outputDeviceID == output?.id && $0.inputDeviceID == input?.id
            })?.id
            if configuration.activeAudioProfileID == nil
                || (!configuration.lockOutputDevice && !configuration.lockInputDevice) {
                configuration.activeAudioProfileID = matchingProfileID
            }
            if let output { configuration.outputAudioVolumes[output.id] = output.volume }
            if let input { configuration.inputAudioVolumes[input.id] = input.volume }
        }
    }

    private func rememberCurrentVolume(for kind: AudioDeviceKind) {
        guard let current = kind == .output ? currentOutput : currentInput else { return }
        configurationStore.update { configuration in
            if kind == .output { configuration.outputAudioVolumes[current.id] = current.volume }
            else { configuration.inputAudioVolumes[current.id] = current.volume }
        }
    }

    private func replaceVolume(for device: AudioDeviceInfo, volume: Double) {
        let update: (AudioDeviceInfo) -> AudioDeviceInfo = { item in
            guard item.id == device.id else { return item }
            return AudioDeviceInfo(
                id: item.id,
                objectID: item.objectID,
                name: item.name,
                kind: item.kind,
                isDefault: item.isDefault,
                volume: volume,
                isMuted: item.isMuted
            )
        }
        if device.kind == .output { outputs = outputs.map(update) }
        else { inputs = inputs.map(update) }
    }

    private func handleCoreAudioEvent(_ event: CoreAudioObserver.Event) {
        guard isEnabled else { return }
        switch event {
        case .topologyChanged:
            topologyRefreshTimer?.invalidate()
            topologyRefreshTimer = Timer.scheduledTimer(withTimeInterval: 0.15, repeats: false) { _ in
                Task { @MainActor in
                    AudioManager.shared.topologyRefreshTimer = nil
                    AudioManager.shared.refresh()
                }
            }
        case .volumeChanged(let objectID, let kind):
            refreshVolume(objectID: objectID, kind: kind)
        }
    }

    private func refreshVolume(objectID: AudioDeviceID, kind: AudioDeviceKind) {
        let devices = kind == .output ? outputs : inputs
        guard let device = devices.first(where: { $0.objectID == objectID }) else { return }
        let volume = CoreAudioController.volume(objectID, kind: kind)
        let isMuted = CoreAudioController.muted(objectID, kind: kind)
        guard abs(device.volume - volume) > 0.0005 || device.isMuted != isMuted else { return }

        let update: (AudioDeviceInfo) -> AudioDeviceInfo = { item in
            guard item.objectID == objectID else { return item }
            return AudioDeviceInfo(
                id: item.id,
                objectID: item.objectID,
                name: item.name,
                kind: item.kind,
                isDefault: item.isDefault,
                volume: volume,
                isMuted: isMuted
            )
        }
        if kind == .output { outputs = outputs.map(update) }
        else { inputs = inputs.map(update) }

        if abs(device.volume - volume) > 0.0005 {
            if kind == .output { pendingOutputVolumes[device.id] = volume }
            else { pendingInputVolumes[device.id] = volume }
            scheduleVolumePersistence()
        }
    }

    private func scheduleVolumePersistence() {
        volumePersistenceTimer?.invalidate()
        volumePersistenceTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: false) { _ in
            Task { @MainActor in
                AudioManager.shared.volumePersistenceTimer = nil
                AudioManager.shared.flushPendingVolumes()
            }
        }
    }

    private func flushPendingVolumes() {
        guard !pendingOutputVolumes.isEmpty || !pendingInputVolumes.isEmpty else { return }
        let outputVolumes = pendingOutputVolumes
        let inputVolumes = pendingInputVolumes
        pendingOutputVolumes.removeAll()
        pendingInputVolumes.removeAll()
        configurationStore.update { configuration in
            configuration.outputAudioVolumes.merge(outputVolumes) { _, latest in latest }
            configuration.inputAudioVolumes.merge(inputVolumes) { _, latest in latest }
        }
    }

    private func discardPendingVolume(for device: AudioDeviceInfo) {
        if device.kind == .output { pendingOutputVolumes.removeValue(forKey: device.id) }
        else { pendingInputVolumes.removeValue(forKey: device.id) }
    }

}

private final class CoreAudioObserver {
    enum Event {
        case topologyChanged
        case volumeChanged(AudioDeviceID, AudioDeviceKind)
    }

    private struct DeviceKey: Hashable {
        let objectID: AudioDeviceID
        let kind: AudioDeviceKind
    }

    private struct Registration {
        let objectID: AudioObjectID
        let address: AudioObjectPropertyAddress
        let listener: AudioObjectPropertyListenerBlock
    }

    private let callbackQueue = DispatchQueue(label: "com.macassistant.core-audio-events")
    private var handler: ((Event) -> Void)?
    private var systemRegistrations: [Registration] = []
    private var deviceRegistrations: [Registration] = []
    private var observedDevices: Set<DeviceKey> = []
    private var systemRegistrationFailed = false
    private var deviceRegistrationFailed = false
    private var registrationAttemptFailed = false

    var needsFrequentPolling: Bool {
        systemRegistrationFailed || deviceRegistrationFailed
    }

    func start(handler: @escaping (Event) -> Void) {
        stop()
        self.handler = handler
        registerSystemListeners()
    }

    func observe(devices: [AudioDeviceInfo]) {
        if systemRegistrationFailed {
            registerSystemListeners()
        }
        let nextDevices = Set(devices.map { DeviceKey(objectID: $0.objectID, kind: $0.kind) })
        guard nextDevices != observedDevices || deviceRegistrationFailed else { return }
        remove(&deviceRegistrations)
        observedDevices = nextDevices
        registrationAttemptFailed = false

        for device in devices {
            for address in CoreAudioController.volumeAddresses(device.kind) {
                if let registration = addListener(
                    objectID: device.objectID,
                    address: address,
                    event: .volumeChanged(device.objectID, device.kind)
                ) {
                    deviceRegistrations.append(registration)
                }
            }
            let muteAddress = AudioObjectPropertyAddress(
                mSelector: kAudioDevicePropertyMute,
                mScope: CoreAudioController.scope(device.kind),
                mElement: kAudioObjectPropertyElementMain
            )
            if let registration = addListener(
                objectID: device.objectID,
                address: muteAddress,
                event: .volumeChanged(device.objectID, device.kind)
            ) {
                deviceRegistrations.append(registration)
            }
        }
        deviceRegistrationFailed = registrationAttemptFailed
    }

    private func registerSystemListeners() {
        remove(&systemRegistrations)
        registrationAttemptFailed = false
        let systemObject = AudioObjectID(kAudioObjectSystemObject)
        let selectors: [AudioObjectPropertySelector] = [
            kAudioHardwarePropertyDevices,
            kAudioHardwarePropertyDefaultOutputDevice,
            kAudioHardwarePropertyDefaultSystemOutputDevice,
            kAudioHardwarePropertyDefaultInputDevice
        ]
        for selector in selectors {
            let address = AudioObjectPropertyAddress(
                mSelector: selector,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain
            )
            if let registration = addListener(objectID: systemObject, address: address, event: .topologyChanged) {
                systemRegistrations.append(registration)
            }
        }
        systemRegistrationFailed = registrationAttemptFailed
    }

    func stop() {
        remove(&deviceRegistrations)
        remove(&systemRegistrations)
        observedDevices.removeAll()
        systemRegistrationFailed = false
        deviceRegistrationFailed = false
        registrationAttemptFailed = false
        handler = nil
    }

    private func addListener(
        objectID: AudioObjectID,
        address: AudioObjectPropertyAddress,
        event: Event
    ) -> Registration? {
        var address = address
        guard AudioObjectHasProperty(objectID, &address) else { return nil }
        let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            DispatchQueue.main.async {
                self?.handler?(event)
            }
        }
        guard AudioObjectAddPropertyListenerBlock(objectID, &address, callbackQueue, listener) == noErr else {
            registrationAttemptFailed = true
            return nil
        }
        return Registration(objectID: objectID, address: address, listener: listener)
    }

    private func remove(_ registrations: inout [Registration]) {
        for registration in registrations {
            var address = registration.address
            AudioObjectRemovePropertyListenerBlock(
                registration.objectID,
                &address,
                callbackQueue,
                registration.listener
            )
        }
        registrations.removeAll()
    }
}

private enum CoreAudioController {
    static func enumerate(_ kind: AudioDeviceKind) throws -> [AudioDeviceInfo] {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        try check(AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size))
        let count = Int(size) / MemoryLayout<AudioDeviceID>.size
        guard count > 0 else { return [] }
        var devices = [AudioDeviceID](repeating: 0, count: count)
        let status = devices.withUnsafeMutableBytes { buffer in
            AudioObjectGetPropertyData(
                AudioObjectID(kAudioObjectSystemObject),
                &address,
                0,
                nil,
                &size,
                buffer.baseAddress!
            )
        }
        try check(status)
        let defaultID = try defaultDeviceID(kind)
        return devices.compactMap { deviceID in
            guard hasStreams(deviceID, kind: kind), let uid = stringProperty(deviceID, selector: kAudioDevicePropertyDeviceUID) else { return nil }
            let name = stringProperty(deviceID, selector: kAudioObjectPropertyName) ?? "Unknown audio device"
            return AudioDeviceInfo(
                id: uid,
                objectID: deviceID,
                name: name,
                kind: kind,
                isDefault: deviceID == defaultID,
                volume: volume(deviceID, kind: kind),
                isMuted: muted(deviceID, kind: kind)
            )
        }.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    static func setDefaultDevice(uid: String, kind: AudioDeviceKind) throws {
        guard let device = try enumerate(kind).first(where: { $0.id == uid }) else {
            throw AudioControlError.deviceUnavailable
        }
        var value = device.objectID
        let selectors: [AudioObjectPropertySelector] = kind == .output
            ? [kAudioHardwarePropertyDefaultOutputDevice, kAudioHardwarePropertyDefaultSystemOutputDevice]
            : [kAudioHardwarePropertyDefaultInputDevice]
        for selector in selectors {
            var address = AudioObjectPropertyAddress(
                mSelector: selector,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain
            )
            try check(AudioObjectSetPropertyData(
                AudioObjectID(kAudioObjectSystemObject),
                &address,
                0,
                nil,
                UInt32(MemoryLayout<AudioDeviceID>.size),
                &value
            ))
        }
    }

    static func setVolume(_ volume: Double, device: AudioDeviceInfo) throws {
        var value = Float32(min(max(volume, 0), 1))
        var changed = false
        for candidate in volumeAddresses(device.kind) {
            var address = candidate
            guard AudioObjectHasProperty(device.objectID, &address) else { continue }
            var settable: DarwinBoolean = false
            guard AudioObjectIsPropertySettable(device.objectID, &address, &settable) == noErr, settable.boolValue else { continue }
            try check(AudioObjectSetPropertyData(device.objectID, &address, 0, nil, UInt32(MemoryLayout<Float32>.size), &value))
            changed = true
            if address.mSelector == kAudioHardwareServiceDeviceProperty_VirtualMainVolume { break }
        }
        guard changed else { throw AudioControlError.volumeUnavailable }
    }

    private static func defaultDeviceID(_ kind: AudioDeviceKind) throws -> AudioDeviceID {
        var address = AudioObjectPropertyAddress(
            mSelector: kind == .output ? kAudioHardwarePropertyDefaultOutputDevice : kAudioHardwarePropertyDefaultInputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var value = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        try check(AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &value))
        return value
    }

    private static func hasStreams(_ device: AudioDeviceID, kind: AudioDeviceKind) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreams,
            mScope: scope(kind),
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        return AudioObjectGetPropertyDataSize(device, &address, 0, nil, &size) == noErr && size > 0
    }

    private static func stringProperty(_ device: AudioDeviceID, selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: selector,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr else { return nil }
        guard let value else { return nil }
        return value.takeUnretainedValue() as String
    }

    static func volume(_ device: AudioDeviceID, kind: AudioDeviceKind) -> Double {
        for candidate in volumeAddresses(kind) {
            var address = candidate
            var value = Float32(1)
            var size = UInt32(MemoryLayout<Float32>.size)
            if AudioObjectHasProperty(device, &address), AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr {
                return Double(value)
            }
        }
        return 1
    }

    static func muted(_ device: AudioDeviceID, kind: AudioDeviceKind) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyMute,
            mScope: scope(kind),
            mElement: kAudioObjectPropertyElementMain
        )
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectHasProperty(device, &address), AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr else { return false }
        return value != 0
    }

    static func volumeAddresses(_ kind: AudioDeviceKind) -> [AudioObjectPropertyAddress] {
        [
            AudioObjectPropertyAddress(
                mSelector: kAudioHardwareServiceDeviceProperty_VirtualMainVolume,
                mScope: scope(kind),
                mElement: kAudioObjectPropertyElementMain
            ),
            AudioObjectPropertyAddress(
                mSelector: kAudioDevicePropertyVolumeScalar,
                mScope: scope(kind),
                mElement: kAudioObjectPropertyElementMain
            ),
            AudioObjectPropertyAddress(
                mSelector: kAudioDevicePropertyVolumeScalar,
                mScope: scope(kind),
                mElement: 1
            ),
            AudioObjectPropertyAddress(
                mSelector: kAudioDevicePropertyVolumeScalar,
                mScope: scope(kind),
                mElement: 2
            )
        ]
    }

    static func scope(_ kind: AudioDeviceKind) -> AudioObjectPropertyScope {
        kind == .output ? kAudioDevicePropertyScopeOutput : kAudioDevicePropertyScopeInput
    }

    private static func check(_ status: OSStatus) throws {
        guard status == noErr else { throw AudioControlError.coreAudio(status) }
    }
}

private enum AudioControlError: LocalizedError {
    case coreAudio(OSStatus)
    case deviceUnavailable
    case volumeUnavailable

    var errorDescription: String? {
        switch self {
        case .coreAudio(let status): return "Core Audio 操作失败（\(status)）"
        case .deviceUnavailable: return "音频设备已断开"
        case .volumeUnavailable: return "该设备不支持软件音量控制"
        }
    }
}

private enum AppFileLogger {
    static func log(_ message: String) {
        guard let root = try? FileManager.default.url(
            for: .applicationSupportDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: true
        ) else { return }
        let directory = root.appendingPathComponent("MACAssistant/logs", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let url = directory.appendingPathComponent("assistant.log")
        let line = "[\(ISO8601DateFormatter().string(from: Date()))] \(message)\n"
        guard let data = line.data(using: .utf8) else { return }
        if FileManager.default.fileExists(atPath: url.path), let handle = try? FileHandle(forWritingTo: url) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: data)
        } else {
            try? data.write(to: url, options: .atomic)
        }
    }
}
