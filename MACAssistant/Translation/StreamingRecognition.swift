import AVFoundation
import Darwin
import Foundation

@MainActor
final class StreamingRecognition {
    private var capture: TranslationAudioCapture?
    private var process: Process?
    private var fileDecoder: Process?
    private var recording: TranslationRecording?

    func stop() {
        capture?.stop()
        capture = nil
        if let process, process.isRunning { process.terminate() }
        process = nil
        if let fileDecoder, fileDecoder.isRunning { fileDecoder.terminate() }
        fileDecoder = nil
    }

    func finishRecording() async throws -> URL? {
        let current = recording
        recording = nil
        return try await current?.finish()
    }

    typealias Translate = (String, Bool, @MainActor (String) -> Void) async throws -> TranslationResult

    func runFile(options: TranslationOptions, model: StreamingSpeechModel, archive: String, file: URL, ready: () -> Void,
                 caption: (TranslationCaption) -> Void, diagnostic: TranslationDiagnosticLog?) async throws {
        let (directory, arguments) = try await prepareRecognition(options: options, model: model, archive: archive,
                                                                  diagnostic: diagnostic)
        try Task.checkCancellation()
        let contents = Bundle.main.bundleURL.appendingPathComponent("Contents")
        let pipe = Pipe()
        let output = Pipe()
        let writer = pipe.fileHandleForWriting
        guard fcntl(writer.fileDescriptor, F_SETNOSIGPIPE, 1) == 0 else {
            throw TranslationError("无法初始化文件识别通道。", "Could not initialize the file recognition pipe.")
        }
        let helper = Process()
        helper.executableURL = contents.appendingPathComponent("Helpers/streaming-asr")
        helper.currentDirectoryURL = directory
        helper.arguments = arguments
        var environment = ProcessInfo.processInfo.environment
        environment["ASSISTANT_FILE_TRANSCRIPTION"] = "1"
        environment["ASSISTANT_MIC_VAD"] = "off"
        helper.environment = environment
        helper.standardInput = pipe
        helper.standardOutput = output
        let (lines, events) = AsyncThrowingStream<String, Error>.makeStream()
        let reader = StreamingLineReader(events: events)
        output.fileHandleForReading.readabilityHandler = { reader.read($0) }
        defer {
            output.fileHandleForReading.readabilityHandler = nil
            stop()
        }
        diagnostic?.watch(helper, name: "file_asr")
        try helper.run()
        process = helper

        var received = lines.makeAsyncIterator()
        let deadline = Task {
            try await Task.sleep(nanoseconds: 60_000_000_000)
            events.finish(throwing: TranslationError("加载音频识别模型超时。", "Audio recognition model load timed out."))
        }
        defer { deadline.cancel() }
        guard try await received.next() == "ready" else {
            throw TranslationError("音频文件识别引擎启动失败。", "Could not start audio file recognition.")
        }
        deadline.cancel()
        try Task.checkCancellation()

        let decoder = Process()
        let pcm = Pipe()
        decoder.executableURL = contents.appendingPathComponent("Helpers/ffmpeg")
        decoder.arguments = ["-nostdin", "-v", "error", "-i", file.path, "-map", "0:a:0",
                             "-ac", "1", "-ar", "16000", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"]
        decoder.standardOutput = pcm
        decoder.standardError = FileHandle.nullDevice
        try decoder.run()
        fileDecoder = decoder
        ready()
        let feed = Task.detached {
            defer { try? writer.close() }
            let packetBytes = 4096 * MemoryLayout<Float>.size
            var buffer = Data()
            var receivedAudio = false
            while let bytes = try pcm.fileHandleForReading.read(upToCount: packetBytes - buffer.count), !bytes.isEmpty {
                try Task.checkCancellation()
                buffer.append(bytes)
                if buffer.count == packetBytes {
                    try send(buffer)
                    buffer.removeAll(keepingCapacity: true)
                    receivedAudio = true
                }
            }
            if !buffer.isEmpty {
                try send(buffer)
                receivedAudio = true
            }
            decoder.waitUntilExit()
            guard receivedAudio, decoder.terminationStatus == 0 else {
                throw TranslationError("无法读取选定的音频文件。", "Could not read the selected audio file.")
            }
            let silence = Data(count: packetBytes)
            for _ in 0..<8 { try send(silence) }

            func send(_ samples: Data) throws {
                guard samples.count % MemoryLayout<Float>.size == 0 else {
                    throw TranslationError("音频解码数据不完整。", "Decoded audio packet is incomplete.")
                }
                let header: [UInt32] = [0, 16_000, UInt32(samples.count / MemoryLayout<Float>.size)]
                try writer.write(contentsOf: header.withUnsafeBytes { Data($0) })
                try writer.write(contentsOf: samples)
            }
        }
        defer { feed.cancel() }

        var paragraphCount = 0
        while let line = try await received.next() {
            try Task.checkCancellation()
            let fields = line.split(separator: "\t", maxSplits: 5, omittingEmptySubsequences: false)
            guard fields.count == 6, fields[0] == "0", fields[1] == "1",
                  let speakerID = Int(fields[2]), let start = Int64(fields[3]), let end = Int64(fields[4]),
                  fields[5].contains(where: { $0.isLetter || $0.isNumber }) else { continue }
            paragraphCount += 1
            let item = TranslationCaption(id: paragraphCount, microphone: false,
                source: String(fields[5]), translation: nil, translationFinal: false,
                speakerID: speakerID, startMs: start, endMs: end)
            caption(item)
        }
        try Task.checkCancellation()
        try await feed.value
        let exitCode = await Task.detached { helper.waitUntilExit(); return helper.terminationStatus }.value
        guard exitCode == 0 else { throw TranslationError("音频文件识别失败。", "Audio file recognition failed.") }
        guard paragraphCount != 0 else {
            throw TranslationError("音频中未识别到语音。", "No speech was recognized in the audio.")
        }
    }

    func run(options: TranslationOptions, model: StreamingSpeechModel, archive: String, record: Bool, capturing: () -> Void, ready: () -> Void,
             translationReady: (() async throws -> Void)?, caption: @escaping (TranslationCaption) -> Void,
             translate: Translate?, diagnostic: TranslationDiagnosticLog?) async throws {
        diagnostic?.write("speech_model_selected model=\(model.id)")
        let folder = Bundle.main.bundleURL.appendingPathComponent("Contents")
        let helperURL = folder.appendingPathComponent("Helpers/streaming-asr")
        let (lines, events) = AsyncThrowingStream<String, Error>.makeStream()
        defer {
            stop()
            events.finish()
        }
        try Task.checkCancellation()
        let recorder = try record ? TranslationRecording(
            executable: helperURL,
            stereo: options.systemAudio && options.microphone) : nil
        recording = recorder
        let audio = StreamingAudioBuffer()
        let send: (Data) -> Void = { data in
            if let recorder, !recorder.append(data) {
                events.finish(throwing: TranslationRecordingError(message: TranslationError(
                    "录音写入速度跟不上。", "The audio recorder could not keep up.")))
            }
            if !audio.append(data) {
                events.finish(throwing: TranslationError("暂存音频已满，语音识别跟不上。", "The audio buffer is full; recognition could not keep up."))
            }
        }
        let system = StreamingAudioPacketizer(source: 0, emit: send)
        let microphone = StreamingAudioPacketizer(source: 1, emit: send)
        let capture = TranslationAudioCapture(system: system.append, microphone: microphone.append) {
            diagnostic?.failure("audio_capture_failed", $0)
            events.finish(throwing: $0)
        }
        self.capture = capture

        let timer = DispatchSource.makeTimerSource(queue: DispatchQueue(label: "assistant.translation.streaming"))
        timer.schedule(deadline: .now() + .milliseconds(100), repeating: .milliseconds(100))
        timer.setEventHandler { system.tick(); microphone.tick() }
        timer.resume()
        defer {
            timer.cancel()
            audio.finish()
        }
        diagnostic?.write("audio_capture_starting system=\(options.systemAudio) microphone=\(options.microphone)")
        do { try await capture.start(system: options.systemAudio, microphone: options.microphone) }
        catch {
            diagnostic?.failure("audio_capture_start_failed", error)
            throw error
        }
        try Task.checkCancellation()
        diagnostic?.write("audio_capture_ready")
        capturing()

        let (model, arguments) = try await prepareRecognition(options: options, model: model, archive: archive,
                                                              diagnostic: diagnostic)
        let helper = Process()
        helper.executableURL = helperURL
        helper.currentDirectoryURL = model
        helper.arguments = arguments
        let correction = StreamingSpeechModel.correction
        let correctionArchive = options.selectedCorrectionModel
        let correctParagraphs = options.enableCorrection && !correctionArchive.isEmpty &&
            correction.languages.contains(options.selectedSourceLanguage)
        diagnostic?.write("review_configuration enabled=\(options.enableCorrection) archive_found=\(!correctionArchive.isEmpty) eligible=\(correctParagraphs)")
        if correctParagraphs {
            helper.arguments?.append("--correction")
            helper.arguments?.append("../../\(correction.download.sha256)/\(correction.archiveDirectory)")
        }
        let detector = folder.appendingPathComponent("Resources/silero_vad.onnx")
        var environment = ProcessInfo.processInfo.environment
        environment["ASSISTANT_MIC_VAD"] = options.microphone ? options.microphoneFilter : "off"
        environment["ASSISTANT_MIC_PAUSE"] = options.microphonePause
        environment["ASSISTANT_MIC_VAD_MODEL"] = FileManager.default.isReadableFile(atPath: detector.path) ? detector.path : nil
        helper.environment = environment
        let input = Pipe()
        let output = Pipe()
        helper.standardInput = input
        helper.standardOutput = output
        guard fcntl(input.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1) == 0 else {
            throw TranslationError("无法初始化流式语音通道。", "Could not initialize the streaming audio pipe.")
        }
        let reader = StreamingLineReader(events: events)
        let outputHandle = output.fileHandleForReading
        outputHandle.readabilityHandler = { reader.read($0) }
        defer { outputHandle.readabilityHandler = nil }
        diagnostic?.watch(helper, name: "streaming_asr")
        diagnostic?.write("speech_helper_checks executable=\(FileManager.default.isExecutableFile(atPath: helperURL.path))")
        do { try helper.run() }
        catch {
            diagnostic?.failure("speech_helper_launch_failed", error)
            throw error
        }
        process = helper
        diagnostic?.write("speech_helper_waiting_for_ready")

        let deadline = Task {
            try await Task.sleep(nanoseconds: 60_000_000_000)
            diagnostic?.write("speech_helper_ready_timeout")
            events.finish(throwing: TranslationError("加载中英流式模型超时。", "Streaming ASR model load timed out."))
        }
        defer { deadline.cancel() }
        var received = lines.makeAsyncIterator()
        guard try await received.next() == "ready" else {
            diagnostic?.write("speech_helper_invalid_or_missing_ready")
            throw TranslationError("中英流式识别启动失败。", "Streaming ASR failed to start.")
        }
        deadline.cancel()
        diagnostic?.write("fast_engine_ready")
        let replayBoundary: [UInt32] = [.max, 2, 0]
        guard audio.append(replayBoundary.withUnsafeBytes { Data($0) }) else {
            throw TranslationError("暂存音频已满，语音识别跟不上。", "The audio buffer is full; recognition could not keep up.")
        }

        let correctionPreparation = Task.detached(priority: .utility) {
            guard correctParagraphs else { return }
            let prepared: Bool
            do {
                _ = try Self.prepareModel(correction, archive: correctionArchive, diagnostic: diagnostic)
                prepared = true
            } catch {
                let cause = error as NSError
                diagnostic?.write("review_preparation_failed domain=\(cause.domain) code=\(cause.code)")
                prepared = false
            }
            guard !Task.isCancelled else { return }
            let header: [UInt32] = [.max, prepared ? 1 : 0, 0]
            if !audio.append(header.withUnsafeBytes { Data($0) }) {
                diagnostic?.write("review_preparation_header_buffer_full")
            }
        }
        defer { correctionPreparation.cancel() }
        let writerHandle = input.fileHandleForWriting
        let writer = Task.detached {
            defer { try? writerHandle.close() }
            do {
                for await frame in audio.frames {
                    audio.consumed(frame.count)
                    try writerHandle.write(contentsOf: frame)
                }
            } catch { events.finish(throwing: error) }
        }
        defer { writer.cancel() }
        ready()

        let (updates, signal) = AsyncStream<Void>.makeStream(bufferingPolicy: .bufferingNewest(1))
        var drafts = [TranslationCaption?](repeating: nil, count: 2)
        var stableSentences = [String?](repeating: nil, count: 2)
        var pendingFinals: [TranslationCaption] = []
        var pendingCorrections: [Int: TranslationCaption] = [:]
        var nextID = 0
        var draftRequest: Task<TranslationResult, Error>?
        var draftInput: (id: Int, source: String)?
        var draftHasOutput = false

        let translator = Task {
            guard let translationReady else { return }
            do { try await translationReady() }
            catch {
                if !Task.isCancelled { events.finish(throwing: error) }
                return
            }
            var updated = updates.makeAsyncIterator()
            var submitted = [TranslationCaption?](repeating: nil, count: 2)
            var completed = [TranslationCaption?](repeating: nil, count: 2)
            var lastRequest = [Double](repeating: -1, count: 2)
            while !Task.isCancelled {
                if !pendingFinals.isEmpty {
                    var final = pendingFinals.removeFirst()
                    let translation = try await translateSafely(final.source, provisional: false) { _ in }
                    try Task.checkCancellation()
                    // On failure the visible preview stays provisional.
                    if !translation.text.isEmpty || (final.translation ?? "").isEmpty {
                        final.translation = translation.text
                        final.cacheHitPercent = translation.cacheHitPercent
                        final.translationFinal = true
                        caption(final)
                    }
                    continue
                }
                let now = ProcessInfo.processInfo.systemUptime
                var nextDeadline: Double?
                var chosen: Int?
                for index in drafts.indices {
                    guard let draft = drafts[index], submitted[index]?.id != draft.id ||
                        submitted[index]?.source != draft.source else { continue }
                    let newSentence = stableSentences[index].map { sentence in
                        submitted[index]?.id != draft.id || submitted[index]?.source.hasPrefix(sentence) != true
                    } ?? false
                    let deadline = lastRequest[index] + 1
                    if newSentence || now >= deadline {
                        if chosen.map({ lastRequest[index] < lastRequest[$0] }) ?? true { chosen = index }
                    } else {
                        nextDeadline = min(nextDeadline ?? deadline, deadline)
                    }
                }
                guard let index = chosen, let request = drafts[index] else {
                    // A new sentence or a pending final can wake us before the deadline.
                    let wake = nextDeadline.map { deadline in
                        Task {
                            let delay = max(0, deadline - ProcessInfo.processInfo.systemUptime)
                            try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
                            if !Task.isCancelled { signal.yield(()) }
                        }
                    }
                    _ = await updated.next()
                    wake?.cancel()
                    continue
                }
                var anchor = completed[index]
                var source = request.source
                if let saved = anchor, saved.id == request.id, source.hasPrefix(saved.source) {
                    let remainder = source.dropFirst(saved.source.count).trimmingCharacters(in: .whitespacesAndNewlines)
                    // Keep a row in one language when a new clause changes the two-way direction.
                    if !remainder.isEmpty, !options.twoWay || ChineseEnglishSource.detect(remainder) == ChineseEnglishSource.detect(saved.source) {
                        source = remainder
                    } else {
                        anchor = nil
                    }
                } else {
                    anchor = nil
                }
                completed[index] = anchor
                lastRequest[index] = now
                draftInput = (request.id, request.source)
                draftHasOutput = false
                let job = Task {
                    try await translateSafely(source, provisional: true) { preview($0, complete: false) }
                }
                draftRequest = job
                let result = try? await job.value
                draftRequest = nil
                draftInput = nil
                try Task.checkCancellation()
                submitted[index] = request
                if let result { preview(result.text, complete: true, cacheHitPercent: result.cacheHitPercent) }

                @MainActor func preview(_ text: String, complete: Bool, cacheHitPercent: Int? = nil) {
                    guard !Task.isCancelled, var latest = drafts[index], latest.id == request.id, !text.isEmpty else { return }
                    draftHasOutput = true
                    if !complete { cancelStaleDraft(latest) }
                    guard Self.stillRepresentsSource(request.source, latest.source) else { return }
                    var previewText = text
                    if let prefix = anchor?.translation, let last = prefix.last {
                        previewText = prefix + ("。！？".contains(last) ? "" : " ") + text
                    }
                    let visible = latest.translation ?? ""
                    // Publish a rephrased open sentence once, after its preview finishes.
                    if complete || (previewText != visible && previewText.hasPrefix(visible)) {
                        latest.translation = previewText
                        latest.cacheHitPercent = cacheHitPercent
                        if latest != drafts[index] {
                            drafts[index] = latest
                            caption(latest)
                        }
                    }
                    if complete, Self.endsSentence(request.source), Self.endsSentence(previewText) {
                        var saved = request
                        saved.translation = previewText
                        completed[index] = saved
                    }
                }
            }
        }
        defer {
            draftRequest?.cancel()
            translator.cancel()
            signal.finish()
        }

        while let line = try await received.next() {
            try Task.checkCancellation()
            if line.hasPrefix("A\t") {
                diagnostic?.write("audio_\(line.dropFirst(2))")
                if line == "A\tinvalid_audio_packet" {
                    throw TranslationError("识别引擎收到无效音频。", "The recognition engine received invalid audio.")
                }
                continue
            }
            if line.hasPrefix("D\t") {
                diagnostic?.write("review_\(line.dropFirst(2))")
                continue
            }
            let fields = line.split(separator: "\t", maxSplits: 5, omittingEmptySubsequences: false)
            guard fields.count == 6, let index = Int(fields[0]), drafts.indices.contains(index),
                  let speakerID = Int(fields[2]), let startMs = Int64(fields[3]), let endMs = Int64(fields[4]),
                  startMs >= 0, endMs >= startMs else { continue }
            let rawSource = fields[5]
            let source = rawSource.trimmingCharacters(in: .whitespacesAndNewlines)
            let kind = fields[1]
            let correctionID = kind.first.map { "FRE".contains($0) } == true ? Int(kind.dropFirst()) : nil
            if let correctionID, kind.first != "F" {
                guard var corrected = pendingCorrections.removeValue(forKey: correctionID) else { continue }
                corrected.review = kind.first == "E" ? .modelFailed : .kept
                if source.contains(where: { $0.isLetter || $0.isNumber }) {
                    if source != corrected.source {
                        corrected.source = source
                        corrected.review = .revised
                    }
                    corrected.startMs = startMs
                    corrected.endMs = endMs
                }
                diagnostic?.write("paragraph id=\(corrected.id) range_ms=\(startMs)-\(endMs) review=\(String(describing: corrected.review))")
                caption(corrected)
                if translationReady != nil { queueFinal(corrected) }
                signal.yield(())
                continue
            }
            let review: CaptionReview?
            switch kind {
            case "1" where !options.enableCorrection: review = .disabled
            case "1" where !correction.languages.contains(options.selectedSourceLanguage): review = .languageUnsupported
            case "1" where correctionArchive.isEmpty: review = .modelMissing
            case "1": review = .unavailable
            case "S1": review = .modelPreparing
            case "S2": review = .modelFailed
            case "S3": review = .noReviewableText
            case "S4": review = .audioUnaligned
            case "S5": review = .outsideLimits
            case "S6": review = .queueFull
            default: review = correctionID == nil ? nil : .pending
            }
            let final = review != nil
            let previous = drafts[index]
            guard source.contains(where: { $0.isLetter || $0.isNumber }) else { continue }
            let oldStableSentence = stableSentences[index]
            stableSentences[index] = nil
            if !final, kind.first == "0", let bytes = Int(kind.dropFirst()), bytes > 0, bytes <= rawSource.utf8.count {
                stableSentences[index] = String(bytes: rawSource.utf8.prefix(bytes), encoding: .utf8)?
                    .trimmingCharacters(in: .whitespacesAndNewlines)
            }
            guard final || stableSentences[index] != oldStableSentence ||
                    source != previous?.source || speakerID != previous?.speakerID ||
                    startMs != previous?.startMs || endMs != previous?.endMs else { continue }
            if previous == nil { nextID += 1 }
            // A shorter final source still uses the visible preview until its replacement arrives.
            let item = TranslationCaption(id: previous?.id ?? nextID, microphone: index == 1, source: source,
                                          translation: previous?.translation, sourceFinal: final, translationFinal: false, speakerID: speakerID,
                                          startMs: startMs, endMs: endMs, review: review,
                                          cacheHitPercent: previous?.cacheHitPercent)
            drafts[index] = final ? nil : item
            if let review { diagnostic?.write("paragraph id=\(item.id) range_ms=\(startMs)-\(endMs) review=\(review)") }
            caption(item)
            if let correctionID {
                pendingCorrections[correctionID] = item
                draftRequest?.cancel()
            } else if translationReady != nil {
                if final { queueFinal(item) }
                else { cancelStaleDraft(item) }
            }
            signal.yield(())
        }
        try Task.checkCancellation()
        throw TranslationError("中英流式识别已意外退出。", "Streaming ASR exited unexpectedly.")

        func queueFinal(_ item: TranslationCaption) {
            if pendingFinals.count == 256 {
                var dropped = pendingFinals.removeFirst()
                if (dropped.translation ?? "").isEmpty {
                    dropped.translation = ""
                    dropped.translationFinal = true
                    caption(dropped)
                }
            }
            pendingFinals.append(item)
            draftRequest?.cancel()
        }

        func cancelStaleDraft(_ current: TranslationCaption) {
            guard draftHasOutput, let input = draftInput, current.id == input.id else { return }
            if !Self.stillRepresentsSource(input.source, current.source) { draftRequest?.cancel() }
        }

        func translateSafely(_ source: String, provisional: Bool, preview: @escaping @MainActor (String) -> Void) async throws -> TranslationResult {
            guard let translate else { return TranslationResult(text: "") }
            do { return try await translate(source, provisional, preview) }
            catch {
                try Task.checkCancellation()
                return TranslationResult(text: "")
            }
        }
    }

    private static func stillRepresentsSource(_ input: String, _ current: String) -> Bool {
        guard current.hasPrefix(input), let last = input.last else { return false }
        guard let next = current.dropFirst(input.count).first else { return true }
        // The latest text may still be completing the same word or number.
        if last.isASCII, last.isLetter, (next.isASCII && next.isLetter) || "'’".contains(next) { return false }
        return !last.isASCII || !last.isNumber || !((next.isASCII && next.isNumber) || ".,".contains(next))
    }

    private static func endsSentence(_ text: String) -> Bool {
        guard text.count >= 2, let last = text.last else { return false }
        if "。！？!?".contains(last) { return true }
        // Avoid locking at a decimal point, ellipsis, or short abbreviation.
        guard last == ".", text.count >= 4 else { return false }
        return text.dropLast().suffix(3).allSatisfy { $0.isASCII && $0.isLetter && $0.isLowercase }
    }

    private func prepareRecognition(options: TranslationOptions, model: StreamingSpeechModel, archive: String,
                                    diagnostic: TranslationDiagnosticLog?) async throws -> (URL, [String]) {
        let speaker = options.selectedSpeakerModel
        let speakerFile = "speaker.gguf"
        let preparation = Task.detached {
            let directory = try Self.prepareModel(model, archive: archive, diagnostic: diagnostic)
            if !speaker.isEmpty {
                diagnostic?.write("speaker_cache_preparing")
                try Self.prepareSpeaker(speaker, destination: directory.appendingPathComponent(speakerFile))
                diagnostic?.write("speaker_cache_ready")
            }
            return directory
        }
        let directory = try await withTaskCancellationHandler(operation: { try await preparation.value },
                                                          onCancel: { preparation.cancel() })
        try Task.checkCancellation()
        let folder = Bundle.main.bundleURL.appendingPathComponent("Contents")
        var arguments = ["1", folder.appendingPathComponent("Frameworks/libsherpa-onnx-c-api.dylib").path]
        if !speaker.isEmpty {
            arguments += [speakerFile, folder.appendingPathComponent("Frameworks/Diarization/libnemo_speech_asr_c.dylib").path,
                          options.compute == "cpu" ? "cpu" : "auto"]
        }
        return (directory, arguments)
    }

    private nonisolated static func prepareSpeaker(_ source: String, destination: URL) throws {
        let model = TranslationModelDownload.onlineSpeaker
        let files = FileManager.default
        let directory = destination.deletingLastPathComponent()
        if model.hasExpectedSize(destination.path) { return }
        let temporary = directory.appendingPathComponent(".speaker-\(UUID().uuidString).part")
        defer { try? files.removeItem(at: temporary) }
        try files.copyItem(at: URL(fileURLWithPath: source), to: temporary)
        guard try model.isVerified(temporary) else {
            throw TranslationError("说话人模型校验失败。", "Speaker model verification failed.")
        }
        if files.fileExists(atPath: destination.path) {
            _ = try files.replaceItemAt(destination, withItemAt: temporary)
        } else {
            do { try files.moveItem(at: temporary, to: destination) }
            catch where model.hasExpectedSize(destination.path) { }
        }
    }

    private nonisolated static func prepareModel(_ model: StreamingSpeechModel, archive: String,
                                                 diagnostic: TranslationDiagnosticLog?) throws -> URL {
        let files = FileManager.default
        let release = model.download
        let modelName = model.archiveDirectory
        let root = try TranslationModelDownload.modelDirectory()
        let destination = root.appendingPathComponent(release.sha256).appendingPathComponent(modelName)
        func complete(_ folder: URL) -> Bool {
            StreamingSpeechModel.runtimeFiles.allSatisfy { files.fileExists(atPath: folder.appendingPathComponent($0).path) }
        }
        if complete(destination) {
            diagnostic?.write("model=\(model.id) cached")
            return destination
        }
        diagnostic?.write("model=\(model.id) verifying_archive")
        guard try release.isVerified(URL(fileURLWithPath: archive)) else {
            throw TranslationError("请选择官方 X-ASR 模型压缩包，或在设置中重新下载。", "Select or download the official X-ASR archive.")
        }
        diagnostic?.write("model=\(model.id) extracting")
        let staging = root.appendingPathComponent(".extract-\(UUID().uuidString)")
        try files.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? files.removeItem(at: staging) }
        let tar = Process()
        tar.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
        tar.currentDirectoryURL = staging
        tar.arguments = ["-xf", archive] + model.archiveFiles.map { "\(modelName)/\($0)" }
        try Task.checkCancellation()
        try tar.run()
        tar.waitUntilExit()
        try Task.checkCancellation()
        diagnostic?.write("model=\(model.id) extraction_exit=\(tar.terminationStatus)")
        let unpacked = staging.appendingPathComponent(modelName)
        guard tar.terminationStatus == 0,
              model.archiveFiles.allSatisfy({ files.fileExists(atPath: unpacked.appendingPathComponent($0).path) }) else {
            throw TranslationError("无法解压流式模型。", "Could not extract the streaming model.")
        }
        for (original, runtime) in zip(model.archiveFiles, StreamingSpeechModel.runtimeFiles) where original != runtime {
            try files.moveItem(at: unpacked.appendingPathComponent(original), to: unpacked.appendingPathComponent(runtime))
        }
        try files.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        do { try files.moveItem(at: unpacked, to: destination) }
        catch where complete(destination) { }
        diagnostic?.write("model=\(model.id) installed")
        return destination
    }
}

final class TranslationDiagnosticLog: @unchecked Sendable {
    private let output: FileHandle
    private let lock = NSLock()

    init() throws {
        let files = FileManager.default
        let directory = try files.url(for: .downloadsDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        let name = "Translation-diagnostics-\(Int(Date().timeIntervalSince1970))-\(UUID()).log"
        let url = directory.appendingPathComponent(name)
        try Data().write(to: url, options: .withoutOverwriting)
        output = try FileHandle(forWritingTo: url)
    }

    func failure(_ stage: String, _ error: Error) {
        let cause = error as NSError
        write("\(stage) domain=\(cause.domain) code=\(cause.code)")
    }

    func watch(_ process: Process, name: String) {
        let pipe = Pipe()
        process.standardError = pipe
        process.environment = (process.environment ?? ProcessInfo.processInfo.environment).merging(
            ["MACASSISTANT_TRANSLATION_DIAGNOSTICS": "1"], uniquingKeysWith: { _, value in value })
        let stderr = TranslationStderrDiagnostics(log: self, process: name)
        pipe.fileHandleForReading.readabilityHandler = { stderr.read($0) }
        process.terminationHandler = { [self] task in
            write("\(name)_exited reason=\(task.terminationReason.rawValue) status=\(task.terminationStatus)")
        }
    }

    deinit { try? output.close() }

    func write(_ message: String) {
        lock.lock()
        defer { lock.unlock() }
        let line = "\(Date().ISO8601Format()) \(message)\n"
        try? output.write(contentsOf: Data(line.utf8))
    }
}

// The child can print paths or model text. Write only stage and error identifiers.
private final class TranslationStderrDiagnostics: @unchecked Sendable {
    private let log: TranslationDiagnosticLog
    private let process: String
    private var pending = Data()
    private var reported: Set<String> = []

    init(log: TranslationDiagnosticLog, process: String) {
        self.log = log
        self.process = process
    }

    func read(_ handle: FileHandle) {
        let bytes = handle.availableData
        if bytes.isEmpty {
            classify(pending)
            handle.readabilityHandler = nil
            return
        }
        pending.append(bytes)
        while let end = pending.firstIndex(of: 10) {
            classify(pending.prefix(upTo: end))
            pending.removeSubrange(...end)
        }
        if pending.count > 4096 { pending.removeAll(keepingCapacity: true) }
    }

    private func classify(_ line: Data) {
        let text = String(decoding: line, as: UTF8.self)
        let lower = text.lowercased()
        let event: String
        if text.hasPrefix("asr_startup="), text.utf8.count < 96,
           text.dropFirst(12).utf8.allSatisfy({ (65...90).contains($0) || (97...122).contains($0) || $0 == 95 }) {
            event = text
        } else if lower.contains("incompatible architecture") || lower.contains("wrong architecture") {
            event = "incompatible_architecture"
        } else if lower.contains("code signature") || lower.contains("library validation") {
            event = "code_signature_or_library_validation"
        } else if lower.contains("library not loaded") || lower.contains("image not found") {
            event = "missing_dynamic_library"
        } else if lower.contains("symbol not found") {
            event = "missing_dynamic_symbol"
        } else if lower.contains("no such file") {
            event = "missing_file"
        } else if lower.contains("permission denied") {
            event = "permission_denied"
        } else {
            return
        }
        if reported.insert(event).inserted { log.write("\(process) \(event)") }
    }
}

private final class StreamingLineReader: @unchecked Sendable {
    private let events: AsyncThrowingStream<String, Error>.Continuation
    private var pending = Data()

    init(events: AsyncThrowingStream<String, Error>.Continuation) { self.events = events }

    func read(_ handle: FileHandle) {
        let bytes = handle.availableData
        if bytes.isEmpty {
            handle.readabilityHandler = nil
            events.finish()
            return
        }
        pending.append(bytes)
        while let end = pending.firstIndex(of: 10) {
            events.yield(String(decoding: pending[..<end], as: UTF8.self))
            pending.removeSubrange(...end)
        }
    }
}

private final class StreamingAudioBuffer: @unchecked Sendable {
    let frames: AsyncStream<Data>
    private let continuation: AsyncStream<Data>.Continuation
    private let lock = NSLock()
    private var bufferedBytes = 0

    init() {
        let (stream, sink) = AsyncStream<Data>.makeStream(bufferingPolicy: .bufferingOldest(16384))
        frames = stream
        continuation = sink
    }

    func append(_ frame: Data) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard bufferedBytes + frame.count <= 64 * 1024 * 1024 else { return false }
        switch continuation.yield(frame) {
        case .enqueued:
            bufferedBytes += frame.count
            return true
        case .terminated: return true
        default: return false
        }
    }

    func consumed(_ bytes: Int) {
        lock.lock()
        bufferedBytes -= bytes
        lock.unlock()
    }

    func finish() { continuation.finish() }
}

private final class StreamingAudioPacketizer: @unchecked Sendable {
    private let source: UInt32
    private let emit: (Data) -> Void
    private let lock = NSLock()
    private var rate: UInt32 = 0
    private var lastPacket = 0.0

    init(source: UInt32, emit: @escaping (Data) -> Void) {
        self.source = source
        self.emit = emit
    }

    func append(_ buffer: AVAudioPCMBuffer) {
        guard let channels = buffer.floatChannelData, (8_000...384_000).contains(buffer.format.sampleRate),
              buffer.format.channelCount > 0 else { return }
        lock.lock()
        defer { lock.unlock() }
        rate = UInt32(buffer.format.sampleRate)
        lastPacket = ProcessInfo.processInfo.systemUptime
        let channelCount = Int(buffer.format.channelCount)
        for offset in stride(from: 0, to: Int(buffer.frameLength), by: 32768) {
            let count = min(32768, Int(buffer.frameLength) - offset)
            var samples = [Float](repeating: 0, count: count)
            for index in samples.indices {
                var sum: Float = 0
                for channel in 0..<channelCount { sum += channels[channel][(offset + index) * buffer.stride] }
                samples[index] = sum.isFinite ? min(1, max(-1, sum / Float(channelCount))) : 0
            }
            send(samples)
        }
    }

    func tick() {
        lock.lock()
        defer { lock.unlock() }
        let now = ProcessInfo.processInfo.systemUptime
        guard rate > 0, now - lastPacket >= 0.15 else { return }
        lastPacket = now
        let silence = [Float](repeating: 0, count: Int(rate / 20))
        send(silence)
        send(silence)
    }

    private func send(_ samples: [Float]) {
        let header = [source, rate, UInt32(samples.count)].map { $0.littleEndian }
        var packet = header.withUnsafeBytes { Data($0) }
        samples.withUnsafeBytes { packet.append(contentsOf: $0) }
        emit(packet)
    }
}
