using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class PitchBlackThemeTests
{
    [Fact]
    public void DarkCanvasResourcesAreOpaqueBlack()
    {
        foreach (var key in new[] { "AppBackgroundBrush", "ApplicationBackgroundBrush", "SolidBackgroundFillColorBaseBrush", "GameDetailsBackgroundBrush" })
            Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(PaletteBrush(key)).Color);
        Assert.True(UiThemeService.Luminance(Assert.IsType<SolidColorBrush>(PaletteBrush("TextPrimaryBrush")).Color) > .7);
    }

    [Fact]
    public void DarkGlassMaterialsRemainNearBlackWhenComposited()
    {
        OnSta(() =>
        {
            foreach (var key in new[] { "SidebarBackgroundBrush", "SurfaceBrush", "SurfaceElevatedBrush", "GlassSurfaceBrush", "DialogSurfaceBrush", "GameDialogSurfaceBrush" })
            {
                var pixels = Render(PaletteBrush(key));
                Assert.True(pixels.Where((_, index) => index % 4 != 3).Max() <= 24, $"{key} raises the black surface too far.");
            }
        });
    }

    [Fact]
    public void SharedAmbientKeepsBlackBaseAndSubtleArtworkColor()
    {
        OnSta(() =>
        {
            var brush = PitchBlackAmbient.Create(new(Colors.Red, Colors.Blue));
            Assert.True(brush.IsFrozen);
            var pixels = Render(brush);
            Assert.All(pixels.Where((_, index) => index % 4 == 3), alpha => Assert.Equal(255, alpha));
            Assert.InRange(pixels.Where((_, index) => index % 4 != 3).Max(), 6, 48);
            Assert.Contains(pixels.Where((_, index) => index % 4 == 2), red => red > 5);
            Assert.Contains(pixels.Where((_, index) => index % 4 == 0), blue => blue > 5);
            Assert.All(pixels.Where((_, index) => index % 4 == 1), green => Assert.InRange(green, 0, 1));
            Assert.All(pixels.Skip(pixels.Length - 64 * 4).Where((_, index) => index % 4 != 3), channel => Assert.InRange(channel, 0, 1));
        });
    }

    [Fact]
    public void GameAmbientPreservesHueWithoutBrighteningTheWholePage()
    {
        OnSta(() =>
        {
            foreach (var color in new[] { Colors.Red, Colors.Blue })
            {
                var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
                    new byte[] { color.B, color.G, color.R, 255, color.B, color.G, color.R, 255 }, 8);
                source.Freeze();
                var image = GameDetailsAmbient.For(source, 3);
                var pixels = new byte[4]; image.CopyPixels(new Int32Rect(160, 160, 1, 1), pixels, 4, 0);
                Assert.True(image.IsFrozen);
                Assert.InRange(pixels.Take(3).Max(), 8, 80);
                if (color == Colors.Red) Assert.True(pixels[2] > pixels[0] + 10);
                else Assert.True(pixels[0] > pixels[2] + 10);
            }
        });
    }

    private static Brush PaletteBrush(string key)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Steamy", "Resources", "Themes", "Dark.xaml"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var palette = XDocument.Load(Path.Combine(directory!.FullName, "src", "Steamy", "Resources", "Themes", "Dark.xaml"));
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var node = new XElement(palette.Root!.Elements().Single(item => (string?)item.Attribute(x + "Key") == key));
        node.Attribute(x + "Key")!.Remove();
        return (Brush)XamlReader.Parse(node.ToString());
    }

    private static byte[] Render(Brush brush)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        { context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 64, 64)); context.DrawRectangle(brush, null, new Rect(0, 0, 64, 64)); }
        var image = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); image.Render(drawing);
        var pixels = new byte[64 * 64 * 4]; image.CopyPixels(pixels, 64 * 4, 0); return pixels;
    }

    private static void OnSta(Action check)
    {
        Exception? failure = null;
        var worker = new Thread(() => { try { check(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
