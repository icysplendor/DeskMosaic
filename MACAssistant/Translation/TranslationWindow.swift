import AppKit
import SwiftUI
import UniformTypeIdentifiers

@MainActor
final class TranslationWindowController: NSObject, ObservableObject, NSWindowDelegate {
    static let shared = TranslationWindowController()

    @Published private var speakerNames: [Int: String] = [:]
    private static let audioExtensions = ["wav", "mp3", "m4a", "aac", "flac", "ogg", "opus", "aif", "aiff", "webm"]
    @Published private(set) var audioFile: URL?
    @Published private(set) var fileSession = false
    @Published private(set) var fileCompleted = false
    @Published private(set) var captions: [TranslationCaption] = []
    @Published private(set) var running = false
    @Published private(set) var listening = false
    @Published private(set) var speechReady = false
    @Published private(set) var recording = false
    @Published private(set) var sourceOnlySession = false
    @Published private(set) var twoWaySession = false
    @Published private(set) var identifiesSpeakers = false
    @Published private(set) var savedRecording: URL?
    @Published private(set) var status = ""
    @Published private(set) var failed = false
    @Published var sourceLanguage = "Chinese"
    @Published var targetLanguage = "English"
    @Published var transcribeOnly = false
    private var window: NSWindow?
    private var job: Task<Void, Never>?
    private var engine: TranslationEngine?
    private var clearedThroughCaption = 0
    private var inputFinishing = false

    func show() {
        if window?.isVisible != true && !running {
            setAudioFile(nil)
            transcribeOnly = false
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
            let content = NSHostingView(rootView: TranslationWindowView(controller: self)
                .environmentObject(SettingsManager.shared))
            DeskWindowChrome.wrap(content, in: newWindow, title: t("录写/翻译", "Transcribe / Translate"))
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

    func stopAndWait() async -> String? {
        let current = job
        let wasRecording = recording
        stop()
        await current?.value
        return wasRecording && failed ? status : nil
    }

    func windowWillClose(_ notification: Notification) {
        stop()
        setAudioFile(nil)
    }

    func openSettings() {
        window?.orderOut(nil)
        stop()
        SettingsWindowController.shared.show(page: .translation)
    }

    var sessionOptions: TranslationOptions {
        var options = SettingsManager.shared.translationOptions
        options.importedAudio = audioFile != nil
        return options
    }

    func chooseAudioFile() {
        guard !running else { return }
        let panel = NSOpenPanel()
        panel.allowedContentTypes = Self.audioExtensions.compactMap { UTType(filenameExtension: $0) }
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        if panel.runModal() == .OK, let file = panel.url { _ = importAudio(file) }
    }

    func importAudio(_ file: URL) -> Bool {
        guard !running, file.isFileURL, Self.audioExtensions.contains(file.pathExtension.lowercased()),
              (try? file.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true else { return false }
        setAudioFile(file, reselected: true)
        return true
    }

    func setAudioFile(_ file: URL?, reselected: Bool = false) {
        guard file != audioFile || reselected else { return }
        audioFile = file
        fileCompleted = false
        let options = sessionOptions
        sourceLanguage = TranslationOptions.selectLanguage(sourceLanguage, from: options.sessionSourceLanguages)
        targetLanguage = TranslationOptions.selectLanguage(targetLanguage, from: options.targetLanguages(for: sourceLanguage), fallback: "English")
        status = file == nil ? t("就绪", "Ready") : t("已准备好处理音频", "Ready to process audio")
        failed = false
    }

    var canSwapLanguages: Bool {
        let options = sessionOptions
        return !running && !transcribeOnly && options.sessionSourceLanguages.contains(targetLanguage) &&
            options.targetLanguages(for: targetLanguage).contains(sourceLanguage)
    }

    func swapLanguages() {
        guard canSwapLanguages else { return }
        let previousSource = sourceLanguage
        sourceLanguage = targetLanguage
        targetLanguage = previousSource
    }

    func toggle(twoWay: Bool = false) {
        if job != nil {
            if engine?.finishLiveInput() == true {
                inputFinishing = true
                listening = false
                status = t("正在处理剩余语音…", "Processing remaining speech…")
            } else { stop() }
            return
        }
        guard !fileCompleted else { return }
        let file = audioFile
        fileSession = file != nil
        var options = sessionOptions
        let record = file == nil && options.recordAudio
        options.sourceLanguage = sourceLanguage
        options.targetLanguage = targetLanguage
        options.transcribeOnly = transcribeOnly
        options.twoWay = twoWay
        twoWaySession = twoWay
        sourceOnlySession = options.transcribeOnly
        identifiesSpeakers = !options.selectedSpeakerModel.isEmpty
        let engine = TranslationEngine()
        self.engine = engine
        running = true
        recording = record
        savedRecording = nil
        failed = false
        clearedThroughCaption = 0
        captions = []
        speakerNames = [:]
        status = t("正在加载模型…", "Loading models…")
        job = Task {
            defer {
                self.engine = nil
                job = nil
                running = false
                listening = false
                inputFinishing = false
                speechReady = false
                recording = false
                twoWaySession = false
                if savedRecording != nil && !failed { status = t("录音已保存", "Recording saved") }
                if sourceOnlySession && Task.isCancelled {
                    for index in captions.indices where !captions[index].sourceFinal {
                        captions[index].sourceFinal = true
                    }
                }
                for index in captions.indices {
                    if captions[index].translation == nil, captions[index].reviewTurns.isEmpty { captions[index].translation = "" }
                    if captions[index].review == .pending { captions[index].review = .unavailable }
                }
            }
            let onSaved: ((URL) -> Void)? = record ? { self.savedRecording = $0 } : nil
            do {
                try await engine.run(options: options, capturing: {
                    guard file == nil else { return }
                    self.listening = true
                    self.status = record ? self.t("正在录音 · 加载模型…", "Recording · Loading models…") :
                        self.t("正在采集 · 加载模型…", "Capturing audio · Loading models…")
                }, status: { backend in
                    guard !self.inputFinishing else { return }
                    self.listening = true
                    self.speechReady = file == nil
                    if file != nil {
                        self.status = backend.map { self.t("正在处理音频 · 逐段翻译", "Processing audio · Translating") + " · " + $0 }
                            ?? self.t("正在识别音频文件…", "Transcribing audio file…")
                        return
                    }
                    let label: String
                    if twoWay {
                        label = record ? self.t("互译并录音", "Two-way + recording") : self.t("正在互译", "Translating both ways")
                    } else {
                        label = record ? self.t("正在录音", "Recording") :
                            self.sourceOnlySession ? self.t("正在记录原文", "Transcribing") : self.t("正在监听", "Listening")
                    }
                    self.status = backend.map { label + " · " + $0 } ?? label
                }, caption: { item in
                    let index = item.id - self.clearedThroughCaption - 1
                    if index == self.captions.count { self.captions.append(item) }
                    else if self.captions.indices.contains(index) { self.captions[index] = item }
                }, recordingSaved: onSaved, audioFile: file)
                try Task.checkCancellation()
                fileCompleted = file != nil
                status = fileCompleted ? t("音频处理完成", "Audio processing complete") : t("已停止", "Stopped")
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

    func clearTranscript() {
        guard let last = captions.last else { return }
        clearedThroughCaption = last.id
        captions.removeAll()
    }

    func scopedSpeakerName(_ id: Int) -> String {
        id > 0 ? t("本段说话人 \(id)", "Passage speaker \(id)") : t("说话人待确认", "Speaker unconfirmed")
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
        let finalCaptions = captions.filter(\.sourceFinal).flatMap { item in
            item.reviewTurns.isEmpty ? [(item, item.id)] : item.reviewTurns.map { ($0, item.id) }
        }
        for (item, paragraph) in finalCaptions {
            let source = includeSource ? item.source : ""
            let translation = includeTranslation && item.translationFinal ? item.translation ?? "" : ""
            guard !source.isEmpty || !translation.isEmpty else { continue }

            var heading = sourceLabel(item)
            if item.speakerWithinParagraph {
                heading += " · " + t("段落 \(paragraph)", "Passage \(paragraph)") + " · " + scopedSpeakerName(item.speakerID)
            } else if item.review != .pending && item.speakerID > 0 {
                let speakerID = "\(t("说话人", "Speaker")) \(item.speakerID)"
                let speaker = speakerNames[item.speakerID].map {
                    "\($0.components(separatedBy: .newlines).joined(separator: " ")) (\(speakerID))"
                } ?? speakerID
                heading += " · " + speaker
            } else if identifiesSpeakers && item.review != .pending && item.turnSources.isEmpty {
                heading += " · " + t("说话人待确认", "Speaker unconfirmed")
            }
            heading += " · " + item.timeRange

            var section = ["**\(markdownText(heading))**"]
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

    func sourceLabel(_ item: TranslationCaption) -> String {
        if fileSession { return t("导入音频", "Imported audio") }
        return item.microphone ? t("麦克风", "Microphone") : t("电脑声音", "System audio")
    }

    private func t(_ chinese: String, _ english: String) -> String {
        SettingsManager.shared.language == "en-US" ? english : chinese
    }
}

private struct TranslationWindowView: View {
    @ObservedObject var controller: TranslationWindowController
    @EnvironmentObject private var settings: SettingsManager
    @State private var scrollPosition = CaptionScrollPosition()
    @State private var confirmingClear = false

    private enum AudioSource: Hashable {
        case none, system, microphone, both, file
    }

    private var audioSource: Binding<AudioSource> {
        Binding {
            if controller.audioFile != nil { return .file }
            switch (settings.translationOptions.systemAudio, settings.translationOptions.microphone) {
            case (true, true): return .both
            case (true, false): return .system
            case (false, true): return .microphone
            case (false, false): return .none
            }
        } set: { source in
            if source == .file {
                controller.chooseAudioFile()
                return
            }
            controller.setAudioFile(nil)
            var options = settings.translationOptions
            options.systemAudio = source == .system || source == .both
            options.microphone = source == .microphone || source == .both
            settings.setTranslationOptions(options)
        }
    }

    private var dark: Bool { settings.theme == "Dark" }
    private let accent = Color(red: 0.30, green: 0.35, blue: 0.45)
    private let stopAccent = Color(red: 0.70, green: 0.24, blue: 0.30)
    private var sessionButtonTitle: String {
        if controller.fileCompleted { return t("音频执行完毕", "Audio processing complete") }
        guard controller.running else { return t("开始", "Start") }
        return controller.twoWaySession ? t("互译结束", "End two-way translation") : t("翻译结束", "End translation")
    }
    private var canStartTwoWay: Bool {
        !controller.transcribeOnly && controller.sessionOptions.canTranslateTwoWay
    }
    private var line: Color {
        dark ? Color(red: 0.20, green: 0.25, blue: 0.35) : Color(red: 0.85, green: 0.86, blue: 0.89)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            VStack(alignment: .leading, spacing: 7) {
                HStack(spacing: 8) {
                    dropdown(t("音源", "Audio source"), selection: audioSource, choices: [
                        (.system, t("电脑声音", "System audio")),
                        (.microphone, t("麦克风", "Microphone")),
                        (.both, t("电脑声＋麦克风", "System + mic")),
                        (.file, t("导入音频", "Import audio"))
                    ], placeholder: t("请选择", "Select"))
                    .disabled(controller.running)
                    sessionButton(sessionButtonTitle, tint: controller.running ? stopAccent : accent) { controller.toggle() }
                        .disabled(controller.fileCompleted)
                        .padding(.leading, 4)
                    if !controller.running && !controller.fileCompleted {
                        Button(t("互译", "Two-way")) {
                            controller.toggle(twoWay: true)
                        }
                        .buttonStyle(.bordered)
                        .disabled(!canStartTwoWay)
                        .help(twoWayHelp)
                    }
                    Spacer()
                    exportMenu
                    Button { controller.openSettings() } label: { Label(t("设置", "Settings"), systemImage: "gearshape") }
                }
                sessionOptionsRow
            }
            .padding(10)
            .background(dark ? Color(nsColor: .windowBackgroundColor) : DeskPalette.iceWhite, in: RoundedRectangle(cornerRadius: 8))
            if let file = controller.audioFile {
                Button { controller.chooseAudioFile() } label: {
                    Label(t("已选择 · ", "Selected · ") + file.lastPathComponent, systemImage: "waveform")
                        .lineLimit(1)
                }
                .help(file.path + "\n" + t("点击更换音频文件", "Click to choose another audio file"))
                .disabled(controller.running)
            }

            ScrollViewReader { reader in
                ScrollView {
                    VStack(spacing: 0) {
                        LazyVStack(alignment: .leading, spacing: 0) {
                            ForEach(displayedCaptions) { row in
                                captionRow(row.caption, latest: row.id == latestCaptionID)
                            }
                        }
                        .padding(8)
                        Color.clear.frame(height: 1).id("caption-bottom")
                    }
                    .background {
                        CaptionScrollObserver { atBottom in
                            scrollPosition.following = atBottom
                            if atBottom { scrollPosition.firstParagraph = nil }
                            else if scrollPosition.firstParagraph == nil {
                                scrollPosition.firstParagraph = controller.captions.suffix(250).first?.id
                            }
                        }
                        .frame(width: 0, height: 0)
                        .allowsHitTesting(false)
                    }
                }
                .onChange(of: controller.captions.last) { last in
                    if last == nil {
                        scrollPosition.following = true
                        scrollPosition.firstParagraph = nil
                    } else if scrollPosition.following {
                        reader.scrollTo("caption-bottom", anchor: .bottom)
                    }
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(dark ? Color(red: 0.08, green: 0.125, blue: 0.2) : DeskPalette.iceWhite, in: RoundedRectangle(cornerRadius: 8))
            .overlay(alignment: .top) { Rectangle().fill(line).frame(height: 1) }
            .overlay(alignment: .bottom) { Rectangle().fill(line).frame(height: 1) }
            .overlay(alignment: controller.captions.isEmpty ? .center : .topLeading) {
                if controller.captions.isEmpty || controller.failed {
                    Text(controller.speechReady ? t("等待语音…", "Waiting for speech…")
                         : (controller.status.isEmpty ? t("选择音源和语言，然后开始录写。", "Choose an audio source and languages, then start transcription.") : controller.status))
                        .font(.system(size: 13))
                        .foregroundStyle(.secondary)
                        .padding(8)
                }
            }

            HStack(spacing: 6) {
                dropdown(t("推理设备", "Compute"), selection: settings.translationBinding(\.compute), choices: [
                    ("auto", t("自动", "Automatic")), ("cpu", "CPU"), ("gpu", t("优先 GPU", "Prefer GPU"))
                ], placeholder: t("自动", "Automatic"))
                .disabled(controller.running)
                fontSizePicker(t("原文", "Source"), settings.translationBinding(\.sourceFontSize))
                    .help(t("原文字号", "Source size"))
                fontSizePicker(t("译文", "Target"), settings.translationBinding(\.targetFontSize))
                    .help(t("译文字号", "Translation size"))
                Toggle(t("显示原文", "Show source"), isOn: settings.translationBinding(\.showSource))
                    .help(t("译文定稿后继续显示原文", "Keep the source visible after the translation is final"))
                Spacer(minLength: 0)
                Button {
                    confirmingClear = true
                } label: {
                    Label(t("清屏", "Clear"), systemImage: "trash")
                }
                .help(t("清空当前字幕", "Clear current captions"))
                .disabled(controller.captions.isEmpty)
            }

        }
        .padding(12)
        .frame(minWidth: 670, minHeight: 270)
        .onDrop(of: [UTType.fileURL], isTargeted: nil) { providers in
            guard !controller.running, providers.count == 1, let provider = providers.first else { return false }
            _ = provider.loadObject(ofClass: NSURL.self) { url, _ in
                guard let file = url as? URL else { return }
                Task { @MainActor in _ = controller.importAudio(file) }
            }
            return true
        }
        .background(dark ? Color(red: 0.067, green: 0.106, blue: 0.169) : DeskPalette.iceWhite)
        .preferredColorScheme(dark ? .dark : .light)
        .sheet(isPresented: $confirmingClear) { clearConfirmation }
        .onChange(of: controller.sessionOptions.sessionSourceLanguages) { available in
            if !controller.running {
                controller.sourceLanguage = TranslationOptions.selectLanguage(controller.sourceLanguage, from: available)
            }
        }
        .onChange(of: controller.sessionOptions.targetLanguages(for: controller.sourceLanguage)) { available in
            if !controller.running {
                controller.targetLanguage = TranslationOptions.selectLanguage(controller.targetLanguage, from: available, fallback: "English")
            }
        }
    }

    private var sessionOptionsRow: some View {
        HStack(spacing: 8) {
            HStack(spacing: 6) {
                languageDropdown(t("源语言", "Source language"), selection: $controller.sourceLanguage,
                                 available: controller.sessionOptions.sessionSourceLanguages)
                    .disabled(controller.sessionOptions.sessionSourceLanguages.isEmpty)
                Button { controller.swapLanguages() } label: {
                    Image(systemName: "arrow.left.arrow.right")
                }
                .accessibilityLabel(t("交换语言", "Swap languages"))
                .help(t("交换语言", "Swap languages"))
                .disabled(!controller.canSwapLanguages)
                languageDropdown(t("目标语言", "Target language"), selection: $controller.targetLanguage,
                                 available: controller.sessionOptions.targetLanguages(for: controller.sourceLanguage))
                    .disabled(controller.transcribeOnly || controller.sessionOptions.availableTargetLanguages.isEmpty)
            }
            .disabled(controller.running)
            Toggle(t("仅记录", "Transcribe only"), isOn: $controller.transcribeOnly)
                .help(t("仅记录原文字幕", "Record source captions only"))
                .toggleStyle(.switch)
                .controlSize(.small)
                .fixedSize()
                .disabled(controller.running)
            Toggle(t("录音", "Record"), isOn: controller.audioFile == nil ? settings.translationBinding(\.recordAudio) : .constant(false))
                .help(controller.audioFile == nil
                      ? t("开始时录音，结束后保存 WAV 到下载文件夹", "Record on start and save a WAV to Downloads")
                      : t("导入音频时无法录音", "Recording is unavailable for imported audio"))
                .toggleStyle(.switch)
                .controlSize(.small)
                .fixedSize()
                .disabled(controller.running || controller.audioFile != nil)
            advancedReviewButton
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

    private var advancedReviewButton: some View {
        let options = settings.translationOptions
        let mossReady = !options.selectedMossModel.isEmpty
        let unavailable = !mossReady && options.selectedCorrectionModel.isEmpty
        let active = mossReady && options.enableCorrection && options.usesMossReview
        let label = t("高级复核", "Advanced review")
        let help = unavailable ? t("未安装复核模型", "No review model installed")
            : !mossReady ? t("下载 MOSS 后可开启", "Install MOSS to enable")
            : controller.audioFile != nil ? t("仅用于实时录写", "Available for live transcription")
            : active ? t("关闭高级复核", "Disable advanced review") : t("开启高级复核", "Enable advanced review")
        return HStack(spacing: 0) {
            Button {
                var updated = settings.translationOptions
                guard !updated.selectedMossModel.isEmpty else { return }
                updated.reviewModel = active ? "x-asr" : "moss"
                if !active { updated.enableCorrection = true }
                settings.setTranslationOptions(updated)
            } label: {
                Image(systemName: unavailable ? "exclamationmark.circle" : active ? "checkmark.seal.fill" : "checkmark.seal")
                    .font(.system(size: 14, weight: .medium))
                    .foregroundStyle(unavailable ? Color.orange : active ? accent : Color.secondary)
                    .frame(width: 30, height: 23)
                    .background(active ? accent.opacity(0.14) : Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 5))
                    .overlay(RoundedRectangle(cornerRadius: 5).strokeBorder(active ? accent.opacity(0.35) : line))
                    .contentShape(Rectangle())
            }
            .buttonStyle(DeskPlainButtonStyle())
            .disabled(!mossReady || controller.running || controller.audioFile != nil)
            .accessibilityLabel(label)
            .accessibilityValue(Text(active ? t("开启", "On") : t("关闭", "Off")))
            .accessibilityHint(Text(help))
        }
        .contentShape(Rectangle())
        .help(help)
    }

    private func sessionButton(_ title: String, tint: Color, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .fontWeight(.semibold)
                .frame(minWidth: 54)
                .fixedSize(horizontal: true, vertical: false)
        }
        .buttonStyle(.borderedProminent)
        .tint(tint)
    }

    private var twoWayHelp: String {
        if controller.transcribeOnly { return t("互译需要关闭「仅记录」", "Turn off Transcribe only to use two-way translation") }
        if !controller.sessionOptions.canTranslateTwoWay {
            return t("需要中英流式识别模型和文本翻译模型", "Requires Chinese/English streaming recognition and a translation model")
        }
        return t("开始中英互译，按段落自动选择方向", "Start two-way translation and choose a direction per paragraph")
    }

    private var clearConfirmation: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(alignment: .top, spacing: 14) {
                Image(systemName: "trash")
                    .font(.system(size: 17, weight: .semibold))
                    .foregroundStyle(stopAccent)
                    .frame(width: 40, height: 40)
                    .background(stopAccent.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 6) {
                    Text(t("清空当前字幕？", "Clear current captions?"))
                        .font(.headline)
                    Text(t("已显示的原文和译文将从窗口和后续导出中移除。录音和已保存的文件会保留。",
                           "Visible captions and translations will be removed from the window and future exports. Audio recordings and saved files are kept."))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            HStack(spacing: 8) {
                Spacer()
                Button(t("保留字幕", "Keep captions")) { confirmingClear = false }
                    .keyboardShortcut(.cancelAction)
                Button(t("清空字幕", "Clear captions"), role: .destructive) {
                    controller.clearTranscript()
                    confirmingClear = false
                }
                .buttonStyle(.borderedProminent)
                .tint(stopAccent)
            }
        }
        .padding(20)
        .frame(width: 420)
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

    private func dropdown<Value: Hashable>(_ title: String, selection: Binding<Value>,
                                           choices: [(Value, String)], placeholder: String = "",
                                           width: CGFloat? = nil) -> some View {
        let selectedIndex = choices.firstIndex { $0.0 == selection.wrappedValue }
        let current = selectedIndex.map { choices[$0].1 } ?? placeholder
        return HStack(spacing: 6) {
            Text(current).lineLimit(1).frame(maxWidth: width == nil ? nil : .infinity, alignment: .leading)
            Path { path in
                path.move(to: CGPoint(x: 1, y: 1))
                path.addLine(to: CGPoint(x: 5, y: 5))
                path.addLine(to: CGPoint(x: 9, y: 1))
            }
            .stroke(dark ? Color(red: 0.67, green: 0.73, blue: 0.82) : Color(red: 0.32, green: 0.38, blue: 0.48),
                    style: StrokeStyle(lineWidth: 1.5, lineCap: .round, lineJoin: .round))
            .frame(width: 10, height: 6)
        }
        .padding(.horizontal, 8)
        .frame(width: width, height: 22)
        .background(dark ? Color(red: 0.082, green: 0.125, blue: 0.2) : DeskPalette.iceWhite,
                    in: RoundedRectangle(cornerRadius: 6))
        .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(line).allowsHitTesting(false))
        .accessibilityHidden(true)
        .overlay {
            // macOS can reduce a SwiftUI Menu label to text, losing custom drawing.
            CaptionPopup(title: title, items: choices.map { $0.1 }, selectedIndex: selectedIndex ?? -1) { index in
                selection.wrappedValue = choices[index].0
                // A canceled file picker leaves the audio source unchanged.
                return choices.firstIndex { $0.0 == selection.wrappedValue } ?? -1
            }
        }
        .fixedSize()
    }

    private func languageDropdown(_ title: String, selection: Binding<String>, available: [String]) -> some View {
        dropdown(title, selection: selection, choices: available.compactMap { id in
            translationLanguageLabel(id, english: settings.language == "en-US").map { (id, $0) }
        }, placeholder: t("未配置模型", "No model"), width: 110)
    }

    private func fontSizePicker(_ title: String, _ selection: Binding<Int>) -> some View {
        HStack(spacing: 4) {
            Text(title)
            dropdown(title, selection: selection, choices: TranslationOptions.fontSizes.map { ($0, String($0)) })
        }
    }

    private func captionReviewLabel(_ review: CaptionReview) -> String {
        switch review {
        case .pending: t("复核中", "Reviewing")
        case .kept: t("已复核 · 保留原文", "Reviewed · Original kept")
        case .mossReviewed: t("MOSS 已复核", "Reviewed by MOSS")
        case .revised: t("已修正", "Revised")
        case .disabled: t("未复核 · 功能已关闭", "Not reviewed · Disabled")
        case .modelMissing: t("未复核 · 缺少所选复核模型", "Not reviewed · Selected review model missing")
        case .languageUnsupported: t("未复核 · 源语言不支持", "Not reviewed · Unsupported source language")
        case .modelPreparing: t("未复核 · 模型正在准备", "Not reviewed · Model preparing")
        case .modelFailed: t("未复核 · 模型加载失败", "Not reviewed · Model failed to load")
        case .noReviewableText: t("未复核 · 无可复核文字", "Not reviewed · No reviewable text")
        case .audioUnaligned: t("未复核 · 音频边界不明确", "Not reviewed · Unclear audio boundaries")
        case .outsideLimits: t("未复核 · 段落长度超出范围", "Not reviewed · Passage length out of range")
        case .queueFull: t("未复核 · 队列已满", "Not reviewed · Queue full")
        case .unavailable: t("未复核 · 状态不可用", "Not reviewed · Status unavailable")
        }
    }

    private struct DisplayedCaption: Identifiable {
        struct ID: Hashable {
            let paragraph: Int
            let turn: Int
        }

        let id: ID
        let caption: TranslationCaption
    }

    private var displayedCaptions: [DisplayedCaption] {
        let captions = controller.captions
        let first = scrollPosition.firstParagraph.flatMap { id in
            captions.firstIndex(where: { $0.id == id })
        } ?? max(0, captions.count - 250)
        return captions[first...].flatMap { paragraph in
            let rows = paragraph.reviewTurns.isEmpty ? [paragraph] : paragraph.reviewTurns
            return rows.enumerated().map { index, caption in
                DisplayedCaption(id: .init(paragraph: paragraph.id, turn: index), caption: caption)
            }
        }
    }

    private var latestCaptionID: DisplayedCaption.ID? {
        controller.captions.last.map { .init(paragraph: $0.id, turn: max(0, $0.reviewTurns.count - 1)) }
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
                Label(controller.sourceLabel(caption),
                      systemImage: controller.fileSession ? "waveform" : caption.microphone ? "mic" : "speaker.wave.2")
                    .foregroundStyle(.secondary)
                if caption.speakerWithinParagraph {
                    Text(controller.scopedSpeakerName(caption.speakerID))
                        .foregroundStyle(.secondary)
                        .help(t("说话人编号仅在本段复核内有效", "Speaker numbers apply within this reviewed passage"))
                } else if caption.sourceFinal && caption.review != .pending && caption.speakerID > 0 {
                    SpeakerNameView(controller: controller, speakerID: caption.speakerID,
                                    help: t("双击修改说话人姓名", "Double-click to rename the speaker"),
                                    editingLabel: t("编辑中", "Editing"))
                        .id(caption.speakerID)
                } else if controller.identifiesSpeakers && caption.sourceFinal && caption.review != .pending && caption.turnSources.isEmpty {
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
                        .foregroundStyle(review == .revised || review == .mossReviewed ? accent : Color.secondary)
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

private final class CaptionScrollPosition {
    var following = true
    var firstParagraph: Int?
}

private struct CaptionScrollObserver: NSViewRepresentable {
    let didScroll: (Bool) -> Void

    func makeNSView(context: Context) -> ObserverView { ObserverView() }

    func updateNSView(_ view: ObserverView, context: Context) {
        view.didScroll = didScroll
        view.observeScrollView()
    }

    final class ObserverView: NSView {
        var didScroll: ((Bool) -> Void)?
        private weak var clipView: NSClipView?
        private var lastOffset: CGFloat = 0

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            observeScrollView()
        }

        func observeScrollView() {
            let clip = enclosingScrollView?.contentView
            guard clipView !== clip else { return }
            if let clipView {
                NotificationCenter.default.removeObserver(self, name: NSView.boundsDidChangeNotification, object: clipView)
            }
            clipView = clip
            if let clip {
                lastOffset = clip.bounds.origin.y
                clip.postsBoundsChangedNotifications = true
                NotificationCenter.default.addObserver(self, selector: #selector(boundsChanged(_:)),
                    name: NSView.boundsDidChangeNotification, object: clip)
            }
        }

        @objc private func boundsChanged(_ notification: Notification) {
            guard let clipView, let scroll = enclosingScrollView, let document = scroll.documentView else { return }
            let offset = clipView.bounds.origin.y
            guard offset != lastOffset else { return }
            lastOffset = offset
            let visible = scroll.documentVisibleRect
            let distance = document.isFlipped ? document.bounds.maxY - visible.maxY : visible.minY - document.bounds.minY
            didScroll?(distance <= 2)
        }

        deinit { NotificationCenter.default.removeObserver(self) }
    }
}

private struct CaptionPopup: NSViewRepresentable {
    let title: String
    let items: [String]
    let selectedIndex: Int
    let onSelect: (Int) -> Int

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSPopUpButton {
        let popup = NSPopUpButton(frame: .zero, pullsDown: false)
        popup.isTransparent = true
        popup.controlSize = .small
        popup.target = context.coordinator
        popup.action = #selector(Coordinator.selectionChanged(_:))
        return popup
    }

    func updateNSView(_ popup: NSPopUpButton, context: Context) {
        context.coordinator.onSelect = onSelect
        popup.setAccessibilityLabel(title)
        if popup.itemTitles != items {
            popup.removeAllItems()
            popup.addItems(withTitles: items)
        }
        if popup.indexOfSelectedItem != selectedIndex {
            popup.selectItem(at: selectedIndex)
        }
        popup.isEnabled = context.environment.isEnabled && !items.isEmpty
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: NSPopUpButton, context: Context) -> CGSize? {
        proposal.replacingUnspecifiedDimensions(by: nsView.intrinsicContentSize)
    }

    @MainActor
    final class Coordinator: NSObject {
        var onSelect: ((Int) -> Int)?

        @objc func selectionChanged(_ popup: NSPopUpButton) {
            let index = popup.indexOfSelectedItem
            guard index >= 0, let onSelect else { return }
            let appliedIndex = onSelect(index)
            if popup.indexOfSelectedItem != appliedIndex {
                popup.selectItem(at: appliedIndex)
            }
        }
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
    @State private var openSections: Set<Int> = [0]
    @State private var downloading: Set<String> = []
    @State private var downloadErrors: [String: String] = [:]


    var body: some View {
        VStack(alignment: .leading, spacing: 11) {
            section(0, t("实时识别", "Live recognition")) {
                settingRow(t("默认源语言", "Default source language")) {
                    Picker("", selection: Binding(
                        get: { settings.translationOptions.selectedSourceLanguage },
                        set: { settings.translationBinding(\.sourceLanguage).wrappedValue = $0 }
                    )) {
                        translationLanguageChoices(settings.translationOptions.availableSourceLanguages, english: settings.language == "en-US")
                    }
                    .disabled(settings.translationOptions.availableSourceLanguages.isEmpty)
                }
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
                file(t("流式说话人模型", "Streaming speaker model"), path: Binding(
                    get: { settings.translationOptions.selectedSpeakerModel },
                    set: { settings.translationBinding(\.onlineSpeakerModel).wrappedValue = $0 }
                ), url: TranslationModelDownload.onlineSpeaker.url, type: UTType(filenameExtension: "gguf"), model: .onlineSpeaker)
            }
            section(1, t("段末复核", "Second pass")) {
                let mossReady = !settings.translationOptions.selectedMossModel.isEmpty
                settingRow(t("启用段末复核", "Enable second pass")) {
                    Toggle("", isOn: settings.translationBinding(\.enableCorrection))
                }
                settingRow(t("复核模型", "Review model")) {
                    Picker("", selection: Binding(
                        get: { mossReady && settings.translationOptions.usesMossReview ? "moss" : "x-asr" },
                        set: { settings.translationBinding(\.reviewModel).wrappedValue = $0 }
                    )) {
                        Text("X-ASR 1920 ms").tag("x-asr")
                        if mossReady { Text("MOSS 0.9B Q8").tag("moss") }
                    }
                    .disabled(!settings.translationOptions.enableCorrection)
                }
                let correction = StreamingSpeechModel.correction
                file("X-ASR 1920 ms", path: Binding(
                    get: { settings.translationOptions.selectedCorrectionModel },
                    set: { settings.translationBinding(\.correctionModel).wrappedValue = $0 }
                ), url: correction.download.url, type: .data, model: correction.download)
                file("MOSS 0.9B Q8", path: Binding(
                    get: { settings.translationOptions.selectedMossModel },
                    set: { settings.translationBinding(\.mossModel).wrappedValue = $0 }
                ), url: TranslationModelDownload.moss.url, type: UTType(filenameExtension: "gguf"), model: .moss)
            }
            section(2, t("文本翻译", "Text translation")) {
                settingRow(t("默认目标语言", "Default target language")) {
                    Picker("", selection: Binding(
                        get: { settings.translationOptions.selectedTargetLanguage },
                        set: { settings.translationBinding(\.targetLanguage).wrappedValue = $0 }
                    )) {
                        translationLanguageChoices(settings.translationOptions.availableTargetLanguages, english: settings.language == "en-US")
                    }
                    .disabled(settings.translationOptions.availableTargetLanguages.isEmpty)
                }
                settingRow(t("文本翻译模型", "Translation model")) {
                    Picker("", selection: settings.translationBinding(\.useLmtTranslation)) {
                        Text("Hy-MT3 1.8B").tag(false)
                        Text("LMT-60 0.6B").tag(true)
                    }
                }
                settingRow(t("实时译文刷新间隔", "Live translation interval")) {
                    Picker("", selection: settings.translationBinding(\.previewIntervalMs)) {
                        ForEach(TranslationOptions.previewIntervalsMs, id: \.self) { interval in
                            Text("\((Double(interval) / 1000).formatted()) \(t("秒", "s"))").tag(interval)
                        }
                    }
                }
                file("Hy-MT3 · 1.35 GB", path: Binding(
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
            section(3, t("音频与诊断", "Audio and diagnostics")) {
                settingRow(t("麦克风人声检测", "Microphone speech detection")) {
                    Picker("", selection: settings.translationBinding(\.microphoneFilter)) {
                        Text(t("温和", "Gentle")).tag("gentle")
                        Text(t("均衡", "Balanced")).tag("balanced")
                        Text(t("关闭", "Off")).tag("off")
                    }
                }
                settingRow(t("麦克风断句等待", "Microphone end-of-speech pause")) {
                    Picker("", selection: settings.translationBinding(\.microphonePause)) {
                        Text(t("标准", "Standard")).tag("normal")
                        Text(t("较长", "Longer")).tag("long")
                    }
                }
                settingRow(t("推理设备", "Compute")) {
                    Picker("", selection: settings.translationBinding(\.compute)) {
                        Text(t("自动", "Automatic")).tag("auto")
                        Text("CPU").tag("cpu")
                        Text(t("优先 GPU", "Prefer GPU")).tag("gpu")
                    }
                }
                settingRow(t("诊断日志（保存到下载）", "Diagnostic log (Downloads)")) {
                    Toggle("", isOn: settings.translationBinding(\.diagnosticsEnabled))
                }
                DisclosureGroup(t("自定义推理引擎", "Custom inference engines")) {
                    file("llama-server / llama", path: settings.translationBinding(\.llamaServer), url: TranslationOptions.llamaRelease,
                         canReset: true)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func settingRow<Control: View>(_ label: String, @ViewBuilder control: () -> Control) -> some View {
        HStack(spacing: 8) {
            Text(label)
            Spacer(minLength: 0)
            control().labelsHidden().frame(width: 180, alignment: .trailing).accessibilityLabel(label)
        }
        .frame(minHeight: 26)
    }

    private func section<Content: View>(_ index: Int, _ title: String, @ViewBuilder content: () -> Content) -> some View {
        let sectionContent = VStack(alignment: .leading, spacing: 10, content: content)
        return DisclosureGroup(isExpanded: Binding(
            get: { openSections.contains(index) },
            set: { if $0 { openSections.insert(index) } else { openSections.remove(index) } }
        )) {
            sectionContent.padding(.top, 8)
        } label: {
            Text(title).font(.system(size: 15, weight: .semibold))
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(settings.theme == "Dark" ? Color.primary.opacity(0.035) : DeskPalette.iceWhite, in: RoundedRectangle(cornerRadius: 8))
        .overlay(alignment: .top) { Rectangle().fill(Color.primary.opacity(0.12)).frame(height: 1) }
    }

    private func file(_ label: String, path: Binding<String>, url: URL, type: UTType? = nil,
                      canReset: Bool = false, model: TranslationModelDownload? = nil) -> some View {
        let busy = model.map { downloading.contains($0.name) } ?? false
        return VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                Text(label).lineLimit(2)
                Spacer(minLength: 4)
                if let model {
                    let installed = !path.wrappedValue.isEmpty
                    HStack(spacing: 5) {
                        Circle().fill(installed ? Color.green : Color.secondary).frame(width: 6, height: 6)
                        Text(installed ? t("已安装", "Installed") : t("未安装", "Missing"))
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize()
                    .help(installed ? path.wrappedValue : model.name)
                } else if canReset && !path.wrappedValue.isEmpty {
                    Button(t("恢复自动", "Reset")) { path.wrappedValue = "" }
                        .help(path.wrappedValue)
                }
                Button {
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
                } label: {
                    Text(t("选择…", "Choose…")).frame(width: 70)
                }
                .frame(width: 86)
                .disabled(busy)
                .accessibilityLabel(t("选择 \(label)", "Choose \(label)"))
                if let model {
                    Button {
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
                    } label: {
                        Text(busy ? t("下载中…", "Working…") : t("下载", "Download")).frame(width: 70)
                    }
                    .frame(width: 86)
                    .disabled(busy)
                    .accessibilityLabel(t("下载 \(label)", "Download \(label)"))
                } else {
                    Link(t("下载", "Download"), destination: url).frame(width: 86)
                }
            }
            .frame(minHeight: 26)
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
        if let label = translationLanguageLabel(id, english: english) {
            Text(label).tag(id)
        }
    }
}

private func translationLanguageLabel(_ id: String, english: Bool) -> String? {
    if id == "auto" { return english ? "Auto-detect" : "自动识别" }
    guard let language = TranslationOptions.languages.first(where: { $0.id == id }) else { return nil }
    return english ? language.id : language.chinese
}
