using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace WinAssistant.Audio.UI;

internal static class ButtonFeedback
{
    public static void Apply(ControlTemplate template, string border)
    {
        var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        if (SystemParameters.ClientAreaAnimation)
        {
            press.EnterActions.Add(Fade(border, 0.72, 70));
            press.ExitActions.Add(Fade(border, 1, 130));
        }
        else
        {
            press.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72, border));
        }
        template.Triggers.Add(press);
    }

    private static BeginStoryboard Fade(string target, double opacity, int milliseconds)
    {
        var fade = new DoubleAnimation { To = opacity, Duration = TimeSpan.FromMilliseconds(milliseconds) };
        Storyboard.SetTargetName(fade, target);
        Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        return new BeginStoryboard { Storyboard = storyboard };
    }
}
