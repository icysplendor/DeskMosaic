using System;
using System.IO;

namespace WinAssistant.Screenshot;

// A monochrome CUR preserves destination pixels through its AND plane, and
// inverts only ring pixels through its XOR plane. An RGBA cursor cannot express
// this operation: it would just draw one color over every background pixel.
internal static class BrushCursorMask
{
    internal static byte[] Create(double diameter)
    {
        if (!double.IsFinite(diameter) || diameter <= 0 || diameter > 250)
            throw new ArgumentOutOfRangeException(nameof(diameter));
        var radius = diameter / 2;
        var center = (int)Math.Ceiling(radius + 1);
        var size = center * 2 + 1;
        var stride = (size + 31) / 32 * 4;
        var xor = new byte[stride * size];
        var and = new byte[xor.Length];
        Array.Fill(and, (byte)255);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - center;
                var dy = y - center;
                // Leave the hotspot transparent even for a one-pixel pen.
                if ((dx != 0 || dy != 0) && Math.Abs(Math.Sqrt(dx * dx + dy * dy) - radius) <= 0.5)
                    xor[(size - 1 - y) * stride + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); // CUR header: reserved, cursor, one image.
        writer.Write((ushort)2);
        writer.Write((ushort)1);
        writer.Write((byte)size);
        writer.Write((byte)size);
        writer.Write((byte)2); // Black/white palette; the mask operation inverts.
        writer.Write((byte)0);
        writer.Write((ushort)center);
        writer.Write((ushort)center);
        writer.Write(40 + 8 + xor.Length + and.Length);
        writer.Write(22); // Offset of BITMAPINFOHEADER.
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2); // XOR and AND planes are stacked in the DIB.
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(0); // BI_RGB.
        writer.Write(xor.Length + and.Length);
        writer.Write(0);
        writer.Write(0);
        writer.Write(2);
        writer.Write(0);
        writer.Write(0x00000000); // RGBQUAD black.
        writer.Write(0x00ffffff); // RGBQUAD white.
        writer.Write(xor);
        writer.Write(and);
        return stream.ToArray();
    }
}
