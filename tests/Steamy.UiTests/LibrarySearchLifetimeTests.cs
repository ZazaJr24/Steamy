using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class LibrarySearchLifetimeTests
{
    [Fact]
    public async Task ActiveSearchesReuseTheIndexAndReopeningBuildsAFreshOne()
    {
        var model = CreateModel(); Seed(model);
        try
        {
            await Search(model, "Fixture");
            var first = IndexTask(model);
            await Search(model, "999");
            Assert.Same(first, IndexTask(model));
            Assert.Equal(999, Assert.Single(model.PagedCatalogItems).AppId);
            await model.OnNavigatedFromAsync();
            await model.OnNavigatedToAsync();
            await Search(model, "999");
            Assert.NotSame(first, IndexTask(model));
            Assert.Equal(999, Assert.Single(model.PagedCatalogItems).AppId);
        }
        finally { await model.OnNavigatedFromAsync(); }
    }

    [Fact]
    public async Task RapidQueriesPublishOnlyTheLatestLocalAndCatalogResults()
    {
        var model = CreateModel(); Seed(model);
        foreach (var id in Enumerable.Range(1, 1000)) model.Games.Add(new Game { AppId = id, Name = $"Fixture {id}" });
        var localReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(model.HasGames) && model.FilteredGames.Count == 1 && model.FilteredGames[0].AppId == 999)
                localReady.TrySetResult();
        };
        try
        {
            foreach (var id in Enumerable.Range(10, 20))
            { model.SearchText = id.ToString(); model.SubmitSearchCommand.Execute(null); }
            await Search(model, "999");
            await localReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(999, Assert.Single(model.PagedCatalogItems).AppId);
            Assert.Equal(999, Assert.Single(model.FilteredGames).AppId);
            Assert.Equal(999, Assert.Single(model.PagedGames).AppId);
        }
        finally { await model.OnNavigatedFromAsync(); }
    }

    [Fact]
    public async Task LeavingTheLibraryReleasesItsSearchIndexWithoutLosingTheQuery()
    {
        var model = CreateModel();
        var index = await SearchThenLeave(model);
        for (var attempt = 0; attempt < 8 && index.IsAlive; attempt++)
        {
            await Task.Delay(20);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true); GC.WaitForPendingFinalizers();
        }
        Assert.False(index.IsAlive, "The inactive library must not retain its normalized titles and fuzzy vocabulary.");
        Assert.Equal("Fixture", model.SearchText);
        GC.KeepAlive(model);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> SearchThenLeave(LibraryViewModel model)
    {
        // Seed the same immutable input that LoadAsync publishes, without a
        // catalog download, Application, Window or global service container.
        Seed(model);
        await Search(model, "Fixture");
        var index = new WeakReference(await IndexTask(model));
        Assert.NotEmpty(model.PagedCatalogItems);
        await model.OnNavigatedFromAsync();
        return index;
    }
    private static void Seed(LibraryViewModel model)
    {
        var items = Enumerable.Range(1, 1000).Select(id => new SteamCatalogItem
            { AppId = id, Name = $"Fixture {id}", AppType = SteamCatalogAppType.Game, IsReleaseVerified = true, HasDetails = true }).ToArray();
        typeof(LibraryViewModel).GetField("_catalogSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, items);
        typeof(LibraryViewModel).GetProperty(nameof(model.CatalogLoaded))!.SetValue(model, true);
        typeof(LibraryViewModel).GetField("_lastCatalogLoad", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, DateTimeOffset.UtcNow);
        typeof(LibraryViewModel).GetField("_lastLibraryScan", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, DateTimeOffset.UtcNow);
    }
    private static Task<GameSearchIndex<SteamCatalogItem>> IndexTask(LibraryViewModel model) =>
        (Task<GameSearchIndex<SteamCatalogItem>>)typeof(LibraryViewModel).GetField("_searchIndexTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
    private static async Task Search(LibraryViewModel model, string query)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler changed = (_, change) =>
        { if (change.PropertyName == nameof(model.IsSearchBusy) && !model.IsSearchBusy) finished.TrySetResult(); };
        model.PropertyChanged += changed;
        try
        {
            model.SearchText = query; model.SubmitSearchCommand.Execute(null);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { model.PropertyChanged -= changed; }
    }

    private static LibraryViewModel CreateModel(ISteamCatalogService? catalog = null, ISpotlightService? spotlight = null, IRyuuCatalogService? ryuu = null, IHubcapCatalogService? hubcap = null) => new(new AppDataStore(), new NavigationService(),
        (ILoggingService)OfflineServiceProxy.Create(typeof(ILoggingService)), catalog ?? new Catalog(),
        ryuu ?? (IRyuuCatalogService)OfflineServiceProxy.Create(typeof(IRyuuCatalogService)), new Artwork(),
        (ILibrarySyncService)OfflineServiceProxy.Create(typeof(ILibrarySyncService)),
        hubcap ?? (IHubcapCatalogService)OfflineServiceProxy.Create(typeof(IHubcapCatalogService)),
        (IFreeManifestCatalogService)OfflineServiceProxy.Create(typeof(IFreeManifestCatalogService)), spotlight: spotlight);

    private class Catalog : ISteamCatalogService
    {
        public virtual Task<SteamCatalogSnapshot> GetCatalogAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No external catalog request is allowed.");
        public Task<SteamCatalogDetails?> GetDetailsAsync(int appId, CancellationToken cancellationToken = default) => Task.FromResult<SteamCatalogDetails?>(null);
        public virtual Task<bool> EnsureDetailsAsync(SteamCatalogItem item, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task PrepareReleaseStatusAsync(IReadOnlyList<SteamCatalogItem> items, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> EnsureArtworkAsync(SteamCatalogItem item, bool includeHeader = false, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> EnsureScreenshotsAsync(SteamCatalogItem item, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void Dispose() { }
    }
    private sealed class Artwork : IArtworkService
    {
        public Task<BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default) => Task.FromResult<BitmapImage?>(null);
        public Task LoadAsync(Game game, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadHeadersAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadManyAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
