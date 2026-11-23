using System.Net.Http;
using System.Security.Cryptography;

namespace WinAssistant.Translation;

internal sealed record TranslationModelDownload(string Url, string FileName, long Size, string Sha256)
{
    internal static readonly TranslationModelDownload Llama = new(TranslationOptions.LlamaModelUrl,
        "Hy-MT2-1.8B-Q4_K_M.gguf", 1_133_080_448, "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699");

    internal static readonly TranslationModelDownload Lmt = new(
        "https://huggingface.co/mradermacher/LMT-60-0.6B-GGUF/resolve/86942b9f925bfccd4196d4982578b46413a1f73f/LMT-60-0.6B.Q8_0.gguf",
        "LMT-60-0.6B.Q8_0.gguf", 804_753_504, "687f437379cf9c20f37e8bb973e084ab2bb4604f01b53292392253129df7261e");

    internal static readonly TranslationModelDownload OnlineSpeaker = new(
        "https://huggingface.co/nvidia/Nemotron-3-Diarization/resolve/6a002fd9cad5e8dfa624d97a7ee0388a7153243d/Nemotron-3-Diarization.q8_0.gguf",
        "Nemotron-3-Diarization.q8_0.gguf", 107_012_128,
        "08456d9e22cd9a323c0364d98375f3746d6e68507ebb705cd46438c534c7a3a1");

    internal static string ModelDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinAssistant", "Models");

    internal string ResolvePath(string? configured)
    {
        if (HasExpectedSize(configured)) return configured!;
        string downloaded = Path.Combine(ModelDirectory, FileName);
        return HasExpectedSize(downloaded) ? downloaded : "";
    }

    internal bool HasExpectedSize(string? path) =>
        !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) && File.Exists(path) && new FileInfo(path).Length == Size;

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromHours(1) };

    internal async Task<string> DownloadAsync(IProgress<int> progress)
    {
        var directory = ModelDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        if (await IsVerifiedAsync(path).ConfigureAwait(false)) return path;

        var temporary = $"{path}.{Guid.NewGuid():N}.part";
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromHours(1));
            var token = deadline.Token;
            using var response = await Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int shown = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    total += count;
                    if (total > Size) throw new InvalidDataException("Unexpected model size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    var percent = (int)(total * 100 / Size);
                    if (percent != shown) progress.Report(shown = percent);
                }
            }
            if (!await IsVerifiedAsync(temporary).ConfigureAwait(false))
                throw new InvalidDataException("Model download did not match the published SHA-256.");
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal async Task<bool> IsVerifiedAsync(string path, CancellationToken token = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != Size) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
        return hash.Equals(Sha256, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record StreamingSpeechModel(string Id, string Chinese, string English, string[] Languages,
    TranslationModelDownload Download, string[] ArchiveFiles)
{
    internal static readonly string[] RuntimeFiles = ["encoder.int8.onnx", "decoder.onnx", "joiner.int8.onnx", "tokens.txt"];

    internal static readonly StreamingSpeechModel Correction = Create(
        "zh-en-correction", "中英段末修正 · 1920 ms", "Chinese/English second pass · 1920 ms", ["auto", "Chinese", "English"],
        "sherpa-onnx-x-asr-1920ms-streaming-zipformer-transducer-zh-en-punct-int8-2026-06-05",
        133_894_723, "fdae310438a22e4acba3ae8280320d25365e66da269fcac2a906c98c51e705e4", RuntimeFiles);

    internal static readonly StreamingSpeechModel[] Catalog =
    [
        Create("zh-en", "中英流式 · 160 ms", "Chinese/English · 160 ms", ["auto", "Chinese", "English"],
            "sherpa-onnx-x-asr-160ms-streaming-zipformer-transducer-zh-en-punct-int8-2026-06-05",
            133_898_007, "8a6fca056e1a342546edd78be4d50274e2c01898e7b8ae8fc336f6410319c399", RuntimeFiles),
        Create("ko", "韩语流式", "Korean streaming", ["Korean"],
            "sherpa-onnx-streaming-zipformer-korean-2024-06-16", 418_218_652,
            "e346a5882a409650472be17326237e24df7bf409db6b4a8a52e1a61422bf2500",
            TransducerFiles("epoch-99-avg-1")),
        Create("fr", "法语流式", "French streaming", ["French"],
            "sherpa-onnx-streaming-zipformer-fr-2023-04-14", 398_444_115,
            "77d4cbd61dcfa55fd2c14efc0ce5c6798be5931c05f35df5bab60c921950bb8c",
            TransducerFiles("epoch-29-avg-9-with-averaged-model"))
    ];

    internal string ArchiveDirectory => Download.FileName[..^".tar.bz2".Length];

    private static string[] TransducerFiles(string checkpoint) =>
        [$"encoder-{checkpoint}.int8.onnx", $"decoder-{checkpoint}.onnx", $"joiner-{checkpoint}.int8.onnx", "tokens.txt"];

    private static StreamingSpeechModel Create(string id, string chinese, string english, string[] languages,
        string archive, long size, string sha256, string[] files) => new(id, chinese, english, languages,
        new($"https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{archive}.tar.bz2",
            $"{archive}.tar.bz2", size, sha256), files);
}
