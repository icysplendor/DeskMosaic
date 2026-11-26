using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using WinAssistant.Audio.Core;

namespace WinAssistant.Translation;

internal static class StreamingRecognition
{
    internal static async Task RunFileAsync(TranslationOptions options, StreamingSpeechModel speechModel, string archive,
        string audioFile, Action ready, Action<TranslationCaption> caption, CancellationToken token)
    {
        const int rate = 16_000;
        string executable = BundledTranslationRuntime.Resolve(null, "streaming", "streaming-asr.exe");
        string model = await PrepareModel(speechModel, archive, token, _ => { });
        var helperArguments = await RecognitionArguments(options, model, token);
        string encoder = WinAssistant.Screenshot.RegionRecording.FindEncoder();
        using var helper = Start(executable, model, true, helperArguments, environment: new()
        {
            ["ASSISTANT_FILE_TRANSCRIPTION"] = "1",
            ["ASSISTANT_MIC_VAD"] = "off"
        });
        using var terminateHelper = token.Register(() => Kill(helper));
        try
        {
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(60));
                if (await helper.StandardOutput.ReadLineAsync(startup.Token) != "ready")
                    throw new InvalidOperationException("音频文件识别引擎启动失败。 / Could not start audio file recognition.");
            }

            var input = new ProcessStartInfo(encoder)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            string[] arguments = ["-nostdin", "-v", "error", "-i", audioFile, "-map", "0:a:0",
                "-ac", "1", "-ar", "16000", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"];
            foreach (var argument in arguments) input.ArgumentList.Add(argument);
            using var decoder = Process.Start(input) ?? throw new InvalidOperationException("Could not start audio decoder.");
            using var terminateDecoder = token.Register(() => Kill(decoder));
            var diagnostics = decoder.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            ready();
            int paragraphCount = 0;
            await Task.WhenAll(FeedAudio(), ReadParagraphs());
            if (helper.ExitCode != 0) throw new IOException("音频文件识别失败。 / Audio file recognition failed.");
            if (paragraphCount == 0) throw new InvalidDataException("音频中未识别到语音。 / No speech was recognized in the audio.");

            async Task FeedAudio()
            {
                await using var output = helper.StandardInput.BaseStream;
                Stream pcm = decoder.StandardOutput.BaseStream;
                var packet = new byte[12 + 4096 * sizeof(float)];
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), rate);
                bool receivedAudio = false;
                try
                {
                    int count;
                    while ((count = await pcm.ReadAtLeastAsync(packet.AsMemory(12), packet.Length - 12, false, token)) != 0)
                    {
                        if (count % sizeof(float) != 0) throw new InvalidDataException("Invalid decoded audio.");
                        receivedAudio = true;
                        await Send(packet, count);
                    }
                    await decoder.WaitForExitAsync(token);
                    await diagnostics;
                    if (!receivedAudio || decoder.ExitCode != 0)
                        throw new IOException("无法读取选定的音频文件。 / Could not read the selected audio file.");
                    // Supply right context and finalize the last spoken paragraph.
                    Array.Clear(packet, 12, packet.Length - 12);
                    for (int index = 0; index < 8; index++) await Send(packet, packet.Length - 12);
                }
                finally { Kill(decoder); }

                async Task Send(byte[] frame, int count)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), count / sizeof(float));
                    await output.WriteAsync(frame.AsMemory(0, 12 + count), token);
                }
            }

            async Task ReadParagraphs()
            {
                while (await helper.StandardOutput.ReadLineAsync(token) is { } line)
                {
                    var fields = line.Split('\t', 6);
                    if (fields.Length != 6 || fields[0] != "0" || fields[1] != "1" ||
                        !int.TryParse(fields[2], out int speakerId) ||
                        !long.TryParse(fields[3], out long start) || !long.TryParse(fields[4], out long end) ||
                        !fields[5].Any(char.IsLetterOrDigit)) continue;
                    var paragraph = new TranslationCaption(++paragraphCount, false, fields[5], null,
                        TranslationFinal: false, SpeakerId: speakerId, StartMs: start, EndMs: end);
                    caption(paragraph);
                }
                await helper.WaitForExitAsync(token);
            }
        }
        finally { Kill(helper); }
    }

    internal static async Task RunAsync(TranslationOptions options, StreamingSpeechModel speechModel, string archive, Action capturing, Action ready, Func<Task>? translationReady,
        Action<TranslationCaption> caption, Func<string, bool, Action<string>, CancellationToken, Task<TranslationResult>>? translate,
        CancellationToken token, Action<string>? recordingSaved, Action<string> Log, CancellationToken finishInput)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var active = session.Token;
        using var captureLifetime = CancellationTokenSource.CreateLinkedTokenSource(active, finishInput);
        var correction = StreamingSpeechModel.Correction;
        string correctionArchive = options.SelectedCorrectionModel;
        string mossPath = options.UsesMossReview ? options.SelectedMossModel : "";
        bool mossReview = options.EnableCorrection && mossPath.Length != 0;
        bool correctParagraphs = !mossReview && options.EnableCorrection && correctionArchive.Length != 0 &&
            correction.Languages.Contains(options.SelectedSourceLanguage);
        string reviewName = mossReview ? "moss" : "x-asr";
        string reviewPath = mossReview ? mossPath : correctionArchive;
        Log($"review_configuration model={reviewName} enabled={options.EnableCorrection} archive_found={reviewPath.Length != 0} eligible={correctParagraphs || mossReview}");
        var frames = new BufferedAudio<byte[]>();
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var state = new object();
        var drafts = new TranslationCaption?[2];
        var stableSentences = new string?[2];
        var pendingFinals = new Queue<TranslationCaption>();
        var pendingCorrections = new Dictionary<int, TranslationCaption>();
        int previewInterval = options.PreviewIntervalMs;
        long firstEligible = Environment.TickCount64 - previewInterval;
        long[] lastRequest = [firstEligible, firstEligible];
        var captures = new List<Task>();
        int captureStarted = 0;
        CancellationTokenSource? draftRequest = null;
        TranslationCaption? draftInput = null;
        bool draftHasOutput = false;
        var id = 0;
        bool recognitionFinished = false;
        var captureReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = ResolveRuntime();

        async Task<string> ResolveRuntime()
        {
            await captureReady.Task;
            return await Task.Run(() => BundledTranslationRuntime.Resolve(null, "streaming", "streaming-asr.exe"));
        }
        var recording = recordingSaved is null ? null : new TranslationRecording(runtime, options.SystemAudio && options.Microphone);
        Task? recognition = null;
        try
        {
            if (options.SystemAudio) StartCapture(false);
            if (options.Microphone) StartCapture(true);
            recognition = RunRecognition();
            var first = await Task.WhenAny(captures.Append(recognition));
            if (finishInput.IsCancellationRequested && !active.IsCancellationRequested)
            {
                try { await Task.WhenAll(captures); }
                catch (OperationCanceledException) when (!active.IsCancellationRequested) { }
                if (Volatile.Read(ref captureStarted) == 0) return;
                frames.Complete();
                await recognition;
            }
            else await first;
        }
        finally
        {
            session.Cancel();
            captureReady.TrySetCanceled(active);
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
            string[] arguments = await RecognitionArguments(options, model, active);
            string? correctionDirectory = correctParagraphs ? CachedModelDirectory(correction) : null;
            if (correctionDirectory is not null)
                arguments = [.. arguments, "--correction", correctionDirectory];
            var microphoneEnvironment = new Dictionary<string, string?>
            {
                ["ASSISTANT_MIC_VAD"] = options.Microphone ? options.MicrophoneFilter : "off",
                ["ASSISTANT_MIC_PAUSE"] = options.MicrophonePause,
                ["ASSISTANT_MIC_VAD_MODEL"] = null,
                ["ASSISTANT_MOSS_REVIEW"] = mossReview ? options.Compute == "cpu" ? "cpu" : "auto" : null,
                ["ASSISTANT_MOSS_MODEL"] = mossReview ? mossPath : null
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
            var replayBoundary = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(replayBoundary, uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(replayBoundary.AsSpan(4), 2);
            if (!frames.TryAppend(replayBoundary, replayBoundary.Length) && !finishInput.IsCancellationRequested)
                throw new InvalidOperationException("暂存音频已满，语音识别跟不上。 / The audio buffer is full; recognition could not keep up.");
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
                if (finishInput.IsCancellationRequested && !active.IsCancellationRequested)
                    await Task.WhenAll(workers);
            }
            finally
            {
                session.Cancel();
                try { await Task.WhenAll(workers); }
                catch (Exception) { /* The first failure is already reported above. */ }
            }
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
                Log($"audio_capture_ready source={(microphone ? "microphone" : "system")}");
                if (Interlocked.Exchange(ref captureStarted, 1) == 0)
                {
                    capturing();
                    captureReady.TrySetResult();
                }
            }, captureLifetime.Token));

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
            await helper.StandardInput.BaseStream.DisposeAsync();
        }

        async Task Recognize(Process helper)
        {
            while (await helper.StandardOutput.ReadLineAsync(active) is { } line)
            {
                if (line.StartsWith("A\t", StringComparison.Ordinal))
                {
                    Log($"audio_{line[2..]}");
                    if (line == "A\tinvalid_audio_packet")
                        throw new InvalidDataException("识别引擎收到无效音频。 / The recognition engine received invalid audio.");
                    continue;
                }
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
                bool correctionEvent = kind.Length > 1 && (kind[0] is 'F' or 'R' or 'E' or 'G') &&
                    int.TryParse(kind.AsSpan(1), out correctionId);
                var review = kind switch
                {
                    "1" when !options.EnableCorrection => CaptionReview.Disabled,
                    "1" when mossReview => CaptionReview.Unavailable,
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
                string rawSource = fields[5];
                string source = rawSource.Trim();
                lock (state)
                {
                    if (correctionEvent && kind[0] != 'F')
                    {
                        if (!pendingCorrections.Remove(correctionId, out var original)) continue;
                        bool accepted = kind[0] == 'R' && source.Any(char.IsLetterOrDigit);
                        bool revised = accepted && source != original.Source;
                        var corrected = original with
                        {
                            Review = kind[0] == 'E' ? CaptionReview.ModelFailed :
                                revised ? CaptionReview.Revised : CaptionReview.Kept
                        };
                        if (kind[0] == 'G' && original.MossTurns(rawSource) is { Length: > 0 } turns)
                            corrected = corrected with { Review = CaptionReview.MossReviewed, ReviewTurns = turns };
                        if (accepted)
                            corrected = corrected with { Source = source, StartMs = startMs, EndMs = endMs };
                        Log($"paragraph id={corrected.Id} range_ms={startMs}-{endMs} review={corrected.Review}");
                        if (translationReady is null || corrected.ReviewTurns.Length == 0) caption(corrected);
                        if (translationReady is not null) QueueFinal(corrected);
                        signal.Writer.TryWrite(true);
                        continue;
                    }
                    var previous = drafts[sourceIndex];
                    if (!source.Any(char.IsLetterOrDigit)) continue;
                    string? oldStableSentence = stableSentences[sourceIndex];
                    stableSentences[sourceIndex] = null;
                    if (!final && kind.StartsWith('0') && int.TryParse(kind.AsSpan(1), out int bytes) && bytes > 0)
                    {
                        byte[] utf8 = Encoding.UTF8.GetBytes(rawSource);
                        if (bytes <= utf8.Length)
                            stableSentences[sourceIndex] = Encoding.UTF8.GetString(utf8, 0, bytes).Trim();
                    }
                    if (!final && stableSentences[sourceIndex] == oldStableSentence && source == previous?.Source &&
                        speakerId == previous?.SpeakerId && startMs == previous?.StartMs && endMs == previous?.EndMs) continue;
                    // A shorter final source still uses the visible preview until its replacement arrives.
                    var item = new TranslationCaption(previous?.Id ?? ++id, sourceIndex == 1, source,
                        previous?.Translation, final, false, speakerId, startMs, endMs,
                        review, previous?.CacheHitPercent);
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
            if (finishInput.IsCancellationRequested)
            {
                await helper.WaitForExitAsync(active);
                if (helper.ExitCode != 0)
                    throw new InvalidOperationException("流式识别未能完成。 / Streaming ASR did not finish successfully.");
                lock (state) recognitionFinished = true;
                signal.Writer.TryWrite(true);
                return;
            }
            throw new InvalidOperationException("中英流式识别已意外退出。 / Streaming ASR exited unexpectedly.");
        }

        void QueueFinal(TranslationCaption item)
        {
            if (pendingFinals.Count == 256)
            {
                var dropped = pendingFinals.Dequeue();
                if (dropped.ReviewTurns.Length != 0) caption(KeepOriginal(dropped));
                else if (string.IsNullOrEmpty(dropped.Translation))
                    caption(dropped with { Translation = "", TranslationFinal = true });
            }
            pendingFinals.Enqueue(item);
            draftRequest?.Cancel();
        }

        static TranslationCaption KeepOriginal(TranslationCaption item) =>
            item with { ReviewTurns = [], Review = CaptionReview.Kept, Translation = item.Translation ?? "" };

        async Task Translate(Func<Task> readyToTranslate)
        {
            await readyToTranslate().WaitAsync(active);
            var submitted = new TranslationCaption?[2];
            var completed = new TranslationCaption?[2];
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
                        if (recognitionFinished) return;
                        long now = Environment.TickCount64;
                        for (int index = 0; index < drafts.Length; index++)
                        {
                            if (drafts[index] is not { } draft) continue;
                            if (submitted[index]?.Id == draft.Id && submitted[index]?.Source == draft.Source) continue;
                            bool newSentence = stableSentences[index] is { } sentence &&
                                (submitted[index]?.Id != draft.Id ||
                                 submitted[index]?.Source.StartsWith(sentence, StringComparison.Ordinal) != true);
                            int remaining = newSentence ? 0 : (int)Math.Max(0, lastRequest[index] + previewInterval - now);
                            if (remaining == 0 && (chosen == -1 || lastRequest[index] < lastRequest[chosen])) chosen = index;
                            delay = Math.Min(delay, remaining);
                        }
                        if (chosen >= 0)
                        {
                            request = drafts[chosen]!;
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
                    if (final.ReviewTurns.Length != 0)
                    {
                        var turns = new List<TranslationCaption>(final.ReviewTurns.Length);
                        foreach (var turn in final.ReviewTurns)
                        {
                            var translated = await TranslateSafely(turn.Source, provisional: false, _ => { }, active);
                            active.ThrowIfCancellationRequested();
                            if (string.IsNullOrWhiteSpace(translated.Text)) break;
                            turns.Add(turn with { Translation = translated.Text, TranslationFinal = true,
                                CacheHitPercent = translated.CacheHitPercent });
                        }
                        if (turns.Count == final.ReviewTurns.Length)
                        {
                            lock (state) caption(final with { ReviewTurns = turns.ToArray(), TranslationFinal = true,
                                Translation = string.Join("\n", turns.Select(turn => turn.Translation)) });
                            continue;
                        }
                        final = KeepOriginal(final);
                        lock (state) caption(final);
                        if (!string.IsNullOrEmpty(final.Translation)) continue;
                    }
                    var result = await TranslateSafely(final.Source, provisional: false, _ => { }, active);
                    active.ThrowIfCancellationRequested();
                    // On failure the visible preview stays provisional.
                    if (result.Text.Length != 0 || string.IsNullOrEmpty(final.Translation))
                        lock (state) caption(final with { Translation = result.Text, TranslationFinal = true,
                            CacheHitPercent = result.CacheHitPercent });
                    continue;
                }

                if (request is null)
                {
                    if (delay == int.MaxValue) await signal.Reader.ReadAsync(active);
                    else
                    {
                        // A new sentence or a pending final can wake us before the deadline.
                        using var wake = CancellationTokenSource.CreateLinkedTokenSource(active);
                        wake.CancelAfter(delay);
                        try { await signal.Reader.ReadAsync(wake.Token); }
                        catch (OperationCanceledException) when (!active.IsCancellationRequested) { }
                    }
                    continue;
                }

                using var previewLifetime = previewCancellation!;
                var anchor = completed[chosen];
                string source = request.Source;
                if (anchor is not null && anchor.Id == request.Id &&
                    source.StartsWith(anchor.Source, StringComparison.Ordinal))
                {
                    string remainder = source[anchor.Source.Length..].TrimStart();
                    // Keep a row in one language when a new clause changes the two-way direction.
                    if (remainder.Length != 0 && (!options.TwoWay ||
                        ChineseEnglishSource.Detect(remainder) == ChineseEnglishSource.Detect(anchor.Source)))
                        source = remainder;
                    else
                        anchor = null;
                }
                else
                {
                    anchor = null;
                }
                completed[chosen] = anchor;
                try
                {
                    var result = await TranslateSafely(source, provisional: true, text => Preview(text, complete: false), previewLifetime.Token);
                    submitted[chosen] = request;
                    Preview(result.Text, complete: true, cacheHitPercent: result.CacheHitPercent);
                }
                finally
                {
                    lock (state)
                    {
                        draftRequest = null;
                        draftInput = null;
                    }
                }

                void Preview(string text, bool complete, int? cacheHitPercent = null)
                {
                    active.ThrowIfCancellationRequested();
                    if (text.Length == 0 || previewLifetime.IsCancellationRequested) return;
                    lock (state)
                    {
                        if (drafts[chosen] is not { } current || current.Id != request.Id) return;
                        draftHasOutput = true;
                        if (!complete) CancelStaleDraft(current);
                        if (!StillRepresentsSource(request.Source, current.Source)) return;
                        string visible = current.Translation ?? "";
                        if (anchor?.Translation is { Length: > 0 } prefix)
                            text = prefix + (prefix[^1] is '。' or '！' or '？' ? "" : " ") + text;

                        // Rephrasing the open sentence is fine. Publish a conflicting preview
                        // once it is complete instead of replacing the text token by token.
                        if (complete || (text != visible && text.StartsWith(visible, StringComparison.Ordinal)))
                        {
                            var updated = current with { Translation = text, CacheHitPercent = cacheHitPercent };
                            if (updated != current)
                            {
                                drafts[chosen] = updated;
                                caption(updated);
                            }
                        }
                        if (complete && EndsSentence(request.Source) && EndsSentence(text))
                            completed[chosen] = request with { Translation = text };
                    }
                }
            }
        }

        void CancelStaleDraft(TranslationCaption current)
        {
            if (!draftHasOutput || draftInput is not { } input || current.Id != input.Id) return;
            if (!StillRepresentsSource(input.Source, current.Source)) draftRequest?.Cancel();
        }

        async Task<TranslationResult> TranslateSafely(string source, bool provisional, Action<string> preview, CancellationToken requestToken)
        {
            try { return await translate!(source, provisional, preview, requestToken); }
            catch (Exception error) when (!active.IsCancellationRequested)
            {
                Debug.WriteLine(error);
                return new TranslationResult("");
            }
        }
    }

    private static bool StillRepresentsSource(string input, string current)
    {
        if (!current.StartsWith(input, StringComparison.Ordinal)) return false;
        if (current.Length == input.Length) return true;
        char next = current[input.Length];
        // Do not display the result for "can" after the source has reached "can't",
        // or the result for a number while the same number is still growing.
        if (char.IsAsciiLetter(input[^1]) && (char.IsAsciiLetter(next) || next is '\'' or '’')) return false;
        return !char.IsAsciiDigit(input[^1]) || !(char.IsAsciiDigit(next) || next is '.' or ',');
    }

    private static bool EndsSentence(string text)
    {
        // A comma can introduce a condition that changes the meaning of the preceding words.
        if (text.Length < 2) return false;
        if (text[^1] is '。' or '！' or '？' or '!' or '?') return true;
        // Avoid locking at a decimal point, ellipsis, or short abbreviation.
        return text.Length >= 4 && text[^1] == '.' &&
            char.IsAsciiLetterLower(text[^2]) && char.IsAsciiLetterLower(text[^3]) && char.IsAsciiLetterLower(text[^4]);
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

    private static async Task<string[]> RecognitionArguments(TranslationOptions options, string model, CancellationToken token)
    {
        string speaker = options.SelectedSpeakerModel;
        if (speaker.Length == 0) return ["1", "-"];

        const string speakerFile = "speaker.gguf";
        await PrepareSpeaker(speaker, Path.Combine(model, speakerFile), token);
        return ["1", "-", speakerFile, "-", options.Compute == "cpu" ? "cpu" : "auto"];
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
        if (pipes)
        {
            // stdin carries binary PCM headers. StreamWriter must emit no encoding preamble.
            info.StandardInputEncoding = new UTF8Encoding(false);
            info.StandardOutputEncoding = Encoding.UTF8;
        }
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
