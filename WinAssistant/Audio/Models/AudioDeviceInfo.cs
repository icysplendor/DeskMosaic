namespace WinAssistant.Audio.Models;

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    AudioDeviceKind Kind,
    bool IsDefault,
    float Volume,
    bool IsMuted)
{
    public static bool IdEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
