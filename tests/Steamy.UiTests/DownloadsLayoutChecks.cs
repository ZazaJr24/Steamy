using System.Windows;
using System.Windows.Controls;
using Steamy.Pages;
namespace Steamy.UiTests;
public sealed partial class PageSmokeTests
{
    private static void CheckDownloadsLayout(DownloadsPage page)
    {
        var metrics = Assert.IsType<System.Windows.Controls.Primitives.UniformGrid>(page.FindName("FocusedMetrics"));
        Assert.Equal(page.ActualWidth < 700 && page.ActualHeight >= 650 ? 2 : 4, metrics.Columns);
        Assert.Equal(4,metrics.Children.Count);
        foreach (var field in metrics.Children.Cast<FrameworkElement>())
        {
            var bounds = field.TransformToAncestor(page).TransformBounds(new Rect(field.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= page.ActualWidth + 1);
        }
        var list = Descendants<ListBox>(page).Single();
        Assert.True(list.ActualHeight >= 60, "The queue needs a finite, usable viewport below live metrics.");
        Assert.True(ScrollViewer.GetHorizontalScrollBarVisibility(list) == ScrollBarVisibility.Disabled);
        Assert.True(System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(list));
        Assert.DoesNotContain(Descendants<TextBlock>(page), text => text.Text == "NETWORK ACTIVITY");
        var hero = Assert.IsType<Border>(page.FindName("DownloadHero"));
        Assert.True(hero.ActualWidth >= page.ActualWidth - 60, "The download artwork should span the workspace.");
        Assert.IsType<System.Windows.Media.ImageBrush>(Assert.IsType<Border>(page.FindName("DownloadHeroArtwork")).Background);
        foreach (var button in Descendants<Button>(hero).Where(button => button.IsVisible))
        {
            var bounds = button.TransformToAncestor(hero).TransformBounds(new Rect(button.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= hero.ActualWidth + 1 && bounds.Bottom <= hero.ActualHeight + 1,
                "Download actions must fit inside the artwork banner.");
        }
    }

    private static void CheckDepotDownloaderLayout(DepotDownloaderPage page)
    {
        var fields = Assert.IsType<System.Windows.Controls.Primitives.UniformGrid>(page.FindName("VersionFields"));
        Assert.Equal(page.ActualWidth < 600 ? 1 : 3, fields.Columns);
        var scroll = Descendants<ScrollViewer>(page).First();
        Assert.True(scroll.ScrollableWidth < 1, "Depot setup must not introduce horizontal scrolling.");
        var summary = Assert.IsType<Border>(page.FindName("DownloadSelectionSummary"));
        Assert.True(summary.ActualWidth > 0);
        foreach (var field in fields.Children.Cast<FrameworkElement>())
        {
            var bounds = field.TransformToAncestor(page).TransformBounds(new Rect(field.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= page.ActualWidth + 1);
        }
    }
}
