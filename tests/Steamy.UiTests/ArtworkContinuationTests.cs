using Steamy.Services;

namespace Steamy.UiTests;

public sealed class ArtworkContinuationTests
{
    [Fact]
    public void DownloadGradientIsCachedDarkAndDitheredWithoutOpeningAWindow()
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                var source = System.Windows.Media.Imaging.BitmapSource.Create(96, 54, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, Crop((_, _) => (160,160,160)), 96 * 4);
                source.Freeze();
                var result = ArtworkContinuation.ForDownload(source, 3);
                Assert.True(result.IsFrozen);
                Assert.Same(result, ArtworkContinuation.ForDownload(source, 3));
                var pixels = new byte[result.PixelWidth * result.PixelHeight * 4];
                result.CopyPixels(pixels, result.PixelWidth * 4, 0);
                var top = Enumerable.Range(0, result.PixelWidth).Select(x => pixels[x * 4]).Distinct().ToArray();
                Assert.True(top.Length > 1, "Dithering must break up a uniform quantized gradient step.");
                Assert.All(top, value => Assert.InRange(value, (byte)0, (byte)64));
                for (var x = 0; x < result.PixelWidth; x++)
                {
                    var offset = ((result.PixelHeight - 1) * result.PixelWidth + x) * 4;
                    Assert.Equal(0, pixels[offset]); Assert.Equal(0, pixels[offset + 1]); Assert.Equal(0, pixels[offset + 2]);
                    Assert.Equal(255, pixels[offset + 3]);
                }
            }
            catch (Exception error) { failure = error; }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void ContinuationPreservesRegionalColorsAndEndsTransparently()
    {
        var height = (int)Math.Ceiling(54 * ArtworkContinuation.HeightInHeroes);
        var result = ArtworkContinuation.ContinuePixels(Crop((x, _) => x < 48 ? (240, 240, 240) : (220, 20, 35)), 96, 54, height);
        var left = Pixel(result, 8, 70); var right = Pixel(result, 88, 70);
        Assert.InRange(Math.Abs(left.R - left.B), 0, 1);
        Assert.True(right.R > right.G * 5);
        Assert.Equal(0, Pixel(result, 88, height - 1).A);
        Assert.Equal(0, Pixel(result, 8, height - 1).A);
    }

    [Fact]
    public void ContinuationHasNoHorizontalEdgeAtHeroBoundary()
    {
        var result = ArtworkContinuation.ContinuePixels(Crop((x, y) => (x * 2, y * 4, 180)), 96, 54, 173);
        foreach (var x in new[] { 8, 48, 88 })
        {
            var above = Pixel(result, x, 53); var below = Pixel(result, x, 55);
            Assert.InRange(Math.Abs(above.R - below.R), 0, 5);
            Assert.InRange(Math.Abs(above.G - below.G), 0, 5);
            Assert.InRange(Math.Abs(above.B - below.B), 0, 5);
        }
    }

    [Fact]
    public void TransparencyRemainsPremultipliedAfterBlurAndFade()
    {
        var input = Crop((_, _) => (200, 120, 80));
        for (var offset = 0; offset < input.Length; offset += 4)
        { input[offset] /= 2; input[offset + 1] /= 2; input[offset + 2] /= 2; input[offset + 3] = 127; }
        var result = ArtworkContinuation.ContinuePixels(input, 96, 54, 173);
        for (var offset = 0; offset < result.Length; offset += 4)
        for (var channel = 0; channel < 3; channel++) Assert.True(result[offset + channel] <= result[offset + 3]);
    }

    private static byte[] Crop(Func<int, int, (int R, int G, int B)> color)
    {
        var result = new byte[96 * 54 * 4];
        for (var y = 0; y < 54; y++)
        for (var x = 0; x < 96; x++)
        {
            var value = color(x, y); var offset = (y * 96 + x) * 4;
            result[offset] = (byte)value.B; result[offset + 1] = (byte)value.G;
            result[offset + 2] = (byte)value.R; result[offset + 3] = 255;
        }
        return result;
    }
    private static (int R, int G, int B, int A) Pixel(byte[] pixels, int x, int y)
    { var offset = (y * 96 + x) * 4; return (pixels[offset + 2], pixels[offset + 1], pixels[offset], pixels[offset + 3]); }
}
