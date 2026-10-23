using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckLibrarySmoothnessOffscreen(IServiceProvider services, AppDataStore store, FixtureArtwork artwork)
    {
        // Detached real page: no HWND, activation, user profile, or live network.
        var folder = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS")!;
        Directory.CreateDirectory(folder);
        AddScreenshotLibrary(store, artwork);
        var fixtures = store.Games.Select(game => new SteamCatalogItem {
            AppId = game.AppId, Name = game.Name, AppType = SteamCatalogAppType.Game,
            ArtworkImage = game.ArtworkImage, HeaderImage = game.HeaderImage }).ToArray();
        var model = services.GetRequiredService<LibraryViewModel>();
        model.PagedCatalogItems.Clear();
        for (var i = 0; i < 3010; i++) model.PagedCatalogItems.Add(fixtures[i % fixtures.Length]);
        typeof(LibraryViewModel).GetProperty(nameof(model.FilteredCatalogCount))!.SetValue(model, 3010);
        var results = new List<object>();
        foreach (var theme in new[] { "Dark", "Light" })
        {
            UiThemeService.Apply(theme);
            var page = new LibraryPage(); LayoutFlow(page, 1440);
            var gallery = (ItemsControl)page.FindName("LibraryGallery");
            var scroll = (ScrollViewer)gallery.Template.FindName("GalleryScroll", gallery);
            var panel = Descendants<VirtualizingGamePanel>(gallery).Single();
            Assert.InRange(panel.RealizedCount, 1, 48);
            scroll.ScrollToVerticalOffset(400); page.UpdateLayout();
            var filterY = ((FrameworkElement)page.FindName("LibraryFilterBar")).TranslatePoint(new Point(), page).Y;
            for (var i = 0; i < 40; i++) { scroll.ScrollToVerticalOffset(400 + i * 3); page.UpdateLayout(); }
            var steps = new List<double>();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 720; i++)
            {
                // Includes crossing rows and reversing, not just one retained row.
                var offset = 400 + (i < 360 ? i : 720 - i) * 3;
                var began = Stopwatch.GetTimestamp();
                scroll.ScrollToVerticalOffset(offset); page.UpdateLayout();
                steps.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            }
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.Equal(filterY, ((FrameworkElement)page.FindName("LibraryFilterBar")).TranslatePoint(new Point(), page).Y, 3);
            Assert.InRange(panel.RealizedCount, 1, 48);
            scroll.ScrollToTop(); page.UpdateLayout();
            foreach (var dpi in new[] { 96d, 144d, 192d })
            {
                // Match the visual's DPI before layout, rather than enlarging
                // glyph runs cached for a 96-DPI tree into a larger bitmap.
                VisualTreeHelper.SetRootDpi(page, new DpiScale(dpi / 96, dpi / 96));
                page.InvalidateMeasure(); LayoutFlow(page, 1440);
                Assert.Equal(dpi / 96, VisualTreeHelper.GetDpi(page).DpiScaleX);
                var bitmap = new RenderTargetBitmap((int)(1440 * dpi / 96), (int)(800 * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(page);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(folder, $"library-{theme}-{dpi}.png")); encoder.Save(file);
            }
            steps.Sort();
            results.Add(new { theme, itemCount = 3010, retainedTiles = panel.RealizedCount, samples = steps.Count,
                medianUiScrollLayoutMs = steps[steps.Count / 2], p95UiScrollLayoutMs = steps[(int)(steps.Count * .95)],
                maxUiScrollLayoutMs = steps[^1], allocatedBytes = bytes });
        }
        Assert.Empty(Application.Current.Windows.Cast<Window>());
        File.WriteAllText(Path.Combine(folder, "library-scroll.json"), JsonSerializer.Serialize(new {
            scope = "720 ScrollToVerticalOffset + UpdateLayout steps on real detached LibraryPage, fixed 3010-item artwork fixture; UI layout CPU time, not presentation FPS.",
            windowsCreated = 0, results }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
