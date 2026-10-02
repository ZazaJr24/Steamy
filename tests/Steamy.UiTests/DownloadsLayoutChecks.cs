using System.Windows;
using System.Windows.Controls;
using Steamy.Pages;
namespace Steamy.UiTests;
public sealed partial class PageSmokeTests
{
    private static void CheckDownloadsLayout(DownloadsPage page)
    {
        var metrics = Assert.IsType<System.Windows.Controls.Primitives.UniformGrid>(page.FindName("FocusedMetrics"));
        Assert.Equal(4, metrics.Columns);
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
    }
}
