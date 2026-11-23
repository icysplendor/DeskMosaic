using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WinAssistant.Audio.Services;

internal static class TrayIconFactory
{
    private const int IconSize = 16;

    public static Icon CreateVolumeIcon(float? volume, bool outputMuted, bool inputMuted)
    {
        using var bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        graphics.Clear(Color.Transparent);

        var value = outputMuted
            ? "X"
            : volume is null
                ? "--"
                : Math.Clamp((int)Math.Round(volume.Value * 100), 0, 100).ToString();
        DrawBackground(graphics);
        if (inputMuted)
        {
            DrawInputMutedFrame(graphics);
        }

        DrawPixelText(graphics, value);

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawInputMutedFrame(Graphics graphics)
    {
        using var frame = new SolidBrush(Color.FromArgb(255, 224, 36, 36));
        graphics.FillRectangle(frame, 0, 0, 16, 1);
        graphics.FillRectangle(frame, 0, 15, 16, 1);
        graphics.FillRectangle(frame, 0, 0, 1, 16);
        graphics.FillRectangle(frame, 15, 0, 1, 16);
    }

    private static void DrawBackground(Graphics graphics)
    {
        using var background = new SolidBrush(Color.FromArgb(255, 5, 96, 89));
        using var highlight = new SolidBrush(Color.FromArgb(255, 24, 141, 132));
        using var shadow = new SolidBrush(Color.FromArgb(255, 9, 31, 43));

        graphics.FillRectangle(background, 1, 1, 14, 14);
        graphics.FillRectangle(highlight, 2, 1, 12, 2);
        graphics.FillRectangle(shadow, 1, 13, 14, 2);
        graphics.FillRectangle(background, 0, 3, 16, 10);
    }

    private static void DrawPixelText(Graphics graphics, string value)
    {
        var scale = value switch
        {
            "X" => 2.25f,
            _ when value.Length <= 1 => 3.0f,
            _ when value.Length == 2 => 2.1f,
            _ => 1.35f
        };
        var gap = value.Length >= 3 ? 0.8f : 1.0f;
        var width = GetTextWidth(value, scale, gap);
        var height = 5 * scale;
        var x = (IconSize - width) / 2.0f;
        var y = (IconSize - height) / 2.0f + (value.Length >= 3 ? 1.0f : 0.0f);

        using var shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        using var foreground = new SolidBrush(Color.White);

        DrawPixelText(graphics, value, shadow, x + 1, y + 1, scale, gap);
        DrawPixelText(graphics, value, foreground, x, y, scale, gap);
    }

    private static float GetTextWidth(string value, float scale, float gap)
    {
        var width = 0.0f;
        foreach (var ch in value)
        {
            width += GetGlyph(ch)[0].Length * scale;
        }

        return width + Math.Max(0, value.Length - 1) * gap;
    }

    private static void DrawPixelText(Graphics graphics, string value, Brush brush, float x, float y, float scale, float gap)
    {
        foreach (var ch in value)
        {
            DrawGlyph(graphics, brush, ch, x, y, scale);
            x += GetGlyph(ch)[0].Length * scale + gap;
        }
    }

    private static void DrawGlyph(Graphics graphics, Brush brush, char ch, float x, float y, float scale)
    {
        var pattern = GetGlyph(ch);
        for (var row = 0; row < pattern.Length; row++)
        {
            for (var column = 0; column < pattern[row].Length; column++)
            {
                if (pattern[row][column] == '1')
                {
                    graphics.FillRectangle(brush, x + column * scale, y + row * scale, scale, scale);
                }
            }
        }
    }

    private static string[] GetGlyph(char ch)
    {
        return ch switch
        {
            '0' => ["111", "101", "101", "101", "111"],
            '1' => ["010", "110", "010", "010", "111"],
            '2' => ["111", "001", "111", "100", "111"],
            '3' => ["111", "001", "111", "001", "111"],
            '4' => ["101", "101", "111", "001", "001"],
            '5' => ["111", "100", "111", "001", "111"],
            '6' => ["111", "100", "111", "101", "111"],
            '7' => ["111", "001", "010", "010", "010"],
            '8' => ["111", "101", "111", "101", "111"],
            '9' => ["111", "101", "111", "001", "111"],
            'X' => ["10001", "01010", "00100", "01010", "10001"],
            '-' => ["000", "000", "111", "000", "000"],
            _ => ["111", "001", "011", "000", "010"]
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
