using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinAssistant.Screenshot;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var limits = new Rect(-300, -100, 240, 500);
        var anchor = new Point(-290, -90);
        foreach (var text in new[] {
            "First line\nSecond line\nThird line",
            string.Concat(Enumerable.Repeat("自动换行测试 English words 😀 ", 20)),
            "https://example.com/" + new string('x', 300)
        })
        {
            var bounds = ScreenshotTextLayout.EditorBounds(text, 18, FontWeights.Medium, anchor, limits);
            Assert(limits.Contains(bounds), "Growing editor escaped selection");
            Assert(bounds.Height > 40, "Long text must use multiple rows");

            var annotation = VerifyNativeEditor(text, bounds);
            var rendered = ScreenshotTextLayout.Render(annotation);
            var stride = rendered.PixelWidth * 4;
            var pixels = new byte[stride * rendered.PixelHeight];
            rendered.CopyPixels(pixels, stride, 0);
            var rowsWithInk = Enumerable.Range(0, rendered.PixelHeight)
                .Count(y => Enumerable.Range(0, rendered.PixelWidth).Any(x => pixels[y * stride + x * 4 + 3] > 0));
            Assert(rowsWithInk > 25, "Export contains only one line of ink");
        }
        foreach (var size in new[] { new Size(30, 10), new Size(200, 100) })
        {
            var edgeLimits = new Rect(-80, 250, size.Width, size.Height);
            var bounds = ScreenshotTextLayout.EditorBounds(new string('W', 600), 44, FontWeights.Normal,
                edgeLimits.BottomRight, edgeLimits);
            Assert(edgeLimits.Contains(bounds), "Editor overflowed a small/edge selection");
            Assert(bounds.Height <= edgeLimits.Height, "Editor grew past region height");
        }
        var plain = ScreenshotTextLayout.EditorBounds("One", 18, FontWeights.Normal, anchor, limits);
        var newline = ScreenshotTextLayout.EditorBounds("One\n", 18, FontWeights.Normal, anchor, limits);
        Assert(newline.Height > plain.Height, "Final empty caret row clipped");
        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
        {
            var bounds = new Rect(0, 0, 200 * scale, 100 * scale);
            var image = ScreenshotTextLayout.Render(new TextAnnotation("First\nSecond\nThird", bounds,
                Colors.Red, 18 * scale, FontWeights.Normal));
            Assert(image.PixelWidth == (int)bounds.Width && image.PixelHeight == (int)bounds.Height,
                "Physical export dimensions changed with DPI");
        }
        application.Shutdown();
        Console.WriteLine("Screenshot text checks passed: wrapping, native editor, complete input, export, bounds and DPI.");
    }

    private static TextAnnotation VerifyNativeEditor(string text, Rect bounds)
    {
        // TextBox.LineCount requires a valid presentation source. Measuring an
        // unattached control can return -1/0 even for explicit multiline text.
        // Host it in a real window and drain Loaded/layout work before reading
        // native metrics; keep the same Canvas/scroll settings as the overlay.
        var editor = new TextBox {
            Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 18,
            FontWeight = FontWeights.Medium, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), Width = bounds.Width, Height = bounds.Height,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var canvas = new Canvas();
        canvas.Children.Add(editor);
        var host = new Window {
            Content = canvas, Width = 400, Height = 600,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0
        };
        try
        {
            host.Show();
            host.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            host.UpdateLayout();
            Assert(PresentationSource.FromVisual(editor) is not null,
                "Native editor must be attached to a presentation source");
            Assert(editor.IsMeasureValid && editor.IsArrangeValid,
                "Native editor layout has not finished");
            Assert(editor.LineCount >= 3, $"Native editor has {editor.LineCount} rows for {text.Length} characters");
            Assert(editor.ViewportWidth > 0, "Native editor has no text viewport");
            // A full region can show a vertical scrollbar, reducing wrap width.
            // Commit the actual viewport width just as OverlayWindow does.
            var wrapWidth = Math.Min(editor.ViewportWidth, bounds.Width);
            var formatted = ScreenshotTextLayout.Format(text, 18, FontWeights.Medium, Brushes.Red, wrapWidth);
            Assert(Math.Abs(editor.ExtentHeight - formatted.Height) < 3,
                $"Editor/export height mismatch: {editor.ExtentHeight} vs {formatted.Height}, width {wrapWidth}");
            var annotation = new TextAnnotation(editor.Text,
                new Rect(bounds.Location, new Size(wrapWidth, bounds.Height)), Colors.Red, 18, FontWeights.Medium);
            Assert(annotation.Text == text, "Commit lost input");
            return annotation;
        }
        finally { host.Close(); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
