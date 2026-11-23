using WinAssistant.Audio.Models;

namespace WinAssistant.Audio.Core;

public interface IAudioDeviceManager : IDisposable
{
    event EventHandler<AudioDeviceKindEventArgs>? DefaultDeviceChanged;
    event EventHandler<AudioVolumeChangedEventArgs>? VolumeChanged;

    void Start();
    void Stop();
    IReadOnlyList<AudioDeviceInfo> Enumerate(AudioDeviceKind kind);
    IReadOnlyList<AudioEndpointIdentity> EnumerateKnownEndpoints(AudioDeviceKind kind);
    string? GetDefaultDeviceId(AudioDeviceKind kind);
    void SetDefaultDevice(string deviceId);
    float GetVolume(string deviceId);
    AudioDeviceVolumeState GetVolumeState(string deviceId);
    void SetVolume(string deviceId, float volume);
}
