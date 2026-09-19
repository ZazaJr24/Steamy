using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SteamContentManager.Pages;
using Xunit;

namespace SteamContentManager.Tests;

/// <summary>Temporary measurement helper: writes the real bounds of the Steamless page to a file.</summary>
public sealed class LayoutProbeScratch
{
    [Fact]
    public void DumpSteamlessBounds()
    {
        var report = new StringBuilder();
        WpfTestHost.Run(() =>
        {
            const double width = 1400;
            var page = new SteamlessPage { Width = width };
            page.Measure(new Size(width, 2000));
            page.Arrange(new Rect(0, 0, width, 2000));
            page.UpdateLayout();
            WpfTestHost.Pump();

            report.AppendLine($"--- direct page: {page.ActualWidth:0} x {page.ActualHeight:0}");
            Walk(page, page, report, 0, 9);

            // The real application hosts the page in the navigation presenter; that extra layer is
            // where the layout can differ, so it is measured the same way.
            var presenter = new Wpf.Ui.Controls.NavigationViewContentPresenter();
            presenter.Navigate(new SteamlessPage());
            WpfTestHost.Pump();
            presenter.Width = width;
            presenter.Measure(new Size(width, 2000));
            presenter.Arrange(new Rect(0, 0, width, 2000));
            presenter.UpdateLayout();
            WpfTestHost.Pump();

            report.AppendLine();
            report.AppendLine($"--- in presenter: {presenter.ActualWidth:0} x {presenter.ActualHeight:0}");
            Walk(presenter, presenter, report, 0, 12);
        });

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "steamless-bounds.txt"), report.ToString());
        Assert.NotEmpty(report.ToString());
    }

    private static void Walk(DependencyObject node, DependencyObject root, StringBuilder report, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        if (node is FrameworkElement element && element.ActualWidth > 1)
        {
            var offset = element.TransformToAncestor((Visual)root).Transform(new Point(0, 0));
            var label = element.GetType().Name;
            if (element is TextBlock text && !string.IsNullOrWhiteSpace(text.Text)) label += $" \"{Trim(text.Text)}\"";
            report.AppendLine($"{new string(' ', depth * 2)}x={offset.X:0}..{offset.X + element.ActualWidth:0} w={element.ActualWidth:0}  {label}");
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < count; index++) Walk(VisualTreeHelper.GetChild(node, index), root, report, depth + 1, maxDepth);
    }

    private static string Trim(string value) => value.Length <= 40 ? value : value[..40] + "…";
}
