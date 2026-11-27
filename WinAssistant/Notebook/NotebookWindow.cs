using WinAssistant.Infrastructure;
using System.ComponentModel;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WinAssistant.Audio.UI;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using System.Windows.Threading;

namespace WinAssistant.Notebook;

internal sealed class NotebookWindow : Window
{
    private readonly WebView2 browser = new();
    private readonly NotebookService service = new();
    private readonly Border frame = new();
    private readonly Grid shell = new();
    // Keep HICON owners alive while the native window/taskbar uses their handles.
    private readonly System.Drawing.Icon smallWindowIcon = LoadWindowIcon(32);
    private readonly System.Drawing.Icon largeWindowIcon = LoadWindowIcon(256);
    private static readonly uint taskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");
    private HwndSource? iconSource;
    private bool iconsDisposed;
    private string? pendingIntent;
    private bool ready;
    private bool hiding;
    private bool warming;
    private double warmLeft, warmTop;
    private TaskCompletionSource<bool>? pendingFlush;
    private NotebookCodeWindow? codeEditor;
    private NotebookCodeWindow? preparedCodeEditor;
    private readonly DispatcherTimer maintenanceTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly DispatcherTimer syncTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    public NotebookWindow()
    {
        Title = "笔记本"; Width = 1280; Height = 860; MinWidth = 900; MinHeight = 600;
        ShowActivated = false;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; Background = new SolidColorBrush(Color.FromRgb(242, 247, 254));
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 38, UseAeroCaptionButtons = false, ResizeBorderThickness = new Thickness(10), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(8) });
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/AppIcon.ico"));
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        shell.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(browser, 1);
        shell.Children.Add(browser);
        frame.Child = shell; frame.BorderThickness = new Thickness(1); frame.CornerRadius = new CornerRadius(8);
        Content = frame;
        UpdateResizeFrame();
        ApplyTheme();
        SourceInitialized += (_, _) =>
        {
            iconSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            iconSource?.AddHook(TaskbarIconMessage);
            TaskbarIdentity.ApplyToWindow(new WindowInteropHelper(this).Handle);
            UpdateLegacyCorners(); ApplyWindowIcons();
        };
        SizeChanged += (_, _) => UpdateLegacyCorners();
        StateChanged += (_, _) => { UpdateResizeFrame(); UpdateLegacyCorners(); };
        Loaded += Initialize;
        Closing += OnClosing;
        maintenanceTimer.Tick += (_, _) => Wake();
        maintenanceTimer.Start();
        syncTimer.Tick += (_, _) => { if (ready && codeEditor is null) _ = browser.ExecuteScriptAsync("window.notebookSync?.()"); };
        syncTimer.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Closed += (_, _) =>
        {
            maintenanceTimer.Stop(); syncTimer.Stop(); SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            ready = false;
            preparedCodeEditor?.Close();
            preparedCodeEditor = null;
            iconsDisposed = true;
            if (iconSource is { IsDisposed: false }) iconSource.RemoveHook(TaskbarIconMessage);
            iconSource = null;
            smallWindowIcon.Dispose(); largeWindowIcon.Dispose();
        };
    }
    private static System.Drawing.Icon LoadWindowIcon(int size)
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/AppIcon.ico"))!.Stream;
        using var icon = new System.Drawing.Icon(stream, size, size);
        return (System.Drawing.Icon)icon.Clone();
    }
    private void ApplyWindowIcons()
    {
        if (iconsDisposed) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        SetNativeIcons(handle);
        // WPF creates a hidden native owner for ShowInTaskbar=false. Explorer
        // can inspect it during startup; do not leave that host with a default icon.
        if (Owner is null)
        {
            var owner = GetWindow(handle, 4 /* GW_OWNER */);
            if (owner != IntPtr.Zero) SetNativeIcons(owner);
        }
    }
    private void SetNativeIcons(IntPtr handle)
    {
        SendMessage(handle, 0x0080 /* WM_SETICON */, IntPtr.Zero /* ICON_SMALL */, smallWindowIcon.Handle);
        SendMessage(handle, 0x0080 /* WM_SETICON */, new IntPtr(1) /* ICON_BIG */, largeWindowIcon.Handle);
    }
    private IntPtr TaskbarIconMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (iconsDisposed) return IntPtr.Zero;
        if (message == 0x0002 /* WM_DESTROY */) TaskbarIdentity.ApplyToWindow(window, clear: true);
        // The button is created asynchronously, and this message also arrives
        // after Explorer restarts. Let WPF finish its own taskbar processing.
        if (taskbarButtonCreated != 0 && (uint)message == taskbarButtonCreated)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyWindowIcons));
        // Answer shell queries directly even if WPF reapplies Icon while the
        // native owner/styles are changing during first startup.
        if (message == 0x007F /* WM_GETICON */ && wParam.ToInt64() is 0 or 1 or 2)
        {
            handled = true;
            return wParam.ToInt64() == 1 ? largeWindowIcon.Handle : smallWindowIcon.Handle;
        }
        return IntPtr.Zero;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    // WebView2 owns a child HWND: WPF chrome cannot receive edge hits over it.
    // Reserve 10 DIP at the sides/bottom (1 DIP line + 9 DIP surface), while
    // keeping the visible frame thin. macOS uses AppKit's native resize edges.
    private void UpdateResizeFrame()
    {
        frame.Padding = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(9, 0, 9, 9);
    }
    // Windows 11 uses DWM corners. Windows 10 needs a native window region,
    // because a WPF clip alone cannot clip the child WebView2 HWND.
    private void UpdateLegacyCorners()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        if (WindowState == WindowState.Maximized) { SetWindowRgn(handle, IntPtr.Zero, true); return; }
        if (!GetWindowRect(handle, out var rect)) return;
        var radius = (int)Math.Round(16 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var region = CreateRoundRectRgn(0, 0, rect.Right - rect.Left + 1, rect.Bottom - rect.Top + 1, radius, radius);
        if (region != IntPtr.Zero && SetWindowRgn(handle, region, true) == 0) DeleteObject(region);
        // On success Windows owns the region.
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    private void ApplyTheme()
    {
        var dark = JsonSerializer.SerializeToElement(service.Settings()).GetProperty("theme").GetString() == "Dark";
        var surface = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#20222C" : "#F9F9FB"));
        var line = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#444754" : "#DADCE3"));
        Background = surface; frame.Background = surface; frame.BorderBrush = line;
        if (shell.Children.Count > 1) shell.Children.RemoveAt(1);
        var bar = new DockPanel { Background = surface, LastChildFill = true };
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        WindowChrome.SetIsHitTestVisibleInChrome(commands, true);
        void Add(string text, string help, Action action)
        {
            var button = new Button
            {
                Content = text,
                ToolTip = help,
                Width = 42,
                Height = 32,
                Background = Brushes.Transparent,
                Foreground = dark ? Brushes.WhiteSmoke : Brushes.Black,
                BorderThickness = new Thickness(0),
                FontSize = 16
            };
            var template = new ControlTemplate(typeof(Button));
            var root = new FrameworkElementFactory(typeof(Border), "captionBackground");
            root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            root.AppendChild(presenter); template.VisualTree = root;
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, help == "关闭笔记窗口" ? Brushes.Firebrick : line, "captionBackground"));
            template.Triggers.Add(hover);
            ButtonFeedback.Apply(template, "captionBackground");
            button.Template = template;
            button.Click += (_, _) => action(); commands.Children.Add(button);
        }
        Add("◐", "切换主题", () => { if (ready) _ = browser.ExecuteScriptAsync("document.getElementById('theme').click()"); });
        Add("−", "最小化", () => WindowState = WindowState.Minimized);
        Add("□", "最大化或还原", () => WindowAction("maximize"));
        Add("×", "关闭笔记窗口", Close);
        DockPanel.SetDock(commands, Dock.Right); bar.Children.Add(commands);
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var headerIcon = new Image { Source = ApplicationImages.HeaderIcon, Width = 24, Height = 24,
            Stretch = Stretch.Uniform, UseLayoutRounding = true, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(headerIcon, BitmapScalingMode.HighQuality);
        identity.Children.Add(headerIcon);
        identity.Children.Add(new TextBlock
        {
            Text = "笔记本",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = dark ? Brushes.WhiteSmoke : Brushes.Black,
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        bar.Children.Add(identity);
        shell.Children.Add(new Border { Child = bar, BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1) });
        ThemeManager.ApplyWindowFrame(this, dark ? "Dark" : "Light");
    }
    private void Navigate()
    {
        if (!ready || pendingIntent is null) return;
        var intent = pendingIntent; pendingIntent = null;
        _ = browser.ExecuteScriptAsync($"window.notebookNavigate?.({JsonSerializer.Serialize(intent)})");
    }
    private async void Initialize(object sender, RoutedEventArgs args)
    {
        Loaded -= Initialize;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinAssistant", "NotebookAssets");
            var assembly = typeof(NotebookWindow).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(x => x.StartsWith("NotebookWeb/", StringComparison.Ordinal)))
            {
                var relative = name["NotebookWeb/".Length..].Replace('\\', '/');
                var destination = Path.Combine(folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var buffer = new MemoryStream(); stream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                if (!System.IO.File.Exists(destination) || !System.IO.File.ReadAllBytes(destination).SequenceEqual(bytes)) NotebookService.AtomicWrite(destination, bytes);
            }
            if (!System.IO.File.Exists(Path.Combine(folder, "index.html"))) throw new IOException("离线笔记资源缺失，请重新安装软件");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinAssistant", "NotebookWebView"));
            await browser.EnsureCoreWebView2Async(environment);
            browser.CoreWebView2.SetVirtualHostNameToFolderMapping("notebook.local", folder, CoreWebView2HostResourceAccessKind.DenyCors);
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            browser.CoreWebView2.WebMessageReceived += Receive;
            browser.CoreWebView2.NavigationStarting += (_, eventArgs) => { if (!eventArgs.Uri.StartsWith("https://notebook.local/", StringComparison.Ordinal)) eventArgs.Cancel = true; };
            browser.CoreWebView2.NewWindowRequested += (_, eventArgs) => { eventArgs.Handled = true; };
            browser.CoreWebView2.PermissionRequested += (_, eventArgs) => eventArgs.State = CoreWebView2PermissionState.Deny;
            browser.CoreWebView2.Navigate("https://notebook.local/index.html");
        }
        catch (Exception error)
        {
            Content = new System.Windows.Controls.TextBlock { Text = "笔记窗口初始化失败：" + error.Message + "\nWindows 笔记需要 Microsoft Edge WebView2 Runtime。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30) };
            CompleteWarmUp();
            Opacity = 1; WindowStyle = WindowStyle.SingleBorderWindow;
        }
    }
    private async void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        if (eventArgs.Source != "https://notebook.local/index.html") return;
        int id = 0;
        try
        {
            using var message = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            id = message.RootElement.GetProperty("id").GetInt32(); var op = message.RootElement.GetProperty("op").GetString(); var args = message.RootElement.GetProperty("args");
            object? result;
            switch (op)
            {
                case "settings": result = service.Settings(); break;
                case "theme": service.SetTheme(args.GetProperty("value").GetString()!); ApplyTheme(); result = service.Settings(); break;
                case "configure": result = await service.ConfigureAsync(args); break;
                case "readClipboard": result = Clipboard.ContainsText() ? Clipboard.GetText() : ""; break;
                case "clipboard": Clipboard.SetText(args.GetProperty("text").GetString() ?? ""); result = null; break;
                case "editCode":
                    if (codeEditor is not null) throw new InvalidOperationException("代码编辑窗口已打开");
                    codeEditor = preparedCodeEditor is { IsAvailable: true }
                        ? preparedCodeEditor
                        : new NotebookCodeWindow(this, browser.CoreWebView2.Environment);
                    preparedCodeEditor = null;
                    try { result = await codeEditor.EditAsync(args); }
                    finally { codeEditor = null; PrepareCodeEditor(); }
                    break;
                case "open":
                case "viewImage": service.Open(args.GetProperty("path").GetString()!); result = null; break;
                case "saveAttachment": {
                    var source = service.Resolve(args.GetProperty("path").GetString()!);
                    var name = System.IO.Path.GetFileName(args.GetProperty("name").GetString() ?? source);
                    var dialog = new SaveFileDialog { FileName = name, OverwritePrompt = true };
                    result = false;
                    if (dialog.ShowDialog(this) == true) {
                        await Task.Run(() => service.ExportAttachment(args.GetProperty("path").GetString()!, dialog.FileName));
                        result = true;
                    }
                    break;
                }
                case "external": service.OpenExternal(args.GetProperty("url").GetString()!); result = null; break;
                case "window": WindowAction(args.GetProperty("action").GetString()!); result = null; break;
                case "flushed": pendingFlush?.TrySetResult(true); result = null; break;
                case "flushFailed": pendingFlush?.TrySetException(new IOException(args.GetProperty("message").GetString())); result = null; break;
                case "ready": ready = true; CompleteWarmUp(); result = null; Navigate(); PrepareCodeEditor(); break;
                default: result = await Task.Run(() => service.ExecuteAsync(op!, args.Clone())); break;
            }
            await browser.ExecuteScriptAsync($"window.nativeReply({id},{JsonSerializer.Serialize(result)},null)");
        }
        catch (Exception error) { if (id != 0) await browser.ExecuteScriptAsync($"window.nativeReply({id},null,{JsonSerializer.Serialize(error.Message)})"); }
    }
    private void PrepareCodeEditor()
    {
        // Keep one unused editor, not previous drafts. Preparing it at idle
        // moves WebView2 creation and offline script loading off the click path.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (!ready || Dispatcher.HasShutdownStarted || codeEditor is not null
                || preparedCodeEditor is { IsAvailable: true }) return;
            NotebookCodeWindow? prepared = null;
            try
            {
                prepared = new NotebookCodeWindow(this, browser.CoreWebView2.Environment);
                preparedCodeEditor = prepared;
                prepared.WarmUp();
            }
            catch
            {
                preparedCodeEditor = null;
                prepared?.Close();
            }
        }));
    }

    private void WindowAction(string action)
    {
        switch (action)
        {
            case "minimize": WindowState = WindowState.Minimized; break;
            case "maximize": WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
            case "drag": if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove(); break;
            case "close": hiding = true; Hide(); hiding = false; break;
        }
    }
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (hiding) return;
        if (codeEditor is not null) { args.Cancel = true; codeEditor.Activate(); return; }
        if (!ready) { args.Cancel = true; Hide(); return; }
        args.Cancel = true; _ = browser.ExecuteScriptAsync("window.notebookClose?.()");
    }
    public void Open(string intent = "last")
    {
        if (codeEditor is not null) {
            if (codeEditor.WindowState == WindowState.Minimized) codeEditor.WindowState = WindowState.Normal;
            codeEditor.Activate(); return;
        }
        CompleteWarmUp(); pendingIntent = intent; Navigate();
        ShowActivated = true; ShowInTaskbar = true; Opacity = 1;
        ApplyWindowIcons(); Show(); ApplyWindowIcons();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyWindowIcons));
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    public void WarmUp()
    {
        if (warming || ready || IsVisible) return;
        warming = true;
        var area = SystemParameters.WorkArea;
        warmLeft = double.IsNaN(Left) ? area.Left + (area.Width - Width) / 2 : Left;
        warmTop = double.IsNaN(Top) ? area.Top + (area.Height - Height) / 2 : Top;
        // WPF opacity does not hide WebView2's child HWND during initialization.
        // Keep the entire warm-up window outside every monitor until explicitly opened.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft - Width - 1000;
        Top = SystemParameters.VirtualScreenTop - Height - 1000;
        Opacity = 0; ShowInTaskbar = false; ShowActivated = false; Show();
        ApplyWindowIcons();
    }
    private void CompleteWarmUp()
    {
        if (!warming) return;
        Hide(); Left = warmLeft; Top = warmTop; Opacity = 1; warming = false;
    }
    private void Wake() { if (ready && codeEditor is null) _ = browser.ExecuteScriptAsync("window.notebookWake?.()"); }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(Wake);
    }
    public async Task FlushAsync()
    {
        if (codeEditor is not null) throw new InvalidOperationException("请先保存或关闭代码编辑窗口");
        if (!ready) return;
        if (pendingFlush is not null) { await pendingFlush.Task; return; }
        pendingFlush = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await browser.ExecuteScriptAsync("window.notebookFlush().then(()=>window.chrome.webview.postMessage({id:0,op:'flushed',args:{}}),error=>window.chrome.webview.postMessage({id:0,op:'flushFailed',args:{message:error.message}}))");
            await pendingFlush.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { pendingFlush = null; }
    }
}
