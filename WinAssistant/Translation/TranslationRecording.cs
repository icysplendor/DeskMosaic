using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace WinAssistant.Translation;

internal sealed class TranslationRecordingException(string message, Exception inner) : IOException(message, inner);

internal sealed class TranslationRecording
{
    private readonly Channel<(byte[] Packet, long Milliseconds)> packets = Channel.CreateBounded<(byte[], long)>(
        new BoundedChannelOptions(600) { SingleReader = true });
    private readonly Process process;
    private readonly Task writer;
    private readonly string temporary;
    private readonly string destination;

    internal TranslationRecording(string executable, bool stereo, string? outputDirectory = null)
    {
        string directory = outputDirectory ?? DownloadsDirectory();
        Directory.CreateDirectory(directory);
        string filename = $"Live-translation-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        destination = Path.Combine(directory, filename + ".wav");
        temporary = Path.Combine(directory, "." + filename + ".part");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
        };
        info.ArgumentList.Add("--record");
        info.ArgumentList.Add(stereo ? "2" : "1");
        info.Environment["ASSISTANT_RECORDING_PATH"] = temporary;
        process = Process.Start(info) ?? throw new InvalidOperationException("无法启动录音。 / Could not start the recorder.");
        writer = Task.Run(WriteAsync);
    }

    internal void Append(byte[] packet)
    {
        if (!packets.Writer.TryWrite((packet, Environment.TickCount64)))
            throw new IOException("录音写入速度跟不上。 / The audio recorder could not keep up.");
    }

    internal async Task<string?> FinishAsync()
    {
        packets.Writer.TryComplete();
        using (process)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await writer.WaitAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0) throw new IOException($"Recorder exited ({process.ExitCode}).");
                if (!File.Exists(temporary)) return null;
                File.Move(temporary, destination);
                return destination;
            }
            catch (Exception error)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                try { await writer; } catch (Exception) { /* The original failure is reported below. */ }
                throw new TranslationRecordingException(File.Exists(temporary)
                    ? $"录音未能完成，临时文件 / Unfinished recording: {temporary}"
                    : "录音未能保存。 / Could not save the recording.", error);
            }
        }
    }

    private async Task WriteAsync()
    {
        // Send the capture timestamp separately so ASR continues to use its existing packet format.
        var timestamp = new byte[8];
        try
        {
            using var input = process.StandardInput.BaseStream;
            await foreach (var (packet, milliseconds) in packets.Reader.ReadAllAsync())
            {
                BinaryPrimitives.WriteInt64LittleEndian(timestamp, milliseconds);
                await input.WriteAsync(packet.AsMemory(0, 12));
                await input.WriteAsync(timestamp);
                await input.WriteAsync(packet.AsMemory(12));
            }
        }
        finally { packets.Writer.TryComplete(); }
    }

    internal static string DownloadsDirectory()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        int result = SHGetKnownFolderPath(in downloads, 0, IntPtr.Zero, out IntPtr path);
        try
        {
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            return Marshal.PtrToStringUni(path) ?? throw new IOException("Downloads directory is unavailable.");
        }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
