using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using WinAssistant.Settings;

namespace WinAssistant.Screenshot;

internal sealed class RecordingSavedWindow : Window
{
    internal RecordingSavedWindow(string path)
    {
        var appearance = ConfigurationStore.Shared.Configuration.Audio;
        var dark = appearance.Theme == "Dark";
        var english = appearance.Language == "en-US";
        string Text(string chinese, string translated) => english ? translated : chinese;
        var surface = Brush(dark ? "#20222C" : "#FFFFFF");
        var foreground = Brush(dark ? "#F4F7FA" : "#152033");
        var muted = Brush(dark ? "#AEB8C8" : "#626B7C");
        var line = Brush(dark ? "#444754" : "#DADCE3");
        var accent = Brush("#4C5872");

        Title = Text("区域录屏已保存", "Recording saved");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = surface;
        Foreground = foreground;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 56, ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
            UseAeroCaptionButtons = false
        });
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Close();
        };

        var body = new StackPanel();
        Content = new Border { Child = body, BorderBrush = line, BorderThickness = new Thickness(1) };
        var header = new DockPanel { Height = 55, Margin = new Thickness(18, 0, 12, 0) };
        var close = CreateButton("×", surface, muted, surface);
        close.FontSize = 20;
        close.Width = 34;
        close.Padding = new Thickness(0);
        close.VerticalAlignment = VerticalAlignment.Center;
        close.ToolTip = Text("关闭", "Close");
        AutomationProperties.SetName(close, (string)close.ToolTip);
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var check = new TextBlock
        {
            Text = "✓", Foreground = Brush(dark ? "#67D8A3" : "#137854"), FontSize = 19,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        header.Children.Add(check);
        header.Children.Add(new TextBlock
        {
            Text = Title, FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
        });
        body.Children.Add(header);
        body.Children.Add(new Border { Height = 1, Background = line });

        var file = new StackPanel();
        file.Children.Add(new TextBlock { Text = "MP4", FontFamily = new FontFamily("Consolas"), Foreground = muted, Margin = new Thickness(0, 0, 0, 6) });
        file.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(path), FontSize = 16, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path
        });
        var folder = Path.GetDirectoryName(path);
        file.Children.Add(new TextBlock
        {
            Text = folder, Foreground = muted, FontSize = 12, Margin = new Thickness(0, 5, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder
        });
        body.Children.Add(new Border
        {
            Child = file, Padding = new Thickness(12), Margin = new Thickness(18, 16, 18, 0),
            CornerRadius = new CornerRadius(8), BorderBrush = line, BorderThickness = new Thickness(1),
            Background = Brush(dark ? "#152033" : "#F0F1F6")
        });

        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brush(dark ? "#FFADA7" : "#A52620"),
            Visibility = Visibility.Collapsed, Margin = new Thickness(18, 8, 18, 0)
        };
        body.Children.Add(error);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(18, 16, 18, 16)
        };
        var reveal = CreateButton(Text("在文件夹中显示", "Show in folder"), surface, foreground, line);
        reveal.Click += (_, _) => Launch(new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            $"/select,\"{path}\""));
        var open = CreateButton(Text("打开视频", "Open video"), accent, Brushes.White, accent);
        open.Margin = new Thickness(8, 0, 0, 0);
        open.IsDefault = true;
        open.Click += (_, _) => Launch(new ProcessStartInfo(path) { UseShellExecute = true });
        actions.Children.Add(reveal);
        actions.Children.Add(open);
        body.Children.Add(actions);

        void Launch(ProcessStartInfo info)
        {
            try
            {
                using var launched = Process.Start(info);
                Close();
            }
            catch (Exception exception)
            {
                error.Text = Text("无法打开录屏文件。", "Could not open the recording.") + " " + exception.Message;
                error.Visibility = Visibility.Visible;
            }
        }
    }

    private static Button CreateButton(string title, Brush background, Brush foreground, Brush borderBrush)
    {
        var border = new FrameworkElementFactory(typeof(Border), "ButtonBorder");
        foreach (var property in new[] { Border.BackgroundProperty, Border.BorderBrushProperty, Border.BorderThicknessProperty })
            border.SetBinding(property, new Binding(property.Name) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetBinding(MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        WinAssistant.Audio.UI.ButtonFeedback.Apply(template, "ButtonBorder");
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.84, "ButtonBorder"));
        template.Triggers.Add(hover);
        return new Button
        {
            Content = title, Background = background, Foreground = foreground, BorderBrush = borderBrush,
            BorderThickness = new Thickness(1), Padding = new Thickness(12, 5, 12, 5), MinHeight = 34,
            FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, Template = template
        };
    }

    private static Brush Brush(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
}
