using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Steamy.Services;

/// <summary>A small, cached continuation of the visible banner crop. No window-sized blur or per-frame sampling.</summary>
internal static class ArtworkContinuation
{
    internal const double HeightInHeroes = 4;
    private const int Width = 320;
    private static readonly ConditionalWeakTable<ImageSource, Dictionary<int, BitmapSource>> Cache = new();

    internal static BitmapSource For(ImageSource artwork, double aspect, bool smooth = false)
    {
        var aspectKey = (int)Math.Round(Math.Clamp(aspect, 1, 6) * 32);
        var key = aspectKey + (smooth ? 1024 : 0);
        var variants = Cache.GetOrCreateValue(artwork);
        if (variants.TryGetValue(key, out var bitmap)) return bitmap;
        if (variants.Count >= 8) variants.Remove(variants.Keys.First());
        var heroHeight = (int)Math.Round(Width / (aspectKey / 32d));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new ImageBrush(artwork) { Stretch = Stretch.UniformToFill }, null, new Rect(0, 0, Width, heroHeight));
        var crop = new RenderTargetBitmap(Width, heroHeight, 96, 96, PixelFormats.Pbgra32);
        crop.Render(visual);
        var pixels = new byte[Width * heroHeight * 4];
        crop.CopyPixels(pixels, Width * 4, 0);
        var height = (int)Math.Ceiling(heroHeight * HeightInHeroes);
        var continuation = ContinuePixels(pixels, Width, heroHeight, height, smooth);
        bitmap = BitmapSource.Create(Width, height, 96, 96, PixelFormats.Pbgra32, null, continuation, Width * 4);
        bitmap.Freeze();
        return variants[key] = bitmap;
    }

    internal static byte[] ContinuePixels(byte[] crop, int width, int heroHeight, int height, bool smooth = false)
        => ContinuePixels(crop, width, heroHeight, height, smooth, fade: true);

    private static byte[] ContinuePixels(byte[] crop, int width, int heroHeight, int height, bool smooth, bool fade)
    {
        if (width < 1 || heroHeight < 1 || height < heroHeight || crop.Length != (long)width * heroHeight * 4)
            throw new ArgumentException("Expected a premultiplied BGRA banner crop.", nameof(crop));
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var position = y / (double)heroHeight;
            // Continue the lower artwork without mirroring it or flattening it to one accent color.
            var sourcePosition = position < .5 ? position : .5 + .38 * (1 - Math.Exp(-(position - .5) / .38));
            var sourceY = Math.Clamp((int)(sourcePosition * heroHeight), 0, heroHeight - 1);
            Buffer.BlockCopy(crop, sourceY * width * 4, pixels, y * width * 4, width * 4);
        }
        // Three small separable box passes approximate a Gaussian, once per cached crop.
        for (var pass = 0; pass < 3; pass++)
        {
            pixels = Blur(pixels, width, height, smooth ? 24 : 8, horizontal: true);
            pixels = Blur(pixels, width, height, smooth ? 24 : 8, horizontal: false);
        }
        if (!fade) return pixels;
        for (var y = 0; y < height; y++)
        {
            var position = y / (double)heroHeight;
            var amount = Math.Clamp((HeightInHeroes - position) / (HeightInHeroes - .65), 0, 1);
            // Smoothstep reaches the neutral body with a zero slope, so there is no second seam.
            var opacity = .94 * amount * amount * (3 - 2 * amount);
            for (var x = 0; x < width; x++)
            for (var channel = 0; channel < 4; channel++)
            {
                var offset = (y * width + x) * 4 + channel;
                pixels[offset] = (byte)Math.Round(pixels[offset] * opacity);
            }
        }
        return pixels;
    }

    internal static BitmapSource ForDownload(ImageSource artwork, double aspect)
    {
        var key = (int)Math.Round(Math.Clamp(aspect, 1, 6) * 32);
        var versions = Cache.GetOrCreateValue(artwork);
        if (versions.TryGetValue(key + 2048, out var cached)) return cached;
        if (versions.Count >= 8) versions.Remove(versions.Keys.First());
        var heroHeight = (int)Math.Round(Width / (key / 32d));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new ImageBrush(artwork) { Stretch = Stretch.UniformToFill }, null, new Rect(0, 0, Width, heroHeight));
        var crop = new RenderTargetBitmap(Width, heroHeight, 96, 96, PixelFormats.Pbgra32);
        crop.Render(visual);
        var pixels = new byte[Width * heroHeight * 4]; crop.CopyPixels(pixels, Width * 4, 0);
        var height = heroHeight * (int)HeightInHeroes;
        var soft = BitmapSource.Create(Width, height, 96, 96, PixelFormats.Pbgra32, null, ContinuePixels(pixels, Width, heroHeight, height, true, false), Width * 4);
        const int outputWidth = 1024;
        var outputHeight = (int)Math.Round(height * outputWidth / (double)Width);
        using (var dc = visual.RenderOpen()) dc.DrawImage(soft, new Rect(0, 0, outputWidth, outputHeight));
        var expanded = new RenderTargetBitmap(outputWidth, outputHeight, 96, 96, PixelFormats.Pbgra32);
        expanded.Render(visual);
        pixels = new byte[outputWidth * outputHeight * 4]; expanded.CopyPixels(pixels, outputWidth * 4, 0);
        for (var y = 0; y < outputHeight; y++)
        {
            var t = 1 - y / (double)(outputHeight - 1);
            var opacity = .36 * t * t * (3 - 2 * t);
            for (var x = 0; x < outputWidth; x++)
            {
                // Stochastic rounding breaks visible 8-bit contours without a live noise shader.
                var hash = unchecked((uint)(x * 374761393 + y * 668265263));
                hash = (hash ^ (hash >> 13)) * 1274126177u;
                var noise = (hash & 65535) / 65536d;
                var offset = (y * outputWidth + x) * 4;
                for (var channel = 0; channel < 3; channel++) pixels[offset + channel] = (byte)Math.Clamp(Math.Floor(pixels[offset + channel] * opacity + noise), 0, 255);
                pixels[offset + 3] = 255;
            }
        }
        var result = BitmapSource.Create(outputWidth, outputHeight, 96, 96, PixelFormats.Bgra32, null, pixels, outputWidth * 4);
        result.Freeze();
        return versions[key + 2048] = result;
    }

    private static byte[] Blur(byte[] input, int width, int height, int radius, bool horizontal)
    {
        var output = new byte[input.Length];
        var length = horizontal ? width : height;
        var lines = horizontal ? height : width;
        var samples = radius * 2 + 1;
        for (var line = 0; line < lines; line++)
        for (var channel = 0; channel < 4; channel++)
        {
            int Offset(int position) => ((horizontal ? line * width + position : position * width + line) * 4) + channel;
            var sum = 0;
            for (var i = -radius; i <= radius; i++) sum += input[Offset(Math.Clamp(i, 0, length - 1))];
            for (var position = 0; position < length; position++)
            {
                output[Offset(position)] = (byte)((sum + samples / 2) / samples);
                sum += input[Offset(Math.Min(length - 1, position + radius + 1))]
                     - input[Offset(Math.Max(0, position - radius))];
            }
        }
        return output;
    }
}
