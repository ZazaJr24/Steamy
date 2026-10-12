using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Steamy.Controls;

/// <summary>Steam library logos can include a large transparent positioning canvas.</summary>
internal static class GameLogoLayout
{
    public static BitmapSource? Trim(BitmapSource? source)
    {
        if (source is null) return null;
        var width = source.PixelWidth; var height = source.PixelHeight;
        if ((long)width * height > 16_000_000) return source;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[width * height * 4]; converted.CopyPixels(pixels, width * 4, 0);
        var left = width; var top = height; var right = -1; var bottom = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            if (pixels[(y * width + x) * 4 + 3] > 8)
            { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        if (right < left) return null;
        if (left == 0 && top == 0 && right == width - 1 && bottom == height - 1) return source;
        var cropped = new CroppedBitmap(source, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        cropped.Freeze(); return cropped;
    }
}
