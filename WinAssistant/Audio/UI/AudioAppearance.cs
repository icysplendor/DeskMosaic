using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinAssistant.Audio.UI;

public static class ThemeManager
{
    internal const string IceWhite = "#F0F1F6";
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static string NormalizeTheme(string? theme) =>
        string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;

    public static void Apply(string? theme)
    {
        // The unified WPF shell applies the palette when it renders. Keeping this
        // method makes the audio state independent from a particular window.
    }

    public static void ApplyWindowFrame(Window window, string? theme)
    {
        void ApplyFrame()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var dark = NormalizeTheme(theme) == Dark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
            }
            var roundedCorners = 2;
            _ = DwmSetWindowAttribute(handle, 33, ref roundedCorners, sizeof(int));
        }

        if (window.IsLoaded) ApplyFrame();
        else window.SourceInitialized += (_, _) => ApplyFrame();
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

public static class LocalizationManager
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    public static string NormalizeLanguage(string? language) =>
        string.Equals(language, English, StringComparison.OrdinalIgnoreCase) ? English : Chinese;
}
