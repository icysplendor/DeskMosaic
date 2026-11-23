using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinAssistant.Infrastructure;

namespace WinAssistant.Screenshot;

internal sealed class OverlayWindow : Window
{
    private readonly ScreenshotSession session;
    private const int ExtendedStyle = -20;
    private const int TransparentToInput = 0x20;
    private const int NoActivate = 0x08000000;
    private const uint KeepPositionAndSize = 0x0003;
    private readonly Canvas inputLayer;
    private TextBox? textEditor;
    internal OverlaySurface Surface { get; }
    internal CapturedMonitor Monitor { get; }

    internal OverlayWindow(ScreenshotSession session, CapturedMonitor monitor, Rect? recordingInput = null)
    {
        this.session = session;
        Monitor = monitor;
        Surface = new OverlaySurface(session, monitor, recordingInput);
        inputLayer = new Canvas
        {
            IsHitTestVisible = true
        };
        var root = new Grid();
        root.Children.Add(Surface);
        root.Children.Add(inputLayer);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        // GDI SRCCOPY leaves layered overlays out of the recording's desktop source.
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Content = root;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (recordingInput is not null) AddExtendedStyle(NoActivate);
            var bounds = Surface.DesktopBounds;
            NativeMethods.SetWindowPos(
                handle,
                NativeMethods.HwndTopMost,
                (int)bounds.Left,
                (int)bounds.Top,
                (int)bounds.Width,
                (int)bounds.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            if (recordingInput is null)
                Monitor.Scale = Math.Max(1, NativeMethods.GetDpiForWindow(handle) / 96.0);
        };

        Surface.MouseLeftButtonDown += (_, eventArgs) =>
        {
            if (recordingInput is null || session.RecordingInputShouldActivate()) ActivateForInput();
            Surface.CaptureMouse();
            session.MouseDown(eventArgs.ClickCount);
            eventArgs.Handled = true;
        };
        Surface.MouseMove += (_, eventArgs) =>
        {
            session.MouseMoved(eventArgs.LeftButton == MouseButtonState.Pressed);
        };
        Surface.MouseLeftButtonUp += (_, eventArgs) =>
        {
            Surface.ReleaseMouseCapture();
            session.MouseUp();
            eventArgs.Handled = true;
        };
        PreviewMouseRightButtonDown += (_, eventArgs) =>
        {
            CaptureMouse();
            eventArgs.Handled = true;
        };
        PreviewMouseRightButtonUp += (_, eventArgs) =>
        {
            ReleaseMouseCapture();
            eventArgs.Handled = true;
            // Keep the overlay alive until Windows has fully dispatched this
            // right-click, otherwise its button-up/context-menu event can reach
            // the application underneath after Cancel closes every overlay.
            Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                (Action)session.Cancel);
        };
        PreviewKeyDown += (_, eventArgs) =>
        {
            if (textEditor is not null) return;
            if (session.KeyDown(eventArgs.Key))
            {
                eventArgs.Handled = true;
            }
        };
    }

    // Layered windows with this style still display their annotations, while all
    // input goes to the app underneath. Separate RecordingInput windows route tools.
    internal void MakeRecordingVisual() => AddExtendedStyle(TransparentToInput | NoActivate);

    internal void RaiseRecordingControl()
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopMost, 0, 0, 0, 0,
            NativeMethods.SwpNoActivate | KeepPositionAndSize);
    }

    private void AddExtendedStyle(int style)
    {
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, ExtendedStyle, GetWindowLong(handle, ExtendedStyle) | style);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint handle, int index, int value);

    internal void ActivateForInput()
    {
        ShowActivated = true;
        Activate();
        Surface.Focus();
    }

    internal void BeginTextEditor(Point globalPoint, double fontSize, FontWeight fontWeight, Color color, Rect availableBounds)
    {
        EndTextEditor();
        ActivateForInput();
        var localPoint = Surface.GlobalToLocal(globalPoint);
        // A native editor belongs to one display's overlay. Constrain both
        // platforms to that display's intersection with the screenshot region.
        var limits = Surface.GlobalToLocal(Rect.Intersect(availableBounds, Monitor.Bounds));
        var editor = new TextBox
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = fontSize,
            FontWeight = fontWeight,
            Foreground = new SolidColorBrush(color),
            CaretBrush = new SolidColorBrush(color),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        InputMethod.SetIsInputMethodEnabled(editor, true);
        void ResizeEditor()
        {
            var bounds = ScreenshotTextLayout.EditorBounds(editor.Text, fontSize, fontWeight, localPoint, limits);
            editor.Width = bounds.Width;
            editor.Height = bounds.Height;
            Canvas.SetLeft(editor, bounds.Left);
            Canvas.SetTop(editor, bounds.Top);
            editor.UpdateLayout();
            // Native font fallback/theme metrics can differ slightly from the
            // initial estimate. Grow again before committing, rather than clip
            // a final line or leave a scrollbar while selection space remains.
            var height = Math.Min(limits.Height, Math.Max(bounds.Height, Math.Ceiling(editor.ExtentHeight) + 2));
            if (height > bounds.Height)
            {
                bounds = new Rect(bounds.Left, Math.Clamp(localPoint.Y, limits.Top, limits.Bottom - height),
                    bounds.Width, height);
                editor.Height = height;
                Canvas.SetTop(editor, bounds.Top);
                editor.UpdateLayout();
            }
            // A visible vertical scrollbar consumes width only when the entire
            // region is full; commit the actual text viewport's wrap width.
            var viewport = new Rect(bounds.Location,
                new Size(editor.ViewportWidth > 0 ? Math.Min(editor.ViewportWidth, bounds.Width) : bounds.Width, bounds.Height));
            var globalBounds = Surface.LocalToGlobal(viewport);
            session.UpdateTextInput(editor.Text, globalBounds, fontSize * Surface.LocalToGlobal(bounds).Width / bounds.Width);
        }
        editor.TextChanged += (_, _) => ResizeEditor();
        editor.PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            if ((eventArgs.Key is Key.Enter or Key.Escape) && session.KeyDown(eventArgs.Key))
            {
                eventArgs.Handled = true;
            }
        };
        inputLayer.Children.Add(editor);
        textEditor = editor;
        ResizeEditor();
        editor.Focus();
    }

    internal void EndTextEditor()
    {
        if (textEditor is null) return;
        inputLayer.Children.Remove(textEditor);
        textEditor = null;
    }

}

internal sealed class OverlaySurface : FrameworkElement
{
    private readonly ScreenshotSession session;
    internal CapturedMonitor Monitor { get; }

    internal Rect DesktopBounds { get; }
    private bool IsRecordingInput { get; }

    internal OverlaySurface(ScreenshotSession session, CapturedMonitor monitor, Rect? recordingInput)
    {
        this.session = session;
        Monitor = monitor;
        DesktopBounds = recordingInput ?? monitor.Bounds;
        IsRecordingInput = recordingInput is not null;
        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (IsRecordingInput)
        {
            drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null,
                new Rect(0, 0, ActualWidth, ActualHeight));
        }
        else session.Render(this, drawingContext);
    }

    internal Rect GlobalToLocal(Rect rect)
    {
        var scaleX = ActualWidth / DesktopBounds.Width;
        var scaleY = ActualHeight / DesktopBounds.Height;
        return new Rect(
            (rect.X - DesktopBounds.X) * scaleX,
            (rect.Y - DesktopBounds.Y) * scaleY,
            rect.Width * scaleX,
            rect.Height * scaleY);
    }

    internal Point GlobalToLocal(Point point)
    {
        var scaleX = ActualWidth / DesktopBounds.Width;
        var scaleY = ActualHeight / DesktopBounds.Height;
        return new Point(
            (point.X - DesktopBounds.X) * scaleX,
            (point.Y - DesktopBounds.Y) * scaleY);
    }

    internal Rect LocalToGlobal(Rect rect)
    {
        var scaleX = DesktopBounds.Width / ActualWidth;
        var scaleY = DesktopBounds.Height / ActualHeight;
        return new Rect(DesktopBounds.Left + rect.X * scaleX, DesktopBounds.Top + rect.Y * scaleY,
            rect.Width * scaleX, rect.Height * scaleY);
    }
}
