using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinAssistant.Infrastructure;

// WM_SETICON controls the HWND icon, but Explorer also resolves a taskbar group
// through AppUserModelID and its backing executable. Declare these before the
// first button is created; silent-start WPF hosts must not define our identity.
internal static class TaskbarIdentity
{
    private const string AppId = "IcySplendor.OSAssistant";
    private static readonly Guid PropertySet = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    internal static void InitializeProcess()
    {
        var result = SetCurrentProcessExplicitAppUserModelID(AppId);
        if (result < 0) Debug.WriteLine($"Taskbar process identity: 0x{result:X8}");
    }

    internal static void ApplyToWindow(IntPtr window, bool clear = false)
    {
        var iid = typeof(IWindowPropertyStore).GUID;
        var result = SHGetPropertyStoreForWindow(window, ref iid, out var store);
        if (result < 0) { Debug.WriteLine($"Taskbar window identity: 0x{result:X8}"); return; }
        try
        {
            // ProcessPath is the installed apphost EXE even in a single-file
            // publish; assembly Location can be empty or an extraction path.
            if (clear) SetString(store, 3, null);
            else if (Environment.ProcessPath is { } executable)
                SetString(store, 3 /* RelaunchIconResource */, executable + ",0");
            SetString(store, 5 /* ID */, clear ? null : AppId);
            // Window property stores apply SetValue immediately (no Commit).
        }
        catch (COMException error) { Debug.WriteLine(error); }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static void SetString(IWindowPropertyStore store, uint id, string? text)
    {
        var key = new PropertyKey { FormatId = PropertySet, Id = id };
        // VT_EMPTY releases window properties before HWND destruction.
        var value = text is null ? default : new PropertyValue { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(text) };
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); }
        finally { Marshal.FreeCoTaskMem(value.Pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { internal Guid FormatId; internal uint Id; }

    // PROPVARIANT is 24 bytes on win-x64, including its 16-byte union.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWindowPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropertyValue value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IWindowPropertyStore store);
}
