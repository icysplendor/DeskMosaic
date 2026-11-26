import CryptoKit
import Darwin
import Foundation

struct TranslationOptions: Codable, Equatable {
    var streamingModel = ""
    var additionalStreamingModels: [String: String] = [:]
    var translationModel = ""
    var lmtTranslationModel = ""
    var useLmtTranslation = false
    var previewIntervalMs = Self.defaultPreviewIntervalMs
    var correctionModel = ""
    var enableCorrection = true
    var reviewModel = "x-asr"
    var mossModel = ""
    var diagnosticsEnabled = false
    var onlineSpeakerModel = ""
    var llamaServer = ""
    var compute = "auto"
    var sourceLanguage = "Chinese"
    var targetLanguage = "English"
    var microphoneFilter = "gentle"
    var microphonePause = "normal"
    var microphone = false
    var systemAudio = true
    var transcribeOnly = false
    var twoWay = false
    var recordAudio = true
    var showSource = true
    var sourceFontSize = 13
    var targetFontSize = 19

    private enum CodingKeys: String, CodingKey {
        case streamingModel, additionalStreamingModels, translationModel, onlineSpeakerModel, llamaServer, compute, sourceLanguage, targetLanguage
        case lmtTranslationModel, useLmtTranslation, previewIntervalMs, correctionModel, enableCorrection, diagnosticsEnabled
        case reviewModel, mossModel
        case microphone, microphoneFilter, microphonePause, systemAudio, recordAudio, showSource, sourceFontSize, targetFontSize
    }

    private enum PreviousTranslationKeys: String, CodingKey { case multiSpeaker }

    init() {}

    init(from decoder: Decoder) throws {
        let fields = try decoder.container(keyedBy: CodingKeys.self)
        streamingModel = try fields.decodeIfPresent(String.self, forKey: .streamingModel) ?? streamingModel
        additionalStreamingModels = try fields.decodeIfPresent([String: String].self, forKey: .additionalStreamingModels) ?? [:]
        translationModel = try fields.decodeIfPresent(String.self, forKey: .translationModel) ?? translationModel
        lmtTranslationModel = try fields.decodeIfPresent(String.self, forKey: .lmtTranslationModel) ?? lmtTranslationModel
        useLmtTranslation = try fields.decodeIfPresent(Bool.self, forKey: .useLmtTranslation) ?? useLmtTranslation
        if let interval = try? fields.decode(Int.self, forKey: .previewIntervalMs), Self.previewIntervalsMs.contains(interval) {
            previewIntervalMs = interval
        }
        correctionModel = try fields.decodeIfPresent(String.self, forKey: .correctionModel) ?? correctionModel
        enableCorrection = try fields.decodeIfPresent(Bool.self, forKey: .enableCorrection) ?? enableCorrection
        diagnosticsEnabled = try fields.decodeIfPresent(Bool.self, forKey: .diagnosticsEnabled) ?? diagnosticsEnabled
        mossModel = try fields.decodeIfPresent(String.self, forKey: .mossModel) ?? mossModel
        let previousMultiSpeaker = try decoder.container(keyedBy: PreviousTranslationKeys.self)
        let wasMultiSpeaker = try previousMultiSpeaker.decodeIfPresent(Bool.self, forKey: .multiSpeaker) ?? false
        reviewModel = (try fields.decodeIfPresent(String.self, forKey: .reviewModel)) ?? (wasMultiSpeaker ? "moss" : "x-asr")
        onlineSpeakerModel = try fields.decodeIfPresent(String.self, forKey: .onlineSpeakerModel) ?? onlineSpeakerModel
        llamaServer = try fields.decodeIfPresent(String.self, forKey: .llamaServer) ?? llamaServer
        compute = try fields.decodeIfPresent(String.self, forKey: .compute) ?? compute
        // Older configurations used automatic source recognition.
        sourceLanguage = try fields.decodeIfPresent(String.self, forKey: .sourceLanguage) ?? "auto"
        targetLanguage = try fields.decodeIfPresent(String.self, forKey: .targetLanguage) ?? targetLanguage
        microphoneFilter = try fields.decodeIfPresent(String.self, forKey: .microphoneFilter) ?? microphoneFilter
        microphonePause = try fields.decodeIfPresent(String.self, forKey: .microphonePause) ?? microphonePause
        microphone = try fields.decodeIfPresent(Bool.self, forKey: .microphone) ?? microphone
        systemAudio = try fields.decodeIfPresent(Bool.self, forKey: .systemAudio) ?? systemAudio
        recordAudio = try fields.decodeIfPresent(Bool.self, forKey: .recordAudio) ?? recordAudio
        showSource = try fields.decodeIfPresent(Bool.self, forKey: .showSource) ?? showSource
        let sourceSize = try fields.decodeIfPresent(Int.self, forKey: .sourceFontSize) ?? sourceFontSize
        let targetSize = try fields.decodeIfPresent(Int.self, forKey: .targetFontSize) ?? targetFontSize
        if Self.fontSizes.contains(sourceSize) { sourceFontSize = sourceSize }
        if Self.fontSizes.contains(targetSize) { targetFontSize = targetSize }
    }

    static let fontSizes = [11, 13, 15, 17, 19, 22, 26, 32]
    static let defaultPreviewIntervalMs = 1500
    static let previewIntervalsMs = Array(stride(from: 500, through: 10_000, by: 500))

    static let languages = [
        (id: "Chinese", chinese: "中文"),
        (id: "English", chinese: "英语"),
        (id: "Japanese", chinese: "日语"),
        (id: "Korean", chinese: "韩语"),
        (id: "French", chinese: "法语"),
        (id: "German", chinese: "德语"),
        (id: "Spanish", chinese: "西班牙语")
    ]

    var availableSourceLanguages: [String] {
        StreamingSpeechModel.catalog.filter { !path(for: $0).isEmpty }.flatMap(\.languages)
    }

    var usesMossReview: Bool { reviewModel == "moss" }
    var selectedMossModel: String { TranslationModelDownload.moss.resolvePath(mossModel) }
    var selectedCorrectionModel: String { StreamingSpeechModel.correction.download.resolvePath(correctionModel) }
    var selectedSpeakerModel: String { TranslationModelDownload.onlineSpeaker.resolvePath(onlineSpeakerModel) }
    var standardTranslationPath: String { TranslationModelDownload.llama.resolvePath(translationModel) }
    var lmtTranslationPath: String { TranslationModelDownload.lmt.resolvePath(lmtTranslationModel) }
    var selectedTranslationModel: String { useLmtTranslation ? lmtTranslationPath : standardTranslationPath }
    // Session-only input mode, omitted from TranslationOptions.CodingKeys.
    var importedAudio = false
    var sessionSourceLanguages: [String] {
        let live = availableSourceLanguages
        guard importedAudio, !selectedCorrectionModel.isEmpty else { return live }
        return live + StreamingSpeechModel.correction.languages.filter { !live.contains($0) }
    }
    var availableTargetLanguages: [String] { targetLanguages(for: selectedSourceLanguage) }

    func targetLanguages(for source: String) -> [String] {
        guard !selectedTranslationModel.isEmpty else { return [] }
        let hubs = ["Chinese", "English"]
        let limitToHubs = useLmtTranslation && source != "auto" && !hubs.contains(source)
        return sessionSourceLanguages.filter { id in
            Self.languages.contains { $0.id == id } && (!limitToHubs || hubs.contains(id))
        }
    }
    var canTranslateTwoWay: Bool { !selectedTranslationModel.isEmpty && modelForSource("auto") != nil }
    var selectedSourceLanguage: String { twoWay ? "auto" : Self.selectLanguage(sourceLanguage, from: sessionSourceLanguages) }
    var selectedTargetLanguage: String { Self.selectLanguage(targetLanguage, from: availableTargetLanguages, fallback: "English") }

    func path(for model: StreamingSpeechModel) -> String {
        if model.id == StreamingSpeechModel.correction.id { return selectedCorrectionModel }
        return model.download.resolvePath(model.id == "zh-en" ? streamingModel : additionalStreamingModels[model.id])
    }

    func modelForSource(_ source: String) -> StreamingSpeechModel? {
        let preferred = StreamingSpeechModel.correction
        if importedAudio, preferred.languages.contains(source), !selectedCorrectionModel.isEmpty { return preferred }
        return StreamingSpeechModel.catalog.first { $0.languages.contains(source) && !path(for: $0).isEmpty }
    }

    mutating func selectModel(_ model: StreamingSpeechModel, path: String) {
        if model.id == "zh-en" { streamingModel = path }
        else { additionalStreamingModels[model.id] = path }
    }

    static func selectLanguage(_ requested: String, from available: [String], fallback: String = "") -> String {
        if available.contains(requested) { return requested }
        return available.contains(fallback) ? fallback : (available.first ?? "")
    }

    static let llamaRelease = URL(string: "https://github.com/ggml-org/llama.cpp/releases")!
}

enum ChineseEnglishSource: String {
    case chinese = "Chinese", english = "English"

    var target: String { self == .chinese ? Self.english.rawValue : Self.chinese.rawValue }

    static func detect(_ text: String) -> Self? {
        var chinese = 0, english = 0
        var first: Self?
        var inEnglishWord = false
        // Count one vote per Han character or English word, with contractions
        // counted as one word. Digits and punctuation are neutral.
        for scalar in text.unicodeScalars {
            let code = scalar.value
            if (0x3400...0x4DBF).contains(code) || (0x4E00...0x9FFF).contains(code) ||
                (0xF900...0xFAFF).contains(code) || (0x20000...0x323AF).contains(code) {
                chinese += 1
                if first == nil { first = .chinese }
                inEnglishWord = false
            } else if (65...90).contains(code) || (97...122).contains(code) {
                if !inEnglishWord { english += 1 }
                if first == nil { first = .english }
                inEnglishWord = true
            } else if code != 0x27 && code != 0x2019 {
                inEnglishWord = false
            }
        }
        if chinese == english { return first }
        return chinese > english ? .chinese : .english
    }
}

struct TranslationModelDownload: Sendable {
    let url: URL
    let name: String
    let size: UInt64
    let sha256: String
    private let preferredURL: URL?

    init(url: URL, name: String, size: UInt64, sha256: String, preferredURL: URL? = nil) {
        self.url = url
        self.name = name
        self.size = size
        self.sha256 = sha256
        self.preferredURL = preferredURL
    }

    private var mirrorURL: URL {
        if let preferredURL { return preferredURL }
        if url.host == "huggingface.co" {
            var components = URLComponents(url: url, resolvingAgainstBaseURL: false)!
            components.host = "hf-mirror.com"
            return components.url!
        }
        return URL(string: "https://ghproxy.net/\(url.absoluteString)")!
    }

    static let llama = Self(
        url: URL(string: "https://huggingface.co/tencent/Hy-MT3-1.8B-GGUF/resolve/4c4370b31f13ccac4ac71133e3a0967c88cc792c/Hy-MT3-1.8B-Q4_K_M.gguf")!,
        name: "Hy-MT3-1.8B-Q4_K_M.gguf", size: 1_348_432_160,
        sha256: "bae6e591103654ff43ef0de7d1492f6e6df1f955505f6bb2cc52e86fc399a942",
        preferredURL: URL(string: "https://modelscope.cn/models/Tencent-Hunyuan/Hy-MT3-1.8B-GGUF/resolve/d90a5d2b61c8598c9cc313464bd74930f526fb2b/Hy-MT3-1.8B-Q4_K_M.gguf")!)

    static let lmt = Self(
        url: URL(string: "https://huggingface.co/mradermacher/LMT-60-0.6B-GGUF/resolve/86942b9f925bfccd4196d4982578b46413a1f73f/LMT-60-0.6B.Q8_0.gguf")!,
        name: "LMT-60-0.6B.Q8_0.gguf", size: 804_753_504,
        sha256: "687f437379cf9c20f37e8bb973e084ab2bb4604f01b53292392253129df7261e")

    static let moss = Self(
        url: URL(string: "https://huggingface.co/handy-computer/MOSS-Transcribe-Diarize-gguf/resolve/5780e665a3eb60694e0402ed25b6775ee587681b/MOSS-Transcribe-Diarize-Q8_0.gguf")!,
        name: "MOSS-Transcribe-Diarize-Q8_0.gguf", size: 986_900_160,
        sha256: "6bc3afe18c18ceec90f8c7e22300942db27ff0549c08f8e864f7168866445944")

    static let onlineSpeaker = Self(
        url: URL(string: "https://huggingface.co/nvidia/Nemotron-3-Diarization/resolve/6a002fd9cad5e8dfa624d97a7ee0388a7153243d/Nemotron-3-Diarization.q8_0.gguf")!,
        name: "Nemotron-3-Diarization.q8_0.gguf", size: 107_012_128,
        sha256: "08456d9e22cd9a323c0364d98375f3746d6e68507ebb705cd46438c534c7a3a1")

    static func modelDirectory() throws -> URL {
        try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: false)
            .appendingPathComponent("MACAssistant/Models", isDirectory: true)
    }

    func hasExpectedSize(_ path: String) -> Bool {
        guard path.hasPrefix("/"),
              let attributes = try? FileManager.default.attributesOfItem(atPath: path),
              let bytes = attributes[.size] as? NSNumber else { return false }
        return bytes.uint64Value == size
    }

    func resolvePath(_ configured: String?) -> String {
        if let configured, hasExpectedSize(configured) { return configured }
        guard let downloaded = try? Self.modelDirectory().appendingPathComponent(name).path,
              hasExpectedSize(downloaded) else { return "" }
        return downloaded
    }

    fileprivate func install() async throws -> String {
        let files = FileManager.default
        let directory = try Self.modelDirectory()
        try files.createDirectory(at: directory, withIntermediateDirectories: true)
        let destination = directory.appendingPathComponent(name)
        if try isVerified(destination) { return destination.path }

        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 3600
        let session = URLSession(configuration: config)
        defer { session.finishTasksAndInvalidate() }
        let staging = directory.appendingPathComponent(".\(UUID().uuidString).part")
        defer { try? files.removeItem(at: staging) }
        do {
            try await download(from: mirrorURL)
        } catch let error as URLError where error.code != .cancelled {
            try Task.checkCancellation()
            try await download(from: url)
        } catch is TranslationError {
            try Task.checkCancellation()
            try await download(from: url)
        }
        if files.fileExists(atPath: destination.path) {
            _ = try files.replaceItemAt(destination, withItemAt: staging)
        } else {
            try files.moveItem(at: staging, to: destination)
        }
        return destination.path

        func download(from source: URL) async throws {
            let (temporary, response) = try await session.download(from: source)
            defer { try? files.removeItem(at: temporary) }
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard status == 200 else {
                throw TranslationError("模型下载失败（HTTP \(status)）。", "Model download failed (HTTP \(status)).")
            }
            try files.moveItem(at: temporary, to: staging)
            guard try isVerified(staging) else {
                try files.removeItem(at: staging)
                throw TranslationError("下载文件的大小或 SHA-256 校验失败。", "The downloaded model failed size or SHA-256 verification.")
            }
        }
    }

    func isVerified(_ path: URL) throws -> Bool {
        guard FileManager.default.fileExists(atPath: path.path) else { return false }
        let attributes = try FileManager.default.attributesOfItem(atPath: path.path)
        guard (attributes[.size] as? NSNumber)?.uint64Value == size else { return false }
        let handle = try FileHandle(forReadingFrom: path)
        defer { try? handle.close() }
        var hash = SHA256()
        while let data = try handle.read(upToCount: 1024 * 1024), !data.isEmpty {
            try Task.checkCancellation()
            hash.update(data: data)
        }
        return hash.finalize().map { String(format: "%02x", $0) }.joined() == sha256
    }
}

struct StreamingSpeechModel: Identifiable, Sendable {
    let id: String
    let chinese: String
    let english: String
    let languages: [String]
    let download: TranslationModelDownload
    let archiveFiles: [String]

    static let runtimeFiles = ["encoder.int8.onnx", "decoder.onnx", "joiner.int8.onnx", "tokens.txt"]
    static let correction = Self(id: "zh-en-correction", chinese: "中英段末修正 · 1920 ms", english: "Chinese/English second pass · 1920 ms",
        languages: ["auto", "Chinese", "English"], archive: "sherpa-onnx-x-asr-1920ms-streaming-zipformer-transducer-zh-en-punct-int8-2026-06-05",
        size: 133_894_723, sha256: "fdae310438a22e4acba3ae8280320d25365e66da269fcac2a906c98c51e705e4", files: runtimeFiles)

    static let catalog = [
        Self(id: "zh-en", chinese: "中英流式 · 160 ms", english: "Chinese/English · 160 ms",
             languages: ["auto", "Chinese", "English"],
             archive: "sherpa-onnx-x-asr-160ms-streaming-zipformer-transducer-zh-en-punct-int8-2026-06-05",
             size: 133_898_007, sha256: "8a6fca056e1a342546edd78be4d50274e2c01898e7b8ae8fc336f6410319c399",
             files: runtimeFiles),
        Self(id: "ko", chinese: "韩语流式", english: "Korean streaming", languages: ["Korean"],
             archive: "sherpa-onnx-streaming-zipformer-korean-2024-06-16",
             size: 418_218_652, sha256: "e346a5882a409650472be17326237e24df7bf409db6b4a8a52e1a61422bf2500",
             files: transducerFiles("epoch-99-avg-1")),
        Self(id: "fr", chinese: "法语流式", english: "French streaming", languages: ["French"],
             archive: "sherpa-onnx-streaming-zipformer-fr-2023-04-14",
             size: 398_444_115, sha256: "77d4cbd61dcfa55fd2c14efc0ce5c6798be5931c05f35df5bab60c921950bb8c",
             files: transducerFiles("epoch-29-avg-9-with-averaged-model"))
    ]

    var archiveDirectory: String { String(download.name.dropLast(".tar.bz2".count)) }

    private init(id: String, chinese: String, english: String, languages: [String], archive: String,
                 size: UInt64, sha256: String, files: [String]) {
        self.id = id
        self.chinese = chinese
        self.english = english
        self.languages = languages
        let name = "\(archive).tar.bz2"
        download = TranslationModelDownload(
            url: URL(string: "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/\(name)")!,
            name: name, size: size, sha256: sha256)
        archiveFiles = files
    }

    private static func transducerFiles(_ checkpoint: String) -> [String] {
        ["encoder-\(checkpoint).int8.onnx", "decoder-\(checkpoint).onnx", "joiner-\(checkpoint).int8.onnx", "tokens.txt"]
    }
}

actor TranslationModelDownloads {
    static let shared = TranslationModelDownloads()
    private var active: [String: Task<String, Error>] = [:]

    func install(_ model: TranslationModelDownload) async throws -> String {
        if let existing = active[model.name] { return try await existing.value }
        let job = Task.detached { try await model.install() }
        active[model.name] = job
        defer { active[model.name] = nil }
        return try await job.value
    }
}

struct TranslationError: LocalizedError {
    let chinese: String
    let english: String

    init(_ chinese: String, _ english: String) {
        self.chinese = chinese
        self.english = english
    }

    var errorDescription: String? { "\(chinese)\n\(english)" }
}

enum CaptionReview {
    case pending, kept, revised, mossReviewed, disabled, modelMissing, languageUnsupported
    case modelPreparing, modelFailed, noReviewableText, audioUnaligned, outsideLimits, queueFull, unavailable
}

struct TranslationCaption: Identifiable, Equatable {
    let id: Int
    let microphone: Bool
    var source: String
    var translation: String?
    var sourceFinal = true
    var translationFinal = true
    var speakerID = 0
    var startMs: Int64 = 0
    var endMs: Int64 = 0
    var review: CaptionReview?
    var cacheHitPercent: Int?
    var reviewTurns: [TranslationCaption] = []
    var speakerWithinParagraph = false

    func mossTurns(_ payload: Substring) -> [TranslationCaption] {
        var turns: [TranslationCaption] = []
        for entry in payload.split(separator: "\u{1E}") {
            let fields = entry.split(separator: "\u{1F}", maxSplits: 3, omittingEmptySubsequences: false)
            guard fields.count == 4, let speaker = Int(fields[0]), let start = Int64(fields[1]),
                  let end = Int64(fields[2]), start >= 0, end > start else { return [] }
            let text = fields[3].trimmingCharacters(in: .whitespacesAndNewlines)
            guard text.contains(where: { $0.isLetter || $0.isNumber }) else { continue }
            turns.append(TranslationCaption(id: turns.count, microphone: microphone, source: text,
                translation: nil, translationFinal: false, speakerID: speaker, startMs: start, endMs: end,
                speakerWithinParagraph: true))
        }
        return turns.sorted { ($0.startMs, $0.id) < ($1.startMs, $1.id) }
    }

    var timeRange: String { "\(Self.formatTime(startMs)) – \(Self.formatTime(endMs))" }

    private static func formatTime(_ milliseconds: Int64) -> String {
        let seconds = milliseconds / 1000
        let fraction = (milliseconds / 100) % 10
        let minutes = (seconds / 60) % 60
        let clock = String(format: "%02lld:%02lld.%lld", minutes, seconds % 60, fraction)
        return seconds >= 3600 ? String(format: "%02lld:", seconds / 3600) + clock : clock
    }
}

struct TranslationResult {
    let text: String
    var cacheHitPercent: Int? = nil
}

@MainActor
final class TranslationEngine {
    private var processes: [Process] = []
    private var streamingRecognition: StreamingRecognition?

    func stop() {
        streamingRecognition?.stop()
        streamingRecognition = nil
        for process in processes where process.isRunning { process.terminate() }
        processes.removeAll()
    }

    func finishLiveInput() -> Bool { streamingRecognition?.finishLiveInput() == true }

    func run(options: TranslationOptions, capturing: () -> Void, status: @escaping (String?) -> Void,
             caption: @escaping (TranslationCaption) -> Void, recordingSaved: ((URL) -> Void)? = nil, audioFile: URL? = nil) async throws {
        let diagnostic = options.diagnosticsEnabled ? (try? TranslationDiagnosticLog()) : nil
        diagnostic?.write("session_started source_language=\(options.selectedSourceLanguage) os=\(ProcessInfo.processInfo.operatingSystemVersionString)")
        defer { diagnostic?.write("session_ended") }
        do {
            try await runSession(options: options, capturing: capturing, status: status, caption: caption,
                                 recordingSaved: recordingSaved, diagnostic: diagnostic, audioFile: audioFile)
        } catch {
            if !Task.isCancelled { diagnostic?.failure("session_failed", error) }
            throw error
        }
    }

    private func runSession(options: TranslationOptions, capturing: () -> Void, status: @escaping (String?) -> Void,
                            caption: @escaping (TranslationCaption) -> Void, recordingSaved: ((URL) -> Void)?,
                            diagnostic: TranslationDiagnosticLog?, audioFile: URL?) async throws {
        var options = options
        options.importedAudio = audioFile != nil
        guard audioFile != nil || options.microphone || options.systemAudio else {
            diagnostic?.write("preflight_no_audio_source")
            throw TranslationError("请选择音源。", "Select an audio source.")
        }
        guard let model = options.modelForSource(options.selectedSourceLanguage) else {
            diagnostic?.write("preflight_speech_model_missing")
            throw TranslationError("请在录写/翻译设置中下载或选择对应的流式语音模型。",
                                   "Install a streaming speech model in Transcribe / Translate settings.")
        }
        let recognition = StreamingRecognition()
        streamingRecognition = recognition
        defer { stop() }
        if options.transcribeOnly {
            try await runRecognition(captureStarted: capturing, translationReady: nil, translate: nil)
            return
        }

        let translationModel = options.selectedTranslationModel
        guard !translationModel.isEmpty else {
            diagnostic?.write("preflight_translation_model_missing")
            throw TranslationError("请在录写/翻译设置中下载或选择当前的文本翻译模型。",
                                   "Install the selected text translation model in Transcribe / Translate settings.")
        }
        let key = UUID().uuidString
        let settings = URLSessionConfiguration.ephemeral
        settings.connectionProxyDictionary = [:]
        settings.timeoutIntervalForRequest = 45
        let http = URLSession(configuration: settings, delegate: TranslationHTTPDelegate(), delegateQueue: nil)
        defer { http.invalidateAndCancel() }

        let bundled = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/llama-server").path
        var llamaStartup: Task<(url: String, backend: String), Error>?
        defer { llamaStartup?.cancel() }

        let selectedTarget = options.selectedTargetLanguage
        let sourceLanguage = options.selectedSourceLanguage
        try await runRecognition(captureStarted: {
            capturing()
            llamaStartup = Task {
                diagnostic?.write("translation_executable_resolving")
                let llamaPath = try executable(options.llamaServer, named: "llama-server", fallbackName: "llama")
                let llamaCommand = URL(fileURLWithPath: llamaPath).lastPathComponent == "llama" ? ["serve"] : []
                return try await startServer(http, path: llamaPath, model: translationModel,
                    arguments: ["--api-key", key, "--ctx-size", "1024", "--parallel", "1", "--no-webui", "--poll", "0"],
                    useBundledMetal: llamaPath == bundled, mode: options.compute, command: llamaCommand, diagnostic: diagnostic)
            }
        }, translationReady: {
            guard let llamaStartup else { throw CancellationError() }
            let server = try await llamaStartup.value
            try Task.checkCancellation()
            status(server.backend)
        }, translate: translateText)

        func runRecognition(captureStarted: () -> Void, translationReady: (() async throws -> Void)?,
                            translate: StreamingRecognition.Translate?) async throws {
            if let audioFile {
                guard let translationReady, let translate else {
                    try await recognition.runFile(options: options, model: model, archive: options.path(for: model), file: audioFile,
                                                  ready: { status(nil) }, caption: caption, diagnostic: diagnostic)
                    return
                }
                let (paragraphs, output) = AsyncThrowingStream<TranslationCaption, Error>.makeStream()
                captureStarted()
                let transcription = Task {
                    do {
                        try await recognition.runFile(options: options, model: model, archive: options.path(for: model),
                            file: audioFile, ready: { status(nil) }, caption: { paragraph in
                                caption(paragraph)
                                output.yield(paragraph)
                            }, diagnostic: diagnostic)
                        output.finish()
                    } catch { output.finish(throwing: error) }
                }
                do {
                    var ready = false
                    for try await var paragraph in paragraphs {
                        if !ready {
                            try await translationReady()
                            ready = true
                        }
                        let result = try await translate(paragraph.source, false) { _ in }
                        try Task.checkCancellation()
                        paragraph.translation = result.text
                        paragraph.cacheHitPercent = result.cacheHitPercent
                        paragraph.translationFinal = true
                        caption(paragraph)
                    }
                    try Task.checkCancellation()
                } catch {
                    transcription.cancel()
                    recognition.stop()
                    await transcription.value
                    throw error
                }
                await transcription.value
                return
            }
            var sessionError: Error?
            do {
                try await recognition.run(options: options, model: model, archive: options.path(for: model),
                                          record: recordingSaved != nil, capturing: captureStarted, ready: { status(nil) },
                                          translationReady: translationReady, caption: caption, translate: translate, diagnostic: diagnostic)
            } catch { sessionError = error }
            if let saved = try await recognition.finishRecording() { recordingSaved?(saved) }
            if let sessionError { throw sessionError }
        }

        func translateText(_ source: String, _ provisional: Bool, _ preview: @MainActor (String) -> Void) async throws -> TranslationResult {
            var inputLanguage = sourceLanguage
            var target = selectedTarget
            if options.twoWay {
                guard let detected = ChineseEnglishSource.detect(source) else {
                    return TranslationResult(text: provisional ? "" : source)
                }
                inputLanguage = detected.rawValue
                target = detected.target
            }
            var instruction = "Translate the following text into \(target). Output only the translation, without explanations.\n\n\(source)"
            if options.useLmtTranslation {
                if inputLanguage == "auto" {
                    inputLanguage = source.unicodeScalars.contains { (0x3400...0x9FFF).contains($0.value) } ? "Chinese" : "English"
                }
                if inputLanguage == target {
                    // In Chinese output mode, let LMT translate English mixed into Mandarin.
                    guard target == "Chinese", source.contains(where: { $0.isASCII && $0.isLetter }) else {
                        return TranslationResult(text: source)
                    }
                    inputLanguage = "English"
                }
                instruction = "Translate the following text from \(inputLanguage) into \(target):\n\(inputLanguage): \(source)\n\(target):"
            }

            guard let llamaStartup else { throw CancellationError() }
            let llama = try await llamaStartup.value
            try Task.checkCancellation()
            var prompt = URLRequest(url: URL(string: llama.url + "/v1/chat/completions")!)
            prompt.httpMethod = "POST"
            prompt.setValue("application/json", forHTTPHeaderField: "Content-Type")
            prompt.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            var body: [String: Any] = [
                "messages": [["role": "user", "content": instruction]],
                "temperature": 0.0, "stream": true, "max_tokens": provisional ? 64 : 224
            ]
            if options.useLmtTranslation { body["chat_template_kwargs"] = ["enable_thinking": false] }
            prompt.httpBody = try JSONSerialization.data(withJSONObject: body)
            let started = ProcessInfo.processInfo.systemUptime
            var timings: CompletionUpdate.Timings?
            var completed = false
            defer {
                if let diagnostic {
                    let kind = provisional ? "preview" : "final"
                    let outcome = completed ? "completed" : Task.isCancelled ? "cancelled" : "failed"
                    let elapsed = Int((ProcessInfo.processInfo.systemUptime - started) * 1000)
                    let metrics = timings?.diagnosticFields ?? "timings=unavailable"
                    diagnostic.write("translation_request kind=\(kind) outcome=\(outcome) backend=\(llama.backend) elapsed_ms=\(elapsed) \(metrics)")
                }
            }
            let (bytes, response) = try await http.bytes(for: prompt)
            guard let response = response as? HTTPURLResponse, (200..<300).contains(response.statusCode) else {
                throw TranslationError("翻译请求失败。", "Translation request failed.")
            }
            var result = ""
            var last = 0.0
            for try await line in bytes.lines {
                try Task.checkCancellation()
                if line == "data: [DONE]" {
                    completed = true
                    return TranslationResult(text: result.trimmingCharacters(in: .whitespacesAndNewlines),
                                             cacheHitPercent: timings?.cacheHitPercent)
                }
                guard line.hasPrefix("data: ") else { continue }
                let update = try JSONDecoder().decode(CompletionUpdate.self, from: Data(line.dropFirst(6).utf8))
                timings = update.timings ?? timings
                guard let text = update.choices?.first?.delta?.content, !text.isEmpty else { continue }
                result += text
                let now = ProcessInfo.processInfo.systemUptime
                if now - last >= 0.12 {
                    preview(result.trimmingCharacters(in: .whitespacesAndNewlines))
                    last = now
                }
            }
            throw TranslationError("译文生成中断。", "Translation stream ended early.")
        }
    }

    private func validate(_ path: String, label: String) throws {
        var directory: ObjCBool = false
        guard path.hasPrefix("/"), FileManager.default.fileExists(atPath: path, isDirectory: &directory),
              !directory.boolValue else {
            throw TranslationError("请在录写/翻译设置中选择 \(label)。", "Select \(label) in Transcribe / Translate settings.")
        }
    }

    private func executable(_ selected: String, named name: String, fallbackName: String? = nil) throws -> String {
        let names = [name, fallbackName].compactMap { $0 }
        let bundled = Bundle.main.bundleURL
            .appendingPathComponent("Contents/Helpers/\(name)").path
        var choices = selected.isEmpty ? [] : [selected]
        if #available(macOS 13.3, *) { choices.append(bundled) }
        choices += names.flatMap { ["/opt/homebrew/bin/\($0)", "/usr/local/bin/\($0)"] }
        for path in choices where FileManager.default.isExecutableFile(atPath: path) {
            try validate(path, label: name)
            return path
        }
        throw TranslationError("无法找到 \(name)。请使用支持录写/翻译的发布版，或配置自定义引擎。",
                               "Could not find \(name). Use a compatible release or configure a custom engine.")
    }

    private func startServer(_ http: URLSession, path: String, model: String, arguments: [String],
                             useBundledMetal: Bool, mode: String, command: [String], diagnostic: TranslationDiagnosticLog?) async throws
        -> (url: String, backend: String) {
        if mode != "cpu" {
            #if arch(arm64)
            let builtInGPU = useBundledMetal
            #else
            let builtInGPU = false
            #endif
            if builtInGPU || !useBundledMetal {
                do {
                    let device = builtInGPU ? ["--device", "MTL0"] : []
                    let address = try await launch(device + ["--gpu-layers", "all"], maxThreads: 2, backend: "gpu")
                    return (address, builtInGPU ? "Metal GPU" : "Custom")
                } catch where !Task.isCancelled {
                    diagnostic?.failure("translation_gpu_fallback", error)
                }
            }
        }
        return (try await launch(["--device", "none", "--gpu-layers", "0"], maxThreads: 4, backend: "cpu"), "CPU")

        func launch(_ device: [String], maxThreads: Int, backend: String) async throws -> String {
            try Task.checkCancellation()
            let port = try Self.localPort()
            let address = "http://127.0.0.1:\(port)"
            let process = Process()
            process.executableURL = URL(fileURLWithPath: path)
            process.currentDirectoryURL = process.executableURL?.deletingLastPathComponent()
            process.arguments = command + ["--host", "127.0.0.1", "--port", String(port), "--model", model]
                + arguments + ["--threads", String(max(1, min(maxThreads, ProcessInfo.processInfo.processorCount / 2)))] + device
            process.standardOutput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            diagnostic?.watch(process, name: "translation_\(backend)")
            diagnostic?.write("translation_starting backend=\(backend)")
            do { try process.run() }
            catch {
                diagnostic?.failure("translation_launch_failed backend=\(backend)", error)
                throw error
            }
            processes.append(process)
            do {
                let deadline = ProcessInfo.processInfo.systemUptime + 60
                var probe = URLRequest(url: URL(string: address + "/health")!)
                probe.timeoutInterval = 1
                while process.isRunning && ProcessInfo.processInfo.systemUptime < deadline {
                    try Task.checkCancellation()
                    if let (_, response) = try? await http.data(for: probe),
                       (response as? HTTPURLResponse)?.statusCode == 200, process.isRunning {
                        diagnostic?.write("translation_ready backend=\(backend)")
                        return address
                    }
                    try await Task.sleep(nanoseconds: 200_000_000)
                }
                diagnostic?.write("translation_health_failed backend=\(backend) running=\(process.isRunning)")
                let name = URL(fileURLWithPath: path).lastPathComponent
                throw TranslationError("\(name) 无法加载模型，请检查运行器和模型的兼容性。",
                                       "\(name) could not load the model. Check runtime and model compatibility.")
            } catch {
                if process.isRunning { process.terminate() }
                throw error
            }
        }
    }

    private static func localPort() throws -> UInt16 {
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw POSIXError(.EIO) }
        defer { close(descriptor) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let result = withUnsafeMutablePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { socketAddress in
                guard bind(descriptor, socketAddress, length) == 0 else { return Int32(-1) }
                return getsockname(descriptor, socketAddress, &length)
            }
        }
        guard result == 0 else { throw POSIXError(.EIO) }
        return UInt16(bigEndian: address.sin_port)
    }

    private struct CompletionUpdate: Decodable {
        struct Choice: Decodable {
            struct Delta: Decodable { let content: String? }
            let delta: Delta?
        }
        struct Timings: Decodable {
            let cache_n: Int?
            let prompt_n: Int?
            let prompt_ms: Double?
            let predicted_n: Int?
            let predicted_ms: Double?

            var cacheHitPercent: Int? {
                guard let cache_n, let prompt_n, cache_n >= 0, prompt_n >= 0 else { return nil }
                let total = Double(cache_n) + Double(prompt_n)
                return total > 0 ? Int((100 * Double(cache_n) / total).rounded()) : nil
            }

            var diagnosticFields: String {
                "cache_n=\(cache_n.map { String($0) } ?? "na") prompt_n=\(prompt_n.map { String($0) } ?? "na") " +
                "cache_hit_percent=\(cacheHitPercent.map { String($0) } ?? "na") prompt_ms=\(prompt_ms.map { String($0) } ?? "na") " +
                "predicted_n=\(predicted_n.map { String($0) } ?? "na") predicted_ms=\(predicted_ms.map { String($0) } ?? "na")"
            }
        }

        let choices: [Choice]?
        let timings: Timings?

        init(from decoder: Decoder) throws {
            let fields = try decoder.container(keyedBy: CodingKeys.self)
            choices = try fields.decodeIfPresent([Choice].self, forKey: .choices)
            timings = try? fields.decode(Timings.self, forKey: .timings)
        }

        private enum CodingKeys: String, CodingKey { case choices, timings }
    }

}

private final class TranslationHTTPDelegate: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}
