using WinAssistant.Infrastructure;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WinAssistant.Audio.UI;

namespace WinAssistant.Notebook;

// The independent native window hosts the same offline editor as macOS.
// Its bridge can return a draft and write text to the clipboard, never access files.
internal sealed class NotebookCodeWindow : Window
{
    private const string EditorUrl = "https://notebook.local/code-editor.html";
    private readonly WebView2 browser = new();
    private readonly System.Windows.Controls.DockPanel shell;
    private JsonElement? initial;
    private readonly CoreWebView2Environment environment;
    private object? result;
    private readonly TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool allowClose, ready, editorInitialized, closed, warming;

    internal NotebookCodeWindow(Window owner, CoreWebView2Environment environment)
    {
        Icon = owner.Icon; Title = "代码块编辑";
        Width = 1100; Height = 760; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.environment = environment;
        Background = owner.Background;
        shell = DeskWindowChrome.Wrap(this, browser, dark: false);
        Content = shell;
        Loaded += Initialize;
        Closing += ConfirmClose;
        Closed += (_, _) => { closed = true; browser.Dispose(); completion.TrySetResult(result); };
    }

    internal bool IsAvailable => !closed;

    // A visible, off-screen HWND lets WebView2 load without flashing a window
    // or stealing focus. No note text is supplied until EditAsync is called.
    internal void WarmUp()
    {
        warming = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft - Width - 1000;
        Top = SystemParameters.VirtualScreenTop - Height - 1000;
        ShowInTaskbar = false;
        ShowActivated = false;
        Show();
    }

    internal async Task<object?> EditAsync(JsonElement args)
    {
        if (closed || initial is not null) throw new InvalidOperationException("代码编辑窗口不可用");
        initial = args.Clone();
        var dark = args.GetProperty("dark").GetBoolean();
        Background = new SolidColorBrush(dark ? Color.FromRgb(17, 27, 43) : Colors.White);
        DeskWindowChrome.SetHeader(shell, this, dark);
        if (warming)
        {
            warming = false;
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }
        ShowInTaskbar = true;
        ShowActivated = true;
        Show();
        ThemeManager.ApplyWindowFrame(this, dark ? "Dark" : "Light");
        Activate();
        try { await InitializeEditorAsync(); }
        catch { allowClose = true; Close(); throw; }
        return await completion.Task;
    }

    private async Task InitializeEditorAsync()
    {
        if (!ready || initial is null || editorInitialized || closed) return;
        editorInitialized = true;
        await browser.ExecuteScriptAsync($"window.codeEditorInitialize({initial.Value.GetRawText()})");
    }
    private async void Initialize(object sender, RoutedEventArgs e)
    {
        Loaded -= Initialize;
        try
        {
            await browser.EnsureCoreWebView2Async(environment);
            if (closed) return;
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinAssistant", "NotebookAssets");
            browser.CoreWebView2.SetVirtualHostNameToFolderMapping("notebook.local", folder, CoreWebView2HostResourceAccessKind.DenyCors);
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            // Reloading a document window must never discard its unsaved draft.
            browser.CoreWebView2.NavigationStarting += (_, a) => { if (a.Uri != EditorUrl || ready) a.Cancel = true; };
            browser.CoreWebView2.ContextMenuRequested += (_, a) => {
                for (var i = a.MenuItems.Count - 1; i >= 0; i--)
                    if (a.MenuItems[i].Name is not ("copy" or "cut" or "paste" or "selectAll")) a.MenuItems.RemoveAt(i);
            };
            browser.CoreWebView2.NewWindowRequested += (_, a) => a.Handled = true;
            browser.CoreWebView2.PermissionRequested += (_, a) => a.State = CoreWebView2PermissionState.Deny;
            browser.CoreWebView2.WebMessageReceived += Receive;
            browser.CoreWebView2.Navigate(EditorUrl);
        }
        catch (Exception error)
        {
            if (closed) return;
            if (initial is null) { allowClose = true; Close(); return; }
            ready = false;
            Content = new System.Windows.Controls.TextBlock { Text = "代码编辑器初始化失败：" + error.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) };
        }
    }
    private async void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != EditorUrl) return;
        int id = 0;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (root.TryGetProperty("id", out var requestId)) id = requestId.GetInt32();
            var args = root.GetProperty("args");
            switch (root.GetProperty("op").GetString())
            {
                case "ready":
                    ready = true;
                    if (initial is null) Hide();
                    else await InitializeEditorAsync();
                    break;
                case "save":
                    result = new { text = args.GetProperty("text").GetString() ?? "", language = args.GetProperty("language").GetString() ?? "" };
                    allowClose = true; Close(); break;
                case "discard": allowClose = true; Close(); break;
                case "clipboard":
                    var text = args.GetProperty("text").GetString() ?? "";
                    if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text);
                    await browser.ExecuteScriptAsync($"window.nativeReply({id},null,null)"); break;
            }
        }
        catch (Exception error)
        {
            if (allowClose || closed) return;
            if (id != 0) {
                try { await browser.ExecuteScriptAsync($"window.nativeReply({id},null,{JsonSerializer.Serialize(error.Message)})"); }
                catch (ObjectDisposedException) { }
            }
            else MessageBox.Show(this, error.Message, "代码编辑器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void ConfirmClose(object? sender, CancelEventArgs e)
    {
        if (allowClose || !editorInitialized) return;
        e.Cancel = true;
        _ = browser.ExecuteScriptAsync("window.codeEditorRequestClose()");
    }
}
