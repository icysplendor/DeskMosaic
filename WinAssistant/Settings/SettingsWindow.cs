using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using System.Windows.Shell;
using WinAssistant.Audio.Models;
using WinAssistant.Audio.Services;
using WinAssistant.Audio.UI;
using WinAssistant.Infrastructure;
using WinAssistant.Translation;

namespace WinAssistant.Settings;

internal sealed partial class SettingsWindow : Window
{
    private enum RecordingTarget { None, Screenshot, NotebookLast, NotebookQuick, Translation, NextProfile, PreviousProfile }

    private readonly GlobalHotKeyManager hotKeyManager;
    private readonly Action captureAction;
    private AppStateService? audioState;
    private readonly Action reloadAllHotKeys;
    private readonly Dictionary<RecordingTarget, Button> shortcutButtons = new();
    private TextBlock statusText = new();
    private RecordingTarget recordingTarget;
    private readonly System.Windows.Threading.DispatcherTimer renderTimer;
    private const int OverviewTab = 5;
    private int selectedTabIndex = OverviewTab;
    private ScrollViewer? pageScroller;
    private int renderedTab;
    private readonly Action quickNote;
    private readonly Action openNotebook;
    private readonly Action openTranslation;

    internal SettingsWindow(
        GlobalHotKeyManager hotKeyManager,
        Action captureAction,
        AppStateService? audioState,
        Action reloadAllHotKeys, Action quickNote, Action openNotebook, Action openTranslation)
    {
        this.hotKeyManager = hotKeyManager;
        this.captureAction = captureAction;
        this.quickNote = quickNote;
        this.openNotebook = openNotebook;
        this.openTranslation = openTranslation;
        this.audioState = audioState;
        this.reloadAllHotKeys = reloadAllHotKeys;
        renderTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        renderTimer.Tick += (_, _) =>
        {
            if (IsMouseCaptureWithin) return;
            renderTimer.Stop();
            Render();
        };

        Title = "辅助工具控制中心";
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/AppIcon.ico", UriKind.Absolute));
        Width = 1160;
        Height = 760;
        MinWidth = 980;
        MinHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 52,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            UseAeroCaptionButtons = false
        });
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Settings/SwitchStyles.xaml")
        });
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        PreviewKeyDown += HandlePreviewKeyDown;
        Deactivated += (_, _) => EndRecording();
        Closed += (_, _) =>
        {
            if (this.audioState is not null) this.audioState.StateChanged -= AudioStateChanged;
            renderTimer.Stop();
        };
        if (audioState is not null) audioState.StateChanged += AudioStateChanged;
        StateChanged += (_, _) => Render();
        Render();
    }

    private bool IsEnglish => audioState?.Snapshot.Language == "en-US";

    internal void AttachAudioState(AppStateService state)
    {
        if (audioState is not null) audioState.StateChanged -= AudioStateChanged;
        audioState = state;
        audioState.StateChanged += AudioStateChanged;
        Render();
    }

    private string T(string chinese, string english) => IsEnglish ? english : chinese;

    private void Render()
    {
        Title = T("辅助工具控制中心", "Assistant Control Center");
        var dark = audioState?.Snapshot.Theme == "Dark";
        ThemeManager.ApplyWindowFrame(this, audioState?.Snapshot.Theme);
        var appBrush = Brush(dark ? "#0B1422" : "#F0F1F6");
        var lineBrush = Brush(dark ? "#444754" : "#DADCE3");
        Background = appBrush;
        Foreground = Brush(dark ? "#F7F9FC" : "#161B28");
        FontSize = 13;
        Resources[typeof(Button)] = BuildButtonStyle(dark);
        var sliderStyle = new Style(typeof(Slider));
        sliderStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#4C5872")));
        Resources[typeof(Slider)] = sliderStyle;

        var root = new Border
        {
            Background = appBrush,
            BorderBrush = lineBrush,
            BorderThickness = new Thickness(1)
        };
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition());
        shell.Children.Add(BuildApplicationTitleBar(dark));

        var content = new Grid { Margin = new Thickness(18) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition());
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (selectedTabIndex != OverviewTab)
        {
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 14) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);
            var language = new Button { Content = IsEnglish ? "中文" : "English", Margin = new Thickness(0, 0, 8, 0) };
            language.Click += (_, _) => audioState?.SetLanguage(IsEnglish ? "zh-CN" : "en-US");
            actions.Children.Add(language);
            if (selectedTabIndex == 0)
            {
                var refresh = new Button { Content = T("刷新", "Refresh"), Background = Brush("#4C5872"), BorderBrush = Brush("#4C5872"), Foreground = Brushes.White };
                refresh.Click += (_, _) => audioState?.Refresh();
                actions.Children.Add(refresh);
            }
            header.Children.Add(actions);
            var headerLabels = new StackPanel();
            headerLabels.Children.Add(new TextBlock { Text = selectedTabIndex == 0 ? T("音频控制中心", "Audio Control Center") : PageName(selectedTabIndex), FontSize = 26, FontWeight = FontWeights.SemiBold });
            headerLabels.Children.Add(new TextBlock { Text = PageSubtitle(selectedTabIndex), Foreground = Muted(dark), Margin = new Thickness(0, 4, 0, 0) });
            header.Children.Add(headerLabels);
            content.Children.Add(header);
        }

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var index in new[] { OverviewTab, 0, 1, 4, 2, 3 })
        {
            var target = index;
            var selected = selectedTabIndex == index;
            var tab = new Button
            {
                Content = index == 0 ? T("音频", "Audio") : PageName(index),
                MinWidth = 72,
                Margin = new Thickness(0, 0, 8, 0),
                Background = selected ? Brush("#333846") : Brushes.Transparent,
                BorderBrush = selected ? Brush("#333846") : Brushes.Transparent,
                Foreground = selected ? Brushes.White : Foreground
            };
            tab.Click += (_, _) => { selectedTabIndex = target; Render(); };
            tabs.Children.Add(tab);
        }
        Grid.SetRow(tabs, 2);
        content.Children.Add(tabs);

        UIElement page = selectedTabIndex switch
        {
            OverviewTab => BuildOverview(dark),
            1 => BuildScreenshotPage(dark),
            2 => BuildShortcutPage(dark),
            3 => BuildGeneralPage(dark),
            4 => BuildTranslationPage(dark),
            _ => BuildAudioPage(dark)
        };
        var scroller = new ScrollViewer
        {
            Content = page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = appBrush
        };
        if (pageScroller is not null && renderedTab == selectedTabIndex)
            scroller.ScrollToVerticalOffset(pageScroller.VerticalOffset);
        pageScroller = scroller;
        renderedTab = selectedTabIndex;
        Grid.SetRow(scroller, 1);
        content.Children.Add(scroller);
        Grid.SetRow(content, 1);
        shell.Children.Add(content);
        root.Child = shell;
        Content = root;
    }

    private UIElement BuildApplicationTitleBar(bool dark)
    {
        var bar = new Grid
        {
            Height = 52,
            Background = Brush(dark ? "#1B1D25" : "#FAFAFC")
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dragArea = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(14, 0, 12, 0)
        };
        var identity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var headerIcon = new Image
        {
            Source = ApplicationImages.HeaderIcon,
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(headerIcon, BitmapScalingMode.HighQuality);
        identity.Children.Add(headerIcon);
        identity.Children.Add(new TextBlock
        {
            Text = "DeskMosaic",
            Margin = new Thickness(9, 0, 0, 0),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        identity.Children.Add(new Border
        {
            Width = 1,
            Height = 20,
            Background = Brush(dark ? "#444754" : "#DADCE3"),
            Margin = new Thickness(16, 0, 0, 0)
        });
        dragArea.Child = identity;
        bar.Children.Add(dragArea);

        var context = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        context.Children.Add(new TextBlock
        {
            Text = PageName(selectedTabIndex),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        context.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Brush("#24B35A"),
            Margin = new Thickness(14, 0, 7, 0)
        });
        context.Children.Add(new TextBlock
        {
            Text = T("运行中", "Running"),
            Foreground = Muted(dark),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(context, 1);
        bar.Children.Add(context);

        var commands = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        commands.Children.Add(BuildTitleBarThemeSwitch(dark));
        commands.Children.Add(CaptionButton("−", T("最小化", "Minimize"), dark, false, () => WindowState = WindowState.Minimized));
        commands.Children.Add(CaptionButton(WindowState == WindowState.Maximized ? "❐" : "□", WindowState == WindowState.Maximized ? T("还原", "Restore") : T("最大化", "Maximize"), dark, false, ToggleMaximize));
        commands.Children.Add(CaptionButton("×", T("关闭", "Close"), dark, true, Close));
        WindowChrome.SetIsHitTestVisibleInChrome(commands, true);
        Grid.SetColumn(commands, 2);
        bar.Children.Add(commands);
        var divider = new Border
        {
            Height = 1,
            Background = Brush(dark ? "#444754" : "#DADCE3"),
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false
        };
        Grid.SetColumnSpan(divider, 3);
        bar.Children.Add(divider);
        return bar;
    }

    private Button BuildTitleBarThemeSwitch(bool dark)
    {
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        choices.Children.Add(new Border
        {
            Width = 27,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = dark ? Brushes.Transparent : Brush("#E9F0FA"),
            Child = new TextBlock { Text = "☀", FontSize = 13, Foreground = dark ? Muted(true) : Brush("#4C5872"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        choices.Children.Add(new Border
        {
            Width = 27,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = dark ? Brush("#243146") : Brushes.Transparent,
            Child = new TextBlock { Text = "◐", FontSize = 13, Foreground = dark ? Brush("#6D9EFF") : Muted(false), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        var button = new Button
        {
            Content = choices,
            ToolTip = dark ? T("切换到浅色模式", "Switch to light mode") : T("切换到深色模式", "Switch to dark mode"),
            Margin = new Thickness(0, 9, 10, 9),
            Padding = new Thickness(3),
            Background = Brush(dark ? "#20222C" : "#FFFFFF"),
            BorderBrush = Brush(dark ? "#444754" : "#DADCE3"),
            BorderThickness = new Thickness(1),
            Focusable = false
        };
        button.Click += (_, _) => audioState?.SetTheme(dark ? "Light" : "Dark");
        return button;
    }

    private Button CaptionButton(string glyph, string toolTip, bool dark, bool danger, Action action)
    {
        var button = new Button
        {
            Content = glyph,
            ToolTip = toolTip,
            Width = 40,
            Height = 52,
            MinHeight = 52,
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = Foreground,
            FontSize = glyph == "−" ? 18 : 15,
            FontWeight = FontWeights.Medium,
            Focusable = false
        };
        button.MouseEnter += (_, _) =>
        {
            button.Background = Brush(danger ? "#E5484D" : dark ? "#253248" : "#EAF1FC");
            if (danger) button.Foreground = Brushes.White;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            button.Foreground = Foreground;
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private string PageName(int index) => index switch
    {
        OverviewTab => T("总览", "Overview"),
        1 => T("截图", "Screenshot"),
        2 => T("快捷键", "Hotkeys"),
        3 => T("通用", "General"),
        4 => T("录写/翻译", "Transcribe / Translate"),
        _ => T("音频控制中心", "Audio Control Center")
    };

    private string PageSubtitle(int index) => index switch
    {
        1 => T("跨显示器捕捉、标注、保存并复制到剪贴板。", "Capture across displays, annotate, save, and copy."),
        2 => T("为常用操作录制不会冲突的全局组合键。", "Record conflict-free global shortcuts for common actions."),
        3 => T("调整外观、语言与启动行为。", "Adjust appearance, language, and startup behavior."),
        4 => T("设置本地模型、音源与推理设备。", "Choose local models, audio sources, and compute."),
        _ => T("用配置组整体切换输入与输出，并锁定当前设备组合。", "Switch input and output together with profiles and lock the active pair.")
    };

    private UIElement BuildAudioPage(bool dark)
    {
        if (audioState is null)
        {
            return WaccCard(new TextBlock { Text = T("音频服务不可用", "Audio service unavailable"), Foreground = Muted(dark) }, dark);
        }
        shortcutButtons.Clear();
        var snapshot = audioState.Snapshot;
        var root = new StackPanel();
        if (!snapshot.AudioEnabled)
        {
            root.Children.Add(BuildAudioMasterSwitch(snapshot, dark));
            root.Children.Add(BuildProfileRecoverySwitch(snapshot, dark));
            root.Children.Add(WaccCard(new TextBlock
            {
                Text = T("音频功能已关闭。设备枚举、音量监听、设备锁定恢复和音频配置组快捷键均已停止。", "Audio is off. Device discovery, volume monitoring, lock recovery, and audio profile hotkeys are stopped."),
                Foreground = Muted(dark),
                TextWrapping = TextWrapping.Wrap
            }, dark));
            return root;
        }
        root.Children.Add(BuildCompactCurrentDetails(snapshot, dark));
        root.Children.Add(BuildAudioProfiles(snapshot, dark));
        root.Children.Add(BuildAudioMasterSwitch(snapshot, dark));
        root.Children.Add(BuildProfileRecoverySwitch(snapshot, dark));
        return root;
    }

    private UIElement BuildAudioProfiles(AppStateSnapshot snapshot, bool dark)
    {
        var panel = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        var previous = new Button { Content = T("‹ 上一个", "‹ Previous"), Margin = new Thickness(0, 0, 8, 0), IsEnabled = snapshot.Profiles.Count > 0 };
        previous.Click += (_, _) => audioState?.CycleProfile(-1);
        actions.Children.Add(previous);
        var next = new Button { Content = T("下一个 ›", "Next ›"), Margin = new Thickness(0, 0, 8, 0), IsEnabled = snapshot.Profiles.Count > 0 };
        next.Click += (_, _) => audioState?.CycleProfile(1);
        actions.Children.Add(next);
        var add = new Button
        {
            Content = T("＋ 新建组合", "+ New profile"),
            Background = Brush("#4C5872"),
            BorderBrush = Brush("#4C5872"),
            Foreground = Brushes.White,
            IsEnabled = snapshot.CurrentOutput is not null && snapshot.CurrentInput is not null
        };
        add.Click += (_, _) => audioState?.AddCurrentProfile();
        actions.Children.Add(add);
        header.Children.Add(actions);
        var labels = new StackPanel();
        labels.Children.Add(new TextBlock { Text = T("音频配置组", "Audio profiles"), FontSize = 18, FontWeight = FontWeights.SemiBold });
        labels.Children.Add(new TextBlock { Text = T("将一台输出设备和一台输入设备保存为一组。", "Save one output and one input device as a pair."), Foreground = Muted(dark), Margin = new Thickness(0, 4, 0, 0) });
        header.Children.Add(labels);
        panel.Children.Add(header);

        if (snapshot.Profiles.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = T("还没有配置组。点击“新建组合”开始。", "No profiles yet. Create a profile to begin."),
                Foreground = Muted(dark),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 18, 0, 18)
            });
        }
        else
        {
            foreach (var profile in snapshot.Profiles)
            {
                panel.Children.Add(BuildAudioProfileRow(profile, snapshot, dark));
            }
        }
        return WaccCard(panel, dark);
    }

    private UIElement BuildAudioProfileRow(AudioConfigurationProfile profile, AppStateSnapshot snapshot, bool dark)
    {
        var savedOutput = snapshot.AllOutputs.FirstOrDefault(device => AudioDeviceInfo.IdEquals(device.Id, profile.OutputDeviceId));
        var savedInput = snapshot.AllInputs.FirstOrDefault(device => AudioDeviceInfo.IdEquals(device.Id, profile.InputDeviceId));
        var outputOnline = savedOutput is not null;
        var inputOnline = savedInput is not null;
        var active = AudioDeviceInfo.IdEquals(snapshot.CurrentOutput?.Id, profile.OutputDeviceId)
            && AudioDeviceInfo.IdEquals(snapshot.CurrentInput?.Id, profile.InputDeviceId);
        var row = new Grid { MinHeight = 58 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.Children.Add(new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = Brush(active ? "#42BE57" : dark ? "#59667A" : "#A8B2C2"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var name = new TextBox
        {
            Text = profile.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Height = 42,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 8, 10, 8),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var output = new ComboBox
        {
            ItemsSource = ProfileDeviceOptions(
                snapshot.AllOutputs,
                profile.OutputDeviceId,
                profile.OutputDeviceName is null
                    ? T("输出设备未连接", "Output unavailable")
                    : $"{profile.OutputDeviceName} · {T("未连接", "Offline")}",
                AudioDeviceKind.Output),
            DisplayMemberPath = nameof(AudioDeviceInfo.Name),
            SelectedValuePath = nameof(AudioDeviceInfo.Id),
            SelectedValue = savedOutput?.Id ?? profile.OutputDeviceId,
            ToolTip = outputOnline ? T("输出设备", "Output device") : T("原输出设备未连接", "Saved output is offline"),
            Background = Brush(dark ? "#262832" : "#FFFFFF"),
            Foreground = Foreground,
            BorderBrush = Brush(dark ? "#46546A" : "#AEB8C8"),
            Height = 42,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(output, 2);
        row.Children.Add(output);
        var input = new ComboBox
        {
            ItemsSource = ProfileDeviceOptions(
                snapshot.AllInputs,
                profile.InputDeviceId,
                profile.InputDeviceName is null
                    ? T("输入设备未连接", "Input unavailable")
                    : $"{profile.InputDeviceName} · {T("未连接", "Offline")}",
                AudioDeviceKind.Input),
            DisplayMemberPath = nameof(AudioDeviceInfo.Name),
            SelectedValuePath = nameof(AudioDeviceInfo.Id),
            SelectedValue = savedInput?.Id ?? profile.InputDeviceId,
            ToolTip = inputOnline ? T("输入设备", "Input device") : T("原输入设备未连接", "Saved input is offline"),
            Background = Brush(dark ? "#262832" : "#FFFFFF"),
            Foreground = Foreground,
            BorderBrush = Brush(dark ? "#46546A" : "#AEB8C8"),
            Height = 42,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(input, 3);
        row.Children.Add(input);
        void SaveProfile()
        {
            var outputId = output.SelectedValue as string ?? profile.OutputDeviceId;
            var inputId = input.SelectedValue as string ?? profile.InputDeviceId;
            audioState?.UpdateProfile(profile.Id, name.Text, outputId, inputId);
        }
        name.LostFocus += (_, _) => SaveProfile();
        output.SelectionChanged += (_, _) => SaveProfile();
        input.SelectionChanged += (_, _) => SaveProfile();
        var apply = new Button
        {
            Content = active ? T("使用中", "Active") : T("应用", "Apply"),
            IsEnabled = !active && outputOnline && inputOnline,
            Background = active ? Brush("#4C5872") : Background,
            Foreground = active ? Brushes.White : Foreground,
            Width = 64,
            Height = 42,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        apply.Click += (_, _) => audioState?.ActivateProfile(profile.Id);
        Grid.SetColumn(apply, 4);
        row.Children.Add(apply);
        var delete = new Button
        {
            Content = "×",
            ToolTip = T("删除配置组", "Delete profile"),
            Width = 34,
            Height = 42,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        delete.Click += (_, _) => audioState?.DeleteProfile(profile.Id);
        Grid.SetColumn(delete, 5);
        row.Children.Add(delete);
        return new Border
        {
            Child = row,
            Background = active ? Brush(dark ? "#414758" : "#55C2CADD") : Brush(dark ? "#20222C" : "#FFFFFF"),
            BorderBrush = active ? Brush("#4C5872") : Brush(dark ? "#444754" : "#DADCE3"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 0, 8)
        };
    }

    private static IReadOnlyList<AudioDeviceInfo> ProfileDeviceOptions(
        IReadOnlyList<AudioDeviceInfo> availableDevices,
        string savedDeviceId,
        string unavailableLabel,
        AudioDeviceKind kind)
    {
        if (availableDevices.Any(device => AudioDeviceInfo.IdEquals(device.Id, savedDeviceId)))
        {
            return availableDevices;
        }

        return
        [
            new AudioDeviceInfo(savedDeviceId, unavailableLabel, kind, false, 0, false),
            .. availableDevices
        ];
    }

    private UIElement BuildCompactCurrentDetails(AppStateSnapshot snapshot, bool dark)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(CompactCurrentDevice(AudioDeviceKind.Output, snapshot.CurrentOutput, snapshot.LockOutput, dark));
        var divider = new Border { Background = Brush(dark ? "#444754" : "#DADCE3"), Margin = new Thickness(20, 0, 20, 0) };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);
        var input = CompactCurrentDevice(AudioDeviceKind.Input, snapshot.CurrentInput, snapshot.LockInput, dark);
        Grid.SetColumn(input, 2);
        grid.Children.Add(input);
        var card = WaccCard(grid, dark);
        card.Margin = new Thickness(0, 0, 0, 12);
        return card;
    }

    private UIElement CompactCurrentDevice(AudioDeviceKind kind, AudioDeviceInfo? device, bool locked, bool dark)
    {
        var output = kind == AudioDeviceKind.Output;
        var panel = new StackPanel();
        var heading = new DockPanel();
        var lockToggle = new CheckBox
        {
            Content = output ? T("锁定输出", "Lock output") : T("锁定输入", "Lock input"),
            IsChecked = locked,
            VerticalAlignment = VerticalAlignment.Center
        };
        lockToggle.Click += (_, _) => audioState?.SetLock(kind, lockToggle.IsChecked == true);
        DockPanel.SetDock(lockToggle, Dock.Right);
        heading.Children.Add(lockToggle);
        heading.Children.Add(new TextBlock
        {
            Text = $"{(output ? "OUT" : "IN")}  {device?.Name ?? T("无活动设备", "No active device")}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(heading);
        var volume = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        volume.ColumnDefinitions.Add(new ColumnDefinition());
        volume.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = device?.Volume * 100 ?? 0, IsEnabled = device is not null };
        slider.ValueChanged += (_, eventArgs) => { if (device is not null) audioState?.SetVolume(device, (float)(eventArgs.NewValue / 100)); };
        volume.Children.Add(slider);
        var percentage = new TextBlock { Text = device is null ? "—" : $"{device.Volume:P0}", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted(dark) };
        Grid.SetColumn(percentage, 1);
        volume.Children.Add(percentage);
        panel.Children.Add(volume);
        return panel;
    }

    private Border BuildWaccDeviceCard(AudioDeviceKind kind, IReadOnlyList<AudioDeviceInfo> devices, bool dark)
    {
        var isOutput = kind == AudioDeviceKind.Output;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var include = new TextBlock { Text = T("加入快速切换", "Include in quick switch"), FontSize = 12, Foreground = Muted(dark) };
        DockPanel.SetDock(include, Dock.Right);
        heading.Children.Add(include);
        heading.Children.Add(new TextBlock { Text = isOutput ? T("输出设备", "Output devices") : T("输入设备", "Input devices"), FontSize = 18, FontWeight = FontWeights.SemiBold });
        grid.Children.Add(heading);
        var panel = new StackPanel();
        if (devices.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = T("未发现可用设备", "No devices found"), Foreground = Muted(dark), Margin = new Thickness(2, 8, 0, 0) });
        }
        foreach (var device in devices) panel.Children.Add(BuildWaccDeviceRow(device, dark));
        var scroller = new ScrollViewer { Content = panel, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (pageScroller is not null && renderedTab == selectedTabIndex)
            scroller.ScrollToVerticalOffset(pageScroller.VerticalOffset);
        pageScroller = scroller;
        renderedTab = selectedTabIndex;
        Grid.SetRow(scroller, 1);
        grid.Children.Add(scroller);
        return WaccCard(grid, dark);
    }

    private UIElement BuildWaccDeviceRow(AudioDeviceInfo device, bool dark)
    {
        var border = new Border
        {
            Background = device.IsDefault ? Brush(dark ? "#414758" : "#55C2CADD") : Brush(dark ? "#20222C" : "#FFFFFF"),
            BorderBrush = device.IsDefault ? Brush("#4C5872") : Brush(dark ? "#444754" : "#DADCE3"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            MinHeight = 74
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(dark ? "#444754" : "#DADCE3"),
            Background = Brush(dark ? "#262832" : "#FFFFFF"),
            Child = new TextBlock { Text = device.Kind == AudioDeviceKind.Output ? "OUT" : "IN", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brush("#4C5872"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        var labels = new StackPanel { Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = device.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        status.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Brush("#42BE57"), Margin = new Thickness(0, 6, 7, 0) });
        status.Children.Add(new TextBlock { Text = device.IsDefault ? T("默认", "Default") : T($"已连接 · {device.Volume:P0}", $"Connected · {device.Volume:P0}"), Foreground = Muted(dark) });
        labels.Children.Add(status);
        Grid.SetColumn(labels, 1);
        row.Children.Add(labels);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var quick = new CheckBox { IsChecked = false, IsEnabled = false, ToolTip = T("设备改由配置组管理", "Devices are managed by profiles"), Margin = new Thickness(0, 0, 14, 0) };
        actions.Children.Add(quick);
        var switchButton = new Button { Content = device.IsDefault ? T("默认", "Default") : T("切换", "Switch"), IsEnabled = !device.IsDefault };
        actions.Children.Add(switchButton);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        border.Child = row;
        return border;
    }

    private UIElement BuildWaccDetails(AppStateSnapshot snapshot, bool dark)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(BuildCurrentDetail(AudioDeviceKind.Output, snapshot.CurrentOutput, snapshot.LockOutput, dark));
        var input = BuildCurrentDetail(AudioDeviceKind.Input, snapshot.CurrentInput, snapshot.LockInput, dark);
        input.Margin = new Thickness(24, 0, 0, 0);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        var card = WaccCard(grid, dark);
        card.Margin = new Thickness(0, 16, 0, 0);
        return card;
    }

    private StackPanel BuildCurrentDetail(AudioDeviceKind kind, AudioDeviceInfo? device, bool locked, bool dark)
    {
        var isOutput = kind == AudioDeviceKind.Output;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = isOutput ? T("当前输出详情", "Current output") : T("当前输入详情", "Current input"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = device is null ? T("无活动设备", "No active device") : $"{device.Name}  {device.Volume:P0}", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 4), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = T("每个设备的音量都会被记住，并在切回时恢复。", "Each device volume is remembered and restored when you switch back."), Foreground = Muted(dark), TextWrapping = TextWrapping.Wrap });
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = device?.Volume * 100 ?? 0, Margin = new Thickness(0, 18, 0, 0), IsEnabled = device is not null };
        slider.ValueChanged += (_, eventArgs) => { if (device is not null) audioState?.SetVolume(device, (float)(eventArgs.NewValue / 100)); };
        panel.Children.Add(slider);
        var lockToggle = new CheckBox { Content = isOutput ? T("锁定输出设备", "Lock output") : T("锁定输入设备", "Lock input"), IsChecked = locked, Margin = new Thickness(0, 18, 0, 0) };
        lockToggle.Click += (_, _) => audioState?.SetLock(kind, lockToggle.IsChecked == true);
        panel.Children.Add(lockToggle);
        return panel;
    }

    private UIElement BuildWaccBottom(AppStateSnapshot snapshot, bool dark)
    {
        var grid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        var hotkeys = new Grid();
        hotkeys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        hotkeys.ColumnDefinitions.Add(new ColumnDefinition());
        var help = new StackPanel();
        help.Children.Add(new TextBlock { Text = T("全局快捷键", "Global hotkeys"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        help.Children.Add(new TextBlock { Text = T("这些快捷键会切换默认音频设备。", "These hotkeys switch your default audio devices."), Foreground = Muted(dark), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 18, 0) });
        hotkeys.Children.Add(help);
        var editors = new StackPanel();
        editors.Children.Add(WaccHotkeyEditor(RecordingTarget.NextProfile, T("下一个", "Next"), new Thickness(0, 0, 0, 10)));
        editors.Children.Add(WaccHotkeyEditor(RecordingTarget.PreviousProfile, T("上一个", "Previous"), new Thickness(0)));
        Grid.SetColumn(editors, 1);
        hotkeys.Children.Add(editors);
        var hotkeyCard = WaccCard(hotkeys, dark);
        hotkeyCard.Margin = new Thickness(0, 0, 9, 0);
        grid.Children.Add(hotkeyCard);

        var options = new StackPanel();
        options.Children.Add(new TextBlock { Text = T("其他设置", "Other settings"), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        var startup = new CheckBox { Content = T("登录时启动", "Start with Windows"), IsChecked = ConfigurationStore.Shared.Configuration.LaunchAtLogin, Margin = new Thickness(0, 0, 0, 12) };
        startup.Click += (_, _) => { ConfigurationStore.Shared.SetLaunchAtLogin(startup.IsChecked == true); audioState?.SetStartupOptions(startup.IsChecked == true, snapshot.SilentStart); };
        options.Children.Add(startup);
        var silent = new CheckBox { Content = T("最小化到托盘", "Minimize to tray"), IsChecked = snapshot.SilentStart };
        silent.Click += (_, _) => audioState?.SetStartupOptions(ConfigurationStore.Shared.Configuration.LaunchAtLogin, silent.IsChecked == true);
        options.Children.Add(silent);
        var optionCard = WaccCard(options, dark);
        optionCard.Margin = new Thickness(9, 0, 0, 0);
        Grid.SetColumn(optionCard, 1);
        grid.Children.Add(optionCard);
        return grid;
    }

    private UIElement WaccHotkeyEditor(RecordingTarget target, string label, Thickness margin)
    {
        var grid = new Grid { Margin = margin };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var value = new TextBox
        {
            Text = ShortcutText(target),
            IsReadOnly = true,
            IsTabStop = false,
            MinHeight = 36,
            Padding = new Thickness(10, 7, 10, 7),
            Background = Background,
            Foreground = Foreground,
            BorderBrush = Brush("#DADCE3"),
            BorderThickness = new Thickness(1)
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        var record = new Button { Content = T("录入", "Record"), Margin = new Thickness(8, 0, 0, 0) };
        record.Click += (_, _) => BeginRecording(target);
        shortcutButtons[target] = record;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(record);
        actions.Children.Add(ClearShortcutButton(target));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return grid;
    }

    private bool showRecordingSettings;

    private UIElement BuildScreenshotPage(bool dark)
    {
        var root = new StackPanel();
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var action = OverviewButton(T("开始框选截图", "Select region to capture"), captureAction, primary: true);
        DockPanel.SetDock(action, Dock.Right);
        bar.Children.Add(action);
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var recording in new[] { false, true })
        {
            var tab = new Button
            {
                Content = recording ? T("录屏文件", "Recording files") : T("截图标注", "Annotations"),
                Margin = new Thickness(0, 0, 6, 0)
            };
            if (recording == showRecordingSettings) { tab.Background = Brush("#4C5872"); tab.Foreground = Brushes.White; }
            tab.Click += (_, _) => { showRecordingSettings = recording; Render(); };
            choices.Children.Add(tab);
        }
        bar.Children.Add(choices);
        root.Children.Add(bar);
        if (showRecordingSettings)
        {
            root.Children.Add(WaccCard(BuildRegionRecordingSettings(), dark));
            return root;
        }

        var defaults = ConfigurationStore.Shared.Configuration.ScreenshotDefaults;
        var defaultsGrid = new Grid();
        defaultsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        defaultsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var controls = new StackPanel();
        controls.Children.Add(new TextBlock { Text = T("默认标注配置", "Default annotation settings"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        controls.Children.Add(new TextBlock { Text = T("截图开始时自动使用这些值；文字粗细只在这里设置。", "New captures start with these values; text weight is configured only here."), Foreground = Muted(dark), Margin = new Thickness(0, 6, 0, 14) });

        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        colorRow.Children.Add(new TextBlock { Text = T("默认颜色", "Default color"), Width = 120, VerticalAlignment = VerticalAlignment.Center });
        var colors = new[]
        {
            "#FFFF0000", "#FFFFF200", "#FF38BDF8", "#FF808080", "#FF708238",
            "#FF000000", "#FFFFFFFF", "#FF8A2BE2", "#FFFF8C00"
        };
        foreach (var color in colors)
        {
            var selected = string.Equals(defaults.Color, color, StringComparison.OrdinalIgnoreCase);
            var swatch = new Button
            {
                Width = 32,
                Height = 32,
                MinHeight = 32,
                Padding = new Thickness(5),
                Margin = new Thickness(0, 0, 8, 0),
                BorderThickness = new Thickness(selected ? 3 : 1),
                BorderBrush = selected ? Brush("#4C5872") : Brush(dark ? "#59667A" : "#BFC8D6"),
                Content = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(8),
                    Background = Brush(color),
                    BorderBrush = Brush("#8893A5"),
                    BorderThickness = color == "#FFFFFFFF" ? new Thickness(1) : new Thickness(0)
                }
            };
            swatch.Click += (_, _) => { ConfigurationStore.Shared.Update(configuration => configuration.ScreenshotDefaults.Color = color); Render(); };
            colorRow.Children.Add(swatch);
        }
        controls.Children.Add(colorRow);

        var strokePicker = new ComboBox { Width = 170, ItemsSource = new[] { 1d, 2d, 3d, 5d, 8d, 12d }, SelectedItem = defaults.StrokeWidth };
        strokePicker.SelectionChanged += (_, _) =>
        {
            if (strokePicker.SelectedItem is double value)
            {
                ConfigurationStore.Shared.Update(configuration => configuration.ScreenshotDefaults.StrokeWidth = value);
                Render();
            }
        };
        controls.Children.Add(ScreenshotSettingRow(T("线条粗细", "Stroke width"), strokePicker));
        var fontSizePicker = new ComboBox { Width = 170, ItemsSource = new[] { 14d, 18d, 24d, 32d, 44d, 60d }, SelectedItem = defaults.FontSize };
        fontSizePicker.SelectionChanged += (_, _) =>
        {
            if (fontSizePicker.SelectedItem is double value)
            {
                ConfigurationStore.Shared.Update(configuration => configuration.ScreenshotDefaults.FontSize = value);
                Render();
            }
        };
        controls.Children.Add(ScreenshotSettingRow(T("文字大小", "Text size"), fontSizePicker));
        var weightPicker = new ComboBox { Width = 170 };
        foreach (var item in new[]
        {
            ("Regular", T("常规", "Regular")),
            ("Medium", T("中等", "Medium")),
            ("SemiBold", T("半粗", "Semibold")),
            ("Bold", T("粗体", "Bold"))
        })
        {
            weightPicker.Items.Add(new ComboBoxItem { Tag = item.Item1, Content = item.Item2, IsSelected = defaults.FontWeight == item.Item1 });
        }
        weightPicker.SelectionChanged += (_, _) =>
        {
            if (weightPicker.SelectedItem is ComboBoxItem { Tag: string value })
            {
                ConfigurationStore.Shared.Update(configuration => configuration.ScreenshotDefaults.FontWeight = value);
                Render();
            }
        };
        controls.Children.Add(ScreenshotSettingRow(T("文字粗细", "Text weight"), weightPicker));
        defaultsGrid.Children.Add(controls);

        var preview = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        preview.Children.Add(new TextBlock { Text = T("预览", "Preview"), FontSize = 12, Foreground = Muted(dark), Margin = new Thickness(0, 0, 0, 12) });
        preview.Children.Add(new Border { Height = Math.Min(12, defaults.StrokeWidth), Background = Brush(defaults.Color), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 18) });
        preview.Children.Add(new TextBlock { Text = "Aa  标注文字 / Annotation", FontSize = Math.Min(32, defaults.FontSize), FontWeight = ScreenshotFontWeight(defaults.FontWeight), Foreground = Brush(defaults.Color), TextWrapping = TextWrapping.Wrap });
        var previewCard = new Border { Child = preview, Background = Brush(dark ? "#20222C" : "#FFFFFF"), BorderBrush = Brush(dark ? "#444754" : "#DADCE3"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(18), Margin = new Thickness(28, 0, 0, 0) };
        Grid.SetColumn(previewCard, 1);
        defaultsGrid.Children.Add(previewCard);
        var defaultsCard = WaccCard(defaultsGrid, dark);
        defaultsCard.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(defaultsCard);
        return root;
    }

    private UIElement BuildRegionRecordingSettings()
    {
        var store = ConfigurationStore.Shared;
        var settings = store.Configuration.RegionRecording;
        var controls = new StackPanel();
        controls.Children.Add(new TextBlock
        {
            Text = T("区域录屏", "Region recording"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });
        var folder = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, MaxWidth = 360 };
        void UpdateFolder() => folder.Content = new TextBlock
        {
            Text = string.IsNullOrEmpty(settings.Directory)
                ? Screenshot.RegionRecording.DefaultDirectory() : settings.Directory,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        UpdateFolder();
        folder.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = ((TextBlock)folder.Content).Text };
            if (picker.ShowDialog(this) == true)
                store.Update(config => config.RegionRecording.Directory = picker.FolderName);
            UpdateFolder();
        };
        var reset = new Button
        {
            Content = T("恢复下载目录", "Use Downloads"),
            Margin = new Thickness(8, 0, 0, 0)
        };
        reset.Click += (_, _) =>
        {
            store.Update(config => config.RegionRecording.Directory = "");
            UpdateFolder();
        };
        var folderControls = new StackPanel { Orientation = Orientation.Horizontal };
        folderControls.Children.Add(folder);
        folderControls.Children.Add(reset);
        controls.Children.Add(ScreenshotSettingRow(T("保存目录", "Save folder"), folderControls));
        AddOptions(T("内存缓存", "Memory buffer"), RegionRecordingSettings.CacheOptions,
            settings.CacheMiB, value => settings.CacheMiB = value, value => $"{value} MiB");
        AddOptions(T("最高分辨率", "Max resolution"), RegionRecordingSettings.HeightOptions,
            settings.MaxHeight, value => settings.MaxHeight = value, value => $"{value}p");
        AddOptions(T("帧率", "Frame rate"), RegionRecordingSettings.FrameRateOptions,
            settings.FrameRate, value => settings.FrameRate = value, value => $"{value} fps");
        AddOptions(T("码率", "Bitrate"), RegionRecordingSettings.BitrateOptions,
            settings.BitrateMbps, value => settings.BitrateMbps = value,
            value => value == 0 ? T("自动 · 清晰优先", "Automatic · High quality") : $"{value} Mbps");
        return controls;

        void AddOptions(string title, int[] values, int current, Action<int> set, Func<int, string> label)
        {
            var picker = new ComboBox { Width = 190, ItemsSource = values.Select(label).ToArray() };
            picker.SelectedIndex = Array.IndexOf(values, current);
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex >= 0)
                    store.Update(_ => set(values[picker.SelectedIndex]));
            };
            controls.Children.Add(ScreenshotSettingRow(title, picker));
        }
    }

    private UIElement ScreenshotSettingRow(string title, FrameworkElement control)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static FontWeight ScreenshotFontWeight(string value) => value switch
    {
        "Regular" => FontWeights.Regular,
        "SemiBold" => FontWeights.SemiBold,
        "Bold" => FontWeights.Bold,
        _ => FontWeights.Medium
    };

    private UIElement BuildShortcutPage(bool dark)
    {
        shortcutButtons.Clear();
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = T("打开工具", "Open a tool"), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(ShortcutRow(RecordingTarget.Screenshot, T("开始截图", "Start capture"), T("跨显示器截图与标注", "Cross-screen capture and annotation")));
        panel.Children.Add(ShortcutRow(RecordingTarget.NotebookLast, T("打开上次笔记页面", "Open last notebook page"), T("恢复上次打开的笔记", "Restore the last opened note")));
        panel.Children.Add(ShortcutRow(RecordingTarget.Translation, T("打开录写/翻译", "Open Transcribe / Translate"), T("显示录写/翻译窗口", "Show the transcription and translation window")));
        panel.Children.Add(ShortcutRow(RecordingTarget.NotebookQuick, T("打开快速笔记", "Open Quick Notes"), T("在固定笔记本中随手记录", "Write in the fixed Quick Notes notebook")));
        var root = new StackPanel();
        var toolsCard = WaccCard(panel, dark);
        toolsCard.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(toolsCard);
        panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = T("切换设备", "Switch devices"), FontWeight = FontWeights.SemiBold });
        if (audioState?.Snapshot.AudioEnabled == true)
        {
            panel.Children.Add(ShortcutRow(RecordingTarget.NextProfile, T("下一个音频配置组", "Next audio profile"), T("整体切换输出和输入设备", "Switch the output and input pair together")));
            panel.Children.Add(ShortcutRow(RecordingTarget.PreviousProfile, T("上一个音频配置组", "Previous audio profile"), T("按相反方向切换配置组", "Cycle through profiles in reverse")));
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = T("音频功能关闭时不会注册音频配置组快捷键。", "Audio profile hotkeys are not registered while audio is off."), Foreground = Muted(dark), Margin = new Thickness(0, 12, 0, 8) });
        }
        statusText = new TextBlock { Foreground = Brushes.IndianRed, Margin = new Thickness(0, 18, 0, 0), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(statusText);
        root.Children.Add(WaccCard(panel, dark));
        return root;
    }

    internal void ShowTranslationPage()
    {
        selectedTabIndex = 4;
        Render();
    }

    private readonly Dictionary<TranslationModelDownload, int> modelDownloads = new();
    private readonly Dictionary<TranslationModelDownload, string> modelDownloadErrors = new();

    private readonly HashSet<int> expandedModelSections = new() { 0 };

    private UIElement BuildTranslationPage(bool dark)
    {
        var panel = new StackPanel();
        var section = new StackPanel();
        var options = ConfigurationStore.Shared.Configuration.Translation;
        void Save(Action<TranslationOptions> update) =>
            ConfigurationStore.Shared.Update(config => update(config.Translation));

        (string Id, string Label)[] LanguageChoices(string[] available) => available.Select(id =>
            (id, id == "auto" ? T("自动识别", "Auto-detect")
                : T(TranslationOptions.Languages.First(language => language.Id == id).Chinese, id))).ToArray();
        BeginSection(T("实时识别", "Live recognition"));
        AddChoice(T("默认源语言", "Default source language"),
            LanguageChoices(options.AvailableSourceLanguages), options.SelectedSourceLanguage, value =>
            {
                Save(item => item.SourceLanguage = value);
                if (options.UseLmtTranslation) Render();
            });
        foreach (var speech in StreamingSpeechModel.Catalog)
        {
            AddFile(T(speech.Chinese, speech.English), options.PathFor(speech), "X-ASR (*.tar.bz2)|*.tar.bz2",
                speech.Download.Url, value => Save(item => item.SelectModel(speech, value)), model: speech.Download);
        }
        AddFile(T("流式说话人模型", "Streaming speaker model"), options.SelectedSpeakerModel, "GGUF (*.gguf)|*.gguf",
            TranslationModelDownload.OnlineSpeaker.Url, value => Save(item => item.OnlineSpeakerModel = value), model: TranslationModelDownload.OnlineSpeaker);

        BeginSection(T("段末复核", "Second pass"));
        AddChoice(T("启用段末复核", "Enable second pass"),
            [("on", T("模型就绪时启用", "On when installed")), ("off", T("关闭", "Off"))],
            options.EnableCorrection ? "on" : "off", value =>
            {
                Save(item => item.EnableCorrection = value == "on");
                Render();
            });
        bool mossReady = options.SelectedMossModel.Length != 0;
        AddChoice(T("复核模型", "Review model"),
            mossReady ? [("x-asr", "X-ASR 1920 ms"), ("moss", "MOSS 0.9B Q8")] : [("x-asr", "X-ASR 1920 ms")],
            mossReady && options.UsesMossReview ? "moss" : "x-asr", value => Save(item =>
            {
                item.ReviewModel = value;
                item.MultiSpeaker = false;
            }), enabled: options.EnableCorrection);
        var correction = StreamingSpeechModel.Correction;
        AddFile("X-ASR 1920 ms", options.SelectedCorrectionModel, "X-ASR (*.tar.bz2)|*.tar.bz2",
            correction.Download.Url, value => Save(item => item.CorrectionModel = value), model: correction.Download);
        AddFile("MOSS 0.9B Q8", options.SelectedMossModel, "GGUF (*.gguf)|*.gguf",
            TranslationModelDownload.Moss.Url, value => Save(item => item.MossModel = value), model: TranslationModelDownload.Moss);

        BeginSection(T("文本翻译", "Text translation"));
        AddChoice(T("默认目标语言", "Default target language"),
            LanguageChoices(options.AvailableTargetLanguages), options.SelectedTargetLanguage, value => Save(item => item.TargetLanguage = value));
        AddChoice(T("文本翻译模型", "Text translation model"),
            [("standard", "Hy-MT3 1.8B"), ("lmt", "LMT-60 0.6B")],
            options.UseLmtTranslation ? "lmt" : "standard",
            value =>
            {
                Save(item => item.UseLmtTranslation = value == "lmt");
                Render();
            });
        AddChoice(T("实时译文刷新间隔", "Live translation interval"),
            TranslationOptions.PreviewIntervalsMs.Select(interval => (interval.ToString(), $"{interval / 1000.0:0.#} {T("秒", "s")}")).ToArray(),
            options.PreviewIntervalMs.ToString(), value => Save(item => item.PreviewIntervalMs = int.Parse(value)));
        AddFile("Hy-MT3 · 1.35 GB", options.StandardTranslationPath, "GGUF (*.gguf)|*.gguf",
            TranslationModelDownload.Llama.Url, value => Save(item =>
            {
                item.TranslationModel = value;
                item.UseLmtTranslation = false;
            }), model: TranslationModelDownload.Llama);
        AddFile("LMT-60 · 805 MB", options.LmtTranslationPath, "GGUF (*.gguf)|*.gguf",
            TranslationModelDownload.Lmt.Url, value => Save(item =>
            {
                item.LmtTranslationModel = value;
                item.UseLmtTranslation = true;
            }), model: TranslationModelDownload.Lmt);
        BeginSection(T("音频与诊断", "Audio and diagnostics"));
        AddChoice(T("麦克风人声检测", "Microphone speech detection"),
            [("gentle", T("温和", "Gentle")), ("balanced", T("均衡", "Balanced")), ("off", T("关闭", "Off"))],
            options.MicrophoneFilter, value => Save(item => item.MicrophoneFilter = value));
        AddChoice(T("麦克风断句等待", "Microphone end-of-speech pause"),
            [("normal", T("标准", "Standard")), ("long", T("较长", "Longer"))],
            options.MicrophonePause, value => Save(item => item.MicrophonePause = value));
        AddChoice(T("推理设备", "Compute"),
            [("auto", T("自动", "Automatic")), ("cpu", "CPU"), ("gpu", T("优先 GPU", "Prefer GPU"))],
            options.Compute, value => Save(item => item.Compute = value));
        AddChoice(T("诊断日志（保存到下载）", "Diagnostic log (Downloads)"),
            [("on", T("开启", "On")), ("off", T("关闭", "Off"))],
            options.DiagnosticsEnabled ? "on" : "off", value => Save(item => item.DiagnosticsEnabled = value == "on"));
        var engines = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        AddFile("llama-server / llama", options.LlamaServer, "llama.cpp|llama-server.exe;llama.exe",
            TranslationOptions.LlamaRelease, value => Save(item => item.LlamaServer = value), engines);
        section.Children.Add(new Expander { Header = T("自定义推理引擎", "Custom inference engines"), Content = engines });

        return panel;

        void BeginSection(string title)
        {
            section = new StackPanel();
            var index = panel.Children.Count;
            var group = new Expander { Header = title, IsExpanded = expandedModelSections.Contains(index), Content = section, Foreground = Foreground };
            group.Expanded += (_, args) => { if (ReferenceEquals(args.OriginalSource, group)) expandedModelSections.Add(index); };
            group.Collapsed += (_, args) => { if (ReferenceEquals(args.OriginalSource, group)) expandedModelSections.Remove(index); };
            panel.Children.Add(new Border
            {
                Child = group, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 0, 10), Background = Brush(dark ? "#262832" : "#FFFFFF"),
                BorderBrush = Brush(dark ? "#444754" : "#DADCE3"), BorderThickness = new Thickness(1)
            });
        }

        void AddChoice(string label, (string Id, string Label)[] choices, string current, Action<string> change, bool enabled = true)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), MinHeight = 26 };
            var picker = new ComboBox { Width = 180, SelectedValuePath = "Tag", IsEnabled = enabled && choices.Length != 0,
                VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(picker, label);
            foreach (var choice in choices)
                picker.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Id });
            picker.SelectedValue = current;
            if (picker.SelectedIndex < 0) picker.SelectedIndex = 0;
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedValue is string value) change(value);
            };
            DockPanel.SetDock(picker, Dock.Right);
            row.Children.Add(picker);
            row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            section.Children.Add(row);
        }

        void AddFile(string label, string path, string filter, string url, Action<string> change, StackPanel? engineOverrides = null, TranslationModelDownload? model = null)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), MinHeight = 26 };
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0), MaxWidth = 240, TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            var download = new Button { Content = T("下载", "Download"), Width = 86, Margin = new Thickness(8, 0, 0, 0) };
            if (model is null)
            {
                download.Click += (_, _) => System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            DockPanel.SetDock(download, Dock.Right);
            row.Children.Add(download);
            var choose = new Button { Content = T("选择…", "Choose…"), Width = 86, Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetName(download, T($"下载 {label}", $"Download {label}"));
            AutomationProperties.SetName(choose, T($"选择 {label}", $"Choose {label}"));
            DockPanel.SetDock(choose, Dock.Right);
            row.Children.Add(choose);
            var placeholder = engineOverrides is null ? T("未安装", "Missing") : T("自动", "Automatic");
            var value = new TextBlock
            {
                Text = string.IsNullOrEmpty(path) ? placeholder : model is null ? Path.GetFileName(path) : T("已安装", "Installed"),
                ToolTip = path, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right, Foreground = Brush(dark ? "#A9B8CC" : "#56677D"),
                FontSize = 12
            };
            if (engineOverrides is not null)
            {
                var reset = new Button { Content = T("恢复自动", "Reset to automatic"), Margin = new Thickness(6, 0, 0, 0) };
                reset.Click += (_, _) =>
                {
                    change("");
                    value.Text = placeholder;
                    value.ToolTip = null;
                };
                DockPanel.SetDock(reset, Dock.Right);
                row.Children.Add(reset);
            }
            if (model is not null)
            {
                var state = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                state.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 6, Height = 6, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
                    Fill = Brush(string.IsNullOrEmpty(path) ? "#8290A3" : "#279961")
                });
                state.Children.Add(value);
                state.ToolTip = string.IsNullOrEmpty(path) ? model.FileName : path;
                row.Children.Add(state);
            }
            else row.Children.Add(value);
            choose.Click += (_, _) =>
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
                if (picker.ShowDialog(this) != true) return;
                if (model is not null && !model.HasExpectedSize(picker.FileName))
                {
                    MessageBox.Show(this, T("请选择此行对应的模型文件。", "Select the model file listed on this row."),
                        T("模型不匹配", "Model does not match"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                change(picker.FileName);
                if (model is not null)
                {
                    Render();
                    return;
                }
                value.Text = Path.GetFileName(picker.FileName);
                value.ToolTip = picker.FileName;
            };
            (engineOverrides ?? section).Children.Add(row);
            if (model is null) return;
            var errorText = new TextBlock
            {
                Text = modelDownloadErrors.GetValueOrDefault(model) ?? "",
                Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap
            };
            section.Children.Add(errorText);
            RefreshDownloadControls();
            download.Click += async (_, _) =>
            {
                if (!modelDownloads.TryAdd(model, 0)) return;
                modelDownloadErrors.Remove(model);
                RefreshDownloadControls();
                try
                {
                    var installedPath = await model.DownloadAsync(new Progress<int>(percent =>
                    {
                        if (!modelDownloads.ContainsKey(model)) return;
                        modelDownloads[model] = percent;
                        RefreshDownloadControls();
                    }));
                    change(installedPath);
                }
                catch (Exception error)
                {
                    modelDownloadErrors[model] = T("下载失败，请重试。", "Download failed. Please retry.") +
                        Environment.NewLine + error.Message;
                }
                finally
                {
                    modelDownloads.Remove(model);
                    // Rebuild the active settings page so it reflects a download completed in the background.
                    if (IsVisible && selectedTabIndex == 4) Render();
                }
            };

            void RefreshDownloadControls()
            {
                var downloading = modelDownloads.TryGetValue(model, out int percent);
                download.IsEnabled = choose.IsEnabled = !downloading;
                download.Content = downloading
                    ? percent == 100 ? T("校验中…", "Verifying…") : $"{percent}%"
                    : T("下载", "Download");
                errorText.Text = modelDownloadErrors.GetValueOrDefault(model) ?? "";
                errorText.Visibility = string.IsNullOrEmpty(errorText.Text) ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private UIElement BuildGeneralPage(bool dark)
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        var panel = new StackPanel();
        var display = new StackPanel();
        panel.Children.Add(new TextBlock { Text = T("后台运行", "Background operation"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        display.Children.Add(new TextBlock { Text = T("界面外观", "Appearance"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        if (audioState is not null)
        {
            panel.Children.Add(BuildAudioMasterSwitch(audioState.Snapshot, dark));
        }
        var launch = Toggle(T("登录时启动辅助工具", "Launch at sign-in"), ConfigurationStore.Shared.Configuration.LaunchAtLogin);
        launch.Click += (_, _) =>
        {
            var enabled = launch.IsChecked == true;
            ConfigurationStore.Shared.SetLaunchAtLogin(enabled);
            audioState?.SetStartupOptions(enabled, audioState.Snapshot.SilentStart);
        };
        panel.Children.Add(launch);
        var silent = Toggle(T("启动时保持在系统托盘", "Start silently in the system tray"), audioState?.Snapshot.SilentStart == true);
        silent.Click += (_, _) => audioState?.SetStartupOptions(ConfigurationStore.Shared.Configuration.LaunchAtLogin, silent.IsChecked == true);
        panel.Children.Add(silent);

        var appearance = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        appearance.Children.Add(new TextBlock { Text = T("主题", "Theme"), Width = 120, VerticalAlignment = VerticalAlignment.Center, Foreground = Foreground });
        var theme = new ComboBox { Width = 170, ItemsSource = new[] { "Light", "Dark" }, SelectedItem = audioState?.Snapshot.Theme };
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is string value) { audioState?.SetTheme(value); Render(); }
        };
        appearance.Children.Add(theme);
        display.Children.Add(appearance);

        var language = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        language.Children.Add(new TextBlock { Text = T("语言", "Language"), Width = 120, VerticalAlignment = VerticalAlignment.Center, Foreground = Foreground });
        var picker = new ComboBox { Width = 170, ItemsSource = new[] { "中文", "English" }, SelectedIndex = IsEnglish ? 1 : 0 };
        picker.SelectionChanged += (_, _) =>
        {
            audioState?.SetLanguage(picker.SelectedIndex == 1 ? "en-US" : "zh-CN");
            Render();
        };
        language.Children.Add(picker);
        display.Children.Add(language);
        var background = WaccCard(panel, dark);
        background.Margin = new Thickness(0, 0, 8, 0);
        layout.Children.Add(background);
        var visual = WaccCard(display, dark);
        Grid.SetColumn(visual, 1);
        layout.Children.Add(visual);
        return layout;
    }

    private UIElement BuildAudioMasterSwitch(AppStateSnapshot snapshot, bool dark)
    {
        var panel = new DockPanel();
        var toggle = Toggle(T("启用音频功能", "Enable audio features"), snapshot.AudioEnabled);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(toggle, Dock.Right);
        toggle.Click += (_, _) =>
        {
            audioState?.SetAudioEnabled(toggle.IsChecked == true);
            reloadAllHotKeys();
            Render();
        };
        panel.Children.Add(toggle);
        var labels = new StackPanel { Margin = new Thickness(0, 2, 20, 2) };
        labels.Children.Add(new TextBlock { Text = T("音频功能", "Audio features"), FontSize = 15, FontWeight = FontWeights.SemiBold });
        labels.Children.Add(new TextBlock
        {
            Text = T("关闭后停止设备枚举、监听、锁定恢复和音频快捷键。", "Turn off device discovery, monitoring, lock recovery, and audio hotkeys."),
            Foreground = Muted(dark),
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(labels);
        return WaccCard(panel, dark);
    }

    private UIElement BuildProfileRecoverySwitch(AppStateSnapshot snapshot, bool dark)
    {
        var panel = new DockPanel();
        var toggle = Toggle(T("按名称恢复设备绑定", "Recover device bindings by name"), snapshot.RecoverProfileDevicesByName);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(toggle, Dock.Right);
        toggle.Click += (_, _) => audioState?.SetProfileDeviceRecovery(toggle.IsChecked == true);
        panel.Children.Add(toggle);
        var labels = new StackPanel { Margin = new Thickness(0, 2, 20, 2) };
        labels.Children.Add(new TextBlock { Text = T("设备绑定恢复", "Device binding recovery"), FontSize = 15, FontWeight = FontWeights.SemiBold });
        labels.Children.Add(new TextBlock
        {
            Text = T("ID 失效时按同名设备修复配置组，包括 Windows 仍记录的离线设备；设备上线后才能切换。", "When an ID changes, repair profiles using a matching device name, including offline endpoints known to Windows. Switching waits until the device is online."),
            Foreground = Muted(dark),
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(labels);
        return WaccCard(panel, dark);
    }

    private UIElement ShortcutRow(RecordingTarget target, string title, string subtitle)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 0), MinHeight = 66 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel();
        labels.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Medium, Foreground = Foreground });
        labels.Children.Add(new TextBlock { Text = subtitle, Foreground = Brush("#768196"), Margin = new Thickness(0, 3, 12, 0), FontSize = 12 });
        row.Children.Add(labels);
        var button = new Button { MinWidth = 160, Height = 34, Content = ShortcutText(target), Padding = new Thickness(12, 4, 12, 4) };
        button.Click += (_, _) => BeginRecording(target);
        shortcutButtons[target] = button;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(button);
        actions.Children.Add(ClearShortcutButton(target));
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return row;
    }

    private Button ClearShortcutButton(RecordingTarget target)
    {
        var button = new Button
        {
            Content = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 1,1 L 9,9 M 9,1 L 1,9"),
                Stroke = Muted(audioState?.Snapshot.Theme == "Dark"),
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 10,
                Height = 10
            },
            ToolTip = T("清除快捷键", "Clear shortcut"),
            IsEnabled = ConfiguredShortcut(target) is not null,
            Width = 28,
            Height = 28,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(0)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, T("清除快捷键", "Clear shortcut"));
        button.Click += (_, _) => ClearShortcut(target);
        return button;
    }

    private void ClearShortcut(RecordingTarget target)
    {
        EndRecording();
        SaveShortcut(target, null);
        string? error = null;
        try { reloadAllHotKeys(); }
        catch (Exception exception) { error = exception.Message; }
        Render();
        statusText.Text = error ?? string.Empty;
    }

    private HotKeyConfiguration? ConfiguredShortcut(RecordingTarget target) => target switch
    {
        RecordingTarget.Screenshot => ConfigurationStore.Shared.Configuration.ScreenshotHotKey,
        RecordingTarget.NotebookLast => ConfigurationStore.Shared.Configuration.NotebookLastHotKey,
        RecordingTarget.NotebookQuick => ConfigurationStore.Shared.Configuration.NotebookQuickHotKey,
        RecordingTarget.Translation => ConfigurationStore.Shared.Configuration.TranslationHotKey,
        RecordingTarget.NextProfile => GlobalHotKeyManager.Parse(ConfigurationStore.Shared.Configuration.Audio.Hotkeys.NextProfile),
        RecordingTarget.PreviousProfile => GlobalHotKeyManager.Parse(ConfigurationStore.Shared.Configuration.Audio.Hotkeys.PreviousProfile),
        _ => null
    };

    private void BeginRecording(RecordingTarget target)
    {
        recordingTarget = target;
        shortcutButtons[target].Content = T("请按组合键…", "Press keys…");
        statusText.Text = "";
        Activate();
        Keyboard.Focus(shortcutButtons[target]);
    }

    private void EndRecording()
    {
        if (recordingTarget == RecordingTarget.None) return;
        foreach (var pair in shortcutButtons)
        {
            pair.Value.Content = selectedTabIndex == 0 ? T("录入", "Record") : ShortcutText(pair.Key);
        }
        recordingTarget = RecordingTarget.None;
    }

    private void HandlePreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (recordingTarget == RecordingTarget.None) return;
        eventArgs.Handled = true;
        if (eventArgs.Key == Key.Escape) { EndRecording(); return; }
        if (eventArgs.Key is Key.Delete or Key.Back) { ClearShortcut(recordingTarget); return; }

        var key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = NativeMethods.HotKeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= NativeMethods.HotKeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= NativeMethods.HotKeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= NativeMethods.HotKeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= NativeMethods.HotKeyModifiers.Win;
        if (modifiers == NativeMethods.HotKeyModifiers.None)
        {
            statusText.Text = T("请至少按下一个修饰键。", "Use at least one modifier key.");
            return;
        }
        var shortcut = new HotKeyConfiguration { Key = (uint)KeyInterop.VirtualKeyFromKey(key), Modifiers = (uint)modifiers };
        foreach (var target in Enum.GetValues<RecordingTarget>())
        {
            if (target == RecordingTarget.None || target == recordingTarget) continue;
            var assigned = ConfiguredShortcut(target);
            if (assigned is not null && assigned.Key == shortcut.Key && assigned.Modifiers == shortcut.Modifiers)
            {
                statusText.Text = T("该快捷键已被其他操作使用，请先清除原快捷键或使用其他组合键。", "This shortcut is used by another action. Clear it first or choose another combination.");
                return;
            }
        }
        var previous = ConfiguredShortcut(recordingTarget);
        try
        {
            SaveShortcut(recordingTarget, shortcut);
            reloadAllHotKeys();
            EndRecording();
            Render();
        }
        catch (Exception error)
        {
            SaveShortcut(recordingTarget, previous);
            var message = error.Message;
            try { reloadAllHotKeys(); }
            catch (Exception restoreError) { message += Environment.NewLine + restoreError.Message; }
            EndRecording();
            Render();
            statusText.Text = message;
        }
    }

    private void SaveShortcut(RecordingTarget target, HotKeyConfiguration? shortcut)
    {
        if (target == RecordingTarget.Screenshot)
        {
            ConfigurationStore.Shared.Update(configuration => configuration.ScreenshotHotKey = shortcut);
            return;
        }
        if (target == RecordingTarget.Translation)
        {
            ConfigurationStore.Shared.Update(configuration => configuration.TranslationHotKey = shortcut);
            return;
        }
        if (target is RecordingTarget.NotebookLast or RecordingTarget.NotebookQuick)
        {
            ConfigurationStore.Shared.Update(configuration =>
            {
                if (target == RecordingTarget.NotebookLast) configuration.NotebookLastHotKey = shortcut;
                else configuration.NotebookQuickHotKey = shortcut;
            });
            return;
        }
        var text = shortcut?.DisplayText.Replace(" + ", "+") ?? string.Empty;
        var current = ConfigurationStore.Shared.Configuration.Audio.Hotkeys;
        audioState?.SetHotkeys(
            target == RecordingTarget.NextProfile ? text : current.NextProfile,
            target == RecordingTarget.PreviousProfile ? text : current.PreviousProfile);
    }

    private string ShortcutText(RecordingTarget target) => target switch
    {
        RecordingTarget.Screenshot => ConfigurationStore.Shared.Configuration.ScreenshotHotKey?.DisplayText ?? T("未设置", "Not set"),
        RecordingTarget.NotebookLast => ConfigurationStore.Shared.Configuration.NotebookLastHotKey?.DisplayText ?? T("未设置", "Not set"),
        RecordingTarget.Translation => ConfigurationStore.Shared.Configuration.TranslationHotKey?.DisplayText ?? T("未设置", "Not set"),
        RecordingTarget.NotebookQuick => ConfigurationStore.Shared.Configuration.NotebookQuickHotKey?.DisplayText ?? T("未设置", "Not set"),
        RecordingTarget.NextProfile => string.IsNullOrWhiteSpace(audioState?.Snapshot.NextProfileHotkey) ? T("未设置", "Not set") : audioState!.Snapshot.NextProfileHotkey,
        RecordingTarget.PreviousProfile => string.IsNullOrWhiteSpace(audioState?.Snapshot.PreviousProfileHotkey) ? T("未设置", "Not set") : audioState!.Snapshot.PreviousProfileHotkey,
        _ => T("未设置", "Not set")
    };

    private void AudioStateChanged(object? sender, AppStateSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            renderTimer.Stop();
            renderTimer.Start();
        }));
    }

    private CheckBox Toggle(string title, bool value) => new() { Content = title, IsChecked = value, FontSize = 13, Foreground = Foreground, Margin = new Thickness(0, 8, 0, 8) };

    private SolidColorBrush Muted(bool dark) => Brush(dark ? "#AEB8C8" : "#626B7C");

    private Style BuildButtonStyle(bool dark)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(dark ? "#20222C" : "#FFFFFF")));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(dark ? "#444754" : "#DADCE3")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 6, 12, 6)));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 34.0));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "buttonBorder");
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetBinding(ContentPresenter.MarginProperty, new Binding(nameof(Control.Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        border.AppendChild(presenter);
        template.VisualTree = border;
        ButtonFeedback.Apply(template, "buttonBorder");
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#4C5872"), "buttonBorder"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private Border Card(string title, string body, bool dark, UIElement? action = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Foreground });
        stack.Children.Add(new TextBlock { Text = body, Margin = new Thickness(0, 10, 0, 0), Foreground = Muted(dark), TextWrapping = TextWrapping.Wrap });
        if (action is not null) stack.Children.Add(action);
        return new Border
        {
            Child = stack,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(dark ? "#444754" : "#DADCE3"),
            Background = Brush(dark ? "#262832" : "#FFFFFF")
        };
    }

    private Border WaccCard(UIElement child, bool dark) => new()
    {
        Child = child,
        Padding = new Thickness(16),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        BorderBrush = Brush(dark ? "#444754" : "#DADCE3"),
        Background = Brush(dark ? "#262832" : "#FFFFFF")
    };

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
