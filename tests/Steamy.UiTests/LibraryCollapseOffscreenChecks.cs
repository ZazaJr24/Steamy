using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckLibraryCollapseOffscreen(IServiceProvider services, AppDataStore store, FixtureArtwork artwork)
    {
        Assert.Empty(Application.Current.Windows.Cast<Window>());
        var folder = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS")!; Directory.CreateDirectory(folder);
        AddScreenshotLibrary(store, artwork);
        var model = services.GetRequiredService<LibraryViewModel>();
        var fixtures = store.Games.Select(game => new SteamCatalogItem
        {
            AppId = game.AppId, Name = game.Name, AppType = SteamCatalogAppType.Game,
            ArtworkImage = game.ArtworkImage, HeaderImage = game.HeaderImage
        }).ToArray();
        model.PagedCatalogItems.Clear();
        for (var i = 0; i < 1000; i++) model.PagedCatalogItems.Add(fixtures[i % fixtures.Length]);
        typeof(LibraryViewModel).GetProperty(nameof(LibraryViewModel.FilteredCatalogCount))!.SetValue(model, 1000);
        foreach (var theme in new[] { "Dark", "Light" })
        {
            UiThemeService.Apply(theme);
            foreach (var width in new[] { 980, 1440, 1920 })
            {
                var page = new LibraryPage(); LayoutFlow(page, width);
                var gallery = (ItemsControl)page.FindName("LibraryGallery");
                var scroll = (ScrollViewer)gallery.Template.FindName("GalleryScroll", gallery);
                var filters = (Border)page.FindName("LibraryFilterBar");
                var heading = (Grid)page.FindName("LibraryHeading");
                var panel = Descendants<VirtualizingGamePanel>(gallery).Single();
                var viewport = scroll.ViewportHeight;
                Assert.True(panel.RealizedCount < 50); Assert.True(SmoothScroll.GetEnabled(scroll));
                var start = filters.TranslatePoint(new Point(), page).Y;
                SaveFlow(page, folder, $"library-{theme}-{width}-expanded", width);
                foreach (var offset in new[] { 24d, 64d, 400d, 10d, 0d })
                {
                    scroll.ScrollToVerticalOffset(offset); LayoutFlow(page, width);
                    var expected = Math.Min(scroll.VerticalOffset, 64);
                    Assert.InRange(Math.Abs(filters.TranslatePoint(new Point(), page).Y - (start - expected)), 0, 1);
                    Assert.Equal(viewport, scroll.ViewportHeight);
                    Assert.Equal(1 - expected / 64, heading.Opacity, 3);
                    Assert.True(panel.RealizedCount < 50);
                    if (offset == 64) SaveFlow(page, folder, $"library-{theme}-{width}-collapsed", width);
                }
                Assert.Equal(0, scroll.VerticalOffset);
                Assert.Equal(1, heading.Opacity);
                Assert.True(((TextBox)page.FindName("LibrarySearch")).IsEnabled);
                var hint = Descendants<TextBlock>(filters).Single(text => BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == "SearchHint");
                hint.SetCurrentValue(TextBlock.TextProperty, "Search feedback remains visible without covering the games.");
                LayoutFlow(page, width);
                Assert.Equal(filters.ActualHeight + 24, gallery.Margin.Top);
                Assert.True(gallery.Margin.Top > 68);
                scroll.ScrollToVerticalOffset(64); LayoutFlow(page, width);
                var filtersBottom = filters.TranslatePoint(new Point(0, filters.ActualHeight), page).Y;
                var galleryTop = gallery.TranslatePoint(new Point(), page).Y;
                Assert.InRange(galleryTop - filtersBottom, 23, 25);
                SaveFlow(page, folder, $"library-{theme}-{width}-hint", width);
                if (width == 1440 && fixtures[0].HeaderImage is { } landscape)
                {
                    var portrait = fixtures[0].ArtworkImage;
                    fixtures[0].ArtworkImage = landscape; LayoutFlow(page, width);
                    var fallback = Descendants<Image>(gallery).First(image => ReferenceEquals(image.Source, landscape));
                    Assert.Equal(Stretch.Uniform, fallback.Stretch);
                    SaveFlow(page, folder, $"library-{theme}-{width}-landscape", width);
                    fixtures[0].ArtworkImage = portrait; LayoutFlow(page, width);
                    Assert.Equal(Stretch.UniformToFill, fallback.Stretch);
                }
            }
        }
        Assert.Empty(Application.Current.Windows.Cast<Window>());
    }
}
