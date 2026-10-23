using System.Windows.Media;
using System.Windows.Media.Imaging;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class GameHeroFadeTests
{
    [Fact]
    public async Task BannerEndsFullyTransparentWithoutABottomSeam()
    {
        var source = SolidSource();
        var faded = await GameHeroFade.ForAsync(source, GameHeroFade.Variant(800, 3), CancellationToken.None);
        Assert.True(faded.IsFrozen);
        Assert.Equal(PixelFormats.Pbgra32, faded.Format);
        var stride = faded.PixelWidth * 4;
        var pixels = new byte[stride * faded.PixelHeight];
        faded.CopyPixels(pixels, stride, 0);
        Assert.Equal(255, pixels[3]);
        Assert.All(pixels.TakeLast(stride), component => Assert.Equal(0, component));
        var previous = 255;
        for (var y = 0; y < faded.PixelHeight; y++)
        {
            var alpha = pixels[y * stride + 3];
            Assert.InRange(alpha, Math.Max(0, previous - 4), previous);
            Assert.All(pixels.Skip(y * stride).Take(4), component => Assert.InRange(component, 0, alpha));
            if (y >= faded.PixelHeight * .95) Assert.InRange(alpha, 0, 1);
            previous = alpha;
        }
    }

    [Fact]
    public async Task RepeatedRequestsReuseTheFrozenBannerAndSmallResizesUseTheSameVariant()
    {
        var source = SolidSource();
        var variant = GameHeroFade.Variant(1280, 3.1);
        Assert.Equal(variant, GameHeroFade.Variant(1278, 3.101));
        var first = await GameHeroFade.ForAsync(source, variant, CancellationToken.None);
        for (var i = 0; i < 50; i++)
            Assert.Same(first, await GameHeroFade.ForAsync(source, variant, CancellationToken.None));
        Assert.Equal(1280, first.PixelWidth);
    }

    [Theory]
    [InlineData(900, 1024)]
    [InlineData(1440, 1536)]
    [InlineData(2560, 2560)]
    [InlineData(3840, 3840)]
    [InlineData(8000, 4096)]
    public void HighDpiBannersRetainPhysicalResolutionWithinTheMemoryLimit(double physicalWidth, int expected)
        => Assert.Equal(expected, GameHeroFade.Variant(physicalWidth, 3) / 1024);

    [Fact]
    public async Task PreparationDoesNotBlockTheCallerAndCancelledQueuedWorkIsDiscarded()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = ArtworkRenderWorker.RunAsync(() => { entered.Set(); release.Wait(); return true; }, CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        using var cancellation = new CancellationTokenSource();
        try
        {
            var pending = GameHeroFade.ForAsync(SolidSource(), GameHeroFade.Variant(1280, 3.1), cancellation.Token);
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { release.Set(); await blocker.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static BitmapSource SolidSource()
    {
        var pixels = Enumerable.Repeat((byte)255, 120 * 40 * 4).ToArray();
        var image = BitmapSource.Create(120, 40, 96, 96, PixelFormats.Pbgra32, null, pixels, 120 * 4);
        image.Freeze(); return image;
    }
}
