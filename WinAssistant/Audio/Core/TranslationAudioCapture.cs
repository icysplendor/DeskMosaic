using System.Runtime.InteropServices;

namespace WinAssistant.Audio.Core;

internal static class TranslationAudioCapture
{
    private const uint Loopback = 0x00020000;
    private const uint SilentBuffer = 2;

    internal static Task Start(bool microphone, Action<float[], int> receive, Action tick, CancellationToken token) =>
        Task.Factory.StartNew(() => Capture(microphone, receive, tick, token), token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Capture(bool microphone, Action<float[], int> receive, Action tick, CancellationToken token)
    {
        var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
        object? endpoint = null;
        object? clientObject = null;
        object? captureObject = null;
        nint format = 0;
        IAudioClient? running = null;
        try
        {
            Check(enumerator.GetDefaultAudioEndpoint(microphone ? EDataFlow.Capture : EDataFlow.Render,
                ERole.Multimedia, out var devicePointer));
            try { endpoint = Marshal.GetObjectForIUnknown(devicePointer); }
            finally { Marshal.Release(devicePointer); }
            var id = typeof(IAudioClient).GUID;
            Check(((IMMDevice)endpoint).Activate(ref id, 23, 0, out clientObject));
            var client = (IAudioClient)clientObject;
            Check(client.GetMixFormat(out format));
            int tag = (ushort)Marshal.ReadInt16(format);
            int channels = Marshal.ReadInt16(format, 2);
            int sampleRate = Marshal.ReadInt32(format, 4);
            int alignment = Marshal.ReadInt16(format, 12);
            int bits = Marshal.ReadInt16(format, 14);
            if (tag == 0xfffe)
                tag = Marshal.ReadInt32(format, 24);
            if (channels <= 0 || sampleRate <= 0 || alignment < channels * (bits / 8) ||
                !(tag == 3 && bits == 32 || tag == 1 && bits is 16 or 24 or 32))
                throw new InvalidOperationException("Unsupported WASAPI audio format.");
            // WASAPI playback loopback captures the default render endpoint without screen capture.
            // macOS uses ScreenCaptureKit for the corresponding playback source.
            Check(client.Initialize(0, microphone ? 0u : Loopback, 1_000_000, 0, format, 0));
            id = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref id, out captureObject));
            var capture = (IAudioCaptureClient)captureObject;
            Check(client.Start());
            running = client;
            while (!token.IsCancellationRequested)
            {
                Check(capture.GetNextPacketSize(out var frames));
                while (frames > 0 && !token.IsCancellationRequested)
                {
                    Check(capture.GetBuffer(out var data, out var count, out var flags, out _, out _));
                    try
                    {
                        var samples = new float[count];
                        if ((flags & SilentBuffer) == 0)
                        {
                            for (var frame = 0; frame < samples.Length; frame++)
                            {
                                for (var channel = 0; channel < channels; channel++)
                                {
                                    var offset = frame * alignment + channel * (bits / 8);
                                    samples[frame] += ReadSample(data, offset, tag, bits) / channels;
                                }
                            }
                        }
                        receive(samples, sampleRate);
                    }
                    finally { Check(capture.ReleaseBuffer(count)); }
                    Check(capture.GetNextPacketSize(out frames));
                }
                tick();
                token.WaitHandle.WaitOne(15);
            }
        }
        finally
        {
            if (running is not null) _ = running.Stop();
            if (format != 0) Marshal.FreeCoTaskMem(format);
            foreach (var resource in new[] { captureObject, clientObject, endpoint, enumerator })
                if (resource is not null) Marshal.ReleaseComObject(resource);
        }
    }

    private static float ReadSample(nint data, int offset, int tag, int bits)
    {
        if (tag == 3) return BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, offset));
        return bits switch
        {
            16 => Marshal.ReadInt16(data, offset) / 32768f,
            24 => (Marshal.ReadByte(data, offset) | Marshal.ReadByte(data, offset + 1) << 8 |
                   (sbyte)Marshal.ReadByte(data, offset + 2) << 16) / 8388608f,
            _ => Marshal.ReadInt32(data, offset) / 2147483648f
        };
    }

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, nint format, nint session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closestFormat);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong position, out ulong counter);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
