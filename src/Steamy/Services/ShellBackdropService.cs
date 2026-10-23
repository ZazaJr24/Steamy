using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Steamy.Services;

internal interface IShellBackdropService
{
    ImageSource? Artwork { get; }
    ImageSource? BlurredArtwork { get; }
    double HeroHeight { get; }
    double HeroTop { get; }
    double ScrollOffset { get; }
    event EventHandler? Changed;
    void SetArtwork(ImageSource? artwork);
    void SetViewport(double height, double top, double scrollOffset);
    void Clear();
}

/// <summary>Shares cached game artwork between the page and its glass navigation pane.</summary>
internal sealed class ShellBackdropService : IShellBackdropService
{
    private readonly ConditionalWeakTable<ImageSource, WeakReference<BitmapSource>> _blurCache = new();
    private CancellationTokenSource? _render;
    public ImageSource? Artwork { get; private set; }
    public ImageSource? BlurredArtwork { get; private set; }
    public double HeroHeight { get; private set; } = 500;
    public double HeroTop { get; private set; } = 34;
    public double ScrollOffset { get; private set; }
    public event EventHandler? Changed;

    public async void SetArtwork(ImageSource? artwork)
    {
        if (ReferenceEquals(Artwork, artwork)) return;
        CancelRender();
        Artwork = artwork;
        BlurredArtwork = null;
        if (artwork is not null && _blurCache.TryGetValue(artwork, out var reference) && reference.TryGetTarget(out var cached))
        {
            BlurredArtwork = cached;
            ImagePipeline.Shared.RememberDerived(artwork, -1, cached);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (artwork is null || BlurredArtwork is not null) return;
        var request = _render = new CancellationTokenSource(); var token = request.Token;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frozen = artwork.IsFrozen ? artwork : (ImageSource)artwork.GetCurrentValueAsFrozen();
        try
        {
            var bitmap = await ArtworkRenderWorker.RunAsync(() => Blur(frozen), token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (_render != request || !ReferenceEquals(Artwork, artwork)) return;
                _blurCache.GetValue(artwork, _ => new(bitmap)).SetTarget(bitmap);
                ImagePipeline.Shared.RememberDerived(artwork, -1, bitmap);
                BlurredArtwork = bitmap;
                Changed?.Invoke(this, EventArgs.Empty);
            }, DispatcherPriority.Background, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException) { }
    }

    public void SetViewport(double height, double top, double scrollOffset)
    {
        if (HeroHeight == height && HeroTop == top && ScrollOffset == scrollOffset) return;
        HeroHeight = height; HeroTop = top; ScrollOffset = scrollOffset;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        CancelRender();
        if (Artwork is null && BlurredArtwork is null && ScrollOffset == 0) return;
        Artwork = BlurredArtwork = null;
        ScrollOffset = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CancelRender()
    {
        _render?.Cancel(); _render?.Dispose(); _render = null;
    }
    private static BitmapSource Blur(ImageSource image)
    {
        // Work on a small image once per source, never on the window or its foreground.
        const int width = 640;
        var height = Math.Clamp((int)(width * image.Height / Math.Max(1, image.Width)), 1, 640);
        var visual = new DrawingVisual { Effect = new BlurEffect { Radius = 12, RenderingBias = RenderingBias.Performance } };
        using (var dc = visual.RenderOpen()) dc.DrawImage(image, new Rect(-20, -20, width + 40, height + 40));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
