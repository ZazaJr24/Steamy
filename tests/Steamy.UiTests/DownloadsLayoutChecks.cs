using System.Windows;
using System.Windows.Controls;
using Steamy.Pages;
namespace Steamy.UiTests;
public sealed partial class PageSmokeTests
{
    private static void CheckDownloadsLayout(DownloadsPage page)
    {
        var cover = Assert.IsType<Grid>(page.FindName("DownloadArtworkPane"));
        Assert.True(cover.ActualWidth >= 100 && cover.ActualHeight >= 160, "Artwork must remain readable without consuming the content column.");
        var title = Assert.IsType<TextBlock>(page.FindName("DownloadHeroTitle"));
        Assert.Equal(Visibility.Visible, title.Visibility);
        Assert.True(title.ActualHeight > 0);
        var progress = Assert.IsType<Steamy.Controls.SmoothProgressBar>(page.FindName("FocusedProgress"));
        Assert.True(progress.ActualWidth > page.ActualWidth * .45, "The primary progress bar must span the card's content, rather than a narrow metrics column.");
        var list = Descendants<ListBox>(page).Single();
        Assert.True(list.ActualHeight >= 60, $"The queue needs a finite, usable viewport below live metrics. Page={page.ActualWidth}×{page.ActualHeight}, queue={list.ActualHeight}, hero={((Border)page.FindName("DownloadHero")).ActualHeight}.");
        Assert.True(ScrollViewer.GetHorizontalScrollBarVisibility(list) == ScrollBarVisibility.Disabled);
        Assert.True(System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(list));
        Assert.DoesNotContain(Descendants<TextBlock>(page), text => text.Text == "NETWORK ACTIVITY");
        var hero = Assert.IsType<Border>(page.FindName("DownloadHero"));
        Assert.True(hero.ActualWidth >= page.ActualWidth - 60, "The download artwork should span the workspace.");
        Assert.IsType<System.Windows.Media.ImageBrush>(Assert.IsType<Border>(page.FindName("DownloadHeroArtwork")).Background);
        foreach (var name in new[] { "DownloadAmbientA", "DownloadAmbientB" })
        {
            var ambient = Assert.IsType<Border>(page.FindName(name));
            Assert.True(hero.IsAncestorOf(ambient), "Artwork colors must stay inside the installation card.");
        }

        var pageA=Assert.IsType<Border>(page.FindName("DownloadPageAmbientA"));
        var pageB=Assert.IsType<Border>(page.FindName("DownloadPageAmbientB"));
        Assert.False(hero.IsAncestorOf(pageA));
        Assert.True(pageA.ActualHeight>=page.ActualHeight-1 && pageB.ActualWidth>=page.ActualWidth-1);
        Assert.True(pageA.Background is System.Windows.Media.DrawingBrush || pageB.Background is System.Windows.Media.DrawingBrush, "Artwork ambient must fill the workspace behind the queue.");
        foreach (var button in Descendants<Button>(hero).Where(button => button.Visibility == Visibility.Visible && button.ActualWidth > 0))
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
