import AVFoundation
import ScreenCaptureKit

final class TranslationAudioCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    private let receiveSystem: (AVAudioPCMBuffer) -> Void
    private let receiveMicrophone: (AVAudioPCMBuffer) -> Void
    private let fail: (Error) -> Void
    private let queue = DispatchQueue(label: "assistant.translation.system-audio")
    private var stream: SCStream?
    private var engine: AVAudioEngine?
    private var inputObserver: NSObjectProtocol?

    init(system: @escaping (AVAudioPCMBuffer) -> Void, microphone: @escaping (AVAudioPCMBuffer) -> Void,
         fail: @escaping (Error) -> Void) {
        receiveSystem = system
        receiveMicrophone = microphone
        self.fail = fail
    }

    @MainActor
    func start(system: Bool, microphone: Bool) async throws {
        if microphone {
            guard await AVCaptureDevice.requestAccess(for: .audio) else {
                throw TranslationError("请在系统设置中允许麦克风访问。", "Allow microphone access in System Settings.")
            }
            try Task.checkCancellation()
            let engine = AVAudioEngine()
            let input = engine.inputNode
            let format = input.outputFormat(forBus: 0)
            guard format.sampleRate > 0, format.channelCount > 0 else {
                throw TranslationError("默认麦克风不可用。", "The default microphone is unavailable.")
            }
            let receive = receiveMicrophone
            input.installTap(onBus: 0, bufferSize: 2048, format: format) { buffer, _ in receive(buffer) }
            self.engine = engine
            try engine.start()
            let onError = fail
            inputObserver = NotificationCenter.default.addObserver(
                forName: .AVAudioEngineConfigurationChange, object: engine, queue: nil
            ) { _ in
                onError(TranslationError("麦克风已变更，请重新开始。", "The microphone changed. Start again."))
            }
        }
        if system {
            // ScreenCaptureKit supports playback capture on macOS 13 and requests screen/system-audio permission.
            // Only audio buffers are subscribed to. Windows uses WASAPI loopback instead.
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
                try Task.checkCancellation()
                guard let display = content.displays.first else {
                    throw TranslationError("没有可用的显示器。", "No display is available.")
                }
                let filter = SCContentFilter(display: display, excludingWindows: [])
                let configuration = SCStreamConfiguration()
                configuration.width = 2
                configuration.height = 2
                configuration.minimumFrameInterval = CMTime(value: 1, timescale: 1)
                configuration.capturesAudio = true
                configuration.excludesCurrentProcessAudio = true
                configuration.sampleRate = 48_000
                configuration.channelCount = 2
                let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
                try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue)
                self.stream = stream
                try await stream.startCapture()
                if Task.isCancelled {
                    try? await stream.stopCapture()
                    throw CancellationError()
                }
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                let detail = error.localizedDescription
                throw TranslationError("无法采集电脑声音，请检查屏幕与系统录音权限。\(detail)",
                                       "Could not capture system audio. Check Screen & System Audio Recording permissions. \(detail)")
            }
        }
    }

    @MainActor
    func stop() {
        if let inputObserver { NotificationCenter.default.removeObserver(inputObserver) }
        inputObserver = nil
        if let engine {
            engine.inputNode.removeTap(onBus: 0)
            engine.stop()
        }
        engine = nil
        if let stream { Task { try? await stream.stopCapture() } }
        stream = nil
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        fail(error)
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sample: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, sample.isValid, let description = sample.formatDescription else { return }
        let format = AVAudioFormat(cmAudioFormatDescription: description)
        do {
            try sample.withAudioBufferList { list, _ in
                guard let pcm = AVAudioPCMBuffer(pcmFormat: format, bufferListNoCopy: list.unsafePointer) else { return }
                pcm.frameLength = AVAudioFrameCount(sample.numSamples)
                receiveSystem(pcm)
            }
        } catch {
            fail(error)
        }
    }
}
