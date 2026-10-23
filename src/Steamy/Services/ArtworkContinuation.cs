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
    private static readonly ConditionalWeakTable<ImageSource, Dictionary<int, WeakReference<BitmapSource>>> Cache = new();
    private static readonly object Gate = new();

    internal static int AspectKey(double aspect) => (int)Math.Round(Math.Clamp(aspect, 1, 6) * 32);
    private static BitmapSource? Cached(ImageSource artwork, int key)
    {
        lock (Gate)
        {
            if (!Cache.TryGetValue(artwork, out var variants) || !variants.TryGetValue(key, out var reference) || !reference.TryGetTarget(out var image)) return null;
            ImagePipeline.Shared.RememberDerived(artwork, key, image);
            return image;
        }
    }
    private static void Store(ImageSource artwork, int key, BitmapSource image)
    {
        lock (Gate)
        {
            var variants = Cache.GetOrCreateValue(artwork);
            if (variants.Count >= 2 && !variants.ContainsKey(key)) variants.Remove(variants.Keys.First());
            variants[key] = new(image);
        }
        ImagePipeline.Shared.RememberDerived(artwork, key, image);
    }
    internal static Task<BitmapSource> ForAsync(ImageSource artwork, double aspect, CancellationToken token, bool smooth = false)
    {
        token.ThrowIfCancellationRequested();
        if (Cached(artwork, AspectKey(aspect) + (smooth ? 1024 : 0)) is { } cached) return Task.FromResult(cached);
        var frozen = artwork.IsFrozen ? artwork : (ImageSource)artwork.GetCurrentValueAsFrozen();
        return ArtworkRenderWorker.RunAsync(() => For(frozen, aspect, smooth), token);
    }

    internal static BitmapSource For(ImageSource artwork, double aspect, bool smooth = false)
    {
        var aspectKey = AspectKey(aspect);
        var key = aspectKey + (smooth ? 1024 : 0);
        if (Cached(artwork, key) is { } saved) return saved;
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
        ExposeAmbient(continuation, Width, heroHeight, smooth);
        var bitmap = BitmapSource.Create(Width, height, 96, 96, PixelFormats.Pbgra32, null, continuation, Width * 4);
        bitmap.Freeze();
        Store(artwork, key, bitmap);
        return bitmap;
    }

    // Keep the source's regional hue, but treat it as reflected light rather
    // than projecting a bright, enlarged photograph behind the card captions.
    // The sharp hero is drawn separately. This runs once per cached texture.
    private static readonly double[] LinearChannels = Enumerable.Range(0, 256).Select(value =>
    {
        var channel = value / 255d;
        return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
    }).ToArray();
    private static byte Encoded(double value) => (byte)Math.Clamp(Math.Round(255 *
        (value <= .0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - .055)), 0, 255);
    private static void ExposeAmbient(byte[] pixels, int width, int heroHeight, bool preview)
    {
        var height = pixels.Length / (width * 4);
        for (var y = 0; y < height; y++)
        {
            var amount = preview ? 1 : Math.Clamp((y / (double)heroHeight - .35) / .65, 0, 1);
            amount = amount * amount * (3 - 2 * amount);
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var alpha = pixels[i + 3];
                if (alpha == 0) continue;
                double Channel(int index) => LinearChannels[Math.Clamp((pixels[i + index] * 255 + alpha / 2) / alpha, 0, 255)];
                var r = Channel(2); var g = Channel(1); var b = Channel(0);
                var luminance = .2126 * r + .7152 * g + .0722 * b;
                var ceiling = preview ? .065 : .045;
                var exposure = 1 - amount + amount * ceiling / (luminance + ceiling);
                pixels[i] = (byte)((Encoded(b * exposure) * alpha + 127) / 255);
                pixels[i + 1] = (byte)((Encoded(g * exposure) * alpha + 127) / 255);
                pixels[i + 2] = (byte)((Encoded(r * exposure) * alpha + 127) / 255);
            }
        }
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
        // Reuse these two local surfaces instead of allocating a new large-object
        // buffer for each of the six passes. Only the result survives the call.
        var scratch = new byte[pixels.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            Blur(pixels, scratch, width, height, smooth ? 24 : 8, horizontal: true);
            Blur(scratch, pixels, width, height, smooth ? 24 : 8, horizontal: false);
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
        var key = AspectKey(aspect);
        if (Cached(artwork, key + 2048) is { } cached) return cached;
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
        Store(artwork, key + 2048, result);
        return result;
    }

        private static void Blur(byte[] input, byte[] output, int width, int height, int radius, bool horizontal)
    {
        var length = horizontal ? width : height;
        var lines = horizontal ? height : width;
        var stride = horizontal ? 4 : width * 4;
        var lineStride = horizontal ? width * 4 : 4;
        var samples = radius * 2 + 1;
        for (var line = 0; line < lines; line++)
        {
            var lineBase = line * lineStride;
            for (var channel = 0; channel < 4; channel++)
            {
                var baseOffset = lineBase + channel;
                var sum = 0;
                for (var i = -radius; i <= radius; i++)
                {
                    var clamped = i < 0 ? 0 : i >= length ? length - 1 : i;
                    sum += input[baseOffset + clamped * stride];
                }
                for (var position = 0; position < length; position++)
                {
                    output[baseOffset + position * stride] = (byte)((sum + samples / 2) / samples);
                    var addPos = position + radius + 1;
                    if (addPos >= length) addPos = length - 1;
                    var subPos = position - radius;
                    if (subPos < 0) subPos = 0;
                    sum += input[baseOffset + addPos * stride] - input[baseOffset + subPos * stride];
                }
            }
        }
    }
}