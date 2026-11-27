import AppKit
import SwiftUI

struct MenuContentView: View {
    @Environment(\.dismiss) private var dismiss
    @EnvironmentObject private var appDelegate: AppDelegate
    @EnvironmentObject private var displays: DisplayManager
    @EnvironmentObject private var mouseScroll: MouseScrollManager
    @EnvironmentObject private var settings: SettingsManager
    @EnvironmentObject private var audio: AudioManager

    private var dark: Bool { settings.theme == "Dark" }
    private var surface: Color { dark ? Color(white: 0.11) : Color(red: 240/255, green: 241/255, blue: 246/255) }
    private var panel: Color { dark ? Color(red: 0.15, green: 0.16, blue: 0.20) : .white }
    private var line: Color { dark ? Color(white: 0.28) : Color(red: 0.85, green: 0.86, blue: 0.89) }
    private let accent = Color(red: 0.30, green: 0.35, blue: 0.45)

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Image("AppEmblem").resizable().frame(width: 22, height: 22)
                Text("DeskMosaic").font(.headline)
                Spacer()
                Button(t("控制中心", "Control center")) {
                    performAfterDismiss { SettingsWindowController.shared.show() }
                }.buttonStyle(DeskPlainButtonStyle())
            }
            Divider()
            HStack(spacing: 6) {
                footerButton(t("框选截图", "Capture area")) { ScreenshotManager.shared.startCapture() }
                footerButton(t("笔记本", "Notebook")) { NotebookWindowController.shared.show() }
                footerButton(t("录写/翻译", "Speech")) { TranslationWindowController.shared.show() }
            }
            if audio.isEnabled {
                currentDevices
                ScrollView {
                    audioProfiles
                }
                .frame(height: audioProfilesHeight)
            } else {
                audioDisabledNotice
            }
            macUtilities
            footer
        }
        .padding(10)
        .frame(width: 330)
        .background(surface)
        .font(.system(size: 13))
        .preferredColorScheme(dark ? .dark : .light)
        .onAppear {
            audio.refresh()
            displays.refresh()
            mouseScroll.refreshPermissionStatus()
        }
    }

    private var audioDisabledNotice: some View {
        HStack(spacing: 10) {
            Image(systemName: "speaker.slash.fill")
                .foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(t("音频功能已关闭", "Audio is off"))
                    .font(.system(size: 13, weight: .semibold))
                Text(t("不会枚举设备或注册音频快捷键", "No device discovery or audio hotkeys"))
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            Spacer()
        }
        .padding(12)
        .background(panel, in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(line))
    }

    private var currentDevices: some View {
        HStack(alignment: .top, spacing: 0) {
            currentColumn(kind: .output, device: audio.currentOutput)
            Divider().padding(.horizontal, 8)
            currentColumn(kind: .input, device: audio.currentInput)
        }
        .padding(10)
        .background(panel, in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(line))
    }

    private var audioProfilesHeight: CGFloat {
        audio.profiles.isEmpty ? 52 : min(278, 34 + CGFloat(min(audio.profiles.count, 4)) * 62)
    }

    private func currentColumn(kind: AudioDeviceKind, device: AudioDeviceInfo?) -> some View {
        let output = kind == .output
        return VStack(alignment: .leading, spacing: 4) {
            Text(output ? t("输出", "Output") : t("输入", "Input"))
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
            Text(device?.name ?? t("无活动设备", "No active device"))
                .font(.system(size: 13, weight: .semibold))
                .lineLimit(2)
                .frame(height: 36, alignment: .topLeading)
            if let device {
                Slider(value: Binding(
                    get: { (output ? audio.currentOutput : audio.currentInput)?.volume ?? 0 },
                    set: { audio.setVolume($0, for: device) }
                ), in: 0...1)
            } else {
                Slider(value: .constant(0), in: 0...1).disabled(true)
            }
        }
        .frame(width: 132)
    }

    private var audioProfiles: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(t("音频配置组", "Audio profiles")).font(.system(size: 14, weight: .semibold))
                Spacer()
                Button { audio.cycleProfile(direction: -1) } label: { Image(systemName: "chevron.left") }
                    .buttonStyle(DeskPlainButtonStyle())
                Button { audio.cycleProfile(direction: 1) } label: { Image(systemName: "chevron.right") }
                    .buttonStyle(DeskPlainButtonStyle())
            }
            if audio.profiles.isEmpty {
                Text(t("请在设置中保存配置组", "Save a profile in Settings"))
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .padding(.vertical, 5)
            } else {
                ForEach(audio.profiles.prefix(4)) { profile in
                    profileRow(profile)
                }
            }
        }
    }

    private func profileRow(_ profile: AudioConfigurationProfile) -> some View {
        let active = audio.currentOutput?.id == profile.outputDeviceID
            && audio.currentInput?.id == profile.inputDeviceID
        let outputName = audio.deviceName(id: profile.outputDeviceID, kind: .output)
        let inputName = audio.deviceName(id: profile.inputDeviceID, kind: .input)
        return Button { audio.activateProfile(id: profile.id) } label: {
            HStack(spacing: 8) {
                Image(systemName: "slider.horizontal.2.square")
                    .font(.system(size: 16, weight: .semibold))
                    .foregroundStyle(accent)
                    .frame(width: 34)
                VStack(alignment: .leading, spacing: 2) {
                    Text(profile.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? t("未命名配置", "Unnamed profile") : profile.name)
                        .font(.system(size: 13, weight: .semibold)).lineLimit(1)
                    Text("\(outputName ?? t("输出未连接", "Output offline")) · \(inputName ?? t("输入未连接", "Input offline"))")
                        .font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer()
                Circle()
                    .stroke(active ? accent : Color.secondary, lineWidth: 2)
                    .background(Circle().fill(active ? accent : Color.clear))
                    .frame(width: 18, height: 18)
            }
            .padding(.horizontal, 10)
            .frame(minHeight: 54)
            .background(active ? accent.opacity(0.13) : surface, in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(active ? accent : line))
        }
        .buttonStyle(DeskPlainButtonStyle())
        .disabled(outputName == nil || inputName == nil)
    }

    private var macUtilities: some View {
        HStack(spacing: 8) {
            Menu {
                if displays.displays.isEmpty { Text(t("未检测到显示器", "No displays found")) }
                ForEach(displays.displays) { display in
                    Menu(display.name) {
                        ForEach(display.modes) { mode in
                            Button {
                                displays.setMode(mode, for: display)
                            } label: {
                                if display.currentModeID == mode.id { Label(mode.label(relativeTo: display.nativeResolution), systemImage: "checkmark") }
                                else { Text(mode.label(relativeTo: display.nativeResolution)) }
                            }
                        }
                    }
                }
            } label: {
                Label(t("显示器", "Displays"), systemImage: "display").frame(maxWidth: .infinity)
            }
            .menuStyle(.borderlessButton)
            .padding(8)
            .background(panel, in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(line))

            Menu {
                ForEach(MouseScrollBehavior.allCases) { behavior in
                    Button {
                        if !mouseScroll.setBehavior(behavior) {
                            performAfterDismiss { SettingsWindowController.shared.show(page: .mouse) }
                        }
                    } label: {
                        if mouseScroll.behavior == behavior { Label(behavior.title, systemImage: "checkmark") }
                        else { Text(behavior.title) }
                    }
                }
                if !mouseScroll.hasAccessibilityPermission {
                    Divider()
                    Button(t("前往辅助功能权限…", "Open accessibility settings…")) {
                        performAfterDismiss { mouseScroll.openAccessibilitySettings() }
                    }
                }
            } label: {
                Label(t("鼠标", "Mouse"), systemImage: "computermouse").frame(maxWidth: .infinity)
            }
            .menuStyle(.borderlessButton)
            .padding(8)
            .background(panel, in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(line))
        }
    }

    private var footer: some View {
        HStack {
            Spacer()
            Button(appDelegate.isPreparingToQuit ? t("正在退出…", "Quitting…") : t("退出 DeskMosaic", "Quit DeskMosaic")) {
                performAfterDismiss { NSApp.terminate(nil) }
            }
            .buttonStyle(DeskPlainButtonStyle())
            .font(.caption).foregroundStyle(.secondary)
            .disabled(appDelegate.isPreparingToQuit)
        }
    }

    private func footerButton(_ title: String, action: @escaping @MainActor () -> Void) -> some View {
        Button { performAfterDismiss(action) } label: {
            Text(title)
                .fontWeight(.semibold)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 5)
                .background(surface, in: RoundedRectangle(cornerRadius: 7))
                .overlay(RoundedRectangle(cornerRadius: 7).stroke(line))
                .contentShape(Rectangle())
        }
        .buttonStyle(DeskPlainButtonStyle())
    }

    private func performAfterDismiss(_ action: @escaping @MainActor () -> Void) {
        dismiss()
        DispatchQueue.main.async { action() }
    }

    private func t(_ chinese: String, _ english: String) -> String { settings.language == "en-US" ? english : chinese }
}
