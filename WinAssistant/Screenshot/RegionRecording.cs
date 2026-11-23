using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WinAssistant.Settings;

namespace WinAssistant.Screenshot;

internal sealed class RegionRecording
{
    private static readonly Guid DownloadsFolder = new("374DE290-123F-4565-9164-39C4925E467B");

    private readonly Process encoder;
    private readonly SemaphoreSlim frameSlot = new(1);
    private readonly string destination;
    private readonly string partialPath;
    private readonly Task<string> diagnostics;
    private Task pendingFrame = Task.CompletedTask;
    private bool finishing;
    private volatile bool hasFrame;

    internal int Width { get; }
    internal int Height { get; }
    internal byte[] Frame { get; }
    internal Task<string> Completion { get; }

    internal static string DefaultDirectory()
    {
        var folder = DownloadsFolder;
        if (SHGetKnownFolderPath(ref folder, 0, IntPtr.Zero, out var path) == 0)
        {
            try
            {
                if (Marshal.PtrToStringUni(path) is { Length: > 0 } directory) return directory;
            }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);

    internal RegionRecording(RegionRecordingSettings settings, int width, int height)
    {
        var executable = FindEncoder();
        var directory = string.IsNullOrWhiteSpace(settings.Directory) ? DefaultDirectory() : settings.Directory;
        Directory.CreateDirectory(directory);
        destination = Path.Combine(directory, $"Recording-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.mp4");
        partialPath = destination + ".part";
        var scale = Math.Min(1, Math.Min(settings.MaxHeight * 16d / 9 / width, (double)settings.MaxHeight / height));
        Width = Math.Max(2, (int)(width * scale) / 2 * 2);
        Height = Math.Max(2, (int)(height * scale) / 2 * 2);
        Frame = new byte[checked(Width * Height * 4)];

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Leave more CPU headroom once the output exceeds the pixel rate of 4K at 30 fps.
        var preset = (long)Width * Height * settings.FrameRate > 250_000_000 ? "superfast" : "veryfast";
        var arguments = new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", $"{Width}x{Height}", "-framerate", settings.FrameRate.ToString(),
            "-use_wallclock_as_timestamps", "1", "-i", "pipe:0", "-an", "-vsync", "vfr",
            "-c:v", "libx264", "-preset", preset, "-tune", "zerolatency", "-pix_fmt", "yuv420p",
            "-g", (2 * settings.FrameRate).ToString()
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(settings.BitrateMbps == 0 ? "-crf" : "-b:v");
        startInfo.ArgumentList.Add(settings.BitrateMbps == 0 ? "18" : $"{settings.BitrateMbps}M");
        foreach (var argument in new[] { "-movflags", "+empty_moov+frag_keyframe+default_base_moof", "-f", "mp4", "pipe:1" })
            startInfo.ArgumentList.Add(argument);
        encoder = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 FFmpeg。");
        diagnostics = encoder.StandardError.ReadToEndAsync();
        Completion = Task.Run(() => SaveOutputAsync(settings.CacheMiB));
    }

    internal bool TryReserveFrame() => !finishing && !Completion.IsCompleted && frameSlot.Wait(0);

    internal void ReleaseFrame() => frameSlot.Release();

    internal void SubmitFrame()
    {
        pendingFrame = Task.Run(async () =>
        {
            try
            {
                await encoder.StandardInput.BaseStream.WriteAsync(Frame).ConfigureAwait(false);
                hasFrame = true;
            }
            catch (IOException) { } // Exit status and stderr are read by SaveOutputAsync.
            catch (ObjectDisposedException) { }
            finally { ReleaseFrame(); }
        });
    }

    internal async Task StopAsync()
    {
        if (finishing) return;
        finishing = true;
        await pendingFrame.ConfigureAwait(false);
        try { encoder.StandardInput.Close(); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private async Task<string> SaveOutputAsync(int cacheMiB)
    {
        FileStream? output = null;
        try
        {
            const int blockSize = 1024 * 1024;
            var cache = new List<(byte[] Buffer, int Count)>();
            using var pipe = encoder.StandardOutput.BaseStream;
            while (true)
            {
                var buffer = new byte[blockSize];
                var count = await pipe.ReadAtLeastAsync(buffer, buffer.Length, false).ConfigureAwait(false);
                if (count == 0) break;
                cache.Add((buffer, count));
                if (cache.Count == cacheMiB) await FlushAsync().ConfigureAwait(false);
            }

            await encoder.WaitForExitAsync().ConfigureAwait(false);
            if (encoder.ExitCode != 0 || !hasFrame)
            {
                var details = (await diagnostics.ConfigureAwait(false)).Trim();
                throw new IOException(details.Length == 0 ? "没有收到可编码的录制画面。" : details);
            }

            await FlushAsync().ConfigureAwait(false);
            if (output is null) throw new IOException("FFmpeg 没有输出视频。");
            await output.DisposeAsync().ConfigureAwait(false);
            output = null;
            File.Move(partialPath, destination);
            return destination;

            async Task FlushAsync()
            {
                if (cache.Count == 0) return;
                output ??= new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    blockSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                foreach (var block in cache)
                    await output.WriteAsync(block.Buffer.AsMemory(0, block.Count)).ConfigureAwait(false);
                cache.Clear();
            }
        }
        catch (Exception error)
        {
            try { if (!encoder.HasExited) encoder.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            if (File.Exists(partialPath))
                throw new IOException($"{error.Message}\n已写入的片段保留在 {partialPath}", error);
            throw;
        }
        finally
        {
            if (output is not null) await output.DisposeAsync().ConfigureAwait(false);
            encoder.Dispose();
        }
    }

    internal static string FindEncoder()
    {
        var assembly = typeof(RegionRecording).Assembly;
        using var binary = assembly.GetManifestResourceStream("RecordingEncoder/ffmpeg.exe")
            ?? throw new FileNotFoundException("当前构建缺少内置录屏编码器，请使用正式发布的 EXE。");
        var digest = Convert.ToHexString(SHA256.HashData(binary));
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinAssistant", "RecordingEncoder", digest);
        var executable = Path.Combine(directory, "ffmpeg.exe");
        if (File.Exists(executable)) return executable;

        var root = Path.GetDirectoryName(directory)!;
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, $".extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            using var license = assembly.GetManifestResourceStream("RecordingEncoder/ffmpeg-license.txt")
                ?? throw new InvalidDataException("内置录屏编码器缺少许可证。");
            using (var output = File.Create(Path.Combine(staging, "ffmpeg-license.txt")))
                license.CopyTo(output);

            binary.Position = 0;
            using (var output = File.Create(Path.Combine(staging, "ffmpeg.exe")))
                binary.CopyTo(output);
            // Publish both files together so concurrent processes only see a complete encoder.
            try { Directory.Move(staging, directory); }
            catch (IOException) when (File.Exists(executable)) { }
            return executable;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
