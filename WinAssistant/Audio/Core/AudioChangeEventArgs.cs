using WinAssistant.Audio.Models;

namespace WinAssistant.Audio.Core;

public sealed class AudioDeviceKindEventArgs(AudioDeviceKind kind) : EventArgs
{
    public AudioDeviceKind Kind { get; } = kind;
}

public sealed class AudioVolumeChangedEventArgs(AudioDeviceKind kind, string deviceId, float volume, bool isMuted) : EventArgs
{
    public AudioDeviceKind Kind { get; } = kind;

    public string DeviceId { get; } = deviceId;

    public float Volume { get; } = volume;

    public bool IsMuted { get; } = isMuted;
}
