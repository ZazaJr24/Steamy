using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.Views;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckEnglishShellAndNoticeOffscreen(IServiceProvider provider)
    {
        Assert.Equal("English", new AppSettings().Language);
        Assert.Equal("en-US", UiLanguageCatalog.ResolveCultureName("System Default"));
        var job = new DownloadJob { State = DownloadJobState.Downloading, EtaSeconds = 75 };
        Assert.Equal("Queue", job.DeckSection);
        Assert.Contains("remaining", job.DeckRemaining);
        job.State = DownloadJobState.Completed;
        Assert.Equal("History", job.DeckSection);

        var dashboard = provider.GetRequiredService<DashboardViewModel>();
        var details = provider.GetRequiredService<GameDetailsViewModel>();
        var navigation = provider.GetRequiredService<INavigationService>();
        Type? route = null;
        navigation.Attach(page => route = page);
        var game = new Game { AppId = 1593500, Name = "God of War", InstallState = GameInstallState.Installed };
        dashboard.ShowAllGamesCommand.Execute(null);
        dashboard.InstalledSearchText = "War";
        dashboard.InstalledSourceFilter = "Steam";
        dashboard.OpenGameCommand.Execute(game);
        Assert.Equal(typeof(GameDetailsPage), route);
        Assert.False(dashboard.IsInstalledLibraryOpen);
        details.BackCommand.Execute(null);
        Assert.Equal(typeof(DashboardPage), route);
        Assert.True(dashboard.IsInstalledLibraryOpen);
        Assert.Equal("War", dashboard.InstalledSearchText);
        Assert.Equal("Steam", dashboard.InstalledSourceFilter);
        dashboard.CloseInstalledLibraryCommand.Execute(null);
        dashboard.OpenGameCommand.Execute(game);
        details.BackCommand.Execute(null);
        Assert.False(dashboard.IsInstalledLibraryOpen);
        dashboard.InstalledSearchText = "";
        dashboard.InstalledSourceFilter = "All games";
        navigation.Detach();

        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        void Layout(Size size)
        {
            root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
        }
        Layout(new Size(1280, 800));
        var tools = (FrameworkElement)window.FindName("ToolsMenuSection");
        var fixes = (FrameworkElement)window.FindName("FixesMenuSection");
        var rail = (Grid)window.FindName("ShellPillItems");
        window.ToggleToolsMenu();
        Assert.Equal(Visibility.Visible, tools.Visibility);
        Assert.Equal(Visibility.Collapsed, fixes.Visibility);
        Assert.Equal(3, PillMotion.GetSelectedIndex(rail));
        window.ToggleToolsMenu(fixes: true);
        Assert.True(window.IsToolsMenuOpen);
        Assert.Equal(Visibility.Collapsed, tools.Visibility);
        Assert.Equal(Visibility.Visible, fixes.Visibility);
        Assert.Equal(4, PillMotion.GetSelectedIndex(rail));
        Assert.Equal(typeof(GameFixesPage), ((Button)window.FindName("GameFixesMenuButton")).Tag);
        Assert.DoesNotContain(Descendants<Button>(fixes), button => button.Tag is Type route && route == typeof(BetterSteamToolsPage));
        Assert.DoesNotContain(Descendants<Button>(tools), button => button.Tag is Type route && route == typeof(GameFixesPage));
        window.ToggleToolsMenu(fixes: true);
        Assert.False(window.IsToolsMenuOpen);
        Assert.Equal(0, PillMotion.GetSelectedIndex(rail));

        var notice = new TransparencyNotice(provider.GetRequiredService<ISettingsService>());
        window.ShowOverlay(notice);
        foreach (var size in new[] { new Size(1280, 800), new Size(660, 520), new Size(420, 400) })
        {
            Layout(size);
            var overlay = (Grid)window.FindName("OverlayLayer");
            Assert.True(notice.ActualHeight <= overlay.ActualHeight + 1);
            var button = (Button)notice.FindName("ContinueButton");
            var bounds = button.TransformToAncestor(notice).TransformBounds(new Rect(button.RenderSize));
            Assert.True(bounds.Bottom <= notice.ActualHeight + 1, "The welcome confirmation must remain inside the viewport.");
            var scroll = (ScrollViewer)notice.FindName("NoticeScroll");
            Assert.True(scroll.ViewportHeight > 0, $"Notice body must scroll at {size}: notice={notice.ActualHeight}, overlay={overlay.ActualHeight}, viewport={scroll.ViewportHeight}, extent={scroll.ExtentHeight}");
            Assert.True(scroll.ScrollableHeight > 0);
        }
        window.Close();
    }
}
