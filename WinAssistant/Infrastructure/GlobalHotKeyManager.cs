using System.ComponentModel;
using System.Windows.Interop;
using WinAssistant.Settings;

namespace WinAssistant.Infrastructure;

internal sealed class GlobalHotKeyManager : IDisposable
{
    private const int ScreenshotHotKeyId = 1;
    private const int NextProfileHotKeyId = 2;
    private const int PreviousProfileHotKeyId = 3;
    private const int NotebookLastHotKeyId = 4;
    private const int NotebookQuickHotKeyId = 5;
    private const int TranslationHotKeyId = 6;
    private readonly HwndSource source;
    private readonly Dictionary<int, Action> actions = new();

    internal GlobalHotKeyManager()
    {
        var parameters = new HwndSourceParameters("WinAssistantHotKeyWindow")
        {
            ParentWindow = new nint(-3),
            WindowStyle = 0
        };
        source = new HwndSource(parameters);
        source.AddHook(WindowProcedure);
    }

    internal void Reload(HotKeyConfiguration? shortcut, Action action)
    {
        Reload(
            shortcut,
            action,
            ConfigurationStore.Shared.Configuration.Audio.Hotkeys.NextProfile,
            () => { },
            ConfigurationStore.Shared.Configuration.Audio.Hotkeys.PreviousProfile,
            () => { });
    }

    internal void Reload(
        HotKeyConfiguration? screenshotShortcut,
        Action screenshotAction,
        string nextProfileShortcut,
        Action nextProfileAction,
        string previousProfileShortcut,
        Action previousProfileAction,
        HotKeyConfiguration? notebookLast = null, Action? openLast = null,
        HotKeyConfiguration? notebookQuick = null, Action? openQuick = null,
        HotKeyConfiguration? translation = null, Action? openTranslation = null)
    {
        foreach (var id in new[] { ScreenshotHotKeyId, NextProfileHotKeyId, PreviousProfileHotKeyId, NotebookLastHotKeyId, NotebookQuickHotKeyId, TranslationHotKeyId })
        {
            NativeMethods.UnregisterHotKey(source.Handle, id);
        }
        actions.Clear();

        Register(ScreenshotHotKeyId, screenshotShortcut, screenshotAction, "截图");
        Register(NextProfileHotKeyId, Parse(nextProfileShortcut), nextProfileAction, "下一个音频配置组");
        Register(PreviousProfileHotKeyId, Parse(previousProfileShortcut), previousProfileAction, "上一个音频配置组");
        Register(NotebookLastHotKeyId, notebookLast, openLast ?? (() => { }), "打开上次笔记页面");
        Register(NotebookQuickHotKeyId, notebookQuick, openQuick ?? (() => { }), "打开快速笔记");
        Register(TranslationHotKeyId, translation, openTranslation ?? (() => { }), "录写/翻译");
    }

    private void Register(int id, HotKeyConfiguration? shortcut, Action action, string label)
    {
        if (shortcut is null) return;

        var modifiers = (NativeMethods.HotKeyModifiers)shortcut.Modifiers
            | NativeMethods.HotKeyModifiers.NoRepeat;
        if (!NativeMethods.RegisterHotKey(
                source.Handle,
                id,
                modifiers,
                shortcut.Key))
        {
            throw new Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                $"无法注册{label}快捷键，它可能已被其他应用占用。");
        }
        actions[id] = action;
    }

    public void Dispose()
    {
        foreach (var id in new[] { ScreenshotHotKeyId, NextProfileHotKeyId, PreviousProfileHotKeyId, NotebookLastHotKeyId, NotebookQuickHotKeyId, TranslationHotKeyId })
        {
            NativeMethods.UnregisterHotKey(source.Handle, id);
        }
        source.RemoveHook(WindowProcedure);
        source.Dispose();
    }

    private nint WindowProcedure(
        nint window,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        if (message == NativeMethods.WmHotKey
            && actions.TryGetValue(wordParameter.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return nint.Zero;
    }

    internal static HotKeyConfiguration? Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var modifiers = NativeMethods.HotKeyModifiers.None;
        uint key = 0;
        foreach (var part in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                modifiers |= NativeMethods.HotKeyModifiers.Control;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= NativeMethods.HotKeyModifiers.Alt;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= NativeMethods.HotKeyModifiers.Shift;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase))
                modifiers |= NativeMethods.HotKeyModifiers.Win;
            else if (part.Length == 1)
                key = char.ToUpperInvariant(part[0]);
            else if (part.StartsWith('F') && uint.TryParse(part[1..], out var function) && function is >= 1 and <= 12)
                key = 0x6F + function;
            else
                key = part switch
                {
                    "Space" => 0x20,
                    "Print Screen" => 0x2C,
                    "←" => 0x25,
                    "↑" => 0x26,
                    "→" => 0x27,
                    "↓" => 0x28,
                    _ => key
                };
        }
        return key == 0 || modifiers == NativeMethods.HotKeyModifiers.None
            ? null
            : new HotKeyConfiguration { Key = key, Modifiers = (uint)modifiers };
    }
}
