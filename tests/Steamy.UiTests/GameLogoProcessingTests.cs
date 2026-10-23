using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Steamy.Controls;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class GameLogoProcessingTests
{
    [Fact]
    public async Task StripScanningPreservesPixelsAcrossStripBoundariesAndAlphaThreshold()
    {
        const int width = 100, height = 97;
        var pixels = new byte[width * height * 4];
        for (var y = 31; y <= 84; y++) for (var x = 20; x <= 82; x++)
        { var offset = (y * width + x) * 4; pixels[offset] = (byte)y; pixels[offset + 1] = (byte)x; pixels[offset + 3] = 255; }
        pixels[3] = 8; // Below the visibility threshold; must not expand the bounds.
        pixels[(84 * width + 82) * 4 + 3] = 9; // Above it, including the last occupied strip.
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); source.Freeze();
        var trimmed = await GameLogoLayout.TrimAsync(source, CancellationToken.None);
        Assert.NotNull(trimmed); Assert.True(trimmed.IsFrozen);
        Assert.Equal(63, trimmed.PixelWidth); Assert.Equal(54, trimmed.PixelHeight);
        var expected = new byte[63 * 54 * 4]; var actual = new byte[expected.Length];
        source.CopyPixels(new Int32Rect(20, 31, 63, 54), expected, 63 * 4, 0);
        trimmed.CopyPixels(actual, 63 * 4, 0);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task LargeLogoScanDoesNotAllocateAFullSizePixelCopy()
    {
        const int width = 2048, height = 1024;
        var pixels = new byte[width * height * 4];
        for (var y = 100; y < 900; y++) for (var x = 500; x < 1500; x++) pixels[(y * width + x) * 4 + 3] = 255;
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); source.Freeze();
        var allocated = await ArtworkRenderWorker.RunAsync(() =>
        {
            GameLogoLayout.Trim(source); // Warm the strip pool and WIC setup.
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = GameLogoLayout.Trim(source);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.NotNull(result); Assert.Equal(1000, result.PixelWidth); Assert.Equal(800, result.PixelHeight);
            return bytes;
        }, CancellationToken.None);
        Assert.InRange(allocated, 0, 1024 * 1024); // The previous scratch copy alone was 8 MiB.
    }
}
