using System.Windows;
using System.Windows.Controls;
using Steamy.Pages;
namespace Steamy.UiTests;
public sealed partial class PageSmokeTests
{
    private static void CheckDownloadsLayout(DownloadsPage page)
    {
        var cover = Assert.IsType<Grid>(page.FindName("DownloadArtworkPane"));
        Assert.True(cover.ActualWidth >= 100 && cover.ActualHeight is >= 80 and <= 95, "The cover belongs in a compact horizontal download row.");
        var title = Assert.IsType<TextBlock>(page.FindName("DownloadHeroTitle"));
        Assert.Equal(Visibility.Visible, title.Visibility);
        Assert.True(title.ActualHeight > 0);
        var progress = Assert.IsType<Steamy.Controls.SmoothProgressBar>(page.FindName("FocusedProgress"));
        var content = Assert.IsType<Grid>(page.FindName("DownloadHeroContent"));
        Assert.True(progress.ActualWidth > 80 && Math.Abs(progress.ActualWidth - content.ActualWidth) < 2, "The progress bar must fill the download row's content column.");
        var list = Descendants<ListBox>(page).Single();
        Assert.True(list.ActualHeight >= 60, $"The queue needs a finite, usable viewport below live metrics. Page={page.ActualWidth}×{page.ActualHeight}, queue={list.ActualHeight}, hero={((Border)page.FindName("DownloadHero")).ActualHeight}.");
        Assert.True(ScrollViewer.GetHorizontalScrollBarVisibility(list) == ScrollBarVisibility.Disabled);
        Assert.True(System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(list));
        Assert.DoesNotContain(Descendants<TextBlock>(page), text => text.Text == "NETWORK ACTIVITY");
        var hero = Assert.IsType<Border>(page.FindName("DownloadHero"));
        var job = Assert.IsType<Steamy.Models.DownloadJob>(hero.DataContext);
        var saved = (job.State, job.Phase, job.Progress, job.HasMeasuredProgress, job.HasWholeGameProgress, job.TransferTotalBytes, job.CurrentDepotDetail);
        try
        {
            job.State = Steamy.Models.DownloadJobState.Downloading; job.Phase = "downloading"; job.TransferTotalBytes = null;
            job.CurrentDepotDetail = "Depot 1/2 · 27.4%"; job.Progress = 27.4; job.HasWholeGameProgress = false;
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            var percentage = Assert.IsType<TextBlock>(page.FindName("DownloadTransferPercentage"));
            Assert.Contains("27", percentage.Text); Assert.Contains("Depot 1/2", percentage.Text);
            Assert.Equal(27.4, progress.SmoothValue); Assert.False(progress.IsIndeterminate);
            job.Progress = 54.8; PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert.Contains("54", percentage.Text); Assert.Equal(54.8, progress.SmoothValue);
        }
        finally { (job.State, job.Phase, job.Progress, job.HasMeasuredProgress, job.HasWholeGameProgress, job.TransferTotalBytes, job.CurrentDepotDetail) = saved; }
        PumpDispatcher(TimeSpan.FromMilliseconds(30));
        Assert.True(hero.ActualWidth >= Math.Min(1120, page.ActualWidth - 60), "Downloads use a bounded readable workspace.");
        var artwork = Assert.IsType<Image>(Assert.IsType<Border>(page.FindName("DownloadHeroArtwork")).Child);
        Assert.Equal(job.HeaderArtUrl, Steamy.Controls.AsyncArtwork.GetUrl(artwork));
        Assert.Equal(System.Windows.Media.Stretch.UniformToFill, artwork.Stretch);
        Assert.Equal(Visibility.Collapsed, ((Border)page.FindName("DownloadHeroAmbient")).Visibility);

        var pageA=Assert.IsType<Border>(page.FindName("DownloadPageAmbientA"));
        var pageB=Assert.IsType<Border>(page.FindName("DownloadPageAmbientB"));
        Assert.False(hero.IsAncestorOf(pageA));
        Assert.True(pageA.ActualHeight>=page.ActualHeight-1 && pageB.ActualWidth>=page.ActualWidth-1);
        Assert.Equal(0, pageA.Opacity); Assert.Equal(0, pageB.Opacity);
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
