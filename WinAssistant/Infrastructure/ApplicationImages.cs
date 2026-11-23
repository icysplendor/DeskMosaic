using System;
using System.Windows.Media.Imaging;

namespace WinAssistant.Infrastructure;

internal static class ApplicationImages
{
    // Window.Icon may decode the ICO's 16px frame. Title bars render at 28 DIP,
    // so use a dedicated high-resolution PNG and let WPF scale for monitor DPI.
    public static BitmapSource HeaderIcon { get; } = LoadHeaderIcon();

    private static BitmapSource LoadHeaderIcon()
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri("pack://application:,,,/Resources/AppHeader.png", UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
