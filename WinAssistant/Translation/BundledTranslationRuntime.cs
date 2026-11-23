using System.IO.Compression;
using System.Security.Cryptography;

namespace WinAssistant.Translation;

internal static class BundledTranslationRuntime
{
    internal static string Resolve(string? selected, string name, string executable)
    {
        if (!string.IsNullOrWhiteSpace(selected) && Path.IsPathFullyQualified(selected) && File.Exists(selected))
            return selected;

        var assembly = typeof(BundledTranslationRuntime).Assembly;
        using var archive = assembly.GetManifestResourceStream($"TranslationRuntime/{name}.zip");
        if (archive is null)
            throw new InvalidOperationException(
                "当前构建缺少内置推理引擎，请使用正式发布版或在录写/翻译设置选择自定义引擎。\n" +
                "This build is missing the bundled inference engine. Use a release or select a custom engine.");

        var hash = Convert.ToHexString(SHA256.HashData(archive));
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinAssistant", "TranslationRuntimes", name);
        var destination = Path.Combine(root, hash);
        var executablePath = Path.Combine(destination, executable);
        if (File.Exists(executablePath)) return executablePath;

        // Publish a complete directory so concurrent sessions never see a half-extracted engine.
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, $".extract-{Guid.NewGuid():N}");
        try
        {
            archive.Position = 0;
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
            zip.ExtractToDirectory(staging);
            if (!File.Exists(Path.Combine(staging, executable)))
                throw new InvalidDataException($"Bundled {name} archive is missing {executable}.");
            using var license = assembly.GetManifestResourceStream($"TranslationRuntime/{name}-license.txt")
                ?? throw new InvalidDataException($"Bundled {name} license is missing.");
            using (var output = File.Create(Path.Combine(staging, "Upstream-LICENSE.txt")))
                license.CopyTo(output);
            try { Directory.Move(staging, destination); }
            catch (IOException) when (File.Exists(executablePath)) { }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        return executablePath;
    }
}
