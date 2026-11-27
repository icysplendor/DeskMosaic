using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WinAssistant.Audio.Models;
using WinAssistant.Audio.Services;

namespace WinAssistant.Audio.UI;

internal sealed class TrayFlyoutWindow : Window
{
    private readonly AppStateService state;
    private readonly Action capture;
    private readonly Action openSettings;
    private readonly Action openNotebook;
    private readonly Action openTranslation;
    private readonly Action quit;
    private readonly System.Windows.Threading.DispatcherTimer renderTimer;
    private bool isRendering;

    internal TrayFlyoutWindow(AppStateService state, Action capture, Action openNotebook, Action openTranslation, Action openSettings, Action quit)
    {
        this.state = state;
        this.capture = capture;
        this.openNotebook = openNotebook;
        this.openTranslation = openTranslation;
        this.openSettings = openSettings;
        this.quit = quit;
        Width = 330;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 600;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, eventArgs) => { if (eventArgs.Key == Key.Escape) Hide(); };
        state.StateChanged += HandleStateChanged;
        renderTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        renderTimer.Tick += (_, _) => { renderTimer.Stop(); if (IsVisible) Render(); };
        Closed += (_, _) => { state.StateChanged -= HandleStateChanged; renderTimer.Stop(); };
        Render();
    }

    internal void ToggleAt(double cursorX, double cursorY, double workLeft, double workTop, double workRight, double workBottom)
    {
        if (IsVisible) { Hide(); return; }
        Render();
        Show();
        UpdateLayout();
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var cursor = transform.Transform(new Point(cursorX, cursorY));
        var topLeft = transform.Transform(new Point(workLeft, workTop));
        var bottomRight = transform.Transform(new Point(workRight, workBottom));
        var height = Math.Max(ActualHeight, 1);
        Left = Math.Clamp(cursor.X - ActualWidth + 18, topLeft.X + 8, Math.Max(topLeft.X + 8, bottomRight.X - ActualWidth - 8));
        Top = Math.Clamp(cursor.Y - height - 12, topLeft.Y + 8, Math.Max(topLeft.Y + 8, bottomRight.Y - height - 8));
        Activate();
    }

    private bool English => state.Snapshot.Language == "en-US";
    private string T(string chinese, string english) => English ? english : chinese;
    private SolidColorBrush TextBrush(bool dark) => Brush(dark ? "#F7F9FC" : "#161B28");
    private SolidColorBrush MutedBrush(bool dark) => Brush(dark ? "#AEB8C8" : "#626B7C");
    private SolidColorBrush PanelBrush(bool dark) => Brush(dark ? "#262832" : ThemeManager.IceWhite);
    private SolidColorBrush SurfaceBrush(bool dark) => Brush(dark ? "#1B1D25" : ThemeManager.IceWhite);
    private SolidColorBrush LineBrush(bool dark) => Brush(dark ? "#444754" : "#DADCE3");

    private void Render()
    {
        isRendering = true;
        var snapshot = state.Snapshot;
        var dark = snapshot.Theme == "Dark";
        Foreground = TextBrush(dark);
        Resources[typeof(Button)] = BuildButtonStyle(dark);

        var root = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(4, 0, 4, 8) };
        var settings = new Button { Content = T("控制中心", "Control center"), Padding = new Thickness(8, 3, 8, 3) };
        settings.Click += (_, _) => { Hide(); openSettings(); };
        DockPanel.SetDock(settings, Dock.Right);
        header.Children.Add(settings);
        header.Children.Add(new TextBlock { Text = "DeskMosaic", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(header);
        root.Children.Add(BuildQuickActions(dark));

        if (snapshot.AudioEnabled)
        {
            root.Children.Add(BuildCurrentCard(snapshot, dark));
            var deviceList = BuildProfiles(snapshot, dark);
            root.Children.Add(deviceList);
        }
        else
        {
            var disabled = InsetCard(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = T("音频功能已关闭", "Audio is off"), FontSize = 14, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = T("不会枚举或监听音频设备。可在设置中重新开启。", "Audio devices are not discovered or monitored. Re-enable audio in Settings."), Foreground = MutedBrush(dark), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) }
                }
            }, dark, new Thickness(0, 0, 0, 10));
            root.Children.Add(disabled);
        }
        var exit = new Button { Content = T("退出 DeskMosaic", "Quit DeskMosaic"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), MinHeight = 28, Padding = new Thickness(10, 4, 10, 4) };
        exit.Click += (_, _) => { Hide(); quit(); };
        root.Children.Add(exit);

        Content = new Border
        {
            Child = root,
            Padding = new Thickness(10),
            Background = dark ? SurfaceBrush(true) : Brush(ThemeManager.IceWhite),
            BorderBrush = LineBrush(dark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 8, Opacity = 0.18, Color = (Color)ColorConverter.ConvertFromString("#1E355B") }
        };
        isRendering = false;
    }

    private UIElement BuildCurrentCard(AppStateSnapshot snapshot, bool dark)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(CurrentDeviceColumn(AudioDeviceKind.Output, snapshot.CurrentOutput, dark));
        var divider = new Border { Background = LineBrush(dark), Margin = new Thickness(8, 0, 8, 0) };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);
        var input = CurrentDeviceColumn(AudioDeviceKind.Input, snapshot.CurrentInput, dark);
        Grid.SetColumn(input, 2);
        grid.Children.Add(input);
        return InsetCard(grid, dark, new Thickness(0, 0, 0, 10));
    }

    private UIElement CurrentDeviceColumn(AudioDeviceKind kind, AudioDeviceInfo? device, bool dark)
    {
        var isOutput = kind == AudioDeviceKind.Output;
        var grid = new Grid { Width = 132 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = isOutput ? T("输出", "Output") : T("输入", "Input"), FontSize = 11, Foreground = MutedBrush(dark) });
        var name = new TextBlock
        {
            Text = device?.Name ?? T("无活动设备", "No active device"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 36,
            Margin = new Thickness(0, 2, 0, 4)
        };
        Grid.SetRow(name, 1);
        grid.Children.Add(name);
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = device is null ? 0 : Math.Round(device.Volume * 100), Width = 126, IsEnabled = device is not null };
        slider.ValueChanged += (_, eventArgs) => { if (!isRendering && device is not null) state.SetVolume(device, (float)(eventArgs.NewValue / 100)); };
        Grid.SetRow(slider, 2);
        grid.Children.Add(slider);
        return grid;
    }

    private UIElement BuildProfiles(AppStateSnapshot snapshot, bool dark)
    {
        var panel = new StackPanel();
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(controls, Dock.Right);
        var previous = new Button { Content = "‹", Width = 30, MinHeight = 26, Padding = new Thickness(0), Margin = new Thickness(0, 0, 5, 0) };
        previous.Click += (_, _) => state.CycleProfile(-1);
        controls.Children.Add(previous);
        var next = new Button { Content = "›", Width = 30, MinHeight = 26, Padding = new Thickness(0) };
        next.Click += (_, _) => state.CycleProfile(1);
        controls.Children.Add(next);
        heading.Children.Add(controls);
        heading.Children.Add(new TextBlock { Text = T("音频配置组", "Audio profiles"), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(heading);
        if (snapshot.Profiles.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = T("请在设置中保存配置组", "Save a profile in Settings"), FontSize = 12, Foreground = MutedBrush(dark), Margin = new Thickness(2, 5, 0, 9) });
        }
        else
        {
            panel.Children.Add(new Border { Height = 1, Background = LineBrush(dark) });
            foreach (var profile in snapshot.Profiles)
            {
                panel.Children.Add(CreateProfileRow(profile, snapshot, dark));
                panel.Children.Add(new Border { Height = 1, Background = LineBrush(dark) });
            }
        }
        return new ScrollViewer { Content = panel, MaxHeight = 278, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private UIElement CreateProfileRow(AudioConfigurationProfile profile, AppStateSnapshot snapshot, bool dark)
    {
        var output = snapshot.AllOutputs.FirstOrDefault(device => AudioDeviceInfo.IdEquals(device.Id, profile.OutputDeviceId));
        var input = snapshot.AllInputs.FirstOrDefault(device => AudioDeviceInfo.IdEquals(device.Id, profile.InputDeviceId));
        var active = AudioDeviceInfo.IdEquals(snapshot.CurrentOutput?.Id, profile.OutputDeviceId)
            && AudioDeviceInfo.IdEquals(snapshot.CurrentInput?.Id, profile.InputDeviceId);
        var button = new Button
        {
            Background = active ? Brush(dark ? "#414758" : ThemeManager.GrayBlue) : SurfaceBrush(dark),
            BorderBrush = active ? Brush("#4C5872") : LineBrush(dark),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(2, 3, 2, 3),
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 54,
            Cursor = Cursors.Hand,
            IsEnabled = output is not null && input is not null
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.Children.Add(new TextBlock { Text = active ? "✓" : "○", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = MutedBrush(dark), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var labels = new StackPanel { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = profile.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        labels.Children.Add(new TextBlock
        {
            Text = $"{output?.Name ?? T("输出未连接", "Output offline")} · {input?.Name ?? T("输入未连接", "Input offline")}",
            FontSize = 11,
            Foreground = MutedBrush(dark),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);
        var radio = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2), BorderBrush = active ? Brush("#4C5872") : MutedBrush(dark), Background = active ? Brush("#4C5872") : Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(radio, 2);
        grid.Children.Add(radio);
        button.Content = grid;
        System.Windows.Automation.AutomationProperties.SetName(button, profile.Name);
        button.Click += (_, _) => { state.ActivateProfile(profile.Id); Hide(); };
        return button;
    }

    private void AddDeviceRows(Panel panel, IReadOnlyList<AudioDeviceInfo> devices, AudioDeviceInfo? current, bool dark)
    {
        if (devices.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = T("没有收藏设备", "No favorite devices"), FontSize = 12, Foreground = MutedBrush(dark), Margin = new Thickness(2, 5, 0, 9) });
            return;
        }
        foreach (var device in devices) panel.Children.Add(CreateDeviceRow(device, AudioDeviceInfo.IdEquals(device.Id, current?.Id), dark));
    }

    private UIElement CreateDeviceRow(AudioDeviceInfo device, bool current, bool dark)
    {
        var border = new Border
        {
            Background = current ? Brush(dark ? "#414758" : ThemeManager.GrayBlue) : SurfaceBrush(dark),
            BorderBrush = current ? Brush("#4C5872") : LineBrush(dark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 50,
            Cursor = Cursors.Hand
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.Children.Add(new TextBlock { Text = device.Kind == AudioDeviceKind.Output ? "OUT" : "IN", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brush("#4C5872"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var labels = new StackPanel { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = device.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        labels.Children.Add(new TextBlock { Text = current ? T("默认", "Default") : device.Volume.ToString("P0"), FontSize = 11, Foreground = MutedBrush(dark), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);
        var radio = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2), BorderBrush = current ? Brush("#4C5872") : MutedBrush(dark), Background = current ? Brush("#4C5872") : Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(radio, 2);
        grid.Children.Add(radio);
        border.Child = grid;
        return border;
    }

    private UIElement BuildQuickActions(bool dark)
    {
        var panel = new StackPanel();
        panel.Children.Add(Row((T("框选截图", "Capture area"), capture), (T("笔记本", "Notes"), openNotebook),
            (T("录写/翻译", "Speech"), openTranslation)));
        return InsetCard(panel, dark, new Thickness(0, 0, 0, 8), new Thickness(4));

        Grid Row(params (string Label, Action Action)[] items)
        {
            var row = new Grid();
            for (var index = 0; index < items.Length; index++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var (label, action) = items[index];
                var button = new Button
                {
                    Content = label, MinHeight = 28, Padding = new Thickness(4),
                    Margin = new Thickness(index == 0 ? 0 : 8, 0, 0, 0)
                };
                button.Click += (_, _) => { Hide(); action(); };
                Grid.SetColumn(button, index);
                row.Children.Add(button);
            }
            return row;
        }
    }

    private Border InsetCard(UIElement child, bool dark, Thickness margin, Thickness? padding = null) => new()
    {
        Child = child,
        Background = PanelBrush(dark),
        BorderBrush = LineBrush(dark),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(10),
        Margin = margin
    };

    private Style BuildButtonStyle(bool dark)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        style.Setters.Add(new Setter(Control.BackgroundProperty, SurfaceBrush(dark)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, TextBrush(dark)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, LineBrush(dark)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Root");
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(HorizontalAlignmentProperty, new Binding("HorizontalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        ButtonFeedback.Apply(template, "Root");
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private void HandleStateChanged(object? sender, AppStateSnapshot snapshot) => Dispatcher.BeginInvoke(new Action(() => { renderTimer.Stop(); renderTimer.Start(); }));

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
