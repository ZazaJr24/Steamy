using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Buffers;
using Steamy.Services;

namespace Steamy.Controls;

/// <summary>Steam library logos can include a large transparent positioning canvas.</summary>
internal static class GameLogoLayout
{
    public static Task<BitmapSource?> TrimAsync(BitmapSource? source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source is null) return Task.FromResult<BitmapSource?>(null);
        var frozen = source.IsFrozen ? source : (BitmapSource)source.GetCurrentValueAsFrozen();
        return ArtworkRenderWorker.RunAsync(() => Trim(frozen, token), token);
    }

    public static BitmapSource? Trim(BitmapSource? source) => Trim(source, CancellationToken.None);

    private static BitmapSource? Trim(BitmapSource? source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source is null) return null;
        var width = source.PixelWidth; var height = source.PixelHeight;
        if ((long)width * height > 16_000_000) return source;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var left = width; var top = height; var right = -1; var bottom = -1;
        // Scan a small strip instead of allocating a full-size 32-bit copy of
        // every logo. Check cancellation between strips when a page is left.
        var stride = width * 4; var rows = Math.Min(height, 32);
        var pixels = ArrayPool<byte>.Shared.Rent(stride * rows);
        try
        {
            for (var start = 0; start < height; start += rows)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(rows, height - start);
                converted.CopyPixels(new Int32Rect(0, start, width, count), pixels, stride, 0);
                for (var y = 0; y < count; y++)
                for (var x = 0; x < width; x++)
                    if (pixels[y * stride + x * 4 + 3] > 8)
                    { left = Math.Min(left, x); top = Math.Min(top, start + y); right = Math.Max(right, x); bottom = Math.Max(bottom, start + y); }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(pixels); }
        if (right < left) return null;
        if (left == 0 && top == 0 && right == width - 1 && bottom == height - 1) return source;
        var cropped = new CroppedBitmap(source, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        cropped.Freeze(); return cropped;
    }
}
