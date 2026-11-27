using WinAssistant.Audio.UI;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinAssistant.Audio.Models;
using WinAssistant.Infrastructure;

namespace WinAssistant.Settings;

internal sealed partial class SettingsWindow
{
    private UIElement BuildOverview(bool dark)
    {
        var snapshot = audioState?.Snapshot;
        var enabled = snapshot?.AudioEnabled == true;
        var output = enabled ? snapshot?.CurrentOutput : null;
        var input = enabled ? snapshot?.CurrentInput : null;
        var profile = snapshot?.Profiles.FirstOrDefault(p =>
            AudioDeviceInfo.IdEquals(output?.Id, p.OutputDeviceId) && AudioDeviceInfo.IdEquals(input?.Id, p.InputDeviceId));
        var root = new StackPanel();
        TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Muted(dark) };
        var summary = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        summary.Children.Add(new TextBlock { Text = T("系统摘要", "System summary"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        summary.Children.Add(new Border
        {
            Background = !enabled || snapshot!.Profiles.Count == 0 ? Brush("#FDF4A5") : Brushes.Transparent, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(12, 0, 0, 0),
            Child = new TextBlock
            {
                Text = !enabled ? T("音频未启用", "Audio inactive")
                    : snapshot!.Profiles.Count == 0 ? T("尚未保存配置组", "No saved profiles")
                    : T($"已保存 {snapshot.Profiles.Count} 个配置组", $"{snapshot.Profiles.Count} saved profiles"),
                Foreground = !enabled || snapshot!.Profiles.Count == 0 ? Brush("#222431") : Muted(dark)
            }
        });
        root.Children.Add(summary);
        var grid = new Grid();
        foreach (var width in new[] { 48, 32, 20 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(grid);

        StackPanel Section(string title, int column, int row, int span = 1)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            var box = WaccCard(panel, dark, outlined: true);
            box.MinHeight = row == 0 ? 258 : 208;
            box.Margin = new Thickness(0, 0, column + span < 3 ? 8 : 0, 8);
            Grid.SetColumn(box, column);
            Grid.SetRow(box, row);
            Grid.SetColumnSpan(box, span);
            grid.Children.Add(box);
            return panel;
        }

        var current = Section(profile is null ? T("当前播放设备", "Current output") : T("当前音频配置", "Current audio profile"), 0, 0);
        var hero = new DockPanel();
        var speakers = new Image { Source = ApplicationImages.OverviewSpeakers, Width = 84, VerticalAlignment = VerticalAlignment.Bottom };
        DockPanel.SetDock(speakers, Dock.Right);
        hero.Children.Add(speakers);
        hero.Children.Add(new TextBlock
        {
            Text = !enabled ? T("音频未启用", "Audio inactive") : profile?.Name ?? output?.Name ?? T("无播放设备", "No output device"),
            FontSize = 40, TextWrapping = TextWrapping.Wrap, MaxHeight = 96, TextTrimming = TextTrimming.CharacterEllipsis
        });
        current.Children.Add(hero);
        foreach (var route in new[] { (T("播放设备", "Output"), output), (T("麦克风", "Input"), input) })
            current.Children.Add(new TextBlock { Text = $"{route.Item1}   {route.Item2?.Name ?? "—"}", Foreground = Muted(dark), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) });
        var capture = Section(T("区域截图", "Region capture"), 1, 0);
        capture.Children.Add(Hint(T("拖选屏幕区域，可标注或录屏。", "Select a region to annotate or record.")));
        capture.Children.Add(new Image { Source = ApplicationImages.CaptureContours, Stretch = Stretch.Uniform, Height = 118 });
        capture.Children.Add(OverviewButton(T("开始框选截图", "Select region to capture"), captureAction, primary: true));
        var profiles = Section(T("设备配置组", "Device profiles"), 2, 0);
        profiles.Children.Add(Hint(T("一键切换播放设备和麦克风。", "Switch output and microphone together.")));
        if (enabled && snapshot is not null)
        {
            foreach (var item in snapshot.Profiles.Take(3))
            {
                var active = profile?.Id == item.Id;
                var button = OverviewButton((active ? "✓  " : "") + item.Name, () => audioState?.ActivateProfile(item.Id));
                var online = snapshot.AllOutputs.Any(d => AudioDeviceInfo.IdEquals(d.Id, item.OutputDeviceId))
                    && snapshot.AllInputs.Any(d => AudioDeviceInfo.IdEquals(d.Id, item.InputDeviceId));
                button.IsEnabled = !active && online;
                if (!online) button.Content = new TextBlock { Text = item.Name + T(" · 离线", " · Offline"), TextTrimming = TextTrimming.CharacterEllipsis };
                profiles.Children.Add(button);
            }
        }
        void OpenAudio() { selectedTabIndex = 0; Render(); }
        profiles.Children.Add(OverviewButton(enabled && snapshot?.Profiles.Count == 0 ? T("设置配置组", "Set up profiles") : T("音频设置", "Audio settings"), OpenAudio));

        var levels = Section(T("音量控制", "Volume levels"), 0, 1);
        if (enabled && snapshot is not null)
        {
            levels.Children.Add(OverviewLevel(T("输出音量", "Output volume"), output, dark));
            levels.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 12) });
            levels.Children.Add(OverviewLevel(T("麦克风音量", "Microphone level"), input, dark));
        }
        else levels.Children.Add(OverviewButton(T("音频设置", "Audio settings"), OpenAudio));
        var actions = Section(T("常用操作", "Quick actions"), 1, 1, 2);
        actions.Children.Add(OverviewButton(T("打开快速笔记", "Open quick notes"), quickNote));
        actions.Children.Add(OverviewButton(T("打开笔记本", "Open notebook"), openNotebook));
        actions.Children.Add(OverviewButton(T("打开录写与翻译", "Open transcription and translation"), openTranslation));
        return root;
    }

    private UIElement OverviewLevel(string label, AudioDeviceInfo? device, bool dark)
    {
        var panel = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var percent = new TextBlock { FontSize = 28, Text = device is null ? "—" : device.Volume.ToString("P0") };
        header.Children.Add(percent);
        header.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(9, 0, 0, 4) });
        panel.Children.Add(header);
        var track = new Grid { Height = 24, Margin = new Thickness(0, 4, 0, 0) };
        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Value = device?.Volume * 100 ?? 0,
            IsEnabled = device is not null, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        track.Children.Add(slider);
        // Keep the native slider for pointer, keyboard and accessibility input.
        // Reveal it while interacting so the thumb and focus ring stay visible.
        var segments = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, IsHitTestVisible = false, Background = Brush(dark ? "#262832" : ThemeManager.IceWhite) };
        var filled = new[] { Brush(dark ? "#818DA7" : "#C2CADD"), Brush(dark ? "#A6B1CA" : "#9DAAC5"), Brush(dark ? "#C2CADD" : "#7685A3") };
        var empty = Brush(dark ? "#424654" : "#E6E8EF");
        for (var index = 0; index < 40; index++)
            segments.Children.Add(new Border { Height = 14, Margin = new Thickness(0, 0, 2, 0) });
        void Paint(double value)
        {
            var count = (int)Math.Round(value * 0.4);
            for (var index = 0; index < segments.Children.Count; index++)
                ((Border)segments.Children[index]).Background = index < count ? filled[index * filled.Length / segments.Children.Count] : empty;
        }
        void Reveal() => segments.Visibility = track.IsMouseOver || slider.IsKeyboardFocusWithin || slider.IsMouseCaptureWithin ? Visibility.Hidden : Visibility.Visible;
        track.MouseEnter += (_, _) => Reveal();
        track.MouseLeave += (_, _) => Reveal();
        slider.IsKeyboardFocusWithinChanged += (_, _) => Reveal();
        slider.LostMouseCapture += (_, _) => Reveal();
        slider.ValueChanged += (_, args) =>
        {
            if (device is null) return;
            percent.Text = $"{args.NewValue:F0}%";
            Paint(args.NewValue);
            audioState?.SetVolume(device, (float)(args.NewValue / 100));
        };
        Paint(slider.Value);
        track.Children.Add(segments);
        panel.Children.Add(track);
        return panel;
    }

    private Button OverviewButton(string title, Action action, bool primary = false)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis }, ToolTip = title,
            Margin = new Thickness(0, 5, 0, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        if (primary)
        {
            bool dark = audioState?.Snapshot.Theme == "Dark";
            button.Background = Brush(dark ? "#333846" : ThemeManager.GrayBlue);
            button.Foreground = dark ? Brushes.White : Brush(ThemeManager.ButtonInk);
        }
        button.Click += (_, _) => action();
        return button;
    }
}
