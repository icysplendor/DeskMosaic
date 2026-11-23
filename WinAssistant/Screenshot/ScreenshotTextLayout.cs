using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinAssistant.Screenshot;

internal static class ScreenshotTextLayout
{
    internal static FormattedText Format(string text, double fontSize, FontWeight weight, Brush color, double width)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            fontSize, color, 1)
        {
            MaxTextWidth = Math.Max(1, width),
            // Explicitly setting None enables emergency wrapping of long
            // words/URLs, matching TextBox.Wrap (the constructor allows overflow).
            Trimming = TextTrimming.None
        };

    internal static Rect EditorBounds(string text, double fontSize, FontWeight weight, Point anchor, Rect limits)
    {
        var line = Format("Ag", fontSize, weight, Brushes.Black, 1000000).Height;
        var left = Math.Clamp(anchor.X, limits.Left, limits.Right - Math.Min(80, limits.Width));
        var natural = Format(string.IsNullOrEmpty(text) ? "输入文字…" : text, fontSize, weight, Brushes.Black, 1000000);
        var width = Math.Min(limits.Right - left, Math.Max(80, Math.Ceiling(natural.WidthIncludingTrailingWhitespace) + 2));
        var measured = Format(string.IsNullOrEmpty(text) || text.EndsWith('\n') ? text + " " : text,
            fontSize, weight, Brushes.Black, width);
        var height = Math.Min(limits.Height, Math.Ceiling(Math.Max(line, measured.Height)) + 2);
        var top = Math.Clamp(anchor.Y, limits.Top, limits.Bottom - height);
        return new Rect(left, top, width, height);
    }

    internal static void Draw(DrawingContext context, TextAnnotation text)
    {
        context.PushClip(new RectangleGeometry(new Rect(0, 0, text.Bounds.Width, text.Bounds.Height)));
        context.DrawText(Format(text.Text, text.FontSize, text.FontWeight,
            new SolidColorBrush(text.Color), text.Bounds.Width), new Point());
        context.Pop();
    }

    // The preview and exported bitmap use the same WPF glyph layout/font,
    // avoiding GDI+'s different line breaks and single-line DrawString overload.
    internal static BitmapSource Render(TextAnnotation text)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) Draw(context, text);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(text.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(text.Bounds.Height)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
