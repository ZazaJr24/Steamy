using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
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
