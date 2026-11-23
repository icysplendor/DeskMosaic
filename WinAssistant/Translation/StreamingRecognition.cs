using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using WinAssistant.Audio.Core;

namespace WinAssistant.Translation;

internal static class StreamingRecognition
{
    internal static async Task RunAsync(TranslationOptions options, StreamingSpeechModel speechModel, string archive, Action capturing, Action ready, Func<Task>? translationReady,
        Action<TranslationCaption> caption, Func<string, bool, Action<string>, CancellationToken, Task<string>>? translate, CancellationToken token, Action<string>? recordingSaved = null)
    {
        using var diagnostic = options.DiagnosticsEnabled ? OpenDiagnosticLog() : null;
        Log($"session_started fast_model={speechModel.Id} source_language={options.SelectedSourceLanguage}");
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var active = session.Token;
        var correction = StreamingSpeechModel.Correction;
        string correctionArchive = options.SelectedCorrectionModel;
        bool correctParagraphs = options.EnableCorrection && correctionArchive.Length != 0 &&
            correction.Languages.Contains(options.SelectedSourceLanguage);
        Log($"review_configuration enabled={options.EnableCorrection} archive_found={correctionArchive.Length != 0} eligible={correctParagraphs}");
        var frames = new BufferedAudio<byte[]>();
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var state = new object();
        var drafts = new TranslationCaption?[2];
        var pendingFinals = new Queue<TranslationCaption>();
        var pendingCorrections = new Dictionary<int, TranslationCaption>();
        var lastRequest = new long[2];
        var captures = new List<Task>();
        int captureStarted = 0;
        CancellationTokenSource? draftRequest = null;
        TranslationCaption? draftInput = null;
        bool draftHasOutput = false;
        var id = 0;
        var runtime = Task.Run(() => BundledTranslationRuntime.Resolve(null, "streaming", "streaming-asr.exe"));
        var recording = recordingSaved is null ? null : new TranslationRecording(runtime, options.SystemAudio && options.Microphone);
        Task? recognition = null;
        try
        {
            if (options.SystemAudio) StartCapture(false);
            if (options.Microphone) StartCapture(true);
            recognition = RunRecognition();
            await await Task.WhenAny(captures.Append(recognition));
        }
        finally
        {
            session.Cancel();
            try { await Task.WhenAll(recognition is null ? captures : captures.Append(recognition)); }
            catch (Exception) { /* The first failure is already reported above. */ }
            frames.Complete();
            if (recording is not null && await recording.FinishAsync() is { } path) recordingSaved?.Invoke(path);
            Log("session_ended");
        }

        async Task RunRecognition()
        {
            string executable = await runtime.WaitAsync(active);
            var model = await PrepareModel(speechModel, archive, active, Log);
            string speaker = options.SelectedSpeakerModel;
            string[] arguments = ["1", "-"];
            if (speaker.Length != 0)
            {
                const string speakerFile = "speaker.gguf";
                await PrepareSpeaker(speaker, Path.Combine(model, speakerFile), active);
                arguments = [.. arguments, speakerFile, "-", options.Compute == "cpu" ? "cpu" : "auto"];
            }
            string? correctionDirectory = correctParagraphs ? CachedModelDirectory(correction) : null;
            if (correctionDirectory is not null)
                arguments = [.. arguments, "--correction", correctionDirectory];
            var microphoneEnvironment = new Dictionary<string, string?>
            {
                ["ASSISTANT_MIC_VAD"] = options.Microphone ? options.MicrophoneFilter : "off",
                ["ASSISTANT_MIC_PAUSE"] = options.MicrophonePause,
                ["ASSISTANT_MIC_VAD_MODEL"] = null
            };
            string detector = Path.Combine(Path.GetDirectoryName(executable)!, "silero_vad.onnx");
            if (File.Exists(detector)) microphoneEnvironment["ASSISTANT_MIC_VAD_MODEL"] = detector;
            using var helper = Start(executable, model, true, arguments, correctionDirectory, microphoneEnvironment);
            using var terminate = active.Register(() => Kill(helper));
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(active))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    if (await helper.StandardOutput.ReadLineAsync(timeout.Token) != "ready")
                        throw new InvalidOperationException("中英流式识别启动失败。 / Streaming ASR failed to start.");
                }
                catch (OperationCanceledException) when (!active.IsCancellationRequested)
                {
                    throw new TimeoutException("加载中英流式模型超时。 / Streaming ASR model load timed out.");
                }
            }

            Log("fast_engine_ready");
            ready();
            var workers = new List<Task> { WriteAudio(helper), Recognize(helper) };
            Task? correctionPreparation = correctParagraphs ? Task.Run(PrepareCorrection) : null;
            if (correctionPreparation is not null) workers.Add(correctionPreparation);
            if (translationReady is not null) workers.Add(Translate(translationReady));
            try
            {
                // The correction preparation task may complete normally while recognition continues.
                var foreground = workers.Where(worker => worker != correctionPreparation);
                await await Task.WhenAny(foreground);
            }
            finally
            {
                session.Cancel();
                try { await Task.WhenAll(workers); }
                catch (Exception) { /* The first failure is already reported above. */ }
            }
        }

        void Log(string message)
        {
            try { diagnostic?.WriteLine($"{DateTimeOffset.Now:O} {message}"); }
            catch (IOException error) { Debug.WriteLine(error); }
        }

        async Task PrepareCorrection()
        {
            using var preparationDeadline = CancellationTokenSource.CreateLinkedTokenSource(active);
            preparationDeadline.CancelAfter(TimeSpan.FromSeconds(60));
            bool prepared = false;
            try
            {
                await PrepareModel(correction, correctionArchive, preparationDeadline.Token, Log);
                prepared = true;
            }
            catch (OperationCanceledException) when (active.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                Log(preparationDeadline.IsCancellationRequested ? "review_preparation_timed_out" :
                    $"review_preparation_failed type={error.GetType().Name} code=0x{error.HResult:X8}");
                Debug.WriteLine(error);
            }
            if (active.IsCancellationRequested) return;
            // A control header goes through the single writer, outside the recording.
            var header = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), prepared ? 1u : 0u);
            if (!frames.TryAppend(header, header.Length)) Log("review_preparation_header_buffer_full");
        }

        void StartCapture(bool microphone)
        {
            int rate = 0;
            long lastPacket = Environment.TickCount64;
            captures.Add(TranslationAudioCapture.Start(microphone, (samples, sampleRate) =>
            {
                rate = sampleRate;
                lastPacket = Environment.TickCount64;
                Enqueue(samples);
            }, () =>
            {
                // Loopback may stop delivering buffers when playback stops.
                if (rate == 0 || Environment.TickCount64 - lastPacket < 100) return;
                lastPacket = Environment.TickCount64;
                Enqueue(new float[rate / 10]);
            }, () =>
            {
                if (Interlocked.Exchange(ref captureStarted, 1) == 0) capturing();
            }, active));

            void Enqueue(float[] samples)
            {
                for (var offset = 0; offset < samples.Length; offset += 32768)
                {
                    int count = Math.Min(32768, samples.Length - offset);
                    var packet = new byte[12 + count * sizeof(float)];
                    var header = MemoryMarshal.Cast<byte, int>(packet.AsSpan(0, 12));
                    header[0] = microphone ? 1 : 0;
                    header[1] = rate;
                    header[2] = count;
                    Buffer.BlockCopy(samples, offset * sizeof(float), packet, 12, count * sizeof(float));
                    recording?.Append(packet);
                    if (!frames.TryAppend(packet, packet.Length) && !active.IsCancellationRequested)
                        throw new InvalidOperationException("暂存音频已满，语音识别跟不上。 / The audio buffer is full; recognition could not keep up.");
                }
            }
        }

        async Task WriteAudio(Process helper)
        {
            await foreach (var packet in frames.ReadAllAsync(active))
                await helper.StandardInput.BaseStream.WriteAsync(packet, active);
        }

        async Task Recognize(Process helper)
        {
            while (await helper.StandardOutput.ReadLineAsync(active) is { } line)
            {
                if (line.StartsWith("D\t", StringComparison.Ordinal))
                {
                    Log($"review_{line[2..]}");
                    continue;
                }
                var fields = line.Split('\t', 6);
                if (fields.Length != 6 || fields[0] is not ("0" or "1") || !int.TryParse(fields[2], out int speakerId) ||
                    !long.TryParse(fields[3], out long startMs) || !long.TryParse(fields[4], out long endMs) ||
                    startMs < 0 || endMs < startMs) continue;
                int sourceIndex = fields[0] == "1" ? 1 : 0;
                string kind = fields[1];
                int correctionId = 0;
                bool correctionEvent = kind.Length > 1 && (kind[0] is 'F' or 'R' or 'E') &&
                    int.TryParse(kind.AsSpan(1), out correctionId);
                var review = kind switch
                {
                    "1" when !options.EnableCorrection => CaptionReview.Disabled,
                    "1" when !correction.Languages.Contains(options.SelectedSourceLanguage) => CaptionReview.LanguageUnsupported,
                    "1" when correctionArchive.Length == 0 => CaptionReview.ModelMissing,
                    "1" => CaptionReview.Unavailable,
                    "S1" => CaptionReview.ModelPreparing,
                    "S2" => CaptionReview.ModelFailed,
                    "S3" => CaptionReview.NoReviewableText,
                    "S4" => CaptionReview.AudioUnaligned,
                    "S5" => CaptionReview.OutsideLimits,
                    "S6" => CaptionReview.QueueFull,
                    _ => correctionEvent ? CaptionReview.Pending : CaptionReview.None
                };
                bool final = review != CaptionReview.None;
                string source = fields[5].Trim();
                lock (state)
                {
                    if (correctionEvent && kind[0] != 'F')
                    {
                        if (!pendingCorrections.Remove(correctionId, out var original)) continue;
                        bool accepted = source.Any(char.IsLetterOrDigit);
                        bool revised = accepted && source != original.Source;
                        var corrected = original with
                        {
                            Review = kind[0] == 'E' ? CaptionReview.ModelFailed :
                                revised ? CaptionReview.Revised : CaptionReview.Kept
                        };
                        if (accepted)
                            corrected = corrected with { Source = source, StartMs = startMs, EndMs = endMs,
                                Translation = revised ? null : original.Translation };
                        Log($"paragraph id={corrected.Id} range_ms={startMs}-{endMs} review={corrected.Review}");
                        caption(corrected);
                        if (translationReady is not null) QueueFinal(corrected);
                        signal.Writer.TryWrite(true);
                        continue;
                    }
                    var previous = drafts[sourceIndex];
                    if (!source.Any(char.IsLetterOrDigit) || (!final && source == previous?.Source &&
                        speakerId == previous?.SpeakerId && startMs == previous?.StartMs && endMs == previous?.EndMs)) continue;
                    // A speaker change can move the tail of the draft to a new paragraph.
                    var translation = final && previous is not null && source.Length < previous.Source.Length
                        ? null : previous?.Translation;
                    var item = new TranslationCaption(previous?.Id ?? ++id, sourceIndex == 1, source,
                        translation, final, false, speakerId, startMs, endMs,
                        review);
                    drafts[sourceIndex] = final ? null : item;
                    if (final) Log($"paragraph id={item.Id} range_ms={startMs}-{endMs} review={item.Review}");
                    caption(item);
                    if (correctionEvent)
                    {
                        pendingCorrections[correctionId] = item;
                        draftRequest?.Cancel();
                    }
                    else if (translationReady is not null)
                    {
                        if (final) QueueFinal(item);
                        else CancelStaleDraft(item);
                    }
                }
                signal.Writer.TryWrite(true);
            }
            active.ThrowIfCancellationRequested();
            throw new InvalidOperationException("中英流式识别已意外退出。 / Streaming ASR exited unexpectedly.");
        }

        void QueueFinal(TranslationCaption item)
        {
            if (pendingFinals.Count == 256) caption(pendingFinals.Dequeue() with { Translation = "", TranslationFinal = true });
            pendingFinals.Enqueue(item);
            draftRequest?.Cancel();
        }

        async Task Translate(Func<Task> readyToTranslate)
        {
            await readyToTranslate().WaitAsync(active);
            var submitted = new TranslationCaption?[2];
            while (true)
            {
                active.ThrowIfCancellationRequested();
                TranslationCaption? final;
                TranslationCaption? request = null;
                int delay = int.MaxValue;
                int chosen = -1;
                CancellationTokenSource? previewCancellation = null;
                lock (state)
                {
                    if (!pendingFinals.TryDequeue(out final))
                    {
                        long now = Environment.TickCount64;
                        for (int index = 0; index < drafts.Length; index++)
                        {
                            if (drafts[index] is not { } draft) continue;
                            string source = PreviewSource(draft.Source);
                            if (source.Length == 0 ||
                                (submitted[index]?.Id == draft.Id && submitted[index]?.Source == source)) continue;
                            int remaining = (int)Math.Max(0, lastRequest[index] + 250 - now);
                            if (remaining == 0 && (chosen == -1 || lastRequest[index] < lastRequest[chosen])) chosen = index;
                            delay = Math.Min(delay, remaining);
                        }
                        if (chosen >= 0)
                        {
                            var draft = drafts[chosen]!;
                            request = draft with { Source = PreviewSource(draft.Source) };
                            lastRequest[chosen] = now;
                            previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(active);
                            draftRequest = previewCancellation;
                            draftInput = request;
                            draftHasOutput = false;
                        }
                    }
                }
                if (final is not null)
                {
                    string translation = await TranslateSafely(final.Source, provisional: false, text =>
                    {
                        if (text.Length == 0 || text.Length < (final.Translation?.Length ?? 0)) return;
                        lock (state) caption(final with { Translation = text });
                    }, active);
                    active.ThrowIfCancellationRequested();
                    lock (state) caption(final with { Translation = translation, TranslationFinal = true });
                    continue;
                }

                if (request is null)
                {
                    if (delay == int.MaxValue) await signal.Reader.ReadAsync(active);
                    else await Task.Delay(delay, active);
                    continue;
                }

                using var previewLifetime = previewCancellation!;
                try
                {
                    var result = await TranslateSafely(request.Source, provisional: true, text => Preview(text, complete: false), previewLifetime.Token);
                    submitted[chosen] = request;
                    Preview(result, complete: true);
                }
                finally
                {
                    lock (state)
                    {
                        draftRequest = null;
                        draftInput = null;
                    }
                }

                void Preview(string text, bool complete)
                {
                    active.ThrowIfCancellationRequested();
                    if (text.Length == 0 || previewLifetime.IsCancellationRequested) return;
                    lock (state)
                    {
                        if (drafts[chosen] is not { } current || current.Id != request.Id) return;
                        if (current.Translation != text &&
                            (complete || string.IsNullOrEmpty(current.Translation) || text.Length >= current.Translation.Length))
                        {
                            drafts[chosen] = current with { Translation = text };
                            caption(drafts[chosen]!);
                        }
                        draftHasOutput = true;
                        if (!complete) CancelStaleDraft(current);
                    }
                }
            }
        }

        void CancelStaleDraft(TranslationCaption current)
        {
            if (!draftHasOutput || draftInput is not { } input || current.Id != input.Id) return;
            int minimumGrowth = input.Source.Any(letter => char.IsLetter(letter) && !char.IsAscii(letter)) ? 4 : 12;
            if (PreviewSource(current.Source).Length - input.Source.Length >= minimumGrowth)
                draftRequest?.Cancel();
        }

        async Task<string> TranslateSafely(string source, bool provisional, Action<string> preview, CancellationToken requestToken)
        {
            try { return await translate!(source, provisional, preview, requestToken); }
            catch (Exception error) when (!active.IsCancellationRequested)
            {
                Debug.WriteLine(error);
                return "";
            }
        }
    }

    private static string PreviewSource(string source)
    {
        // The rightmost Latin word often ends mid-word while the speaker is still talking.
        if (!char.IsAsciiLetter(source[^1])) return source;
        int end = source.Length;
        while (end > 0 && char.IsAsciiLetter(source[end - 1])) end--;
        return source[..end].TrimEnd();
    }

    private static TextWriter? OpenDiagnosticLog()
    {
        try
        {
            var directory = TranslationRecording.DownloadsDirectory();
            Directory.CreateDirectory(directory);
            var name = $"Translation-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log";
            return TextWriter.Synchronized(new StreamWriter(Path.Combine(directory, name)) { AutoFlush = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine(error);
            return null;
        }
    }

    private static string CachedModelDirectory(StreamingSpeechModel model) =>
        Path.Combine(TranslationModelDownload.ModelDirectory, model.Download.Sha256, model.ArchiveDirectory);

    private static async Task<string> PrepareModel(StreamingSpeechModel model, string archive, CancellationToken token, Action<string> log)
    {
        var release = model.Download;
        string modelName = model.ArchiveDirectory;
        var root = TranslationModelDownload.ModelDirectory;
        string installed = CachedModelDirectory(model);
        if (StreamingSpeechModel.RuntimeFiles.All(file => File.Exists(Path.Combine(installed, file))))
        {
            log($"model={model.Id} cached");
            return installed;
        }
        log($"model={model.Id} verifying_archive");
        if (!await release.IsVerifiedAsync(archive, token))
            throw new InvalidDataException("请选择官方 X-ASR 模型压缩包，或在设置中重新下载。 / Select or download the official X-ASR archive.");

        log($"model={model.Id} extracting");
        string temporary = Path.Combine(root, ".extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
            // The Linux path is used by the build-time smoke test.
            if (!OperatingSystem.IsWindows()) tar = "/usr/bin/tar";
            using var process = Start(tar, temporary, false,
                ["-xf", archive, .. model.ArchiveFiles.Select(file => modelName + "/" + file)]);
            using var stop = token.Register(() => Kill(process));
            await process.WaitForExitAsync(token);
            log($"model={model.Id} extraction_exit={process.ExitCode}");
            string extracted = Path.Combine(temporary, modelName);
            if (process.ExitCode != 0 || !model.ArchiveFiles.All(file => File.Exists(Path.Combine(extracted, file))))
                throw new InvalidDataException("无法解压流式模型。 / Could not extract the streaming model.");
            foreach (var (archiveFile, runtimeFile) in model.ArchiveFiles.Zip(StreamingSpeechModel.RuntimeFiles))
            {
                if (archiveFile != runtimeFile)
                    File.Move(Path.Combine(extracted, archiveFile), Path.Combine(extracted, runtimeFile));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
            try { Directory.Move(extracted, installed); }
            catch (IOException) when (StreamingSpeechModel.RuntimeFiles.All(file => File.Exists(Path.Combine(installed, file)))) { }
        }
        finally { Directory.Delete(temporary, recursive: true); }
        log($"model={model.Id} installed");
        return installed;
    }

    private static async Task PrepareSpeaker(string source, string destination, CancellationToken token)
    {
        var model = TranslationModelDownload.OnlineSpeaker;
        if (model.HasExpectedSize(destination)) return;
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(temporary))
                await input.CopyToAsync(output, token);
            if (!await model.IsVerifiedAsync(temporary, token))
                throw new InvalidDataException("说话人模型校验失败。 / Speaker model verification failed.");
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static Process Start(string executable, string workingDirectory, bool pipes, string[] args, string? correctionDirectory = null,
        Dictionary<string, string?>? environment = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = pipes, RedirectStandardOutput = pipes
        };
        if (pipes) info.StandardOutputEncoding = Encoding.UTF8;
        // The Win32 environment preserves Unicode even if the process uses a legacy command-line code page.
        if (OperatingSystem.IsWindows() && correctionDirectory is not null)
            info.Environment["WINASSISTANT_REVIEW_MODEL_DIR"] = correctionDirectory;
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null) info.Environment.Remove(key);
                else info.Environment[key] = value;
            }
        }
        foreach (var argument in args) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception error) { Debug.WriteLine(error); }
    }
}
