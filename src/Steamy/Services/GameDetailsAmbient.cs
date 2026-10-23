using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Steamy.Services;

/// <summary>A small, cached image backdrop preserving the banner's spatial colors across the page.</summary>
internal static class GameDetailsAmbient
{
    private const int Size = 320;
    private static readonly double[] LinearChannels = Enumerable.Range(0, 256)
        .Select(channel => { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }).ToArray();
    private static readonly byte[] EncodedChannels = Enumerable.Range(0, 65536)
        .Select(channel => { var value = channel / 65535d; return (byte)Math.Clamp(Math.Round(255 * (value <= .0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - .055)), 0, 255); }).ToArray();
    private static readonly ConditionalWeakTable<ImageSource, Dictionary<int, WeakReference<BitmapSource>>> Cache = new();
    private static readonly object Gate = new();
    internal static int AspectKey(double aspect) => (int)Math.Round(Math.Clamp(aspect, 1, 6) * 8);

    internal static BitmapSource For(ImageSource artwork, double aspect)
    {
        var key = AspectKey(aspect);
        var variants = Cache.GetOrCreateValue(artwork);
        lock (Gate)
        {
            if (variants.TryGetValue(key, out var reference) && reference.TryGetTarget(out var cached))
            { ImagePipeline.Shared.RememberDerived(artwork, -1000 - key, cached); return cached; }
        }
        var heroHeight = (int)Math.Round(Size / (key / 8d));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new ImageBrush(artwork) { Stretch = Stretch.UniformToFill }, null, new Rect(0, 0, Size, heroHeight));
        var crop = new RenderTargetBitmap(Size, heroHeight, 96, 96, PixelFormats.Pbgra32);
        crop.Render(visual); crop.Freeze();
        // Expand the image in both dimensions before a one-time blur. This retains
        // light/dark regions and multiple colors instead of extracting one accent.
        visual.Effect = new BlurEffect { Radius = 30, RenderingBias = RenderingBias.Performance };
        using (var dc = visual.RenderOpen()) dc.DrawImage(crop, new Rect(-40, -40, Size + 80, Size + 80));
        var blurred = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        blurred.Render(visual);
        var pixels = new byte[Size * Size * 4]; blurred.CopyPixels(pixels, Size * 4, 0);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var i = (y * Size + x) * 4;
            var r = Linear(pixels[i + 2]);
            var g = Linear(pixels[i + 1]);
            var b = Linear(pixels[i]);
            var luminance = .2126 * r + .7152 * g + .0722 * b;
            // Preserve spatial hues as dim light against black, without lifting
            // the entire page to the banner's brightness or filling it with gray.
            var exposure = .12 / (luminance + .15) * (1 - .18 * y / (Size - 1d));
            var red = Encoded(r * exposure); var green = Encoded(g * exposure); var blue = Encoded(b * exposure);
            var gain = Math.Min(.46, 80d / Math.Max(1d, Math.Max(red, Math.Max(green, blue))));
            pixels[i + 2] = (byte)Math.Round(red * gain);
            pixels[i + 1] = (byte)Math.Round(green * gain);
            pixels[i] = (byte)Math.Round(blue * gain);
            pixels[i + 3] = 255;
        }
        var result = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Size * 4);
        result.Freeze();
        lock (Gate)
        {
            if (variants.Count >= 2) variants.Remove(variants.Keys.First());
            variants[key] = new(result);
        }
        ImagePipeline.Shared.RememberDerived(artwork, -1000 - key, result);
        return result;
    }
    internal static Task<BitmapSource> ForAsync(ImageSource artwork, double aspect, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (Gate)
        {
            if (Cache.TryGetValue(artwork, out var variants) && variants.TryGetValue(AspectKey(aspect), out var reference) && reference.TryGetTarget(out var image))
                return Task.FromResult(image);
        }
        var frozen = artwork.IsFrozen ? artwork : (ImageSource)artwork.GetCurrentValueAsFrozen();
        return ArtworkRenderWorker.RunAsync(() => For(frozen, aspect), token);
    }
    private static double Linear(double channel)
    {
        var lower = Math.Clamp((int)channel, 0, 255);
        return lower == 255 ? LinearChannels[255] : LinearChannels[lower]
            + (LinearChannels[lower + 1] - LinearChannels[lower]) * (channel - lower);
    }
    private static byte Encoded(double value) => EncodedChannels[Math.Clamp((int)Math.Round(value * 65535), 0, 65535)];
}
