import SwiftUI

@main
struct MACAssistantApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var displays = DisplayManager.shared
    @StateObject private var mouseScroll = MouseScrollManager.shared
    @StateObject private var settings = SettingsManager.shared
    @StateObject private var audio = AudioManager.shared

    var body: some Scene {
        MenuBarExtra {
            MenuContentView()
                .environmentObject(displays)
                .environmentObject(mouseScroll)
                .environmentObject(settings)
                .environmentObject(audio)
        } label: {
            HStack(spacing: 3) {
                Image("MenuBarIcon")
                    .renderingMode(.template)
                if audio.isEnabled, let output = audio.currentOutput {
                    Text(output.isMuted ? "×" : "\(Int((output.volume * 100).rounded()))")
                        .monospacedDigit()
                }
            }
            .accessibilityLabel("DeskMosaic")
        }
        .menuBarExtraStyle(.window)
    }
}
