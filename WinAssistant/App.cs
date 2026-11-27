using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows;
using WinAssistant.Infrastructure;
using WinAssistant.Screenshot;
using WinAssistant.Settings;
using WinAssistant.Audio.Core;
using WinAssistant.Audio.Models;
using WinAssistant.Audio.Services;
using WinAssistant.Audio.Storage;
using WinAssistant.Audio.UI;
using WinAssistant.Audio.Diagnostics;
using Forms = System.Windows.Forms;

namespace WinAssistant;

internal sealed class App : Application
{
    private Mutex? singleInstanceMutex;
    private Forms.NotifyIcon? trayIcon;
    private GlobalHotKeyManager? hotKeyManager;
    private SettingsWindow? settingsWindow;
    private Notebook.NotebookWindow? notebookWindow;
    private Translation.TranslationWindow? translationWindow;
    private AudioDeviceManager? audioManager;
    private AppStateService? audioState;
    private TrayFlyoutWindow? trayFlyout;
    private Icon? dynamicTrayIcon;
    private Icon? brandedTrayIcon;
    private bool isExiting;
    private bool exitRequested;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--verify-recording-encoder"])
        {
            try
            {
                using var encoder = Process.Start(new ProcessStartInfo(RegionRecording.FindEncoder(), "-version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (encoder is null) return 1;
                if (encoder.WaitForExit(15000)) return encoder.ExitCode;
                encoder.Kill();
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); }
            return 1;
        }

        // Set before any WPF/WinForms window (including silent-start hosts).
        TaskbarIdentity.InitializeProcess();
        var application = new App
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        return application.Run();
    }

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        singleInstanceMutex = new Mutex(true, "WinAssistant.Singleton", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        hotKeyManager = new GlobalHotKeyManager();
        try
        {
            ReloadHotKeys();
        }
        catch (Exception error)
        {
            MessageBox.Show(
                error.Message,
                "DeskMosaic",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        CreateTrayIcon();
        _ = ScreenshotManager.Shared.WarmUpAfterStartup();

        var audioConfiguration = ConfigurationStore.Shared.Configuration.Audio;
        var forceShow = eventArgs.Args.Contains("--show", StringComparer.OrdinalIgnoreCase);
        var forceSilent = eventArgs.Args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
        if (forceShow || (!forceSilent && !audioConfiguration.SilentStart))
        {
            ShowSettings();
        }
        _ = StartAudioAsync();
        notebookWindow ??= new Notebook.NotebookWindow();
        notebookWindow.WarmUp();
    }

    private async Task StartAudioAsync()
    {
        AudioDeviceManager? manager = null;
        AppStateService? state = null;
        try
        {
            var store = new UnifiedAudioConfigStore();
            var configuration = await store.LoadAsync();
            configuration.StartWithWindows = ConfigurationStore.Shared.Configuration.LaunchAtLogin;
            await Task.Run(() =>
            {
                manager = new AudioDeviceManager();
                state = new AppStateService(manager, store, configuration);
                state.Start();
            });
            if (isExiting || Dispatcher.HasShutdownStarted)
            {
                state?.Dispose();
                manager?.Dispose();
                return;
            }
            await Dispatcher.InvokeAsync(() =>
            {
                if (isExiting)
                {
                    state?.Dispose();
                    manager?.Dispose();
                    return;
                }
                audioManager = manager;
                audioState = state;
                state!.StateChanged += AudioStateChanged;
                ApplyTrayAudioState(state.Snapshot);
                settingsWindow?.AttachAudioState(state);
                try { ReloadHotKeys(); }
                catch (Exception error) { AppLogger.Error(error, "Could not register audio hotkeys."); }
            });
        }
        catch (Exception error)
        {
            state?.Dispose();
            manager?.Dispose();
            AppLogger.Error(error, "Could not start audio service.");
        }
    }

    private async void RequestExit()
    {
        if (exitRequested) return;
        exitRequested = true;
        try
        {
            if (notebookWindow is not null) await notebookWindow.FlushAsync();
            if (translationWindow is not null) await translationWindow.StopAndWaitAsync();
            Shutdown();
        }
        catch (Exception error)
        {
            notebookWindow?.Open();
            MessageBox.Show("笔记尚未成功保存，已保留窗口。请处理保存错误后再退出。\n" + error.Message,
                "DeskMosaic", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { exitRequested = false; }
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        isExiting = true;
        translationWindow?.Stop();
        trayFlyout?.Close();
        trayIcon?.Dispose();
        if (audioState is not null) audioState.StateChanged -= AudioStateChanged;
        dynamicTrayIcon?.Dispose();
        brandedTrayIcon?.Dispose();
        hotKeyManager?.Dispose();
        audioState?.Dispose();
        audioManager?.Dispose();
        singleInstanceMutex?.Dispose();
        base.OnExit(eventArgs);
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) => RebuildTrayMenu(menu);

        using var stream = GetResourceStream(new Uri("pack://application:,,,/Resources/TrayIcon.ico"))!.Stream;
        using var icon = new Icon(stream, 16, 16);
        brandedTrayIcon = (Icon)icon.Clone();
        trayIcon = new Forms.NotifyIcon
        {
            Text = "DeskMosaic",
            Icon = brandedTrayIcon,
            Visible = true,
            ContextMenuStrip = menu
        };
        trayIcon.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
            {
                Dispatcher.Invoke(() =>
                {
                    if (audioState is null)
                    {
                        RebuildTrayMenu(menu);
                        menu.Show(Forms.Control.MousePosition);
                        return;
                    }
                    trayFlyout ??= new TrayFlyoutWindow(audioState, StartCapture, () => OpenNotebook(), OpenTranslation, ShowSettings, RequestExit);
                    var cursor = Forms.Control.MousePosition;
                    var area = Forms.Screen.FromPoint(cursor).WorkingArea;
                    trayFlyout.ToggleAt(cursor.X, cursor.Y, area.Left, area.Top, area.Right, area.Bottom);
                });
            }
        };
        trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(StartCapture);
        if (audioState is not null) ApplyTrayAudioState(audioState.Snapshot);
    }

    private void AudioStateChanged(object? sender, AppStateSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() => ApplyTrayAudioState(snapshot));
    }

    private void ApplyTrayAudioState(AppStateSnapshot snapshot)
    {
        if (trayIcon is null) return;
        var icon = snapshot.AudioEnabled
            ? TrayIconFactory.CreateVolumeIcon(
                snapshot.CurrentOutput?.Volume,
                snapshot.CurrentOutput?.IsMuted == true,
                snapshot.CurrentInput?.IsMuted == true)
            : null;
        trayIcon.Icon = icon ?? brandedTrayIcon;
        dynamicTrayIcon?.Dispose();
        dynamicTrayIcon = icon;
        var output = snapshot.CurrentOutput?.Name ?? "无输出设备";
        var input = snapshot.CurrentInput?.Name ?? "无输入设备";
        var tooltip = snapshot.AudioEnabled
            ? $"DeskMosaic\n输出：{output}\n输入：{input}"
            : "DeskMosaic\n音频功能已关闭";
        trayIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..60] + "...";
    }

    private void RebuildTrayMenu(Forms.ContextMenuStrip menu)
    {
        var english = audioState?.Snapshot.Language == "en-US";
        string T(string chinese, string translated) => english ? translated : chinese;
        menu.Items.Clear();
        menu.Items.Add(T("立即截图", "Capture now"), null, (_, _) =>
            Dispatcher.Invoke(() => ScreenshotManager.Shared.StartCapture(waitForMenuDismissal: true)));

        if (audioState?.Snapshot.AudioEnabled == true)
        {
            var snapshot = audioState.Snapshot;
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(BuildProfileMenu(snapshot));
            menu.Items.Add(BuildVolumeMenu(T("输出音量", "Output volume"), snapshot.CurrentOutput));
            menu.Items.Add(BuildVolumeMenu(T("输入音量", "Input volume"), snapshot.CurrentInput));
        }

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(T("笔记本…", "Notebook…"), null, (_, _) => Dispatcher.Invoke(() => OpenNotebook()));
        menu.Items.Add(T("录写/翻译…", "Transcribe / Translate…"), null, (_, _) => Dispatcher.Invoke(OpenTranslation));
        menu.Items.Add(T("统一设置…", "Settings…"), null, (_, _) => Dispatcher.Invoke(ShowSettings));
        var launchAtLogin = new Forms.ToolStripMenuItem(T("登录时启动", "Launch at sign-in"))
        {
            Checked = ConfigurationStore.Shared.Configuration.LaunchAtLogin,
            CheckOnClick = true
        };
        launchAtLogin.CheckedChanged += (_, _) =>
        {
            ConfigurationStore.Shared.SetLaunchAtLogin(launchAtLogin.Checked);
            if (audioState is not null)
            {
                audioState.SetStartupOptions(
                    launchAtLogin.Checked,
                    audioState.Snapshot.SilentStart);
            }
        };
        menu.Items.Add(launchAtLogin);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(T("退出 DeskMosaic", "Quit DeskMosaic"), null, (_, _) => Dispatcher.Invoke(RequestExit));
    }

    private Forms.ToolStripMenuItem BuildProfileMenu(AppStateSnapshot snapshot)
    {
        var english = audioState?.Snapshot.Language == "en-US";
        var root = new Forms.ToolStripMenuItem(english ? "Audio profiles" : "音频配置组");
        if (snapshot.Profiles.Count == 0)
        {
            root.DropDownItems.Add(english ? "No profiles saved" : "尚未保存配置组").Enabled = false;
            return root;
        }

        foreach (var profile in snapshot.Profiles)
        {
            var output = snapshot.AllOutputs.FirstOrDefault(device => AudioDeviceInfo.IdEquals(device.Id, profile.OutputDeviceId));
            var input = snapshot.AllInputs.FirstOrDefault(device => AudioDeviceInfo.IdEquals(device.Id, profile.InputDeviceId));
            var active = AudioDeviceInfo.IdEquals(snapshot.CurrentOutput?.Id, profile.OutputDeviceId)
                && AudioDeviceInfo.IdEquals(snapshot.CurrentInput?.Id, profile.InputDeviceId);
            var pair = $"{output?.Name ?? (english ? "Output offline" : "输出未连接")} + {input?.Name ?? (english ? "Input offline" : "输入未连接")}";
            var item = new Forms.ToolStripMenuItem($"{profile.Name}  ·  {pair}")
            {
                Checked = active,
                Enabled = output is not null && input is not null
            };
            item.Click += (_, _) => audioState?.ActivateProfile(profile.Id);
            root.DropDownItems.Add(item);
        }
        root.DropDownItems.Add(new Forms.ToolStripSeparator());
        root.DropDownItems.Add(english ? "Previous profile" : "上一个配置组", null, (_, _) => audioState?.CycleProfile(-1));
        root.DropDownItems.Add(english ? "Next profile" : "下一个配置组", null, (_, _) => audioState?.CycleProfile(1));
        return root;
    }

    private Forms.ToolStripMenuItem BuildVolumeMenu(string title, AudioDeviceInfo? device)
    {
        var root = new Forms.ToolStripMenuItem(
            device is null ? $"{title}：无设备" : $"{title}：{device.Volume:P0}")
        {
            Enabled = device is not null
        };
        if (device is null) return root;

        var slider = new Forms.TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickStyle = Forms.TickStyle.None,
            Value = Math.Clamp((int)Math.Round(device.Volume * 100), 0, 100),
            Width = 220,
            Height = 36
        };
        slider.ValueChanged += (_, _) =>
        {
            root.Text = $"{title}：{slider.Value}%";
            audioState?.SetVolume(device, slider.Value / 100f);
        };
        root.DropDownItems.Add(new Forms.ToolStripControlHost(slider)
        {
            AutoSize = false,
            Size = new System.Drawing.Size(230, 42)
        });
        return root;
    }

    private void StartCapture()
    {
        ScreenshotManager.Shared.StartCapture();
    }

    private void ShowSettings()
    {
        if (hotKeyManager is null) return;
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(hotKeyManager, StartCapture, audioState, ReloadHotKeys,
                () => OpenNotebook("quick"), () => OpenNotebook(), OpenTranslation);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    private void OpenTranslation()
    {
        if (translationWindow is null)
        {
            translationWindow = new Translation.TranslationWindow(() =>
            {
                ShowSettings();
                settingsWindow?.ShowTranslationPage();
            });
            translationWindow.Closed += (_, _) => translationWindow = null;
        }
        translationWindow.Open();
    }

    private void OpenNotebook(string intent = "last")
    {
        notebookWindow ??= new Notebook.NotebookWindow();
        notebookWindow.Open(intent);
    }
    private void ReloadHotKeys()
    {
        if (hotKeyManager is null) return;
        var snapshot = audioState?.Snapshot;
        var audioEnabled = snapshot?.AudioEnabled == true;
        hotKeyManager.Reload(
            ConfigurationStore.Shared.Configuration.ScreenshotHotKey,
            StartCapture,
            audioEnabled ? snapshot!.NextProfileHotkey : string.Empty,
            () => audioState?.CycleProfile(1),
            audioEnabled ? snapshot!.PreviousProfileHotkey : string.Empty,
            () => audioState?.CycleProfile(-1),
            ConfigurationStore.Shared.Configuration.NotebookLastHotKey, () => OpenNotebook(),
            ConfigurationStore.Shared.Configuration.NotebookQuickHotKey, () => OpenNotebook("quick"),
            ConfigurationStore.Shared.Configuration.TranslationHotKey, OpenTranslation);
    }
}
