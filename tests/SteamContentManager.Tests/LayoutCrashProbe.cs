using System.Text;
using System.Windows;
using System.Windows.Controls;
using SteamContentManager.Pages;
using Xunit;

namespace SteamContentManager.Tests;

// TEMPORARY diagnostic probe — identifies which page makes the layout measure recurse.
public sealed class LayoutCrashProbe
{
    [Fact]
    public void MeasurePagesOneByOne()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "layout-crash-probe.txt");
        File.WriteAllText(logPath, "start\n");

        var pageTypes = new[]
        {
            typeof(DashboardPage), typeof(LibraryPage), typeof(DownloadsPage),
            typeof(DepotDownloaderPage), typeof(ModFixesPage), typeof(GameFixesPage),
            typeof(OnlineFixesPage), typeof(DenuvoGenerationPage), typeof(DenuvoFixesPage),
            typeof(HvFixesPage), typeof(SteamlessPage),
            typeof(AchievementsPage), typeof(DepotsPage), typeof(ManifestPage),
            typeof(BranchesPage), typeof(HubPage), typeof(LogsPage), typeof(SettingsPage)
        };

        WpfTestHost.Run(() =>
        {
            foreach (var pageType in pageTypes)
            {
                File.AppendAllText(logPath, $"measuring {pageType.Name}\n");
                var page = (Page)Activator.CreateInstance(pageType)!;
                page.Width = 1000;
                page.Measure(new Size(1000, 2000));
                page.Arrange(new Rect(0, 0, 1000, 2000));
                page.UpdateLayout();
                WpfTestHost.Pump();
                File.AppendAllText(logPath, $"ok {pageType.Name}\n");
            }
        });

        Assert.True(true, File.ReadAllText(logPath));
    }
}
