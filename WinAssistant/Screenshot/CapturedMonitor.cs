using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace WinAssistant.Screenshot;

internal sealed class CapturedMonitor : IDisposable
{
    internal Forms.Screen Screen { get; }
    internal Rect Bounds { get; }
    internal Bitmap Bitmap { get; }
    internal BitmapSource ImageSource { get; }
    internal double Scale { get; set; } = 1;

    private CapturedMonitor(Forms.Screen screen, Bitmap bitmap, BitmapSource imageSource)
    {
        Screen = screen;
        Bounds = new Rect(
            screen.Bounds.Left,
            screen.Bounds.Top,
            screen.Bounds.Width,
            screen.Bounds.Height);
        Bitmap = bitmap;
        ImageSource = imageSource;
    }

    internal static IReadOnlyList<CapturedMonitor> CaptureAll()
    {
        return Forms.Screen.AllScreens.Select(screen =>
        {
            var bitmap = new Bitmap(
                screen.Bounds.Width,
                screen.Bounds.Height,
                PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    screen.Bounds.Left,
                    screen.Bounds.Top,
                    0,
                    0,
                    bitmap.Size,
                    CopyPixelOperation.SourceCopy);
            }

            var handle = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    handle,
                    nint.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return new CapturedMonitor(screen, bitmap, source);
            }
            finally
            {
                Infrastructure.NativeMethods.DeleteObject(handle);
            }
        }).ToArray();
    }

    internal static BitmapSource CreateWarmUpImage()
    {
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Black);
        }
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                nint.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            Infrastructure.NativeMethods.DeleteObject(handle);
        }
    }

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}
