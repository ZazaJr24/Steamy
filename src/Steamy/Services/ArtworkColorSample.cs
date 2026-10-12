using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Steamy.Services;

/// <summary>Samples source artwork once, avoiding live page captures for toolbar contrast.</summary>
internal sealed class ArtworkColorSample
{
    private const int Width = 96, Height = 54;
    private static readonly ConditionalWeakTable<ImageSource, ArtworkColorSample> Cache = new();
    private readonly byte[] _pixels = new byte[Width * Height * 4];
    private ArtworkColorSample(ImageSource source)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawImage(source, new Rect(0, 0, Width, Height));
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.CopyPixels(_pixels, Width * 4, 0);
    }
    internal static double Luminance(ImageSource source, double x, double y, double frameAspect)
        => UiThemeService.Luminance(ColorAt(source, x, y, frameAspect));
    internal static Color ColorAt(ImageSource source, double x, double y, double frameAspect)
    {
        var sourceAspect = source.Width / Math.Max(1, source.Height);
        if (frameAspect > sourceAspect) y = .5 + (y - .5) * sourceAspect / frameAspect;
        else x = .5 + (x - .5) * frameAspect / sourceAspect;
        var sample = Cache.GetValue(source, image => new ArtworkColorSample(image));
        var offset = (Math.Clamp((int)(y * Height), 0, Height - 1) * Width + Math.Clamp((int)(x * Width), 0, Width - 1)) * 4;
        return Color.FromRgb(sample._pixels[offset + 2], sample._pixels[offset + 1], sample._pixels[offset]);
    }
}
