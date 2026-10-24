using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class PaperThemeTests
{
    [Fact]
    public void LightPaletteHasCalmTintedCanvasesAndReadableBodyText()
    {
        OnSta(() =>
        {
            var expected = (Color)ColorConverter.ConvertFromString("#F1F5F9");
            foreach (var key in new[] { "AppBackgroundBrush", "GameDetailsBackgroundBrush", "ApplicationBackgroundBrush", "SolidBackgroundFillColorBaseBrush" })
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(PaletteBrush(key)).Color);
            foreach (var key in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "TextTertiaryBrush" })
                Assert.True((1.05 / (UiThemeService.Luminance(Assert.IsType<SolidColorBrush>(PaletteBrush(key)).Color) + .05)) >= 4.5, key);
            foreach (var key in new[] { "SidebarBackgroundBrush", "SurfaceBrush", "SurfaceElevatedBrush", "GlassSurfaceBrush", "DialogSurfaceBrush" })
                Assert.True(Render(PaletteBrush(key)).Where((_, index) => index % 4 != 3).Min() >= 230, key);
        });
    }

    [Fact]
    public void AmbientKeepsTheCanvasWhiteWithOnlyASubtleColorTint()
    {
        OnSta(() =>
        {
            var brush = PaperAmbient.Create(new(Colors.Red, Colors.Blue));
            Assert.True(brush.IsFrozen);
            var pixels = Render(brush);
            Assert.All(pixels.Where((_, index) => index % 4 == 3), alpha => Assert.Equal(255, alpha));
            Assert.InRange(pixels.Where((_, index) => index % 4 != 3).Min(), 230, 254);
            Assert.Contains(pixels.Where((_, index) => index % 4 != 3), channel => channel == 255);
        });
    }

    [Fact]
    public async Task OlderSettingsAdoptWhiteOnceAndASubsequentThemeChoiceRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-paper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, """{"Appearance":"Dark","BackgroundColor":"#000000","Language":"German","AmbientEnabled":false}""");
            var service = new JsonSettingsService(path);
            var settings = service.Load();
            Assert.Equal("Light", settings.Appearance); Assert.Equal("#FFFFFF", settings.BackgroundColor);
            Assert.Equal("German", settings.Language); Assert.False(settings.AmbientEnabled);
            Assert.Equal(1, settings.AppearanceDesignVersion);
            settings.Appearance = "Dark"; settings.BackgroundColor = "#102030";
            await service.SaveAsync(settings);
            var reloaded = new JsonSettingsService(path).Load();
            Assert.Equal("Dark", reloaded.Appearance); Assert.Equal("#102030", reloaded.BackgroundColor);
            Assert.Equal("Light", JsonSerializer.Deserialize<AppSettings>("{}")!.Appearance);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static Brush PaletteBrush(string key)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src/Steamy/Resources/Themes/Light.xaml"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var palette = XDocument.Load(Path.Combine(directory!.FullName, "src/Steamy/Resources/Themes/Light.xaml"));
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var node = new XElement(palette.Root!.Elements().Single(element => (string?)element.Attribute(x + "Key") == key));
        node.Attribute(x + "Key")!.Remove();
        return (Brush)XamlReader.Parse(node.ToString());
    }
    private static byte[] Render(Brush brush)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        { context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 64, 64)); context.DrawRectangle(brush, null, new Rect(0, 0, 64, 64)); }
        var image = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); image.Render(drawing);
        var pixels = new byte[64 * 64 * 4]; image.CopyPixels(pixels, 64 * 4, 0); return pixels;
    }
    private static void OnSta(Action check)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { check(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
