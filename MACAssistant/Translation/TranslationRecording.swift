import Darwin
import Foundation

struct TranslationRecordingError: LocalizedError {
    let message: TranslationError
    var errorDescription: String? { message.errorDescription }
}

final class TranslationRecording: @unchecked Sendable {
    private let packets: AsyncStream<Data>.Continuation
    private let worker: Task<URL?, Error>
    private let process: Process

    init(executable: URL, stereo: Bool, outputDirectory: URL? = nil) throws {
        let files = FileManager.default
        let directory = try outputDirectory ?? files.url(for: .downloadsDirectory, in: .userDomainMask,
                                                         appropriateFor: nil, create: true)
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        let name = "Live-translation-\(formatter.string(from: Date()))-\(UUID().uuidString)"
        let destination = directory.appendingPathComponent(name + ".wav")
        let temporary = directory.appendingPathComponent("." + name + ".part")
        let child = Process()
        child.executableURL = executable
        child.arguments = ["--record", stereo ? "2" : "1"]
        var environment = ProcessInfo.processInfo.environment
        environment["ASSISTANT_RECORDING_PATH"] = temporary.path
        child.environment = environment
        let pipe = Pipe()
        let handle = pipe.fileHandleForWriting
        guard fcntl(handle.fileDescriptor, F_SETNOSIGPIPE, 1) == 0 else {
            throw TranslationRecordingError(message: TranslationError("无法建立录音通道。", "Could not open the recording pipe."))
        }
        child.standardInput = pipe
        try child.run()

        let (stream, sink) = AsyncStream<Data>.makeStream(bufferingPolicy: .bufferingOldest(600))
        packets = sink
        process = child
        worker = Task.detached {
            defer { sink.finish() }
            do {
                do {
                    for await packet in stream { try handle.write(contentsOf: packet) }
                    try handle.close()
                } catch {
                    try? handle.close()
                    throw error
                }
                child.waitUntilExit()
                guard child.terminationStatus == 0 else {
                    throw TranslationError("录音进程意外退出。", "The audio recorder exited unexpectedly.")
                }
                guard files.fileExists(atPath: temporary.path) else { return nil }
                try files.moveItem(at: temporary, to: destination)
                return destination
            } catch {
                if child.isRunning { child.terminate() }
                if files.fileExists(atPath: temporary.path) {
                    throw TranslationRecordingError(message: TranslationError(
                        "录音未能完成。临时文件：\(temporary.path)", "Unfinished recording: \(temporary.path)"))
                }
                throw TranslationRecordingError(message: TranslationError("录音未能保存。", "Could not save the recording."))
            }
        }
    }

    func append(_ packet: Data) -> Bool {
        var timestamp = UInt64(ProcessInfo.processInfo.systemUptime * 1000).littleEndian
        var frame = Data(capacity: packet.count + 8)
        frame.append(packet.prefix(12))
        withUnsafeBytes(of: &timestamp) { frame.append(contentsOf: $0) }
        frame.append(packet.dropFirst(12))
        if case .enqueued = packets.yield(frame) { return true }
        return false
    }

    func finish() async throws -> URL? {
        packets.finish()
        let child = process
        let timeout = Task.detached {
            try await Task.sleep(nanoseconds: 15_000_000_000)
            if child.isRunning { child.terminate() }
        }
        defer { timeout.cancel() }
        return try await worker.value
    }
}
