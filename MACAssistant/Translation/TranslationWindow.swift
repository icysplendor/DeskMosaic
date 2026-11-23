import AppKit
import SwiftUI
import UniformTypeIdentifiers

@MainActor
final class TranslationWindowController: NSObject, ObservableObject, NSWindowDelegate {
    static let shared = TranslationWindowController()

    @Published private var speakerNames: [Int: String] = [:]
    @Published private(set) var captions: [TranslationCaption] = []
    @Published private(set) var running = false
    @Published private(set) var listening = false
    @Published private(set) var recording = false
    @Published private(set) var sourceOnlySession = false
    @Published private(set) var identifiesSpeakers = false
    @Published private(set) var savedRecording: URL?
    @Published private(set) var status = ""
    @Published private(set) var failed = false
    @Published var sourceLanguage = "Chinese"
    @Published var targetLanguage = "English"
    private var window: NSWindow?
    private var job: Task<Void, Never>?
    private var engine: TranslationEngine?

    func show() {
        if window?.isVisible != true {
            let defaults = SettingsManager.shared.translationOptions
            sourceLanguage = defaults.selectedSourceLanguage
            targetLanguage = defaults.selectedTargetLanguage
        }
        if window == nil {
            let newWindow = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 820, height: 490),
                                     styleMask: [.titled, .closable, .miniaturizable, .resizable],
                                     backing: .buffered, defer: false)
            newWindow.minSize = NSSize(width: 700, height: 310)
            newWindow.level = .floating
            newWindow.isReleasedWhenClosed = false
            newWindow.delegate = self
            newWindow.contentView = NSHostingView(rootView: TranslationWindowView(controller: self)
                .environmentObject(SettingsManager.shared))
            newWindow.center()
            window = newWindow
        }
        window?.title = t("录写/翻译", "Transcribe / Translate")
        if window?.isMiniaturized == true { window?.deminiaturize(nil) }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    func stop() {
        if recording, job?.isCancelled == false { status = t("正在保存录音…", "Saving recording…") }
        job?.cancel()
        engine?.stop()
    }

    func stopAndWait() async {
        let current = job
        stop()
        await current?.value
    }

    func windowWillClose(_ notification: Notification) { stop() }

    func openSettings() {
        window?.orderOut(nil)
        stop()
        SettingsWindowController.shared.show(page: .translation)
    }

    var canSwapLanguages: Bool {
        let options = SettingsManager.shared.translationOptions
        return !running && !options.transcribeOnly && options.availableSourceLanguages.contains(targetLanguage) &&
            options.targetLanguages(for: targetLanguage).contains(sourceLanguage)
    }

    func swapLanguages() {
        guard canSwapLanguages else { return }
        let previousSource = sourceLanguage
        sourceLanguage = targetLanguage
        targetLanguage = previousSource
    }

    func toggle(record: Bool = false) {
        guard job == nil else { stop(); return }
        var options = SettingsManager.shared.translationOptions
        options.sourceLanguage = sourceLanguage
        options.targetLanguage = targetLanguage
        sourceOnlySession = options.transcribeOnly
        identifiesSpeakers = !options.selectedSpeakerModel.isEmpty
        let engine = TranslationEngine()
        self.engine = engine
        running = true
        recording = record
        savedRecording = nil
        failed = false
        captions = []
        speakerNames = [:]
        status = t("正在加载模型…", "Loading models…")
        job = Task {
            defer {
                self.engine = nil
                job = nil
                running = false
                listening = false
                recording = false
                if savedRecording != nil && !failed { status = t("录音已保存", "Recording saved") }
                if sourceOnlySession && Task.isCancelled {
                    for index in captions.indices where !captions[index].sourceFinal {
                        captions[index].sourceFinal = true
                    }
                }
                for index in captions.indices where captions[index].translation == nil {
                    captions[index].translation = ""
                }
            }
            let onSaved: ((URL) -> Void)? = record ? { self.savedRecording = $0 } : nil
            do {
                try await engine.run(options: options, status: { backend in
                    self.listening = true
                    let label = record ? self.t("正在录音", "Recording") :
                        self.sourceOnlySession ? self.t("正在记录原文", "Transcribing") : self.t("正在监听", "Listening")
                    self.status = backend.map { label + " · " + $0 } ?? label
                }, caption: { item in
                    if item.id > self.captions.count { self.captions.append(item) }
                    else { self.captions[item.id - 1] = item }
                }, recordingSaved: onSaved)
                status = t("已停止", "Stopped")
            } catch {
                if Task.isCancelled && !(error is TranslationRecordingError) { status = t("已停止", "Stopped") }
                else {
                    failed = true
                    if let error = error as? TranslationError { status = t(error.chinese, error.english) }
                    else { status = error.localizedDescription }
                }
            }
        }
    }

    func speakerName(_ id: Int) -> String {
        speakerNames[id] ?? "\(t("说话人", "Speaker")) \(id)"
    }

    func renameSpeaker(_ id: Int, to name: String) {
        let value = name.trimmingCharacters(in: .whitespacesAndNewlines)
        speakerNames[id] = value.isEmpty ? nil : value
    }

    var hasTranslations: Bool {
        captions.contains { $0.sourceFinal && $0.translationFinal && !($0.translation ?? "").isEmpty }
    }

    var hasFinalCaptions: Bool { captions.contains(where: \.sourceFinal) }

    func export(includeSource: Bool, includeTranslation: Bool) {
        var sections: [String] = []
        for item in captions where item.sourceFinal {
            let source = includeSource ? item.source : ""
            let translation = includeTranslation && item.translationFinal ? item.translation ?? "" : ""
            guard !source.isEmpty || !translation.isEmpty else { continue }

            var heading = item.microphone ? t("麦克风", "Microphone") : t("电脑声音", "System audio")
            if item.speakerID > 0 {
                let speakerID = "\(t("说话人", "Speaker")) \(item.speakerID)"
                let speaker = speakerNames[item.speakerID].map {
                    "\($0.components(separatedBy: .newlines).joined(separator: " ")) (\(speakerID))"
                } ?? speakerID
                heading += " · " + speaker
            } else if identifiesSpeakers {
                heading += " · " + t("说话人待确认", "Speaker unconfirmed")
            }

            var section = ["## \(markdownText(heading))"]
            if !source.isEmpty {
                if includeTranslation { section.append("**\(t("原文", "Source"))**") }
                section.append(markdownText(source))
            }
            if !translation.isEmpty {
                if includeSource { section.append("**\(t("译文", "Translation"))**") }
                section.append(markdownText(translation))
            }
            sections.append(section.joined(separator: "\n\n"))
        }
        guard !sections.isEmpty, let window else { return }
        let text = "# \(t("录写/翻译", "Transcribe / Translate"))\n\n\(sections.joined(separator: "\n\n"))\n"

        let save = NSSavePanel()
        if let markdownType = UTType(filenameExtension: "md") {
            save.allowedContentTypes = [markdownType]
        }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        save.nameFieldStringValue = "transcript-\(formatter.string(from: Date())).md"
        save.beginSheetModal(for: window) { response in
            guard response == .OK, let destination = save.url else { return }
            do {
                try text.write(to: destination, atomically: true, encoding: .utf8)
            } catch {
                let alert = NSAlert(error: error)
                alert.runModal()
            }
        }
    }

    private func markdownText(_ value: String) -> String {
        var escaped = ""
        for character in value {
            if "\\`*_{}[]<>#|".contains(character) { escaped.append("\\") }
            escaped.append(character)
        }
        return escaped
    }

    private func t(_ chinese: String, _ english: String) -> String {
        SettingsManager.shared.language == "en-US" ? english : chinese
    }
}

private struct TranslationWindowView: View {
    @ObservedObject var controller: TranslationWindowController
    @EnvironmentObject private var settings: SettingsManager

    private enum AudioSource: Hashable {
        case none, system, microphone, both
    }

    private var audioSource: Binding<AudioSource> {
        Binding {
            switch (settings.translationOptions.systemAudio, settings.translationOptions.microphone) {
            case (true, true): return .both
            case (true, false): return .system
            case (false, true): return .microphone
            case (false, false): return .none
            }
        } set: { source in
            var options = settings.translationOptions
            options.systemAudio = source == .system || source == .both
            options.microphone = source == .microphone || source == .both
            settings.setTranslationOptions(options)
        }
    }

    private var dark: Bool { settings.theme == "Dark" }
    private let accent = Color(red: 0.14, green: 0.42, blue: 0.996)
    private var line: Color {
        dark ? Color(red: 0.20, green: 0.25, blue: 0.35) : Color(red: 0.85, green: 0.88, blue: 0.93)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Picker(t("音源", "Audio source"), selection: audioSource) {
                    if audioSource.wrappedValue == .none {
                        Text(t("请选择", "Select")).tag(AudioSource.none)
                    }
                    Text(t("电脑声音", "System audio")).tag(AudioSource.system)
                    Text(t("麦克风", "Microphone")).tag(AudioSource.microphone)
                    Text(t("电脑声＋麦克风", "System + mic")).tag(AudioSource.both)
                }
                .labelsHidden()
                .fixedSize()
                .disabled(controller.running)
                Picker(t("推理设备", "Compute"), selection: settings.translationBinding(\.compute)) {
                    Text(t("自动", "Automatic")).tag("auto")
                    Text("CPU").tag("cpu")
                    Text(t("优先 GPU", "Prefer GPU")).tag("gpu")
                }
                .labelsHidden()
                .fixedSize()
                .disabled(controller.running)
                fontSizePicker(t("原文", "Source"), settings.translationBinding(\.sourceFontSize))
                    .help(t("原文字号", "Source size"))
                fontSizePicker(t("译文", "Target"), settings.translationBinding(\.targetFontSize))
                    .help(t("译文字号", "Translation size"))
                Toggle(t("保留原文", "Keep source"), isOn: settings.translationBinding(\.showSource))
                    .help(t("译文定稿后继续显示原文", "Keep the source visible after the translation is final"))
                exportMenu
                Button { controller.openSettings() } label: {
                    Image(systemName: "gearshape")
                }
                .accessibilityLabel(t("录写/翻译设置", "Transcribe / Translate settings"))
                .help(t("录写/翻译设置", "Transcribe / Translate settings"))
                Spacer(minLength: 0)

            }

            ScrollViewReader { reader in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(controller.captions.suffix(250)) { caption in
                            captionRow(caption, latest: caption.id == controller.captions.last?.id)
                                .id(caption.id)
                        }
                    }
                    .padding(8)
                }
                .onChange(of: controller.captions.last) { last in
                    if let last { reader.scrollTo(last.id, anchor: .bottom) }
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(dark ? Color(red: 0.08, green: 0.125, blue: 0.2) : .white, in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(line).allowsHitTesting(false))
            .overlay(alignment: controller.captions.isEmpty ? .center : .topLeading) {
                if controller.captions.isEmpty || controller.failed {
                    Text(controller.listening ? t("等待语音…", "Waiting for speech…")
                         : (controller.status.isEmpty ? t("字幕会显示在这里", "Captions appear here") : controller.status))
                        .font(.system(size: 13))
                        .foregroundStyle(.secondary)
                        .padding(8)
                }
            }

            HStack(spacing: 8) {
                HStack(spacing: 6) {
                    Picker(t("源语言", "Source language"), selection: $controller.sourceLanguage) {
                        translationLanguageChoices(settings.translationOptions.availableSourceLanguages, english: settings.language == "en-US")
                    }
                    .labelsHidden()
                    .frame(width: 110)
                    .disabled(settings.translationOptions.availableSourceLanguages.isEmpty)
                    Button { controller.swapLanguages() } label: {
                        Image(systemName: "arrow.left.arrow.right")
                    }
                    .accessibilityLabel(t("交换语言", "Swap languages"))
                    .help(t("交换语言", "Swap languages"))
                    .disabled(!controller.canSwapLanguages)
                    Picker(t("目标语言", "Target language"), selection: $controller.targetLanguage) {
                        translationLanguageChoices(settings.translationOptions.targetLanguages(for: controller.sourceLanguage), english: settings.language == "en-US")
                    }
                    .labelsHidden()
                    .frame(width: 110)
                    .disabled(settings.translationOptions.transcribeOnly || settings.translationOptions.availableTargetLanguages.isEmpty)
                }
                .disabled(controller.running)
                Toggle(t("仅记录", "Transcribe only"), isOn: settings.translationBinding(\.transcribeOnly))
                    .help(t("仅记录原文字幕", "Record source captions only"))
                    .toggleStyle(.switch)
                    .controlSize(.small)
                    .fixedSize()
                    .disabled(controller.running)
                Button(controller.running ? t("停止", "Stop") : t("开始", "Start")) { controller.toggle() }
                    .buttonStyle(.borderedProminent)
                    .tint(accent)
                    .padding(.leading, 4)
                Button(controller.recording ? t("录音已开启", "Recording enabled") : t("开始并录音", "Start and record")) {
                    controller.toggle(record: true)
                }
                .disabled(controller.running)
                Spacer(minLength: 8)
                HStack(spacing: 6) {
                    Circle()
                        .fill(controller.failed || (controller.listening && controller.recording) ? Color.red
                              : controller.listening ? Color.green : Color.secondary)
                        .frame(width: 8, height: 8)
                    Text(controller.status.isEmpty ? t("就绪", "Ready") : controller.status)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                .help(controller.savedRecording?.path ?? controller.status)
                .accessibilityLabel(controller.savedRecording?.path ?? controller.status)
            }
        }
        .padding(12)
        .frame(minWidth: 670, minHeight: 270)
        .background(dark ? Color(red: 0.067, green: 0.106, blue: 0.169) : Color(red: 0.965, green: 0.973, blue: 0.988))
        .preferredColorScheme(dark ? .dark : .light)
        .onChange(of: settings.translationOptions.availableSourceLanguages) { available in
            if !controller.running {
                controller.sourceLanguage = TranslationOptions.selectLanguage(controller.sourceLanguage, from: available)
            }
        }
        .onChange(of: settings.translationOptions.targetLanguages(for: controller.sourceLanguage)) { available in
            if !controller.running {
                controller.targetLanguage = TranslationOptions.selectLanguage(controller.targetLanguage, from: available, fallback: "English")
            }
        }
    }

    private var exportMenu: some View {
        Menu {
            if !controller.sourceOnlySession {
                Button(t("原文和译文", "Source and translation")) {
                    controller.export(includeSource: true, includeTranslation: true)
                }
            }
            Button(t("仅原文", "Source only")) {
                controller.export(includeSource: true, includeTranslation: false)
            }
            if !controller.sourceOnlySession {
                Button(t("仅译文", "Translation only")) {
                    controller.export(includeSource: false, includeTranslation: true)
                }
                .disabled(!controller.hasTranslations)
            }
        } label: {
            Image(systemName: "square.and.arrow.down")
        }
        .accessibilityLabel(t("导出全文", "Export transcript"))
        .help(t("导出全文", "Export transcript"))
        .fixedSize()
        .disabled(!controller.hasFinalCaptions)
    }

    private func fontSizePicker(_ title: String, _ selection: Binding<Int>) -> some View {
        Picker(title, selection: selection) {
            ForEach(TranslationOptions.fontSizes, id: \.self) { size in
                Text(String(size)).tag(size)
            }
        }
        .fixedSize()
    }

    private func captionReviewLabel(_ review: CaptionReview) -> String {
        switch review {
        case .pending: t("复核中", "Reviewing")
        case .kept: t("已复核 · 保留原文", "Reviewed · Original kept")
        case .revised: t("已修正", "Revised")
        case .disabled: t("未复核 · 功能已关闭", "Not reviewed · Disabled")
        case .modelMissing: t("未复核 · 缺少 1920 ms 模型", "Not reviewed · 1920 ms model missing")
        case .languageUnsupported: t("未复核 · 源语言不支持", "Not reviewed · Unsupported source language")
        case .modelPreparing: t("未复核 · 模型正在准备", "Not reviewed · Model preparing")
        case .modelFailed: t("未复核 · 模型加载失败", "Not reviewed · Model failed to load")
        case .noChinese: t("未复核 · 此段无中文", "Not reviewed · No Chinese in this passage")
        case .audioUnaligned: t("未复核 · 音频边界不明确", "Not reviewed · Unclear audio boundaries")
        case .outsideLimits: t("未复核 · 段落长度超出范围", "Not reviewed · Passage length out of range")
        case .queueFull: t("未复核 · 队列已满", "Not reviewed · Queue full")
        case .unavailable: t("未复核 · 状态不可用", "Not reviewed · Status unavailable")
        }
    }

    private func captionRow(_ caption: TranslationCaption, latest: Bool) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            let translation = caption.translation ?? ""
            if controller.sourceOnlySession || !caption.translationFinal ||
                ((settings.translationOptions.showSource || translation.isEmpty) && translation != caption.source) {
                Text(caption.source)
                    .font(.system(size: CGFloat(settings.translationOptions.sourceFontSize)))
                    .foregroundStyle(translation.isEmpty ? Color.primary : Color.secondary)
                    .textSelection(.enabled)
            }
            if !controller.sourceOnlySession {
                let visibleText = caption.translation == nil ? t("正在翻译…", "Translating…") :
                    (translation.isEmpty ? t("未生成译文", "Translation unavailable") : translation)
                Text(visibleText + (caption.translationFinal || translation.isEmpty ? "" : " …"))
                    .font(.system(size: CGFloat(settings.translationOptions.targetFontSize), weight: .semibold))
                    .foregroundStyle(translation.isEmpty ? Color.secondary : Color.primary)
                    .textSelection(.enabled)
            }
            HStack(spacing: 8) {
                Label(caption.microphone ? t("麦克风", "Microphone") : t("电脑声音", "System audio"),
                      systemImage: caption.microphone ? "mic" : "speaker.wave.2")
                    .foregroundStyle(.secondary)
                if caption.speakerID > 0 {
                    SpeakerNameView(controller: controller, speakerID: caption.speakerID,
                                    help: t("双击修改说话人姓名", "Double-click to rename the speaker"),
                                    editingLabel: t("编辑中", "Editing"))
                        .id(caption.speakerID)
                } else if controller.identifiesSpeakers && caption.sourceFinal {
                    Text(t("说话人待确认", "Speaker unconfirmed"))
                        .foregroundStyle(.secondary)
                        .help(t("这句话的声音不足以可靠区分说话人", "This phrase cannot be assigned to a speaker reliably"))
                }
                Text(caption.timeRange)
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                    .help(t("这段音频的起止时间", "Start and end time of this passage"))
                if let review = caption.review {
                    Text(captionReviewLabel(review))
                        .foregroundStyle(review == .revised ? accent : Color.secondary)
                }
            }
            .font(.system(size: 11))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.vertical, 10)
        .padding(.horizontal, 14)
        .background(latest ? accent.opacity(dark ? 0.20 : 0.09) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
        .overlay(alignment: .leading) {
            if latest { RoundedRectangle(cornerRadius: 2).fill(accent).frame(width: 3) }
        }
        .overlay(alignment: .bottom) {
            if !latest { Rectangle().fill(line).frame(height: 1) }
        }
    }

    private func t(_ chinese: String, _ english: String) -> String {
        settings.language == "en-US" ? english : chinese
    }
}

private struct SpeakerNameView: View {
    @ObservedObject var controller: TranslationWindowController
    let speakerID: Int
    let help: String
    let editingLabel: String
    @State private var draft = ""
    @State private var original = ""
    @State private var editing = false
    @FocusState private var focused: Bool

    var body: some View {
        if editing {
            HStack(spacing: 5) {
                TextField(help, text: $draft)
                    .textFieldStyle(.plain)
                    .padding(.horizontal, 5)
                    .padding(.vertical, 2)
                    .frame(width: 148)
                    .background(.background, in: RoundedRectangle(cornerRadius: 4))
                    .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Color.accentColor, lineWidth: 1.5))
                    .focused($focused)
                    .onAppear { focused = true }
                    .onChange(of: draft) { controller.renameSpeaker(speakerID, to: $0) }
                    .onChange(of: focused) { if !$0 { editing = false } }
                    .onSubmit { editing = false }
                    .onExitCommand {
                        controller.renameSpeaker(speakerID, to: original)
                        editing = false
                    }
                Text(editingLabel).foregroundStyle(Color.accentColor)
            }
        } else {
            Text(controller.speakerName(speakerID))
                .help(help)
                .onTapGesture(count: 2) {
                    original = controller.speakerName(speakerID)
                    draft = original
                    editing = true
                }
        }
    }
}

struct TranslationSettingsView: View {
    @EnvironmentObject private var settings: SettingsManager
    @State private var downloading: Set<String> = []
    @State private var downloadErrors: [String: String] = [:]


    var body: some View {
        VStack(alignment: .leading, spacing: 11) {
            section(t("语言与推理", "Languages and compute")) {
                Picker(t("默认源语言", "Default source language"), selection: Binding(
                    get: { settings.translationOptions.selectedSourceLanguage },
                    set: { settings.translationBinding(\.sourceLanguage).wrappedValue = $0 }
                )) {
                    translationLanguageChoices(settings.translationOptions.availableSourceLanguages, english: settings.language == "en-US")
                }
                .frame(width: 330)
                .disabled(settings.translationOptions.availableSourceLanguages.isEmpty)
                Picker(t("默认目标语言", "Default target language"), selection: Binding(
                    get: { settings.translationOptions.selectedTargetLanguage },
                    set: { settings.translationBinding(\.targetLanguage).wrappedValue = $0 }
                )) {
                    translationLanguageChoices(settings.translationOptions.availableTargetLanguages, english: settings.language == "en-US")
                }
                .frame(width: 330)
                .disabled(settings.translationOptions.availableTargetLanguages.isEmpty)
                Picker(t("推理设备", "Compute"), selection: settings.translationBinding(\.compute)) {
                    Text(t("自动", "Automatic")).tag("auto")
                    Text("CPU").tag("cpu")
                    Text(t("优先 GPU", "Prefer GPU")).tag("gpu")
                }
                .frame(width: 290)
            }
            section(t("麦克风", "Microphone")) {
                Picker(t("人声检测", "Speech detection"), selection: settings.translationBinding(\.microphoneFilter)) {
                    Text(t("温和", "Gentle")).tag("gentle")
                    Text(t("均衡", "Balanced")).tag("balanced")
                    Text(t("关闭", "Off")).tag("off")
                }
                .frame(width: 290)
                Picker(t("断句等待", "End-of-speech pause"), selection: settings.translationBinding(\.microphonePause)) {
                    Text(t("标准", "Standard")).tag("normal")
                    Text(t("较长", "Longer")).tag("long")
                }
                .frame(width: 290)
            }
            section(t("识别与说话人", "Speech and speakers")) {
                ForEach(StreamingSpeechModel.catalog) { speech in
                    file(t(speech.chinese, speech.english), path: Binding(
                        get: { settings.translationOptions.path(for: speech) },
                        set: { path in
                            var options = settings.translationOptions
                            options.selectModel(speech, path: path)
                            settings.setTranslationOptions(options)
                        }
                    ), url: speech.download.url, type: .data, model: speech.download)
                }
                let correction = StreamingSpeechModel.correction
                file(t(correction.chinese, correction.english), path: Binding(
                    get: { settings.translationOptions.selectedCorrectionModel },
                    set: { settings.translationBinding(\.correctionModel).wrappedValue = $0 }
                ), url: correction.download.url, type: .data, model: correction.download)
                Toggle(t("模型就绪时启用段末修正", "Enable second pass when installed"),
                       isOn: settings.translationBinding(\.enableCorrection))
                file(t("流式说话人模型", "Streaming speaker model"), path: Binding(
                    get: { settings.translationOptions.selectedSpeakerModel },
                    set: { settings.translationBinding(\.onlineSpeakerModel).wrappedValue = $0 }
                ), url: TranslationModelDownload.onlineSpeaker.url, type: UTType(filenameExtension: "gguf"), model: .onlineSpeaker)
            }
            section(t("文本翻译", "Text translation")) {
                Picker(t("文本翻译模型", "Text translation model"), selection: settings.translationBinding(\.useLmtTranslation)) {
                    Text("Hy-MT2 1.8B").tag(false)
                    Text("LMT-60 0.6B").tag(true)
                }
                .frame(width: 290)
                file("Hy-MT2 · 1.13 GB", path: Binding(
                    get: { settings.translationOptions.standardTranslationPath },
                    set: { path in
                        var options = settings.translationOptions
                        options.translationModel = path
                        options.useLmtTranslation = false
                        settings.setTranslationOptions(options)
                    }
                ), url: TranslationModelDownload.llama.url, type: UTType(filenameExtension: "gguf"), model: .llama)
                file("LMT-60 · 805 MB", path: Binding(
                    get: { settings.translationOptions.lmtTranslationPath },
                    set: { path in
                        var options = settings.translationOptions
                        options.lmtTranslationModel = path
                        options.useLmtTranslation = true
                        settings.setTranslationOptions(options)
                    }
                ), url: TranslationModelDownload.lmt.url, type: UTType(filenameExtension: "gguf"), model: .lmt)
            }
            section(t("诊断与高级设置", "Diagnostics and advanced")) {
                Toggle(t("诊断日志（保存到下载）", "Diagnostic log (Downloads)"),
                       isOn: settings.translationBinding(\.diagnosticsEnabled))
                DisclosureGroup(t("自定义推理引擎", "Custom inference engines")) {
                    file("llama-server / llama", path: settings.translationBinding(\.llamaServer), url: TranslationOptions.llamaRelease,
                         canReset: true)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func section<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.system(size: 15, weight: .semibold))
            content()
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Color.primary.opacity(0.12)))
    }

    private func file(_ label: String, path: Binding<String>, url: URL, type: UTType? = nil,
                      canReset: Bool = false, model: TranslationModelDownload? = nil) -> some View {
        let busy = model.map { downloading.contains($0.name) } ?? false
        return VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                Text(label).frame(width: 150, alignment: .leading)
                Text(path.wrappedValue.isEmpty
                     ? (canReset ? t("自动", "Automatic") : t("未选择", "Not selected")) : URL(fileURLWithPath: path.wrappedValue).lastPathComponent)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .foregroundStyle(.secondary)
                    .help(path.wrappedValue)
                    .frame(maxWidth: .infinity, alignment: .leading)
                Button(t("选择…", "Choose…")) {
                    let panel = NSOpenPanel()
                    panel.title = label
                    panel.canChooseDirectories = false
                    panel.allowsMultipleSelection = false
                    if let type { panel.allowedContentTypes = [type] }
                    if panel.runModal() == .OK, let url = panel.url {
                        if let model, !model.hasExpectedSize(url.path) {
                            downloadErrors[model.name] = t("请选择此行对应的模型文件。", "Select the model file listed on this row.")
                        } else {
                            if let model { downloadErrors[model.name] = nil }
                            path.wrappedValue = url.path
                        }
                    }
                }
                .disabled(busy)
                if canReset && !path.wrappedValue.isEmpty {
                    Button(t("恢复自动", "Reset to automatic")) { path.wrappedValue = "" }
                }
                if let model {
                    Button(busy ? t("下载并校验中…", "Downloading…") : t("下载", "Download")) {
                        downloading.insert(model.name)
                        downloadErrors[model.name] = nil
                        Task {
                            defer { downloading.remove(model.name) }
                            do {
                                path.wrappedValue = try await TranslationModelDownloads.shared.install(model)
                            } catch {
                                if let error = error as? TranslationError {
                                    downloadErrors[model.name] = t(error.chinese, error.english)
                                } else {
                                    downloadErrors[model.name] = error.localizedDescription
                                }
                            }
                        }
                    }
                    .disabled(busy)
                } else {
                    Link(t("下载", "Download"), destination: url)
                }
            }
            if let model, let error = downloadErrors[model.name] {
                Text(error).font(.caption).foregroundStyle(.red)
            }
        }
    }

    private func t(_ chinese: String, _ english: String) -> String {
        settings.language == "en-US" ? english : chinese
    }
}


@ViewBuilder
private func translationLanguageChoices(_ available: [String], english: Bool) -> some View {
    if available.isEmpty {
        Text(english ? "No model" : "未配置模型").tag("")
    }
    ForEach(available, id: \.self) { id in
        if id == "auto" {
            Text(english ? "Auto-detect" : "自动识别").tag(id)
        } else if let language = TranslationOptions.languages.first(where: { $0.id == id }) {
            Text(english ? language.id : language.chinese).tag(id)
        }
    }
}
