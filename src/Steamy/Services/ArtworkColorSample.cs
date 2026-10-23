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
    private readonly double _sourceAspect;
    private ArtworkColorSample(ImageSource source)
    {
        _sourceAspect=source.Width / Math.Max(1,source.Height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawImage(source, new Rect(0, 0, Width, Height));
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.CopyPixels(_pixels, Width * 4, 0);
    }
    internal static double Luminance(ImageSource source, double x, double y, double frameAspect)
        => UiThemeService.Luminance(ColorAt(source, x, y, frameAspect));
    internal static bool TryLuminance(ImageSource source, double x, double y, double frameAspect, out double luminance)
    {
        if (!Cache.TryGetValue(source,out var sample)) { luminance=0; return false; }
        luminance=UiThemeService.Luminance(sample.ColorAtCore(x,y,frameAspect));
        return true;
    }
    internal static Task<ArtworkColorSample> ForAsync(ImageSource source,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(Cache.TryGetValue(source,out var cached)) return Task.FromResult(cached);
        var frozen=source.IsFrozen ? source : (ImageSource)source.GetCurrentValueAsFrozen();
        return ArtworkRenderWorker.RunAsync(()=>Cache.GetValue(source,_=>new ArtworkColorSample(frozen)),token);
    }
    internal static Color ColorAt(ImageSource source, double x, double y, double frameAspect)
    {
        var sample = Cache.GetValue(source, image => new ArtworkColorSample(image));
        return sample.ColorAtCore(x,y,frameAspect);
    }
    internal Color ColorAtCore(double x, double y, double frameAspect)
    {
        var sourceAspect=_sourceAspect;
        if (frameAspect > sourceAspect) y = .5 + (y - .5) * sourceAspect / frameAspect;
        else x = .5 + (x - .5) * frameAspect / sourceAspect;
        var offset = (Math.Clamp((int)(y * Height), 0, Height - 1) * Width + Math.Clamp((int)(x * Width), 0, Width - 1)) * 4;
        return Color.FromRgb(_pixels[offset + 2], _pixels[offset + 1], _pixels[offset]);
    }
}
