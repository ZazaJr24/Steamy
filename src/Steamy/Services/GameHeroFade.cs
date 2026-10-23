using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Steamy.Services;

/// <summary>Fades a banner once off the UI thread; scrolling paints one frozen image.</summary>
internal static class GameHeroFade
{
    private static readonly ConditionalWeakTable<ImageSource, Dictionary<int, WeakReference<BitmapSource>>> Cache = new();
    private static readonly object Gate = new();

    internal static int Variant(double pixelWidth, double aspect)
    {
        // Round up at the monitor's physical resolution. Downsampling a 2560px
        // window to 1600px made its already faded banner unnecessarily soft.
        var width = (int)(Math.Ceiling(Math.Clamp(pixelWidth, 256, 4096) / 256) * 256);
        var ratio = (int)Math.Round(Math.Clamp(aspect, 1, 8) * 32);
        return width * 1024 + ratio;
    }

    internal static async Task<BitmapSource> ForAsync(ImageSource artwork, int variant, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (Gate)
        {
            if (Cache.TryGetValue(artwork, out var variants) && variants.TryGetValue(variant, out var entry)
                && entry.TryGetTarget(out var ready)) return ready;
        }
        var frozen = artwork.IsFrozen ? artwork : (ImageSource)artwork.GetCurrentValueAsFrozen();
        return await ArtworkRenderWorker.RunAsync(() => Render(frozen, variant, token), token).ConfigureAwait(false);
    }

    private static BitmapSource Render(ImageSource artwork, int variant, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (Gate)
        {
            if (Cache.TryGetValue(artwork, out var variants) && variants.TryGetValue(variant, out var entry)
                && entry.TryGetTarget(out var ready)) return ready;
        }
        var width = variant / 1024;
        var aspect = (variant % 1024) / 32d;
        var height = Math.Max(2, (int)Math.Round(width / aspect));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new ImageBrush(artwork) { Stretch = Stretch.UniformToFill }, null, new Rect(0, 0, width, height));
        var crop = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        crop.Render(visual);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        crop.CopyPixels(pixels, stride, 0);
        for (var y = 0; y < height; y++)
        {
            if ((y & 15) == 0) token.ThrowIfCancellationRequested();
            var t = Math.Clamp((y / (height - 1d) - .08) / .92, 0, 1);
            var remaining = 1 - t;
            // A flat derivative at both ends and an almost transparent tail avoid a visible seam.
            var opacity = remaining * remaining * remaining * (1 + 3 * t);
            var gain = (int)Math.Round(opacity * 65535);
            if (gain == 65535) continue;
            if (gain == 0) { Array.Clear(pixels, y * stride, stride); continue; }
            for (var i = y * stride; i < (y + 1) * stride; i++)
                pixels[i] = (byte)((pixels[i] * gain + 32767) / 65535);
        }
        token.ThrowIfCancellationRequested();
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        lock (Gate)
        {
            var variants = Cache.GetOrCreateValue(artwork);
            if (variants.Count >= 3) variants.Remove(variants.Keys.First());
            variants[variant] = new(result);
        }
        ImagePipeline.Shared.RememberDerived(artwork, -30000 - variant, result);
        return result;
    }
}
