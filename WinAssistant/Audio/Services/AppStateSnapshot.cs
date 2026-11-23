using WinAssistant.Audio.Models;

namespace WinAssistant.Audio.Services;

public sealed record AppStateSnapshot(
    IReadOnlyList<AudioDeviceInfo> AllOutputs,
    IReadOnlyList<AudioDeviceInfo> AllInputs,
    AudioDeviceInfo? CurrentOutput,
    AudioDeviceInfo? CurrentInput,
    bool AudioEnabled,
    bool RecoverProfileDevicesByName,
    bool LockOutput,
    bool LockInput,
    bool StartWithWindows,
    bool SilentStart,
    string Theme,
    string Language,
    IReadOnlyList<AudioConfigurationProfile> Profiles,
    string? ActiveProfileId,
    string NextProfileHotkey,
    string PreviousProfileHotkey);
