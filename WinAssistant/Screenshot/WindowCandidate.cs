using System.Runtime.InteropServices;
using System.Windows;
using WinAssistant.Infrastructure;

namespace WinAssistant.Screenshot;

internal sealed record WindowCandidate(Rect Bounds)
{
    internal static IReadOnlyList<WindowCandidate> Current()
    {
        var processIdentifier = (uint)Environment.ProcessId;
        var windows = new List<WindowCandidate>();
        NativeMethods.EnumWindows((window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window)) return true;
            NativeMethods.GetWindowThreadProcessId(window, out var owner);
            if (owner == processIdentifier) return true;

            if (NativeMethods.DwmGetWindowAttribute(
                    window,
                    NativeMethods.DwmwaCloaked,
                    out int cloaked,
                    sizeof(int)) == 0
                && cloaked != 0)
            {
                return true;
            }

            NativeMethods.Rect nativeBounds;
            var result = NativeMethods.DwmGetWindowAttribute(
                window,
                NativeMethods.DwmwaExtendedFrameBounds,
                out nativeBounds,
                Marshal.SizeOf<NativeMethods.Rect>());
            if (result != 0 && !NativeMethods.GetWindowRect(window, out nativeBounds))
            {
                return true;
            }
            if (nativeBounds.Width < 40 || nativeBounds.Height < 30) return true;

            windows.Add(new WindowCandidate(new Rect(
                nativeBounds.Left,
                nativeBounds.Top,
                nativeBounds.Width,
                nativeBounds.Height)));
            return true;
        }, nint.Zero);
        return windows;
    }
}
