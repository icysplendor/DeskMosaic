using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using WinAssistant.Audio.Models;

namespace WinAssistant.Settings;

internal sealed class AppConfiguration
{
    public int Version { get; set; } = 5;
    public HotKeyConfiguration? ScreenshotHotKey { get; set; }
    public HotKeyConfiguration? NotebookLastHotKey { get; set; }
    public HotKeyConfiguration? NotebookQuickHotKey { get; set; }
    public HotKeyConfiguration? TranslationHotKey { get; set; }
    public Translation.TranslationOptions Translation { get; set; } = new() { SourceLanguage = "Chinese" };
    public ScreenshotDefaultsConfiguration ScreenshotDefaults { get; set; } = new();
    public RegionRecordingSettings RegionRecording { get; set; } = new();
    public bool LaunchAtLogin { get; set; }
    public AppConfig Audio { get; set; } = new() { SilentStart = true };
}

internal sealed class ScreenshotDefaultsConfiguration
{
    public string Color { get; set; } = "#FFFF0000";
    public double StrokeWidth { get; set; } = 3;
    public double FontSize { get; set; } = 18;
    public string FontWeight { get; set; } = "Medium";
}

internal sealed class RegionRecordingSettings
{
    internal static readonly int[] CacheOptions = [64, 128, 256, 512, 1024];
    internal static readonly int[] HeightOptions = [720, 1080, 1440, 2160];
    internal static readonly int[] FrameRateOptions = [15, 24, 30, 60];
    internal static readonly int[] BitrateOptions = [0, 2, 4, 8, 12, 20, 35, 50, 65, 80, 100];

    public string Directory { get; set; } = "";
    public int CacheMiB { get; set; } = 512;
    public int MaxHeight { get; set; } = 1080;
    public int FrameRate { get; set; } = 30;
    public int BitrateMbps { get; set; }

    internal void Normalize()
    {
        Directory ??= "";
        if (!CacheOptions.Contains(CacheMiB)) CacheMiB = 512;
        if (!HeightOptions.Contains(MaxHeight)) MaxHeight = 1080;
        if (!FrameRateOptions.Contains(FrameRate)) FrameRate = 30;
        if (!BitrateOptions.Contains(BitrateMbps)) BitrateMbps = 0;
    }
}

internal sealed class HotKeyConfiguration
{
    public uint Key { get; set; }
    public uint Modifiers { get; set; }

    [JsonIgnore]
    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & 0x0002) != 0) parts.Add("Ctrl");
            if ((Modifiers & 0x0001) != 0) parts.Add("Alt");
            if ((Modifiers & 0x0004) != 0) parts.Add("Shift");
            if ((Modifiers & 0x0008) != 0) parts.Add("Win");
            parts.Add(KeyName(Key));
            return string.Join(" + ", parts);
        }
    }

    private static string KeyName(uint key)
    {
        if ((key is >= 0x30 and <= 0x39)
            || (key is >= 0x41 and <= 0x5A))
        {
            return ((char)key).ToString();
        }
        if (key is >= 0x70 and <= 0x7B)
        {
            return $"F{key - 0x6F}";
        }
        return key switch
        {
            0x20 => "Space",
            0x2C => "Print Screen",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            _ => $"Key {key}"
        };
    }
}

internal sealed class ConfigurationStore
{
    internal static ConfigurationStore Shared { get; } = new();

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WinAssistant";
    private readonly string directory;
    private readonly string primaryPath;
    private readonly string backupPath;
    private readonly object syncRoot = new();
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    internal AppConfiguration Configuration { get; private set; }

    private ConfigurationStore()
    {
        directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinAssistant");
        primaryPath = Path.Combine(directory, "configuration-v1.json");
        backupPath = Path.Combine(directory, "configuration-v1.backup.json");
        Configuration = Load();
        ApplyLaunchAtLogin(Configuration.LaunchAtLogin);
    }

    internal void Update(Action<AppConfiguration> mutation)
    {
        lock (syncRoot)
        {
            mutation(Configuration);
            Save();
        }
    }

    internal void SetLaunchAtLogin(bool enabled)
    {
        ApplyLaunchAtLogin(enabled);
        Update(configuration =>
        {
            configuration.LaunchAtLogin = enabled;
            configuration.Audio.StartWithWindows = enabled;
        });
    }

    private AppConfiguration Load()
    {
        foreach (var path in new[] { primaryPath, backupPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var value = JsonSerializer.Deserialize<AppConfiguration>(
                    File.ReadAllText(path),
                    jsonOptions);
                if (value is not null && value.Version is 1 or 2 or 3 or 4 or 5)
                {
                    value.Version = 5;
                    value.Audio ??= new AppConfig { SilentStart = true };
                    value.ScreenshotDefaults ??= new ScreenshotDefaultsConfiguration();
                    value.Audio.StartWithWindows = value.LaunchAtLogin;
                    value.Translation ??= new() { SourceLanguage = "Chinese" };
                    NormalizeAudio(value.Audio);
                    NormalizeScreenshot(value.ScreenshotDefaults);
                    value.RegionRecording ??= new RegionRecordingSettings();
                    value.RegionRecording.Normalize();
                    return value;
                }
            }
            catch
            {
                // Try the next recoverable copy.
            }
        }
        return new AppConfiguration();
    }

    private static void NormalizeAudio(AppConfig audio)
    {
        audio.OutputWhitelist ??= [];
        audio.InputWhitelist ??= [];
        audio.OutputVolumes ??= [];
        audio.InputVolumes ??= [];
        audio.Hotkeys ??= new HotkeyConfig();
        audio.Profiles ??= [];
        if (string.IsNullOrWhiteSpace(audio.Hotkeys.NextProfile))
        {
            audio.Hotkeys.NextProfile = audio.Hotkeys.NextOutput ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(audio.Hotkeys.PreviousProfile))
        {
            audio.Hotkeys.PreviousProfile = audio.Hotkeys.NextInput ?? string.Empty;
        }
        audio.Profiles.RemoveAll(profile => string.IsNullOrWhiteSpace(profile.OutputDeviceId) || string.IsNullOrWhiteSpace(profile.InputDeviceId));
        foreach (var (profile, index) in audio.Profiles.Select((profile, index) => (profile, index)))
        {
            if (string.IsNullOrWhiteSpace(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = $"配置 {index + 1}";
        }
        if (audio.Profiles.Count == 0
            && !string.IsNullOrWhiteSpace(audio.PreferredOutputDeviceId)
            && !string.IsNullOrWhiteSpace(audio.PreferredInputDeviceId))
        {
            var migrated = new AudioConfigurationProfile
            {
                Id = "migrated-default-profile",
                Name = "默认配置",
                OutputDeviceId = audio.PreferredOutputDeviceId!,
                InputDeviceId = audio.PreferredInputDeviceId!
            };
            audio.Profiles.Add(migrated);
            audio.ActiveProfileId = migrated.Id;
        }
        if (audio.ActiveProfileId is not null && !audio.Profiles.Any(profile => profile.Id == audio.ActiveProfileId))
        {
            audio.ActiveProfileId = null;
        }
        audio.Theme = string.Equals(audio.Theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        audio.Language = string.Equals(audio.Language, "en-US", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
    }

    private static void NormalizeScreenshot(ScreenshotDefaultsConfiguration defaults)
    {
        var colors = new[]
        {
            "#FFFF0000", "#FFFFF200", "#FF38BDF8", "#FF808080", "#FF708238",
            "#FF000000", "#FFFFFFFF", "#FF8A2BE2", "#FFFF8C00"
        };
        if (!colors.Contains(defaults.Color, StringComparer.OrdinalIgnoreCase)) defaults.Color = colors[0];
        defaults.StrokeWidth = new[] { 1d, 2d, 3d, 5d, 8d, 12d }.Contains(defaults.StrokeWidth) ? defaults.StrokeWidth : 3;
        defaults.FontSize = new[] { 14d, 18d, 24d, 32d, 44d, 60d }.Contains(defaults.FontSize) ? defaults.FontSize : 18;
        defaults.FontWeight = defaults.FontWeight is "Regular" or "Medium" or "SemiBold" or "Bold" ? defaults.FontWeight : "Medium";
    }

    private void Save()
    {
        Directory.CreateDirectory(directory);
        var temporaryPath = primaryPath + ".tmp";
        var json = JsonSerializer.Serialize(Configuration, jsonOptions);
        File.WriteAllText(temporaryPath, json);
        if (IsValidConfiguration(primaryPath))
        {
            File.Copy(primaryPath, backupPath, true);
        }
        File.Move(temporaryPath, primaryPath, true);
    }

    private bool IsValidConfiguration(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var value = JsonSerializer.Deserialize<AppConfiguration>(
                File.ReadAllText(path),
                jsonOptions);
            return value is not null && value.Version is 1 or 2 or 3 or 4 or 5;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyLaunchAtLogin(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                key?.SetValue(RunValueName, $"\"{executable}\" --silent");
            }
        }
        else
        {
            key?.DeleteValue(RunValueName, false);
        }
    }
}
