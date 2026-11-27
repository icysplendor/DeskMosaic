import AppKit
import SwiftUI

struct SettingsView: View {
    enum Page: String, CaseIterable, Identifiable {
        case overview, audio, screenshot, translation, displays, mouse, hotkeys, general

        var id: String { rawValue }
        var symbol: String {
            switch self {
            case .overview: return "square.grid.2x2"
            case .audio: return "speaker.wave.2"
            case .displays: return "display"
            case .screenshot: return "camera.viewfinder"
            case .mouse: return "computermouse"
            case .hotkeys: return "keyboard"
            case .general: return "gearshape"
            case .translation: return "captions.bubble"
            }
        }
    }

    private struct ScreenshotColorChoice: Identifiable {
        let hex: String
        let color: Color
        var id: String { hex }
    }

    @EnvironmentObject private var displays: DisplayManager
    @EnvironmentObject private var mouseScroll: MouseScrollManager
    @EnvironmentObject private var settings: SettingsManager
    @EnvironmentObject private var audio: AudioManager
    @State private var recordingSettings = false
    @Binding var page: Page

    init(page: Binding<Page>) { _page = page }

    private var dark: Bool { settings.theme == "Dark" }
    private var surface: Color { dark ? Color(white: 0.11) : Color(red: 240/255, green: 241/255, blue: 246/255) }
    private var panel: Color { dark ? Color(red: 0.15, green: 0.16, blue: 0.20) : .white }
    private var line: Color { dark ? Color(white: 0.28) : Color(red: 0.85, green: 0.86, blue: 0.89) }
    private let accent = Color(red: 0.30, green: 0.35, blue: 0.45)

    var body: some View {
        VStack(spacing: 0) {
            applicationTitleBar
            if page != .overview { pageHeader }
            ScrollView {
                pageContent
                    .padding(.horizontal, 18)
                    .padding(.bottom, 12)
            }
            Divider()
            pageTabs
        }
        .background(surface)
        .font(.system(size: 13))
        .preferredColorScheme(dark ? .dark : .light)
        .onAppear {
            audio.refresh()
            displays.refresh()
            settings.refresh()
            mouseScroll.refreshPermissionStatus()
        }
    }

    private var applicationTitleBar: some View {
        ZStack {
            WindowDragRegion()
            HStack(spacing: 0) {
                HStack(spacing: 0) {
                    windowControl(color: Color(red: 1, green: 0.37, blue: 0.34), symbol: "xmark", help: t("关闭", "Close")) {
                        activeWindow?.performClose(nil)
                    }
                    windowControl(color: Color(red: 1, green: 0.74, blue: 0.22), symbol: "minus", help: t("最小化", "Minimize")) {
                        activeWindow?.miniaturize(nil)
                    }
                    windowControl(color: Color(red: 0.18, green: 0.78, blue: 0.35), symbol: "plus", help: t("缩放", "Zoom")) {
                        activeWindow?.zoom(nil)
                    }
                }
                .padding(.trailing, 10)

                Image("AppEmblem")
                    .resizable()
                    .interpolation(.high)
                    .frame(width: 28, height: 28)
                Text("DeskMosaic")
                    .font(.system(size: 14, weight: .semibold))
                    .padding(.leading, 9)
                Rectangle()
                    .fill(line)
                    .frame(width: 1, height: 20)
                    .padding(.horizontal, 16)
                Spacer()
                HStack(spacing: 14) {
                    Text(pageName(page))
                        .font(.system(size: 12, weight: .semibold))
                    Circle()
                        .fill(Color(red: 0.14, green: 0.7, blue: 0.35))
                        .frame(width: 6, height: 6)
                    Text(t("运行中", "Running"))
                        .font(.system(size: 10))
                        .foregroundStyle(.secondary)
                }
                Spacer()
                titleBarThemeSwitch
            }
            .padding(.horizontal, 18)
        }
        .frame(height: 52)
        .background(dark ? Color(red: 0.055, green: 0.086, blue: 0.13) : Color(red: 0.982, green: 0.989, blue: 0.998))
        .overlay(alignment: .bottom) {
            Rectangle().fill(line).frame(height: 1)
        }
    }

    private var titleBarThemeSwitch: some View {
        Button {
            settings.setTheme(dark ? "Light" : "Dark")
        } label: {
            HStack(spacing: 2) {
                Image(systemName: "sun.max.fill")
                    .foregroundStyle(dark ? Color.secondary : accent)
                    .frame(width: 25, height: 23)
                    .background(dark ? Color.clear : panel, in: RoundedRectangle(cornerRadius: 6))
                Image(systemName: "moon.fill")
                    .foregroundStyle(dark ? accent : Color.secondary)
                    .frame(width: 25, height: 23)
                    .background(dark ? panel : Color.clear, in: RoundedRectangle(cornerRadius: 6))
            }
            .font(.system(size: 11, weight: .semibold))
            .padding(3)
            .background(surface.opacity(0.72), in: RoundedRectangle(cornerRadius: 9))
            .overlay(RoundedRectangle(cornerRadius: 9).stroke(line))
        }
        .buttonStyle(DeskPlainButtonStyle())
        .help(dark ? t("切换到浅色模式", "Switch to light mode") : t("切换到深色模式", "Switch to dark mode"))
    }

    private var activeWindow: NSWindow? {
        NSApp.keyWindow ?? NSApp.mainWindow
    }

    private func windowControl(color: Color, symbol: String, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Circle()
                .fill(color)
                .frame(width: 14, height: 14)
                .overlay {
                    Image(systemName: symbol)
                        .font(.system(size: 7, weight: .bold))
                        .foregroundStyle(Color.black.opacity(0.58))
                }
                .frame(width: 22, height: 28)
                .contentShape(Rectangle())
        }
        .buttonStyle(DeskPlainButtonStyle())
        .accessibilityLabel(help)
        .help(help)
    }

    private var pageHeader: some View {
        HStack(alignment: .center) {
            VStack(alignment: .leading, spacing: 5) {
                Text(pageTitle)
                    .font(.system(size: 26, weight: .semibold))
                Text(pageSubtitle)
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button(settings.language == "en-US" ? "中文" : "English") {
                settings.setLanguage(settings.language == "en-US" ? "zh-CN" : "en-US")
            }
            .buttonStyle(WaccButtonStyle(surface: surface, line: line))
            if page == .audio {
                Button { audio.refresh() } label: {
                    Text(t("刷新", "Refresh"))
                }
                .buttonStyle(WaccPrimaryButtonStyle(accent: accent))
            }
        }
        .padding(.horizontal, 18)
        .padding(.top, 18)
        .padding(.bottom, 14)
        .background(surface)
    }

    private var pageTabs: some View {
        HStack(spacing: 8) {
            ForEach(Page.allCases) { item in
                Button(pageName(item)) { page = item }
                    .buttonStyle(WaccTabButtonStyle(selected: page == item, accent: accent, panel: panel, line: line))
            }
            Spacer()
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 10)
        .background(surface)
    }

    @ViewBuilder private var pageContent: some View {
        switch page {
        case .overview: overviewPage
        case .audio: audioPage
        case .displays: displaysPage
        case .screenshot: screenshotPage
        case .mouse: mousePage
        case .hotkeys: hotkeysPage
        case .general: generalPage
        case .translation:
            TranslationSettingsView()
        }
    }

    private var overviewPage: some View {
        let enabled = audio.isEnabled
        let output = enabled ? audio.currentOutput : nil
        let input = enabled ? audio.currentInput : nil
        let active = audio.profiles.first { $0.outputDeviceID == output?.id && $0.inputDeviceID == input?.id }
        return VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 12) {
                Text(t("系统摘要", "System summary")).fontWeight(.semibold)
                Text(!enabled ? t("音频未启用", "Audio inactive")
                     : audio.profiles.isEmpty ? t("尚未保存配置组", "No saved profiles")
                     : t("已保存 \(audio.profiles.count) 个配置组", "\(audio.profiles.count) saved profiles"))
                    .padding(.horizontal, 10).padding(.vertical, 3)
                    .foregroundStyle(Color.black)
                    .background(Color(red: 253/255, green: 244/255, blue: 165/255))
            }
            .padding(.vertical, 9)
            GeometryReader { geometry in
                let width = geometry.size.width - 16
                HStack(alignment: .top, spacing: 8) {
                    overviewTile(active == nil ? t("当前播放设备", "Current output") : t("当前音频配置", "Current audio profile")) {
                        HStack {
                            Text(!enabled ? t("音频未启用", "Audio inactive") : active?.name ?? output?.name ?? t("无播放设备", "No output device"))
                                .font(.system(size: 40)).lineLimit(2)
                            Spacer()
                            Image("OverviewSpeakers").resizable().scaledToFit().frame(width: 84)
                                .accessibilityHidden(true)
                        }
                        Text(t("播放设备　", "Output   ") + (output?.name ?? "—")).lineLimit(1)
                        Text(t("麦克风　", "Input   ") + (input?.name ?? "—")).lineLimit(1)
                    }
                    .frame(width: width * 0.48)
                    overviewTile(t("区域截图", "Region capture")) {
                        Text(t("拖选屏幕区域，可标注或录屏。", "Select a region to annotate or record.")).font(.caption).foregroundStyle(.secondary)
                        Image("CaptureContours").resizable().scaledToFit().frame(maxWidth: .infinity)
                            .frame(height: 118).accessibilityHidden(true)
                        Button { ScreenshotManager.shared.startCapture() } label: {
                            Text(t("开始框选截图", "Select region to capture")).frame(maxWidth: .infinity)
                        }
                        .buttonStyle(WaccPrimaryButtonStyle(accent: accent))
                    }
                    .frame(width: width * 0.32)
                    overviewTile(t("设备配置组", "Device profiles")) {
                        Text(t("一键切换播放设备和麦克风。", "Switch output and microphone together.")).font(.caption).foregroundStyle(.secondary)
                        if enabled {
                            ForEach(audio.profiles.prefix(3)) { profile in
                                let selected = active?.id == profile.id
                                let online = audio.outputs.contains { $0.id == profile.outputDeviceID }
                                    && audio.inputs.contains { $0.id == profile.inputDeviceID }
                                overviewButton((selected ? "✓  " : "") + profile.name + (online ? "" : t(" · 离线", " · Offline"))) {
                                    audio.activateProfile(id: profile.id)
                                }
                                .disabled(selected || !online)
                            }
                        }
                        overviewButton(enabled && audio.profiles.isEmpty ? t("设置配置组", "Set up profiles") : t("音频设置", "Audio settings")) { page = .audio }
                    }
                    .frame(width: width * 0.20)
                }
            }
            .frame(height: 266)
            GeometryReader { geometry in
                HStack(alignment: .top, spacing: 8) {
                    overviewTile(t("音量控制", "Volume levels")) {
                        if enabled {
                            overviewLevel(t("输出音量", "Output volume"), device: output)
                            Divider()
                            overviewLevel(t("麦克风音量", "Microphone level"), device: input)
                        } else {
                            overviewButton(t("音频设置", "Audio settings")) { page = .audio }
                        }
                    }
                    .frame(width: (geometry.size.width - 16) * 0.48)
                    overviewTile(t("常用操作", "Quick actions")) {
                        overviewButton(t("打开快速笔记", "Open quick notes")) { NotebookWindowController.shared.show(intent: "quick") }
                        overviewButton(t("打开笔记本", "Open notebook")) { NotebookWindowController.shared.show() }
                        overviewButton(t("打开录写与翻译", "Open transcription and translation")) { TranslationWindowController.shared.show() }
                    }
                }
            }
            .frame(height: 212)
        }
    }

    private func overviewTile<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).fontWeight(.semibold)
            content()
            Spacer(minLength: 0)
        }
        .padding(16)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(panel, in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(line))
    }

    private func overviewLevel(_ label: String, device: AudioDeviceInfo?) -> some View {
        OverviewVolumeLevel(label: label, value: Binding(
            get: { device?.volume ?? 0 },
            set: { if let device { audio.setVolume($0, for: device) } }
        ), available: device != nil, surface: panel)
    }

    private func overviewButton(_ title: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title).lineLimit(1).frame(maxWidth: .infinity, alignment: .leading)
        }
        .buttonStyle(WaccButtonStyle(surface: surface, line: line))
        .help(title)
    }

    private var audioPage: some View {
        VStack(alignment: .leading, spacing: 12) {
            if audio.isEnabled {
                card {
                    HStack(alignment: .top, spacing: 20) {
                        compactCurrentDevice(kind: .output, device: audio.currentOutput)
                        Divider()
                        compactCurrentDevice(kind: .input, device: audio.currentInput)
                    }
                }
                card {
                    HStack {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(t("音频配置组", "Audio profiles"))
                                .font(.system(size: 18, weight: .semibold))
                            Text(t("将一台输出设备和一台输入设备保存为一组。", "Save one output and one input device as a pair."))
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button { audio.cycleProfile(direction: -1) } label: {
                            Label(t("上一个", "Previous"), systemImage: "chevron.left")
                        }
                        .buttonStyle(WaccButtonStyle(surface: surface, line: line))
                        .disabled(audio.profiles.isEmpty)
                        Button { audio.cycleProfile(direction: 1) } label: {
                            Label(t("下一个", "Next"), systemImage: "chevron.right")
                        }
                        .buttonStyle(WaccButtonStyle(surface: surface, line: line))
                        .disabled(audio.profiles.isEmpty)
                        Button { audio.addCurrentProfile() } label: {
                            Label(t("新建组合", "New profile"), systemImage: "plus")
                        }
                        .buttonStyle(WaccPrimaryButtonStyle(accent: accent))
                        .disabled(audio.currentOutput == nil || audio.currentInput == nil)
                    }
                    if audio.profiles.isEmpty {
                        Text(t("还没有配置组。点击“新建组合”开始。", "No profiles yet. Create a profile to begin."))
                            .foregroundStyle(.secondary)
                            .padding(.vertical, 18)
                            .frame(maxWidth: .infinity)
                    } else {
                        VStack(spacing: 8) {
                            ForEach(audio.profiles) { profile in
                                audioProfileRow(profile)
                            }
                        }
                        .padding(.top, 8)
                    }
                }
                if let error = audio.lastError {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .font(.callout)
                        .foregroundStyle(.red)
                        .padding(12)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(Color.red.opacity(0.08), in: RoundedRectangle(cornerRadius: 9))
                }
            } else {
                card {
                    Label(t("音频功能已关闭", "Audio features are off"), systemImage: "speaker.slash.fill")
                        .font(.system(size: 16, weight: .semibold))
                    Text(t("不会访问 Core Audio，也不会运行设备枚举、音量记忆、锁定恢复或配置组快捷键。", "Core Audio is not accessed; discovery, volume memory, lock recovery, and profile hotkeys are stopped."))
                        .foregroundStyle(.secondary)
                }
            }
            card {
                settingToggle(
                    t("启用音频功能", "Enable audio features"),
                    t("关闭后停止设备枚举、音量监听、设备锁定与配置组快捷键", "Turn off device discovery, volume monitoring, device locks, and profile hotkeys"),
                    audio.isEnabled ? "speaker.wave.2.fill" : "speaker.slash.fill",
                    Binding(get: { audio.isEnabled }, set: audio.setEnabled)
                )
            }
        }
    }

    private func audioProfileRow(_ profile: AudioConfigurationProfile) -> some View {
        let outputOnline = audio.outputs.contains(where: { $0.id == profile.outputDeviceID })
        let inputOnline = audio.inputs.contains(where: { $0.id == profile.inputDeviceID })
        let active = audio.currentOutput?.id == profile.outputDeviceID
            && audio.currentInput?.id == profile.inputDeviceID
        return HStack(spacing: 12) {
            Circle()
                .fill(active ? Color(red: 0.259, green: 0.745, blue: 0.341) : Color.secondary.opacity(0.35))
                .frame(width: 8, height: 8)
            TextField(t("配置组名称", "Profile name"), text: Binding(
                get: { profile.name },
                set: { audio.updateProfile(id: profile.id, name: $0, outputDeviceID: profile.outputDeviceID, inputDeviceID: profile.inputDeviceID) }
            ))
            .textFieldStyle(.plain)
            .font(.system(size: 14, weight: .semibold))
            .frame(width: 150)
            Picker(t("输出", "Output"), selection: Binding(
                get: { profile.outputDeviceID },
                set: { audio.updateProfile(id: profile.id, name: profile.name, outputDeviceID: $0, inputDeviceID: profile.inputDeviceID) }
            )) {
                if !outputOnline { Text(t("输出设备未连接", "Output unavailable")).tag(profile.outputDeviceID) }
                ForEach(audio.outputs) { Text($0.name).tag($0.id) }
            }
            .frame(maxWidth: .infinity)
            Picker(t("输入", "Input"), selection: Binding(
                get: { profile.inputDeviceID },
                set: { audio.updateProfile(id: profile.id, name: profile.name, outputDeviceID: profile.outputDeviceID, inputDeviceID: $0) }
            )) {
                if !inputOnline { Text(t("输入设备未连接", "Input unavailable")).tag(profile.inputDeviceID) }
                ForEach(audio.inputs) { Text($0.name).tag($0.id) }
            }
            .frame(maxWidth: .infinity)
            Button(active ? t("使用中", "Active") : t("应用", "Apply")) { audio.activateProfile(id: profile.id) }
                .fontWeight(.semibold)
                .foregroundStyle(active ? Color.white : Color.primary)
                .frame(width: 68, height: 34)
                .background(active ? accent : surface, in: RoundedRectangle(cornerRadius: 8))
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(active ? accent : line))
                .buttonStyle(DeskPlainButtonStyle())
                .disabled(active || !outputOnline || !inputOnline)
            Button(role: .destructive) { audio.deleteProfile(id: profile.id) } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(WaccButtonStyle(surface: surface, line: line))
            .frame(width: 34, height: 34)
        }
        .padding(.horizontal, 12)
        .frame(minHeight: 54)
        .background(active ? accent.opacity(0.1) : panel, in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(active ? accent : line))
    }

    private func compactCurrentDevice(kind: AudioDeviceKind, device: AudioDeviceInfo?) -> some View {
        let output = kind == .output
        return VStack(alignment: .leading, spacing: 9) {
            HStack(spacing: 8) {
                Image(systemName: output ? "speaker.wave.2.fill" : "mic.fill")
                    .foregroundStyle(accent)
                Text(output ? t("当前输出", "Current output") : t("当前输入", "Current input"))
                    .font(.system(size: 14, weight: .semibold))
                Text(device?.name ?? t("无活动设备", "No active device"))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Spacer()
                Toggle(output ? t("锁定输出", "Lock output") : t("锁定输入", "Lock input"), isOn: Binding(
                    get: { output ? PersistentConfigurationStore.shared.configuration.lockOutputDevice : PersistentConfigurationStore.shared.configuration.lockInputDevice },
                    set: { audio.setLock(kind, enabled: $0) }
                ))
                .toggleStyle(.switch)
                .controlSize(.small)
            }
            HStack(spacing: 10) {
                if let device {
                    Slider(value: Binding(
                        get: { (output ? audio.currentOutput : audio.currentInput)?.volume ?? 0 },
                        set: { audio.setVolume($0, for: device) }
                    ), in: 0...1)
                    Text("\(Int((device.volume * 100).rounded()))%")
                        .monospacedDigit()
                        .frame(width: 42, alignment: .trailing)
                } else {
                    Slider(value: .constant(0), in: 0...1).disabled(true)
                    Text("—").frame(width: 42)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var screenshotPage: some View {
        VStack(spacing: 16) {
            HStack {
                Picker(t("设置项目", "Settings"), selection: $recordingSettings) {
                    Text(t("截图标注", "Annotations")).tag(false)
                    Text(t("录屏文件", "Recording files")).tag(true)
                }.pickerStyle(.segmented).labelsHidden().frame(width: 280)
                Spacer()
                Button(t("开始框选截图", "Select region to capture")) { ScreenshotManager.shared.startCapture() }
                    .buttonStyle(WaccPrimaryButtonStyle(accent: accent))
            }
            if recordingSettings { regionRecordingCard } else { annotationSettings }
        }
    }

    private var annotationSettings: some View {
        card {
            HStack(alignment: .center, spacing: 34) {
                VStack(alignment: .leading, spacing: 13) {
                    Text(t("默认标注配置", "Default annotation settings"))
                        .font(.system(size: 16, weight: .semibold))
                    Text(t("截图开始时自动使用这些值；文字粗细只在这里设置。", "New captures start with these values; text weight is configured only here."))
                        .foregroundStyle(.secondary)

                    HStack(spacing: 10) {
                        Text(t("默认颜色", "Default color")).frame(width: 110, alignment: .leading)
                        ForEach(screenshotColorChoices) { choice in
                            Button { settings.setScreenshotAnnotationColor(choice.hex) } label: {
                                Circle()
                                    .fill(choice.color)
                                    .overlay(Circle().stroke(line, lineWidth: 1))
                                    .frame(width: 20, height: 20)
                                    .padding(5)
                                    .background(surface, in: RoundedRectangle(cornerRadius: 8))
                                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(settings.screenshotAnnotationColor == choice.hex ? accent : line, lineWidth: settings.screenshotAnnotationColor == choice.hex ? 3 : 1))
                            }
                            .buttonStyle(DeskPlainButtonStyle())
                        }
                    }
                    screenshotDefaultPicker(t("线条粗细", "Stroke width"), selection: Binding(get: { settings.screenshotStrokeWidth }, set: settings.setScreenshotStrokeWidth), values: [1, 2, 3, 5, 8, 12])
                    screenshotDefaultPicker(t("文字大小", "Text size"), selection: Binding(get: { settings.screenshotFontSize }, set: settings.setScreenshotFontSize), values: [14, 18, 24, 32, 44, 60])
                    HStack {
                        Text(t("文字粗细", "Text weight")).frame(width: 110, alignment: .leading)
                        Picker("", selection: Binding(get: { settings.screenshotFontWeight }, set: settings.setScreenshotFontWeight)) {
                            Text(t("常规", "Regular")).tag("Regular")
                            Text(t("中等", "Medium")).tag("Medium")
                            Text(t("半粗", "Semibold")).tag("SemiBold")
                            Text(t("粗体", "Bold")).tag("Bold")
                        }
                        .frame(width: 190)
                    }
                }
                Spacer()
                screenshotDefaultsPreview
                    .frame(width: 310)
            }
        }
    }

    private var regionRecordingCard: some View {
        card {
            Text(t("区域录屏", "Region recording")).font(.system(size: 16, weight: .semibold))
            HStack {
                Text(t("保存目录", "Save folder")).frame(width: 110, alignment: .leading)
                Button {
                    let picker = NSOpenPanel()
                    picker.canChooseFiles = false
                    picker.canChooseDirectories = true
                    picker.canCreateDirectories = true
                    picker.directoryURL = settings.regionRecording.outputDirectory
                    guard picker.runModal() == .OK, let url = picker.url else { return }
                    settings.setRegionRecording(\.directory, url.path)
                } label: {
                    Text(settings.regionRecording.outputDirectory.path)
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .frame(maxWidth: 340)
                }
                if !settings.regionRecording.directory.isEmpty {
                    Button(t("恢复下载目录", "Use Downloads")) { settings.setRegionRecording(\.directory, "") }
                }
            }
            recordingPicker(t("内存缓存", "Memory buffer"), \.cacheMiB,
                            RegionRecordingSettings.cacheOptions) { "\($0) MiB" }
            recordingPicker(t("最高分辨率", "Max resolution"), \.maxHeight,
                            RegionRecordingSettings.heightOptions) { "\($0)p" }
            recordingPicker(t("帧率", "Frame rate"), \.frameRate,
                            RegionRecordingSettings.frameRateOptions) { "\($0) fps" }
            recordingPicker(t("码率", "Bitrate"), \.bitrateMbps, RegionRecordingSettings.bitrateOptions) {
                $0 == 0 ? t("自动 · 清晰优先", "Automatic · High quality") : "\($0) Mbps"
            }
        }
    }

    private func recordingPicker(_ title: String, _ keyPath: WritableKeyPath<RegionRecordingSettings, Int>,
                                 _ values: [Int], label: @escaping (Int) -> String) -> some View {
        HStack {
            Text(title).frame(width: 110, alignment: .leading)
            Picker("", selection: Binding(get: { settings.regionRecording[keyPath: keyPath] },
                                          set: { settings.setRegionRecording(keyPath, $0) })) {
                ForEach(values, id: \.self) { value in Text(label(value)).tag(value) }
            }
            .frame(width: 190)
        }
    }

    private var screenshotColorChoices: [ScreenshotColorChoice] {
        [
            ScreenshotColorChoice(hex: "#FFFF0000", color: Color(red: 1, green: 0, blue: 0)),
            ScreenshotColorChoice(hex: "#FFFFF200", color: Color(red: 1, green: 242 / 255, blue: 0)),
            ScreenshotColorChoice(hex: "#FF38BDF8", color: Color(red: 56 / 255, green: 189 / 255, blue: 248 / 255)),
            ScreenshotColorChoice(hex: "#FF808080", color: Color(red: 128 / 255, green: 128 / 255, blue: 128 / 255)),
            ScreenshotColorChoice(hex: "#FF708238", color: Color(red: 112 / 255, green: 130 / 255, blue: 56 / 255)),
            ScreenshotColorChoice(hex: "#FF000000", color: .black),
            ScreenshotColorChoice(hex: "#FFFFFFFF", color: .white),
            ScreenshotColorChoice(hex: "#FF8A2BE2", color: Color(red: 138 / 255, green: 43 / 255, blue: 226 / 255)),
            ScreenshotColorChoice(hex: "#FFFF8C00", color: Color(red: 1, green: 140 / 255, blue: 0))
        ]
    }

    private func screenshotDefaultPicker(_ title: String, selection: Binding<Double>, values: [Double]) -> some View {
        HStack {
            Text(title).frame(width: 110, alignment: .leading)
            Picker("", selection: selection) {
                ForEach(values, id: \.self) { value in Text("\(Int(value))").tag(value) }
            }
            .frame(width: 190)
        }
    }

    private var screenshotDefaultsPreview: some View {
        let color = screenshotColorChoices.first(where: { $0.hex == settings.screenshotAnnotationColor })?.color ?? .red
        let weight: Font.Weight = switch settings.screenshotFontWeight {
        case "Regular": .regular
        case "SemiBold": .semibold
        case "Bold": .bold
        default: .medium
        }
        return VStack(alignment: .leading, spacing: 16) {
            Text(t("预览", "Preview")).font(.system(size: 12)).foregroundStyle(.secondary)
            Capsule()
                .fill(color)
                .frame(height: CGFloat(min(12, settings.screenshotStrokeWidth)))
            Text("Aa  标注文字 / Annotation")
                .font(.system(size: CGFloat(min(32, settings.screenshotFontSize)), weight: weight))
                .foregroundStyle(color)
                .lineLimit(2)
        }
        .padding(18)
        .frame(maxWidth: .infinity, minHeight: 155, alignment: .leading)
        .background(surface, in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(line))
    }

    private var displaysPage: some View {
        VStack(spacing: 16) {
            if displays.displays.isEmpty {
                card { Label(t("未检测到显示器", "No displays found"), systemImage: "display.trianglebadge.exclamationmark").foregroundStyle(.secondary) }
            }
            ForEach(displays.displays) { display in
                card {
                    HStack {
                        Label(display.name, systemImage: "display").font(.headline)
                        Spacer()
                        Button(t("刷新", "Refresh")) { displays.refresh() }
                            .buttonStyle(WaccButtonStyle(surface: surface, line: line))
                    }
                    Picker(t("分辨率", "Resolution"), selection: modeSelection(for: display)) {
                        ForEach(display.modes) { mode in
                            Text(mode.label(relativeTo: display.nativeResolution)).tag(Optional(mode.id))
                        }
                    }
                    if let native = display.nativeResolution {
                        Text(t("原生分辨率：\(native.label)", "Native resolution: \(native.label)"))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Divider()
                    DisclosureGroup(t("分辨率快捷键", "Resolution hotkeys")) {
                        ForEach(display.modes) { mode in
                            shortcutRow(title: mode.label(relativeTo: display.nativeResolution), subtitle: t("直接切换此模式", "Switch directly to this mode"), shortcut: displayShortcutBinding(display: display, mode: mode))
                        }
                    }
                }
            }
        }
    }

    private var mousePage: some View {
        HStack(alignment: .top, spacing: 10) {
            card {
                Label(t("鼠标滚轮方向", "Mouse wheel direction"), systemImage: "computermouse").font(.headline)
                Picker("", selection: Binding(
                    get: { mouseScroll.behavior },
                    set: { mouseScroll.setBehavior($0) }
                )) {
                    ForEach(MouseScrollBehavior.allCases) { behavior in Text(behavior.title).tag(behavior) }
                }
                .pickerStyle(.segmented)
                Text(t("只修改外接鼠标滚轮；触控板继续使用 macOS 原始设置。", "Only changes external mouse scrolling; the trackpad keeps the macOS setting."))
                    .font(.caption).foregroundStyle(.secondary)
                Divider()
                shortcutRow(title: t("切换正常/反向滚动", "Toggle normal/reversed scrolling"), subtitle: t("用快捷键切换滚动行为。", "Toggle scrolling behavior with a shortcut."), shortcut: Binding(get: { settings.mouseToggleShortcut }, set: settings.setMouseToggleShortcut))
            }
            card {
                HStack {
                    Label(mouseScroll.hasAccessibilityPermission ? t("辅助功能权限已开启", "Accessibility permission granted") : t("需要辅助功能权限", "Accessibility permission required"), systemImage: mouseScroll.hasAccessibilityPermission ? "checkmark.shield.fill" : "exclamationmark.triangle.fill")
                        .foregroundStyle(mouseScroll.hasAccessibilityPermission ? Color.green : Color.orange)
                    Spacer()
                    Button(t("重新检查", "Check again")) { mouseScroll.refreshPermissionStatus() }
                        .buttonStyle(WaccButtonStyle(surface: surface, line: line))
                    if !mouseScroll.hasAccessibilityPermission {
                        Button(t("前往系统设置…", "Open System Settings…")) { mouseScroll.openAccessibilitySettings() }
                    }
                }
                if let error = mouseScroll.startupError { Text(error).font(.caption).foregroundStyle(.secondary) }
            }
        }
    }

    private var hotkeysPage: some View {
        VStack(alignment: .leading, spacing: 10) {
            card {
                Text(t("打开工具", "Open a tool")).font(.headline)
                shortcutRow(title: t("开始截图", "Start capture"), subtitle: t("跨显示器截图与标注", "Cross-display capture and annotation"), shortcut: Binding(get: { settings.screenshotShortcut }, set: settings.setScreenshotShortcut))
                Divider()
                shortcutRow(title: t("打开上次笔记页面", "Open last notebook page"), subtitle: t("恢复上次打开的笔记", "Restore the last opened note"), shortcut: Binding(get: { settings.notebookLastShortcut }, set: settings.setNotebookLastShortcut))
                Divider()
                shortcutRow(title: t("打开录写/翻译", "Open Transcribe / Translate"), subtitle: t("显示录写/翻译窗口", "Show the transcription and translation window"), shortcut: Binding(get: { settings.translationShortcut }, set: settings.setTranslationShortcut))
                Divider()
                shortcutRow(title: t("打开快速笔记", "Open Quick Notes"), subtitle: t("在固定笔记本中随手记录", "Write in the fixed Quick Notes notebook"), shortcut: Binding(get: { settings.notebookQuickShortcut }, set: settings.setNotebookQuickShortcut))
            }
            card {
                Text(t("切换设备", "Switch devices")).font(.headline)
                if audio.isEnabled {
                    shortcutRow(title: t("下一个音频配置组", "Next audio profile"), subtitle: t("整体切换输出和输入设备", "Switch the output and input pair together"), shortcut: Binding(get: { settings.nextAudioProfileShortcut }, set: settings.setNextAudioProfileShortcut))
                    Divider()
                    shortcutRow(title: t("上一个音频配置组", "Previous audio profile"), subtitle: t("按相反方向切换配置组", "Cycle through profiles in reverse"), shortcut: Binding(get: { settings.previousAudioProfileShortcut }, set: settings.setPreviousAudioProfileShortcut))
                } else {
                    Divider()
                    Label(t("音频功能关闭时不注册配置组快捷键", "Profile hotkeys are not registered while audio is off"), systemImage: "speaker.slash.fill")
                        .foregroundStyle(.secondary)
                        .padding(.vertical, 7)
                }
            }
            if let error = settings.shortcutError {
                Label(error, systemImage: "exclamationmark.triangle.fill").font(.callout).foregroundStyle(.red)
            }
        }
    }

    private var generalPage: some View {
        HStack(alignment: .top, spacing: 10) {
            card {
                Text(t("后台运行", "Background operation")).font(.headline)
                settingToggle(t("启用音频功能", "Enable audio features"), t("关闭后完全停止音频设备访问与相关快捷键", "Completely stop audio device access and related hotkeys"), audio.isEnabled ? "speaker.wave.2.fill" : "speaker.slash.fill", Binding(get: { audio.isEnabled }, set: audio.setEnabled))
                Divider()
                settingToggle(t("登录时启动 DeskMosaic", "Launch DeskMosaic at sign-in"), t("登录后自动在菜单栏运行", "Run automatically in the menu bar after sign-in"), "power", Binding(get: { settings.launchesAtLogin }, set: settings.setLaunchAtLogin))
                Divider()
                settingToggle(t("启动时保持后台运行", "Start silently in the menu bar"), t("不自动打开设置窗口", "Do not open the settings window automatically"), "menubar.rectangle", Binding(get: { settings.silentStart }, set: settings.setSilentStart))
                if let error = settings.loginItemError { Text(error).font(.callout).foregroundStyle(.red) }
            }
            card {
                Text(t("界面外观", "Appearance")).font(.headline)
                HStack {
                    Label(t("主题", "Theme"), systemImage: "circle.lefthalf.filled")
                    Spacer()
                    Picker("", selection: Binding(get: { settings.theme }, set: settings.setTheme)) {
                        Text(t("浅色", "Light")).tag("Light")
                        Text(t("深色", "Dark")).tag("Dark")
                    }.frame(width: 150)
                }
                Divider()
                HStack {
                    Label(t("语言", "Language"), systemImage: "globe")
                    Spacer()
                    Picker("", selection: Binding(get: { settings.language }, set: settings.setLanguage)) {
                        Text("中文").tag("zh-CN")
                        Text("English").tag("en-US")
                    }.frame(width: 150)
                }
            }
        }
    }

    private func settingToggle(_ title: String, _ subtitle: String, _ symbol: String, _ value: Binding<Bool>) -> some View {
        HStack(spacing: 11) {
            Image(systemName: symbol).frame(width: 18)
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                Text(subtitle).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Toggle("", isOn: value).labelsHidden().toggleStyle(.switch).accessibilityLabel(title)
        }
        .padding(.vertical, 4)
    }

    private func settingStatus(_ title: String, _ subtitle: String, _ symbol: String, _ status: String) -> some View {
        HStack(spacing: 11) {
            Image(systemName: symbol).frame(width: 18)
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                Text(subtitle).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Text(status).font(.caption.weight(.semibold)).foregroundStyle(accent)
        }
        .padding(.vertical, 4)
    }

    private func shortcutRow(title: String, subtitle: String, shortcut: Binding<KeyboardShortcut?>) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                Text(title)
                Text(subtitle).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            HStack(spacing: 4) {
                ShortcutRecorder(shortcut: shortcut).frame(width: 145, height: 28)
                Button { shortcut.wrappedValue = nil } label: {
                    Image(systemName: "xmark")
                        .font(.system(size: 10, weight: .medium))
                        .foregroundStyle(.secondary)
                        .frame(width: 28, height: 28)
                        .contentShape(RoundedRectangle(cornerRadius: 8))
                }
                .buttonStyle(.borderless)
                .disabled(shortcut.wrappedValue == nil)
                .accessibilityLabel(t("清除快捷键", "Clear shortcut"))
                .help(t("清除快捷键", "Clear shortcut"))
            }
        }
        .padding(.vertical, 7)
    }

    private func card<Content: View>(@ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 12, content: content)
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .topLeading)
            .background(panel, in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(line))
    }

    private func modeSelection(for display: ConnectedDisplay) -> Binding<Int32?> {
        Binding(
            get: { displays.displays.first(where: { $0.id == display.id })?.currentModeID },
            set: { modeID in
                guard let modeID,
                      let current = displays.displays.first(where: { $0.id == display.id }),
                      let mode = current.modes.first(where: { $0.id == modeID }) else { return }
                displays.setMode(mode, for: current)
            }
        )
    }

    private func displayShortcutBinding(display: ConnectedDisplay, mode: DisplayMode) -> Binding<KeyboardShortcut?> {
        let actionID = display.shortcutIdentifier(for: mode)
        return Binding(get: { settings.displayShortcuts[actionID] }, set: { settings.setDisplayShortcut($0, actionID: actionID) })
    }

    private func pageName(_ value: Page) -> String {
        switch value {
        case .overview: return t("总览", "Overview")
        case .audio: return t("音频", "Audio")
        case .displays: return t("显示器", "Displays")
        case .screenshot: return t("截图", "Screenshot")
        case .mouse: return t("鼠标", "Mouse")
        case .hotkeys: return t("快捷键", "Hotkeys")
        case .general: return t("通用", "General")
        case .translation: return t("录写/翻译", "Transcribe / Translate")
        }
    }

    private var pageTitle: String { page == .audio ? t("音频控制中心", "Audio Control Center") : pageName(page) }
    private var pageSubtitle: String {
        switch page {
        case .overview: return ""
        case .audio: return t("用配置组整体切换输入与输出，并锁定当前设备组合。", "Switch input and output together with profiles and lock the active pair.")
        case .displays: return t("调整分辨率，并为常用显示模式设置快捷键。", "Adjust resolutions and assign hotkeys to display modes.")
        case .screenshot: return t("跨显示器捕捉、标注、保存并复制到剪贴板。", "Capture across displays, annotate, save, and copy.")
        case .mouse: return t("单独控制外接鼠标滚轮，不改变触控板。", "Control external mouse scrolling without changing the trackpad.")
        case .hotkeys: return t("为常用操作录制不会冲突的全局组合键。", "Record conflict-free global shortcuts for common actions.")
        case .general: return t("调整外观、语言与启动行为。", "Adjust appearance, language, and startup behavior.")
        case .translation: return t("设置本地模型、音源与推理设备。", "Choose local models, audio sources, and compute.")
        }
    }

    private func t(_ chinese: String, _ english: String) -> String { settings.language == "en-US" ? english : chinese }
}

private struct WaccButtonStyle: ButtonStyle {
    let surface: Color
    let line: Color

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .fontWeight(.semibold)
            .padding(.horizontal, 14)
            .padding(.vertical, 7)
            .background(surface.opacity(configuration.isPressed ? 0.7 : 1), in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(line))
            .modifier(DeskPressFeedback(pressed: configuration.isPressed))
    }
}

private struct WaccPrimaryButtonStyle: ButtonStyle {
    let accent: Color

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .fontWeight(.semibold)
            .foregroundStyle(.white)
            .padding(.horizontal, 14)
            .padding(.vertical, 7)
            .background(accent.opacity(configuration.isPressed ? 0.78 : 1), in: RoundedRectangle(cornerRadius: 8))
            .modifier(DeskPressFeedback(pressed: configuration.isPressed))
    }
}

private struct WaccTabButtonStyle: ButtonStyle {
    let selected: Bool
    let accent: Color
    let panel: Color
    let line: Color

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .fontWeight(.semibold)
            .foregroundStyle(selected ? Color.white : Color.primary)
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background(selected ? accent : Color.clear, in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(selected ? accent : Color.clear))
            .modifier(DeskPressFeedback(pressed: configuration.isPressed))
    }
}

private struct OverviewVolumeLevel: View {
    let label: String
    @Binding var value: Double
    let available: Bool
    let surface: Color
    @State private var hovering = false
    @State private var editing = false
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .lastTextBaseline) {
                Text(available ? "\(Int((value * 100).rounded()))%" : "—")
                    .font(.system(size: 28)).monospacedDigit()
                Text(label)
            }
            Slider(value: $value, in: 0...1, onEditingChanged: { editing = $0 })
                .focused($focused).disabled(!available).accessibilityLabel(label)
                .frame(height: 24)
                .overlay {
                    if !hovering && !focused && !editing {
                        HStack(spacing: 2) {
                            ForEach(0..<40, id: \.self) { index in
                                Rectangle().fill(index < Int((value * 40).rounded())
                                    ? Color(red: 0.62, green: 0.67, blue: 0.77) : Color.secondary.opacity(0.2))
                            }
                        }
                        .frame(height: 14).frame(maxHeight: .infinity)
                        .background(surface).allowsHitTesting(false).accessibilityHidden(true)
                    }
                }
                .onHover { hovering = $0 }
        }
    }
}

struct DeskPressFeedback: ViewModifier {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let pressed: Bool

    func body(content: Content) -> some View {
        content
            .opacity(pressed ? 0.78 : 1)
            .scaleEffect(pressed && !reduceMotion ? 0.98 : 1)
            .animation(reduceMotion ? nil : .easeOut(duration: 0.12), value: pressed)
    }
}

struct DeskPlainButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.modifier(DeskPressFeedback(pressed: configuration.isPressed))
    }
}

private struct WindowDragRegion: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView {
        DragView()
    }

    func updateNSView(_ nsView: NSView, context: Context) { }

    private final class DragView: NSView {
        override func mouseDown(with event: NSEvent) {
            guard let window else { return }
            if event.clickCount == 2 {
                window.zoom(nil)
            } else {
                window.performDrag(with: event)
            }
        }
    }
}

@MainActor
final class SettingsWindowController: NSObject, NSWindowDelegate, ObservableObject {
    static let shared = SettingsWindowController()
    private var settingsWindow: NSWindow?

    @Published var page: SettingsView.Page = .overview

    func show(page: SettingsView.Page? = nil) {
        if let page { self.page = page }
        let window = settingsWindow ?? makeWindow()
        settingsWindow = window
        if window.isMiniaturized { window.deminiaturize(nil) }
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    private func makeWindow() -> NSWindow {
        let content = SettingsWindowRoot(controller: self)
            .environmentObject(DisplayManager.shared)
            .environmentObject(MouseScrollManager.shared)
            .environmentObject(SettingsManager.shared)
            .environmentObject(AudioManager.shared)
            .frame(minWidth: 980, minHeight: 660)
            .ignoresSafeArea(.container, edges: .top)

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1160, height: 760),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "DeskMosaic"
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.styleMask.insert(.fullSizeContentView)
        window.contentView = NSHostingView(rootView: content)
        window.standardWindowButton(.closeButton)?.isHidden = true
        window.standardWindowButton(.miniaturizeButton)?.isHidden = true
        window.standardWindowButton(.zoomButton)?.isHidden = true
        window.titlebarSeparatorStyle = .none
        window.isMovableByWindowBackground = false
        window.isReleasedWhenClosed = false
        window.center()
        window.delegate = self
        return window
    }
}


private struct SettingsWindowRoot: View {
    @ObservedObject var controller: SettingsWindowController

    var body: some View { SettingsView(page: $controller.page) }
}
