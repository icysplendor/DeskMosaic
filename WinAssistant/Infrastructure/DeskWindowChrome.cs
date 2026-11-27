using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shell;
using WinAssistant.Audio.UI;
using WinAssistant.Settings;

namespace WinAssistant.Infrastructure;

internal static class DeskWindowChrome
{
    internal static StackPanel Controls(Window window, bool dark, bool closeOnly = false)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        WindowChrome.SetIsHitTestVisibleInChrome(group, true);
        string Label(string chinese, string english) =>
            ConfigurationStore.Shared.Configuration.Audio.Language == "en-US" ? english : chinese;
        Button Add(string path, string label, Action action, bool close = false)
        {
            var button = new Button
            {
                Width = 36, Height = 30, MinHeight = 0, Margin = new Thickness(1, 0, 1, 0),
                Padding = new Thickness(0), Tag = Geometry.Parse(path), ToolTip = label,
                Foreground = dark ? Brushes.WhiteSmoke : Brushes.Black
            };
            AutomationProperties.SetName(button, label);
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "Chrome");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            var icon = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path), "Glyph");
            icon.SetValue(FrameworkElement.WidthProperty, 14.0);
            icon.SetValue(FrameworkElement.HeightProperty, 14.0);
            icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            icon.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.4);
            icon.SetBinding(System.Windows.Shapes.Path.DataProperty, new Binding("Tag") { RelativeSource = RelativeSource.TemplatedParent });
            icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(icon);
            template.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, close ? new SolidColorBrush(Color.FromRgb(181, 59, 74))
                : new SolidColorBrush(dark ? Color.FromRgb(61, 64, 76) : Color.FromRgb(234, 235, 238)), "Chrome"));
            if (close) hover.Setters.Add(new Setter(System.Windows.Shapes.Shape.StrokeProperty, Brushes.White, "Glyph"));
            template.Triggers.Add(hover);
            ButtonFeedback.Apply(template, "Chrome");
            button.Template = template;
            button.Click += (_, _) => action();
            group.Children.Add(button);
            return button;
        }
        if (closeOnly)
        {
            Add("M2,2 L12,12 M12,2 L2,12", Label("关闭", "Close"), window.Close, close: true);
            return group;
        }
        Add("M2,7 L12,7", Label("最小化", "Minimize"), () => window.WindowState = WindowState.Minimized);
        var zoom = Add("M2,2 H12 V12 H2 Z", "", () => window.WindowState =
            window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        void UpdateZoom(object? sender, EventArgs e)
        {
            bool maximized = window.WindowState == WindowState.Maximized;
            zoom.Tag = Geometry.Parse(maximized ? "M2,5 H9 V12 H2 Z M5,3 V2 H12 V9 H11" : "M2,2 H12 V12 H2 Z");
            var label = maximized ? Label("还原", "Restore") : Label("最大化", "Maximize");
            zoom.ToolTip = label;
            AutomationProperties.SetName(zoom, label);
        }
        UpdateZoom(null, EventArgs.Empty);
        group.Loaded += (_, _) => { UpdateZoom(null, EventArgs.Empty); window.StateChanged += UpdateZoom; };
        group.Unloaded += (_, _) => window.StateChanged -= UpdateZoom;
        Add("M2,2 L12,12 M12,2 L2,12", Label("关闭", "Close"), window.Close, close: true);
        return group;
    }

    internal static DockPanel Wrap(Window window, FrameworkElement content, bool dark)
    {
        window.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 38, UseAeroCaptionButtons = false, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(8)
        });
        var shell = new DockPanel();
        shell.Children.Add(content);
        SetHeader(shell, window, dark);
        return shell;
    }

    internal static void SetHeader(DockPanel shell, Window window, bool dark)
    {
        var header = new DockPanel { Height = 38, Margin = new Thickness(14, 0, 6, 0) };
        var controls = Controls(window, dark);
        DockPanel.SetDock(controls, Dock.Right);
        header.Children.Add(controls);
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = dark ? Brushes.WhiteSmoke : Brushes.Black, VerticalAlignment = VerticalAlignment.Center };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        header.Children.Add(title);
        var bar = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = dark ? Brushes.DimGray : Brushes.Gainsboro };
        if (shell.Children.Count > 1) shell.Children.RemoveAt(0);
        DockPanel.SetDock(bar, Dock.Top);
        shell.Children.Insert(0, bar);
    }
}
