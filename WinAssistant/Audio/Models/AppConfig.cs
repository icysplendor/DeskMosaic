namespace WinAssistant.Audio.Models;

public sealed class AppConfig
{
    public bool AudioEnabled { get; set; } = true;
    public bool RecoverProfileDevicesByName { get; set; } = true;
    public List<string> OutputWhitelist { get; set; } = [];
    public List<string> InputWhitelist { get; set; } = [];
    public Dictionary<string, float> OutputVolumes { get; set; } = [];
    public Dictionary<string, float> InputVolumes { get; set; } = [];
    public string? PreferredOutputDeviceId { get; set; }
    public string? PreferredInputDeviceId { get; set; }
    public bool LockOutputDevice { get; set; } = true;
    public bool LockInputDevice { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool SilentStart { get; set; }
    public string Theme { get; set; } = "Light";
    public string Language { get; set; } = "zh-CN";
    public HotkeyConfig Hotkeys { get; set; } = new();
    public List<AudioConfigurationProfile> Profiles { get; set; } = [];
    public string? ActiveProfileId { get; set; }
}

public sealed class HotkeyConfig
{
    public string NextOutput { get; set; } = string.Empty;
    public string NextInput { get; set; } = string.Empty;
    public string NextProfile { get; set; } = string.Empty;
    public string PreviousProfile { get; set; } = string.Empty;
}

public sealed class AudioConfigurationProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string OutputDeviceId { get; set; } = string.Empty;
    public string InputDeviceId { get; set; } = string.Empty;
    public string? OutputDeviceName { get; set; }
    public string? InputDeviceName { get; set; }
}
