using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckDashboardLayout(DashboardPage page)
    {
        var hero = Assert.IsAssignableFrom<FrameworkElement>(page.FindName("DashboardHero"));
        var artwork = Assert.IsAssignableFrom<FrameworkElement>(page.FindName("SpotlightArtworkFrame"));
        var title = Assert.IsType<TextBlock>(page.FindName("SpotlightTitle"));
        var description = Assert.IsType<TextBlock>(page.FindName("SpotlightDescription"));
        Assert.InRange(description.ActualWidth, 1, 360);
        var actions = Assert.IsAssignableFrom<FrameworkElement>(page.FindName("SpotlightActions"));
        var paging = Assert.IsAssignableFrom<FrameworkElement>(page.FindName("SpotlightPaging"));
        var countdown = Assert.IsAssignableFrom<FrameworkElement>(page.FindName("SpotlightCountdown"));
        var previews = Assert.IsAssignableFrom<FrameworkElement>(page.FindName("SpotlightPreviews"));
        Assert.Equal(page.ActualWidth < 700 ? Visibility.Collapsed : Visibility.Visible, previews.Visibility);
        if (previews.Visibility == Visibility.Visible)
        {
            var titleStart = title.TranslatePoint(new Point(0,0), hero);
            var previewStart = previews.TranslatePoint(new Point(0,0), hero);
            Assert.True(titleStart.X + title.ActualWidth + 16 <= previewStart.X,
                "Spotlight text must retain space beside the artwork previews.");
        }
        if (Grid.GetRow(paging) == Grid.GetRow(actions))
        {
            var actionStart = actions.TranslatePoint(new Point(0,0), hero);
            var pagingStart = paging.TranslatePoint(new Point(0,0), hero);
            Assert.True(actionStart.X + actions.ActualWidth + 8 <= pagingStart.X,
                "Spotlight actions and navigation must have separate space.");
        }
        var countdownStart = countdown.TranslatePoint(new Point(0,0), actions);
        Assert.InRange(countdownStart.X, 0, 1);
        Assert.InRange(countdownStart.Y, 0, 1);
        Assert.True(countdown.ActualWidth >= 100 && countdown.ActualHeight >= 40);
        Assert.Empty(Descendants<Button>(actions));
        Assert.InRange(hero.ActualHeight, 340, 380);
        Assert.Equal(new Thickness(0), Assert.IsType<Border>(hero).BorderThickness);
        Assert.True(artwork.ActualWidth > 0);
        Assert.True(artwork.ActualHeight > 0);
        Assert.False(string.IsNullOrWhiteSpace(title.Text));
        foreach (var element in new[] { hero, artwork, title, actions, paging })
        {
            var start = element.TranslatePoint(new Point(0, 0), page);
            Assert.True(start.X >= -1 && start.X + element.ActualWidth <= page.ActualWidth + 1,
                $"{element.Name} extends beyond the dashboard at width {page.ActualWidth}.");
        }
        foreach (var button in Descendants<Button>(actions))
        {
            Assert.True(button.ActualWidth >= 24 && button.ActualHeight >= 24);
            var start = button.TranslatePoint(new Point(0, 0), hero);
            Assert.True(start.X >= -1 && start.X + button.ActualWidth <= hero.ActualWidth + 1);
            Assert.True(start.Y >= -1 && start.Y + button.ActualHeight <= hero.ActualHeight + 1,
                $"Spotlight action is clipped at width {page.ActualWidth}.");
        }
        Assert.All(Descendants<Button>(paging), button =>
            Assert.True(button.ActualWidth >= 24 && button.ActualHeight >= 24));
    }

    private static void CheckPersonalDashboard(IServiceProvider provider, MemoryActivityFixture activity)
    {
        var store = provider.GetRequiredService<IAppDataStore>();
        var model = provider.GetRequiredService<DashboardViewModel>();
        var installed = new Game { AppId = 42, Name = "An installed fixture", InstallState = GameInstallState.Installed };
        store.Games.Add(installed);
        try
        {
            var refresh = model.RefreshActivityAsync();
            PumpUntil(() => refresh.IsCompleted);
            refresh.GetAwaiter().GetResult();
            Assert.Contains(model.PersonalLibrary, entry => ReferenceEquals(entry.Game, installed));
            Assert.False(model.HasContinueGame); // Installed does not mean it was opened or played.
            var toggle = ((IAsyncRelayCommand)model.ToggleFavoriteCommand).ExecuteAsync(installed);
            PumpUntil(() => toggle.IsCompleted);
            toggle.GetAwaiter().GetResult();
            Assert.Same(installed, Assert.Single(model.FavoriteGames).Game);
            Assert.True(Assert.Single(model.PersonalLibrary).IsFavorite);
            activity.RecordLaunchAsync(installed.AppId, DateTimeOffset.UtcNow.AddHours(-1)).GetAwaiter().GetResult();
            refresh = model.RefreshActivityAsync();
            PumpUntil(() => refresh.IsCompleted);
            refresh.GetAwaiter().GetResult();
            Assert.True(model.HasContinueGame);
            Assert.Same(installed, model.ContinueGame!.Game);
            Assert.Contains("Opened in Steamy", Assert.Single(model.RecentlyOpenedGames).OpenedLabel);
            toggle = ((IAsyncRelayCommand)model.ToggleFavoriteCommand).ExecuteAsync(installed);
            PumpUntil(() => toggle.IsCompleted);
            toggle.GetAwaiter().GetResult();
            Assert.Empty(model.FavoriteGames);
        }
        finally
        {
            store.Games.Remove(installed);
            var refresh = model.RefreshActivityAsync();
            PumpUntil(() => refresh.IsCompleted);
            refresh.GetAwaiter().GetResult();
        }
        Assert.False(model.HasContinueGame); // A removed installation never remains a launch target.

        var catalogFavorite = new Game { AppId = 10, Name = "A catalog-only fixture", InstallState = GameInstallState.NotInstalled };
        var library = provider.GetRequiredService<LibraryViewModel>();
        var previousSource = library.SelectedSourceFilter;
        var previousSearch = library.SearchText;
        try
        {
            var toggle = ((IAsyncRelayCommand)model.ToggleFavoriteCommand).ExecuteAsync(catalogFavorite);
            PumpUntil(() => toggle.IsCompleted);
            toggle.GetAwaiter().GetResult();
            var favorite = Assert.Single(model.FavoriteGames);
            Assert.Equal(10, favorite.Game.AppId);
            Assert.Equal("An offline library game", favorite.Game.Name);
            Assert.Equal(GameInstallState.NotInstalled, favorite.Game.InstallState);
            Assert.True(favorite.IsFavorite);
            Assert.DoesNotContain(store.Games, game => game.AppId == catalogFavorite.AppId);
            Assert.Empty(model.PersonalLibrary);
            Assert.False(model.HasContinueGame);
            library.SearchText = string.Empty;
            library.SelectedSourceFilter = "Favorites";
            var catalogRefresh = ((IAsyncRelayCommand)library.RefreshCatalogCommand).ExecuteAsync(null);
            PumpUntil(() => catalogRefresh.IsCompleted);
            catalogRefresh.GetAwaiter().GetResult();
            PumpUntil(() => library.PagedCatalogItems.Count == 1);
            Assert.Equal(catalogFavorite.AppId, Assert.Single(library.PagedCatalogItems).AppId);

            activity.RecordLaunchAsync(catalogFavorite.AppId).GetAwaiter().GetResult();
            var refresh = model.RefreshActivityAsync();
            PumpUntil(() => refresh.IsCompleted);
            refresh.GetAwaiter().GetResult();
            Assert.False(model.HasContinueGame); // A saved catalog favorite cannot become an installed launch target.
            Assert.Empty(model.RecentlyOpenedGames);
        }
        finally
        {
            if (activity.GetAsync().GetAwaiter().GetResult().FavoriteAppIds.Contains(catalogFavorite.AppId))
                activity.ToggleFavoriteAsync(catalogFavorite.AppId).GetAwaiter().GetResult();
            if (library.SelectedSourceFilter == "Favorites")
                PumpUntil(() => library.PagedCatalogItems.Count == 0);
            library.SelectedSourceFilter = previousSource;
            library.SearchText = previousSearch;
            var refresh = model.RefreshActivityAsync();
            PumpUntil(() => refresh.IsCompleted);
            refresh.GetAwaiter().GetResult();
        }
        Assert.Empty(model.FavoriteGames);
    }
}
