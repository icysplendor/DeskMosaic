using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Text.Json;

namespace WinAssistant.Translation;

internal enum CaptionReview
{
    None, Pending, Kept, Revised, MossReviewed, Disabled, ModelMissing, LanguageUnsupported,
    ModelPreparing, ModelFailed, NoReviewableText, AudioUnaligned, OutsideLimits, QueueFull, Unavailable
}

internal sealed record TranslationCaption(int Id, bool Microphone, string Source, string? Translation, bool SourceFinal = true, bool TranslationFinal = true, int SpeakerId = 0, long StartMs = 0, long EndMs = 0, CaptionReview Review = CaptionReview.None, int? CacheHitPercent = null)
{
    internal TranslationCaption[] ReviewTurns { get; init; } = [];
    internal bool SpeakerWithinParagraph { get; init; }

    internal TranslationCaption[] MossTurns(string payload)
    {
        var turns = new List<TranslationCaption>();
        foreach (string entry in payload.Split('\u001e'))
        {
            string[] fields = entry.Split('\u001f', 4);
            if (fields.Length != 4 || !int.TryParse(fields[0], out int speaker) ||
                !long.TryParse(fields[1], out long start) || !long.TryParse(fields[2], out long end) || start < 0 || end <= start)
                return [];
            string source = fields[3].Trim();
            if (source.Any(char.IsLetterOrDigit))
                turns.Add(new TranslationCaption(turns.Count, Microphone, source, null, TranslationFinal: false,
                    SpeakerId: speaker, StartMs: start, EndMs: end) { SpeakerWithinParagraph = true });
        }
        var merged = new List<TranslationCaption>();
        foreach (var turn in turns.OrderBy(turn => turn.StartMs))
        {
            if (merged.Count > 0 && turn.SpeakerId > 0 && merged[^1].SpeakerId == turn.SpeakerId)
            {
                var previous = merged[^1];
                string separator = char.IsAscii(previous.Source[^1]) && char.IsAscii(turn.Source[0]) ? " " : "";
                merged[^1] = previous with { Source = previous.Source + separator + turn.Source,
                    EndMs = Math.Max(previous.EndMs, turn.EndMs) };
            }
            else merged.Add(turn);
        }
        return merged.ToArray();
    }

    internal string TimeRange => $"{FormatTime(StartMs)} – {FormatTime(EndMs)}";

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        string clock = time.ToString(@"mm\:ss\.f");
        return time.TotalHours >= 1 ? $"{(long)time.TotalHours:00}:{clock}" : clock;
    }
}

internal sealed record TranslationResult(string Text, int? CacheHitPercent = null);

internal sealed class TranslationEngine
{
    internal async Task RunAsync(TranslationOptions options, Action capturing, Action<string?> status,
        Action<TranslationCaption> caption, CancellationToken token, Action<string>? recordingSaved = null, string? audioFile = null, CancellationToken finishInput = default)
    {
        options.ImportedAudio = audioFile is not null;
        if (audioFile is null && !options.SystemAudio && !options.Microphone)
            throw new InvalidOperationException("Select an audio source / 请选择音源。");
        var model = options.ModelForSource(options.SelectedSourceLanguage)
            ?? throw new InvalidOperationException("请在录写/翻译设置中下载或选择对应的流式语音模型。 / Install a streaming speech model in Transcribe / Translate settings.");
        string translationModel = options.TranscribeOnly ? "" : options.SelectedTranslationModel;
        if (!options.TranscribeOnly && translationModel.Length == 0)
            throw new InvalidOperationException("请在录写/翻译设置中下载或选择当前的文本翻译模型。 / Install the selected text translation model in Transcribe / Translate settings.");
        using var diagnostic = options.DiagnosticsEnabled ? OpenDiagnosticLog() : null;
        Log($"session_started fast_model={model.Id} source_language={options.SelectedSourceLanguage}");
        if (options.TranscribeOnly)
        {
            if (audioFile is not null)
            {
                await StreamingRecognition.RunFileAsync(options, model, options.PathFor(model), audioFile, () => status(null), caption, token);
                return;
            }
            await StreamingRecognition.RunAsync(options, model, options.PathFor(model), capturing, () => status(null),
                null, caption, null, token, recordingSaved, Log, finishInput);
            return;
        }

        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var active = session.Token;
        var key = Guid.NewGuid().ToString("N");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        var captureReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llamaStartup = StartTranslation();
        string selectedTarget = options.SelectedTargetLanguage;
        string sourceLanguage = options.SelectedSourceLanguage;
        try
        {
            if (audioFile is not null)
            {
                var paragraphs = Channel.CreateUnbounded<TranslationCaption>(
                    new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
                captureReady.TrySetResult();
                var recognition = RecognizeFile();
                try
                {
                    LocalServer? server = null;
                    await foreach (var paragraph in paragraphs.Reader.ReadAllAsync(active))
                    {
                        if (server is null)
                        {
                            server = await llamaStartup.WaitAsync(active);
                            status(server.Backend);
                        }
                        var result = await TranslateText(paragraph.Source, false, _ => { }, active);
                        active.ThrowIfCancellationRequested();
                        caption(paragraph with { Translation = result.Text, TranslationFinal = true,
                            CacheHitPercent = result.CacheHitPercent });
                    }
                }
                finally
                {
                    session.Cancel();
                    await recognition;
                }
                return;

                async Task RecognizeFile()
                {
                    try
                    {
                        await StreamingRecognition.RunFileAsync(options, model, options.PathFor(model), audioFile, () => status(null),
                            paragraph =>
                            {
                                caption(paragraph);
                                paragraphs.Writer.TryWrite(paragraph);
                            }, active);
                        paragraphs.Writer.TryComplete();
                    }
                    catch (Exception error) { paragraphs.Writer.TryComplete(error); }
                }
            }
            await StreamingRecognition.RunAsync(options, model, options.PathFor(model), () =>
                {
                    capturing();
                    captureReady.TrySetResult();
                }, () => status(null),
                async () =>
                {
                    var server = await llamaStartup.WaitAsync(active);
                    status(server.Backend);
                }, caption, TranslateText, active, recordingSaved, Log, finishInput);
        }
        finally
        {
            session.Cancel();
            try { using var server = await llamaStartup; }
            catch (Exception) { /* The translation worker reports startup failures. */ }
            if (audioFile is not null) Log("session_ended");
        }

        void Log(string message)
        {
            try { diagnostic?.WriteLine($"{DateTimeOffset.Now:O} {message}"); }
            catch (IOException error) { Debug.WriteLine(error); }
        }

        async Task<LocalServer> StartTranslation()
        {
            await captureReady.Task.WaitAsync(active);
            var llamaPath = await Task.Run(() =>
                BundledTranslationRuntime.Resolve(options.LlamaServer, "llama", "llama-server.exe"), active);
            bool custom = !string.IsNullOrWhiteSpace(options.LlamaServer) && Path.IsPathFullyQualified(options.LlamaServer) &&
                File.Exists(options.LlamaServer);
            if (options.Compute != "cpu")
            {
                try
                {
                    var accelerated = custom ? llamaPath : await Task.Run(
                        () => BundledTranslationRuntime.Resolve(null, "llama-vulkan", "llama-server.exe"), active);
                    var devices = custom ? Array.Empty<string>() : new[] { "--device", "Vulkan0" };
                    return await Launch(accelerated, [.. devices, "--gpu-layers", "all"], custom ? "Custom" : "Vulkan GPU");
                }
                catch (Exception error) when (!active.IsCancellationRequested)
                {
                    Debug.WriteLine(error); // A compatible GPU driver is optional.
                }
            }
            return await Launch(llamaPath, ["--device", "none", "--gpu-layers", "0"], "CPU");

            Task<LocalServer> Launch(string path, string[] devices, string backend)
            {
                string[] command = Path.GetFileNameWithoutExtension(path).Equals("llama", StringComparison.OrdinalIgnoreCase) ? ["serve"] : [];
                int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, backend == "CPU" ? 4 : 2);
                return StartServer(http, path, translationModel,
                    [.. command, "--api-key", key, "--ctx-size", "1024", "--parallel", "1", "--no-webui",
                     "--threads", threads.ToString(), "--poll", "0", .. devices], backend, active);
            }
        }

        async Task<TranslationResult> TranslateText(string source, bool provisional, Action<string> preview, CancellationToken cancellation)
        {
            string inputLanguage = sourceLanguage;
            string target = selectedTarget;
            if (options.TwoWay)
            {
                var detected = ChineseEnglishSource.Detect(source);
                if (detected is null) return new TranslationResult(provisional ? "" : source);
                (inputLanguage, target) = detected.Value;
            }
            string instruction = $"Translate the following text into {target}. Output only the translation, without explanations.\n\n{source}";
            if (options.UseLmtTranslation)
            {
                if (inputLanguage == "auto")
                    inputLanguage = source.Any(character => character is >= '\u3400' and <= '\u9fff') ? "Chinese" : "English";
                if (inputLanguage == target)
                {
                    // In Chinese output mode, let LMT translate English mixed into Mandarin.
                    if (target != "Chinese" || !source.Any(char.IsAsciiLetter)) return new TranslationResult(source);
                    inputLanguage = "English";
                }
                instruction = $"Translate the following text from {inputLanguage} into {target}:\n{inputLanguage}: {source}\n{target}:";
            }

            var llama = await llamaStartup.WaitAsync(cancellation);
            using var request = new HttpRequestMessage(HttpMethod.Post, llama.Url + "/v1/chat/completions");
            request.Headers.Authorization = new("Bearer", key);
            var body = new Dictionary<string, object>
            {
                ["messages"] = new[] { new { role = "user", content = instruction } },
                ["temperature"] = 0.0,
                ["stream"] = true,
                ["max_tokens"] = provisional ? 64 : 224
            };
            if (options.UseLmtTranslation) body["chat_template_kwargs"] = new { enable_thinking = false };
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            long started = Environment.TickCount64;
            TranslationTimings? timings = null;
            bool completed = false;
            try
            {
                using var translated = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
                translated.EnsureSuccessStatusCode();
                using var stream = new StreamReader(await translated.Content.ReadAsStreamAsync(cancellation));
                var result = new StringBuilder();
                long last = 0;
                while (await stream.ReadLineAsync(cancellation) is { } line)
                {
                    if (line == "data: [DONE]")
                    {
                        completed = true;
                        return new TranslationResult(result.ToString().Trim(), timings?.CacheHitPercent);
                    }
                    if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                    using var update = JsonDocument.Parse(line[6..]);
                    var chunk = update.RootElement;
                    if (chunk.TryGetProperty("timings", out var metrics) && metrics.ValueKind == JsonValueKind.Object)
                        timings = TranslationTimings.Read(metrics);
                    if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
                        choices.GetArrayLength() == 0 ||
                        !choices[0].TryGetProperty("delta", out var delta) || !delta.TryGetProperty("content", out var content)) continue;
                    result.Append(content.GetString());
                    long now = Environment.TickCount64;
                    if (result.Length == 0 || now - last < 120) continue;
                    preview(result.ToString().Trim());
                    last = now;
                }
                throw new IOException("Translation stream ended early.");
            }
            finally
            {
                if (diagnostic is not null)
                {
                    string kind = provisional ? "preview" : "final";
                    string outcome = completed ? "completed" : cancellation.IsCancellationRequested ? "cancelled" : "failed";
                    Log($"translation_request kind={kind} outcome={outcome} backend={llama.Backend} " +
                        $"elapsed_ms={Environment.TickCount64 - started} {timings?.DiagnosticFields ?? "timings=unavailable"}");
                }
            }
        }
    }

    private sealed record TranslationTimings(int? Cached, int? Prompt, double? PromptMs, int? Predicted, double? PredictedMs)
    {
        internal int? CacheHitPercent => Cached is >= 0 && Prompt is >= 0 && Cached.Value + (double)Prompt.Value > 0
            ? (int)Math.Round(100.0 * Cached.Value / (Cached.Value + (double)Prompt.Value), MidpointRounding.AwayFromZero)
            : null;

        internal string DiagnosticFields =>
            $"cache_n={Field(Cached)} prompt_n={Field(Prompt)} cache_hit_percent={Field(CacheHitPercent)} " +
            $"prompt_ms={Field(PromptMs)} predicted_n={Field(Predicted)} predicted_ms={Field(PredictedMs)}";

        internal static TranslationTimings Read(JsonElement metrics) =>
            new(Count(metrics, "cache_n"), Count(metrics, "prompt_n"), Milliseconds(metrics, "prompt_ms"),
                Count(metrics, "predicted_n"), Milliseconds(metrics, "predicted_ms"));

        private static string Field(IFormattable? value) =>
            value?.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? "na";

        private static int? Count(JsonElement metrics, string key) =>
            metrics.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out int value) ? value : null;

        private static double? Milliseconds(JsonElement metrics, string key) =>
            metrics.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out double value) ? value : null;
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

    private static async Task<LocalServer> StartServer(HttpClient http, string executable, string model,
        string[] arguments, string backend, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var start = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var arg in arguments.Concat(["--host", "127.0.0.1", "--port", port.ToString(), "--model", model]))
            start.ArgumentList.Add(arg);
        var server = new LocalServer(Process.Start(start) ?? throw new InvalidOperationException("Could not start model server."),
            $"http://127.0.0.1:{port}", backend, token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (!server.Process.HasExited)
            {
                try
                {
                    using var response = await http.GetAsync(server.Url + "/health", timeout.Token);
                    if (response.IsSuccessStatusCode) return server;
                }
                catch (HttpRequestException) { }
                await Task.Delay(200, timeout.Token);
            }
            throw new InvalidOperationException($"{Path.GetFileName(executable)} exited ({server.Process.ExitCode}). Check runtime and model compatibility.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            server.Dispose();
            throw new TimeoutException($"{Path.GetFileName(executable)} did not load the model within 60 seconds.");
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    private sealed class LocalServer : IDisposable
    {
        private readonly CancellationTokenRegistration cancellation;
        internal Process Process { get; }
        internal string Url { get; }
        internal string Backend { get; }

        internal LocalServer(Process process, string url, string backend, CancellationToken token)
        {
            Process = process;
            Url = url;
            Backend = backend;
            cancellation = token.Register(Terminate);
        }

        public void Dispose()
        {
            cancellation.Dispose();
            Terminate();
            Process.Dispose();
        }

        private void Terminate()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception error) { Debug.WriteLine(error); }
        }
    }

}
