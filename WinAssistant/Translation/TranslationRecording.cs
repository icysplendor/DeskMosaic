using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace WinAssistant.Translation;

internal sealed class TranslationRecordingException(string message, Exception inner) : IOException(message, inner);

internal sealed class TranslationRecording
{
    private readonly BufferedAudio<(byte[] Packet, long Milliseconds)> packets = new();
    private readonly CancellationTokenSource abort = new();
    private readonly Task writer;
    private readonly string temporary;
    private readonly string destination;

    internal TranslationRecording(Task<string> executable, bool stereo, string? outputDirectory = null)
    {
        string directory = outputDirectory ?? DownloadsDirectory();
        Directory.CreateDirectory(directory);
        string filename = $"Live-translation-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        destination = Path.Combine(directory, filename + ".wav");
        temporary = Path.Combine(directory, "." + filename + ".part");
        writer = Task.Run(() => WriteAsync(executable, stereo));
    }

    internal void Append(byte[] packet)
    {
        if (!packets.TryAppend((packet, Environment.TickCount64), packet.Length))
            throw new IOException("录音写入速度跟不上。 / The audio recorder could not keep up.");
    }

    internal async Task<string?> FinishAsync()
    {
        packets.Complete();
        using (abort)
        {
            abort.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await writer;
                if (!File.Exists(temporary)) return null;
                File.Move(temporary, destination);
                return destination;
            }
            catch (Exception error)
            {
                throw new TranslationRecordingException(File.Exists(temporary)
                    ? $"录音未能完成，临时文件 / Unfinished recording: {temporary}"
                    : "录音未能保存。 / Could not save the recording.", error);
            }
        }
    }

    private async Task WriteAsync(Task<string> executable, bool stereo)
    {
        var token = abort.Token;
        try
        {
            await using var queued = packets.ReadAllAsync(token).GetAsyncEnumerator();
            if (!await queued.MoveNextAsync()) return;
            var info = new ProcessStartInfo(await executable.WaitAsync(token))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
            };
            info.ArgumentList.Add("--record");
            info.ArgumentList.Add(stereo ? "2" : "1");
            info.Environment["ASSISTANT_RECORDING_PATH"] = temporary;
            using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动录音。 / Could not start the recorder.");
            using var kill = token.Register(() => Stop(process));
            try
            {
                // Preserve capture timestamps when replaying audio collected before the helper launched.
                await using (var input = process.StandardInput.BaseStream)
                {
                    var timestamp = new byte[8];
                    do
                    {
                        var (packet, milliseconds) = queued.Current;
                        BinaryPrimitives.WriteInt64LittleEndian(timestamp, milliseconds);
                        await input.WriteAsync(packet.AsMemory(0, 12), token);
                        await input.WriteAsync(timestamp, token);
                        await input.WriteAsync(packet.AsMemory(12), token);
                    } while (await queued.MoveNextAsync());
                }
                await process.WaitForExitAsync(token);
                if (process.ExitCode != 0) throw new IOException($"Recorder exited ({process.ExitCode}).");
            }
            catch
            {
                Stop(process);
                throw;
            }
        }
        finally { packets.Complete(); }

        static void Stop(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
        }
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


// Two capture sources can write concurrently. Bound both the number of
// packets and their bytes, since hardware buffer sizes vary.
internal sealed class BufferedAudio<T>
{
    private const int MaxBytes = 64 * 1024 * 1024;
    private readonly Channel<(T Value, int Bytes)> queue = Channel.CreateBounded<(T, int)>(
        new BoundedChannelOptions(16384) { SingleReader = true });
    private int bytesBuffered;

    internal bool TryAppend(T value, int bytes)
    {
        if (Interlocked.Add(ref bytesBuffered, bytes) <= MaxBytes && queue.Writer.TryWrite((value, bytes)))
            return true;
        Interlocked.Add(ref bytesBuffered, -bytes);
        return false;
    }

    internal async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var (value, bytes) in queue.Reader.ReadAllAsync(token))
        {
            Interlocked.Add(ref bytesBuffered, -bytes);
            yield return value;
        }
    }

    internal void Complete() => queue.Writer.TryComplete();
}
