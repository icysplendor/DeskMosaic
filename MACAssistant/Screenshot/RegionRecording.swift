import AppKit
import CoreImage
import CoreMedia
import CoreVideo
import Darwin
import ScreenCaptureKit

struct RegionRecordingSettings: Codable, Equatable, Sendable {
    static let cacheOptions = [64, 128, 256, 512, 1024]
    static let heightOptions = [720, 1080, 1440, 2160]
    static let frameRateOptions = [15, 24, 30, 60]
    static let bitrateOptions = [0, 2, 4, 8, 12, 20, 35, 50, 65, 80, 100]

    var directory = ""
    var cacheMiB = 512
    var maxHeight = 1080
    var frameRate = 30
    var bitrateMbps = 0

    private enum CodingKeys: String, CodingKey {
        case directory, cacheMiB, maxHeight, frameRate, bitrateMbps
    }

    init() {}

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        directory = try values.decodeIfPresent(String.self, forKey: .directory) ?? ""
        if let value = try values.decodeIfPresent(Int.self, forKey: .cacheMiB),
           Self.cacheOptions.contains(value) { cacheMiB = value }
        if let value = try values.decodeIfPresent(Int.self, forKey: .maxHeight),
           Self.heightOptions.contains(value) { maxHeight = value }
        if let value = try values.decodeIfPresent(Int.self, forKey: .frameRate),
           Self.frameRateOptions.contains(value) { frameRate = value }
        if let value = try values.decodeIfPresent(Int.self, forKey: .bitrateMbps),
           Self.bitrateOptions.contains(value) { bitrateMbps = value }
    }

    var outputDirectory: URL {
        if !directory.isEmpty { return URL(fileURLWithPath: directory, isDirectory: true) }
        return FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads", isDirectory: true)
    }
}

private struct RegionRecordingError: LocalizedError {
    let errorDescription: String?
    init(_ message: String) { errorDescription = message }
}

final class RegionRecording: @unchecked Sendable {
    let width: Int
    let height: Int
    private let process = Process()
    private let input = Pipe()
    private let output = Pipe()
    private let errors = Pipe()
    private let inputQueue = DispatchQueue(label: "screenshot.recording.input", qos: .userInitiated)
    private let frameSlot = DispatchSemaphore(value: 1)
    private let errorGroup = DispatchGroup()
    private var diagnostics = Data()
    private var wroteFrame = false
    // Owned by the producer or by inputQueue, never both. frameSlot hands it back after the pipe write.
    private var reusableFrame: Data
    private let destination: URL
    private let partial: URL

    static func outputDimensions(pixelWidth: Int, pixelHeight: Int, maxHeight: Int) -> (width: Int, height: Int) {
        let scale = min(1, CGFloat(maxHeight) * 16 / 9 / CGFloat(pixelWidth),
                        CGFloat(maxHeight) / CGFloat(pixelHeight))
        return (max(2, Int(CGFloat(pixelWidth) * scale) / 2 * 2),
                max(2, Int(CGFloat(pixelHeight) * scale) / 2 * 2))
    }

    init(settings: RegionRecordingSettings, dimensions: (width: Int, height: Int),
         completion: @escaping @Sendable (Result<URL, Error>) -> Void) throws {
        width = dimensions.width
        height = dimensions.height
        reusableFrame = Data(count: dimensions.width * dimensions.height * 4)
        let executable = Bundle.main.bundleURL
            .appendingPathComponent("Contents/Helpers/ffmpeg")
        let fileManager = FileManager.default
        guard fileManager.isExecutableFile(atPath: executable.path) else {
            throw RegionRecordingError("当前构建缺少内置录屏编码器，请使用正式发布的 DMG。")
        }
        try fileManager.createDirectory(at: settings.outputDirectory, withIntermediateDirectories: true)
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        destination = settings.outputDirectory.appendingPathComponent(
            "Recording-\(formatter.string(from: Date()))-\(UUID().uuidString).mp4")
        partial = destination.appendingPathExtension("part")
        process.executableURL = executable
        // Leave more CPU headroom once the output exceeds the pixel rate of 4K at 30 fps.
        let preset = width * height * settings.frameRate > 250_000_000 ? "superfast" : "veryfast"
        process.arguments = [
            "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", "\(width)x\(height)", "-framerate", "\(settings.frameRate)",
            "-use_wallclock_as_timestamps", "1", "-i", "pipe:0", "-an", "-vsync", "vfr",
            "-c:v", "libx264", "-preset", preset, "-tune", "zerolatency", "-pix_fmt", "yuv420p",
            "-g", "\(2 * settings.frameRate)",
            settings.bitrateMbps == 0 ? "-crf" : "-b:v",
            settings.bitrateMbps == 0 ? "18" : "\(settings.bitrateMbps)M",
            "-movflags", "+empty_moov+frag_keyframe+default_base_moof", "-f", "mp4", "pipe:1"
        ]
        process.standardInput = input
        process.standardOutput = output
        process.standardError = errors
        // A failed encoder closes stdin while a background frame write may still be pending.
        _ = fcntl(input.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1)
        try process.run()

        errorGroup.enter()
        DispatchQueue.global(qos: .utility).async {
            self.diagnostics = (try? self.errors.fileHandleForReading.readToEnd()) ?? Data()
            self.errorGroup.leave()
        }
        DispatchQueue.global(qos: .utility).async {
            completion(Result { try self.saveOutput(cacheMiB: settings.cacheMiB) })
        }
    }

    func tryReserveFrame() -> Bool { frameSlot.wait(timeout: .now()) == .success }

    func takeFrameBuffer() -> Data {
        let buffer = reusableFrame
        reusableFrame = Data()
        return buffer
    }

    func releaseFrame(_ buffer: Data? = nil) {
        if let buffer { reusableFrame = buffer }
        frameSlot.signal()
    }

    func submit(_ frame: Data) {
        inputQueue.async {
            defer { self.releaseFrame(frame) }
            do {
                try self.input.fileHandleForWriting.write(contentsOf: frame)
                self.wroteFrame = true
            } catch {
                // Read the encoder exit status and stderr on the output queue.
            }
        }
    }

    func stop() {
        inputQueue.async { try? self.input.fileHandleForWriting.close() }
    }

    private func saveOutput(cacheMiB: Int) throws -> URL {
        let blockSize = 1024 * 1024
        var cache: [Data] = []
        var writing: FileHandle?
        defer { try? writing?.close() }

        func flush() throws {
            guard !cache.isEmpty else { return }
            if writing == nil {
                guard FileManager.default.createFile(atPath: partial.path, contents: nil) else {
                    throw RegionRecordingError("无法创建录屏文件。")
                }
                writing = try FileHandle(forWritingTo: partial)
            }
            for block in cache { try writing?.write(contentsOf: block) }
            cache.removeAll(keepingCapacity: true)
        }

        do {
            let pipe = output.fileHandleForReading
            var block = Data(capacity: blockSize)
            while let bytes = try pipe.read(upToCount: blockSize - block.count), !bytes.isEmpty {
                block.append(bytes)
                if block.count == blockSize {
                    cache.append(block)
                    block = Data()
                    if cache.count == cacheMiB { try flush() }
                    block.reserveCapacity(blockSize)
                }
            }
            if !block.isEmpty { cache.append(block) }

            process.waitUntilExit()
            errorGroup.wait()
            guard process.terminationStatus == 0 else {
                let details = String(decoding: diagnostics, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
                throw RegionRecordingError(details.isEmpty ? "FFmpeg 无法完成编码。" : details)
            }
            guard inputQueue.sync(execute: { wroteFrame }) else {
                throw RegionRecordingError("没有收到可编码的录制画面。")
            }
            try flush()
            guard let writing else { throw RegionRecordingError("FFmpeg 没有输出视频。") }
            try writing.close()
            try FileManager.default.moveItem(at: partial, to: destination)
            return destination
        } catch {
            if process.isRunning {
                try? output.fileHandleForReading.close()
                process.terminate()
            }
            if FileManager.default.fileExists(atPath: partial.path) {
                throw RegionRecordingError("\(error.localizedDescription)\n已写入的片段保留在 \(partial.path)")
            }
            throw error
        }
    }
}

// ScreenCaptureKit excludes the screenshot overlays. Keep only the latest
// complete frame from each intersected display, independently of encoder speed.
final class RecordingDisplay: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    let rect: CGRect
    private let lock = NSLock()
    private let context = CIContext(options: [.cacheIntermediates: false])
    private let captureQueue = DispatchQueue(label: "screenshot.recording.display", qos: .userInitiated)
    private let onFailure: @Sendable (Error) -> Void
    private var latest: CGImage?
    private var stream: SCStream?

    init(rect: CGRect, onFailure: @escaping @Sendable (Error) -> Void) {
        self.rect = rect
        self.onFailure = onFailure
    }

    func start(display: SCDisplay, excluding windows: [SCWindow], sourceRect: CGRect,
               width: Int, height: Int, fps: Int) async throws {
        let filter = SCContentFilter(display: display, excludingWindows: windows)
        let configuration = SCStreamConfiguration()
        configuration.sourceRect = sourceRect
        configuration.width = width
        configuration.height = height
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: Int32(fps))
        configuration.queueDepth = 3
        configuration.showsCursor = false
        let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: captureQueue)
        do {
            try await stream.startCapture()
            self.stream = stream
        } catch {
            try? stream.removeStreamOutput(self, type: .screen)
            throw error
        }
    }

    func stop() async {
        guard let stream else { return }
        self.stream = nil
        try? await stream.stopCapture()
        try? stream.removeStreamOutput(self, type: .screen)
    }

    func image() -> CGImage? {
        lock.lock()
        defer { lock.unlock() }
        return latest
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, let attachments = CMSampleBufferGetSampleAttachmentsArray(
            sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let status = attachments.first?[.status] as? Int else { return }
        let frame = SCFrameStatus(rawValue: status)
        guard frame == .complete || frame == .started || frame == .blank else { return }
        // ScreenCaptureKit delivers this on captureQueue. Convert the buffer here
        // so the recording UI never waits for Core Image to render a new frame.
        let image: CGImage?
        if frame != .blank, let buffer = CMSampleBufferGetImageBuffer(sampleBuffer) {
            let input = CIImage(cvPixelBuffer: buffer)
            image = context.createCGImage(input, from: input.extent)
        } else {
            image = nil
        }
        lock.lock()
        latest = image
        lock.unlock()
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) { onFailure(error) }
}
