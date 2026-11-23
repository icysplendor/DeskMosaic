using System.ComponentModel;
using System.Runtime.InteropServices;
using WinAssistant.Screenshot;

// Decode production CUR bytes as a consumer: check mask semantics, physical
// dimensions and centered hotspot. Native loading is additionally checked on
// Windows, while the bitmap/format checks can run on Linux and macOS too.
var diameters = new double[] { 1, 2, 3, 5, 8, 12, 20, 32, 48, 72, 96 }
    .Concat(new double[] { 1.25, 1.5, 2, 3, 4 }.SelectMany(scale =>
        new double[] { 1, 2, 3, 5, 8, 12 }.Select(width => width * scale)))
    .Distinct().Order().ToArray();
foreach (var diameter in diameters)
{
    var bytes = BrushCursorMask.Create(diameter);
    using var reader = new BinaryReader(new MemoryStream(bytes));
    Check(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 2 && reader.ReadUInt16() == 1, "CUR header");
    var width = reader.ReadByte();
    Check(reader.ReadByte() == width && reader.ReadByte() == 2 && reader.ReadByte() == 0, "CUR dimensions/palette");
    var hotspotX = reader.ReadUInt16();
    var hotspotY = reader.ReadUInt16();
    Check(hotspotX == width / 2 && hotspotY == width / 2, "centered hotspot");
    var length = reader.ReadInt32();
    var offset = reader.ReadInt32();
    Check(offset + length == bytes.Length, "complete image payload");
    Check(reader.ReadInt32() == 40 && reader.ReadInt32() == width && reader.ReadInt32() == width * 2, "DIB dimensions");
    Check(reader.ReadUInt16() == 1 && reader.ReadUInt16() == 1 && reader.ReadInt32() == 0, "monochrome DIB");
    var stride = (width + 31) / 32 * 4;
    var planeLength = stride * width;
    var xorStart = offset + 40 + 8;
    var andStart = xorStart + planeLength;
    bool Bit(int start, int x, int y) => (bytes[start + (width - 1 - y) * stride + x / 8] & (0x80 >> (x % 8))) != 0;
    Check(!Bit(xorStart, hotspotX, hotspotY), "hollow center");
    var ring = new List<(int X, int Y)>();
    for (var y = 0; y < width; y++)
    {
        for (var x = 0; x < width; x++)
        {
            Check(Bit(andStart, x, y), "AND mask preserves destination instead of painting a fixed color");
            var invert = Bit(xorStart, x, y);
            if (invert) ring.Add((x, y));
            Check(invert == Bit(xorStart, width - 1 - x, y) && invert == Bit(xorStart, x, width - 1 - y), "symmetric ring");
            if (x == 0 || y == 0 || x == width - 1 || y == width - 1) Check(!invert, "transparent padding");
            // Different colors under adjacent ring pixels must be inverted
            // independently. No center-color sampling or opaque white outline.
            foreach (var background in new byte[] { 0, 17, 63, 128, 201, 255 })
            {
                var output = background ^ (invert ? 255 : 0);
                Check(output == (invert ? 255 - background : background), "per-pixel inversion");
            }
        }
    }
    Check(ring.Count > 0, "nonempty ring");
    Check(Math.Abs(ring.Max(p => p.X) - ring.Min(p => p.X) - diameter) <= 1.01, "actual brush diameter");
    if (OperatingSystem.IsWindows()) NativeCursor.Check(bytes, width, hotspotX, hotspotY);
}
Console.WriteLine($"PASS {diameters.Length} brush sizes: CUR format, transparent center/padding, centered hotspot, ring symmetry, physical diameter and per-pixel inversion");
Console.WriteLine(OperatingSystem.IsWindows() ? "PASS Windows native cursor loading and dimensions" : "SKIP Windows native loading (requires Windows)");

static void Check(bool success, string message)
{
    if (!success) throw new InvalidOperationException(message);
}

internal static class NativeCursor
{
    internal static void Check(byte[] bytes, int size, int hotspotX, int hotspotY)
    {
        var path = Path.Combine(Path.GetTempPath(), $"assistant-brush-{Guid.NewGuid():N}.cur");
        nint cursor = 0;
        IconInfo info = default;
        try
        {
            File.WriteAllBytes(path, bytes);
            cursor = LoadImage(0, path, 2, 0, 0, 0x10);
            if (cursor == 0 || !GetIconInfo(cursor, out info)) throw new Win32Exception();
            if (info.IsIcon || info.HotspotX != hotspotX || info.HotspotY != hotspotY || info.Color != 0)
                throw new InvalidOperationException("Native cursor changed mask format or hotspot");
            if (GetObject(info.Mask, Marshal.SizeOf<Bitmap>(), out var bitmap) == 0) throw new Win32Exception();
            if (bitmap.Width != size || bitmap.Height != size * 2)
                throw new InvalidOperationException("Native cursor was resized instead of matching the brush");
        }
        finally
        {
            if (cursor != 0) DestroyCursor(cursor);
            if (info.Mask != 0) DeleteObject(info.Mask);
            if (info.Color != 0) DeleteObject(info.Color);
            File.Delete(path);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] internal bool IsIcon;
        internal uint HotspotX, HotspotY;
        internal nint Mask, Color;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        internal int Type, Width, Height, WidthBytes;
        internal ushort Planes, BitsPixel;
        internal nint Bits;
    }
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)]
    private static extern int GetObject(nint obj, int size, out Bitmap bitmap);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(nint cursor);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);
}
