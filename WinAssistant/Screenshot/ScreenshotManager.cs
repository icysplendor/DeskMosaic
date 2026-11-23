using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WinAssistant.Audio.Diagnostics;
using WinAssistant.Infrastructure;

namespace WinAssistant.Screenshot;

internal sealed class ScreenshotManager
{
    internal static ScreenshotManager Shared { get; } = new();

    private ScreenshotSession? session;
    private bool isStarting;

    internal async Task WarmUpAfterStartup()
    {
        var dispatcher = Application.Current.Dispatcher;
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try
        {
            if (dispatcher.HasShutdownStarted) return;
            var timer = Stopwatch.StartNew();
            await Task.Run(() => { _ = WindowCandidate.Current(); }).ConfigureAwait(false);
            if (dispatcher.HasShutdownStarted) return;
            await dispatcher.InvokeAsync(() =>
            {
                if (session is not null || isStarting) return;
                var image = CapturedMonitor.CreateWarmUpImage();
                var previousMainWindow = Application.Current.MainWindow;
                var window = new Window
                {
                    Width = 1,
                    Height = 1,
                    Left = -32000,
                    Top = -32000,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Opacity = 0,
                    Content = new Image { Source = image }
                };
                try { window.Show(); }
                finally
                {
                    window.Close();
                    Application.Current.MainWindow = previousMainWindow;
                }
                if (timer.ElapsedMilliseconds >= 1000)
                    AppLogger.Info($"Screenshot graphics warm-up took {timer.ElapsedMilliseconds} ms.");
            });
        }
        catch (Exception error)
        {
            AppLogger.Error(error, "Could not warm up screenshot graphics.");
        }
    }

    internal async void StartCapture(bool waitForMenuDismissal = false)
    {
        if (session is not null || isStarting) return;
        isStarting = true;
        if (waitForMenuDismissal)
        {
            await Task.Delay(120);
        }
        var previousWindow = NativeMethods.GetForegroundWindow();
        IReadOnlyList<CapturedMonitor>? capturedMonitors = null;
        var timer = Stopwatch.StartNew();
        try
        {
            capturedMonitors = CapturedMonitor.CaptureAll();
            var capturedAt = timer.ElapsedMilliseconds;
            if (capturedMonitors.Count == 0)
                throw new InvalidOperationException("没有检测到可截图的显示器。");
            var candidates = WindowCandidate.Current();
            var candidatesAt = timer.ElapsedMilliseconds;
            var recordingStarted = false;
            session = new ScreenshotSession(capturedMonitors, candidates, () =>
            {
                session = null;
                if (!recordingStarted && previousWindow != nint.Zero)
                    NativeMethods.SetForegroundWindow(previousWindow);
            }, () =>
            {
                recordingStarted = true;
                if (previousWindow != nint.Zero)
                    NativeMethods.SetForegroundWindow(previousWindow);
            });
            capturedMonitors = null;
            var preparedAt = timer.ElapsedMilliseconds;
            session.Present();
            if (timer.ElapsedMilliseconds >= 1000)
            {
                AppLogger.Info($"Screenshot startup took {timer.ElapsedMilliseconds} ms: capture {capturedAt} ms, windows {candidatesAt - capturedAt} ms, preparation {preparedAt - candidatesAt} ms, overlay {timer.ElapsedMilliseconds - preparedAt} ms.");
            }
        }
        catch (Exception error)
        {
            if (capturedMonitors is not null)
            {
                foreach (var monitor in capturedMonitors) monitor.Dispose();
            }
            session?.Dispose();
            session = null;
            MessageBox.Show(
                $"无法开始截图：{error.Message}",
                "DeskMosaic",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            isStarting = false;
        }
    }
}
