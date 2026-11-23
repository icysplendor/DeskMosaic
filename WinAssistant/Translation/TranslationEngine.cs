using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WinAssistant.Translation;

internal enum CaptionReview
{
    None, Pending, Kept, Revised, Disabled, ModelMissing, LanguageUnsupported,
    ModelPreparing, ModelFailed, NoChinese, AudioUnaligned, OutsideLimits, QueueFull, Unavailable
}

internal sealed record TranslationCaption(int Id, bool Microphone, string Source, string? Translation, bool SourceFinal = true, bool TranslationFinal = true, int SpeakerId = 0, long StartMs = 0, long EndMs = 0, CaptionReview Review = CaptionReview.None)
{
    internal string TimeRange => $"{FormatTime(StartMs)} – {FormatTime(EndMs)}";

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        string clock = time.ToString(@"mm\:ss\.f");
        return time.TotalHours >= 1 ? $"{(long)time.TotalHours:00}:{clock}" : clock;
    }
}

internal sealed class TranslationEngine
{
    internal async Task RunAsync(TranslationOptions options, Action<string?> status,
        Action<TranslationCaption> caption, CancellationToken token, Action<string>? recordingSaved = null)
    {
        if (!options.SystemAudio && !options.Microphone)
            throw new InvalidOperationException("Select an audio source / 请选择音源。");
        var model = options.ModelForSource(options.SelectedSourceLanguage)
            ?? throw new InvalidOperationException("请在录写/翻译设置中下载或选择对应的流式语音模型。 / Install a streaming speech model in Transcribe / Translate settings.");
        string translationModel = options.TranscribeOnly ? "" : options.SelectedTranslationModel;
        if (!options.TranscribeOnly && translationModel.Length == 0)
            throw new InvalidOperationException("请在录写/翻译设置中下载或选择当前的文本翻译模型。 / Install the selected text translation model in Transcribe / Translate settings.");
        var recognizerPath = await Task.Run(() =>
            BundledTranslationRuntime.Resolve(null, "streaming", "streaming-asr.exe"), token);
        token.ThrowIfCancellationRequested();
        if (options.TranscribeOnly)
        {
            await StreamingRecognition.RunAsync(options, model, options.PathFor(model), recognizerPath, () => status(null),
                null, caption, null, token, recordingSaved);
            return;
        }

        var llamaPath = await Task.Run(() =>
            BundledTranslationRuntime.Resolve(options.LlamaServer, "llama", "llama-server.exe"), token);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var active = session.Token;
        var key = Guid.NewGuid().ToString("N");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        var llamaStartup = StartTranslation();
        string target = options.SelectedTargetLanguage;
        string sourceLanguage = options.SelectedSourceLanguage;
        try
        {
            await StreamingRecognition.RunAsync(options, model, options.PathFor(model), recognizerPath, () => status(null),
                async () =>
                {
                    var server = await llamaStartup.WaitAsync(active);
                    status(server.Backend);
                }, caption, TranslateText, active, recordingSaved);
        }
        finally
        {
            session.Cancel();
            try { using var server = await llamaStartup; }
            catch (Exception) { /* The translation worker reports startup failures. */ }
        }

        async Task<LocalServer> StartTranslation()
        {
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

        async Task<string> TranslateText(string source, bool provisional, Action<string> preview, CancellationToken cancellation)
        {
            string inputLanguage = sourceLanguage;
            string instruction = $"Translate the following text into {target}.\n\n{source}";
            if (options.UseLmtTranslation)
            {
                if (inputLanguage == "auto")
                    inputLanguage = source.Any(character => character is >= '\u3400' and <= '\u9fff') ? "Chinese" : "English";
                if (inputLanguage == target)
                {
                    // In Chinese output mode, let LMT translate English mixed into Mandarin.
                    if (target != "Chinese" || !source.Any(char.IsAsciiLetter)) return source;
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
            using var translated = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            translated.EnsureSuccessStatusCode();
            using var stream = new StreamReader(await translated.Content.ReadAsStreamAsync(cancellation));
            var result = new StringBuilder();
            long last = 0;
            while (await stream.ReadLineAsync(cancellation) is { } line)
            {
                if (line == "data: [DONE]") return result.ToString().Trim();
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                using var update = JsonDocument.Parse(line[6..]);
                if (!update.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0 ||
                    !choices[0].TryGetProperty("delta", out var delta) || !delta.TryGetProperty("content", out var content)) continue;
                result.Append(content.GetString());
                long now = Environment.TickCount64;
                if (result.Length == 0 || now - last < 120) continue;
                preview(result.ToString().Trim());
                last = now;
            }
            throw new IOException("Translation stream ended early.");
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
