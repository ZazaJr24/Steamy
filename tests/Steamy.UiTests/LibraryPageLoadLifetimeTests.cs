using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed partial class LibrarySearchLifetimeTests
{
    [Fact]
    public async Task OptionalProvidersStartBeforeTheSteamCatalogCompletes()
    {
        var catalog = new GatedCatalog(); var ryuu = new GatedOptionalCatalog(); var hubcap = new GatedOptionalCatalog();
        var model = CreateModel(catalog, ryuu: ryuu, hubcap: hubcap);
        var load = ((IAsyncRelayCommand)model.LoadCatalogCommand).ExecuteAsync(null);
        try
        {
            await Task.WhenAll(catalog.FirstStarted.Task, ryuu.Started.Task, hubcap.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(catalog.First.Task.IsCompleted);
            catalog.First.SetResult(Snapshot(42));
            ryuu.Response.SetResult(Snapshot(43)); hubcap.Response.SetResult(Snapshot(44));
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var id in new[] { 42, 43, 44 }) Assert.Contains(model.CatalogItems, item => item.AppId == id);
        }
        finally
        {
            catalog.First.TrySetResult(Snapshot(42)); ryuu.Response.TrySetResult(Snapshot(43)); hubcap.Response.TrySetResult(Snapshot(44));
            await model.OnNavigatedFromAsync(); await load.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class GatedOptionalCatalog : IRyuuCatalogService, IHubcapCatalogService
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<SteamCatalogSnapshot> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SteamCatalogSnapshot> GetGamesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        { Started.TrySetResult(); return Response.Task; }
    }

    [Fact]
    public async Task LateCatalogCannotPopulateTheInactiveLibrary()
    {
        var catalog = new GatedCatalog(); var model = CreateModel(catalog);
        var loading = ((IAsyncRelayCommand)model.LoadCatalogCommand).ExecuteAsync(null);
        try
        {
            await catalog.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.OnNavigatedFromAsync();
            catalog.First.SetResult(Snapshot(42));
            await loading.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(model.CatalogItems); Assert.Empty(model.PagedCatalogItems);
            Assert.False(model.IsCatalogLoading);
        }
        finally
        {
            catalog.First.TrySetResult(Snapshot(42));
            await loading.WaitAsync(TimeSpan.FromSeconds(5)); await model.OnNavigatedFromAsync();
        }
    }

    [Fact]
    public async Task ReenteringTheLibraryStartsANewLoadBeforeTheOldProviderReturns()
    {
        var catalog = new GatedCatalog(); var model = CreateModel(catalog);
        var oldLoad = ((IAsyncRelayCommand)model.LoadCatalogCommand).ExecuteAsync(null);
        Task? newActivation = null;
        try
        {
            await catalog.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.OnNavigatedFromAsync();
            newActivation = model.OnNavigatedToAsync();
            await catalog.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await oldLoad.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(catalog.FirstToken.IsCancellationRequested);
            Assert.False(catalog.SecondToken.IsCancellationRequested);
            Assert.True(model.IsCatalogLoading); Assert.False(newActivation.IsCompleted);
            catalog.First.SetResult(Snapshot(42));
            Assert.Empty(model.CatalogItems); Assert.True(model.IsCatalogLoading);
            catalog.Second.SetResult(Snapshot(43));
            await newActivation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(model.CatalogItems, item => item.AppId == 43);
            Assert.DoesNotContain(model.CatalogItems, item => item.AppId == 42);
            Assert.False(model.IsCatalogLoading);
        }
        finally
        {
            catalog.First.TrySetResult(Snapshot(42)); catalog.Second.TrySetResult(Snapshot(43));
            await oldLoad.WaitAsync(TimeSpan.FromSeconds(5));
            if (newActivation is not null) await newActivation.WaitAsync(TimeSpan.FromSeconds(5));
            await model.OnNavigatedFromAsync();
        }
    }

    [Fact]
    public async Task LateReleaseFeedCannotRecreateAnInactiveSearchIndex()
    {
        var spotlight = new GatedSpotlight(); var model = CreateModel(spotlight: spotlight); Seed(model);
        await Search(model, "Fixture");
        var load = (Task)typeof(Steamy.ViewModels.LibraryViewModel).GetMethod("RefreshReleaseFilterAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(model, [true])!;
        try
        {
            await spotlight.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.OnNavigatedFromAsync();
            Assert.True(spotlight.Token.IsCancellationRequested);
            await load.WaitAsync(TimeSpan.FromSeconds(1));
            spotlight.Response.SetResult(SpotlightSnapshot.Empty);
            Assert.Null(typeof(Steamy.ViewModels.LibraryViewModel).GetField("_searchIndexTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(model));
            Assert.False(model.IsSearchBusy); Assert.Equal("Fixture", model.SearchText);
        }
        finally
        { spotlight.Response.TrySetResult(SpotlightSnapshot.Empty); await load.WaitAsync(TimeSpan.FromSeconds(5)); await model.OnNavigatedFromAsync(); }
    }

    [Fact]
    public async Task LeavingRecommendationsStopsWaitingForUnresponsiveMetadata()
    {
        var catalog = new GatedDetailsCatalog(); var model = CreateModel(catalog); Seed(model);
        var load = (Task)typeof(Steamy.ViewModels.LibraryViewModel).GetMethod("PrepareRecommendationsAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(model, null)!;
        try
        {
            await catalog.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.OnNavigatedFromAsync();
            await load.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.InRange(catalog.Calls, 1, 4);
            Assert.Equal(new[] { "All genres" }, model.GenreFilters);
            Assert.False(catalog.Response.Task.IsCompleted);
        }
        finally
        { catalog.Response.TrySetResult(true); await load.WaitAsync(TimeSpan.FromSeconds(5)); await model.OnNavigatedFromAsync(); }
    }

    private static SteamCatalogSnapshot Snapshot(int id) => new(true,
        [new() { AppId = id, Name = $"Fixture {id}", AppType = SteamCatalogAppType.Game, IsReleaseVerified = true, HasDetails = true }], DateTimeOffset.UtcNow, false, "Fixture");

    private sealed class GatedCatalog : Catalog
    {
        internal readonly TaskCompletionSource<SteamCatalogSnapshot> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FirstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<SteamCatalogSnapshot> Second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SecondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken FirstToken, SecondToken;
        private int _calls;
        // Deliberately return late even when the consumer's cancellation is requested.
        public override Task<SteamCatalogSnapshot> GetCatalogAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1) { FirstToken = cancellationToken; FirstStarted.TrySetResult(); return First.Task; }
            SecondToken = cancellationToken; SecondStarted.TrySetResult(); return Second.Task;
        }
    }
    private sealed class GatedSpotlight : ISpotlightService
    {
        public SpotlightSnapshot Cached => SpotlightSnapshot.Empty;
        internal readonly TaskCompletionSource<SpotlightSnapshot> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        public Task<SpotlightSnapshot> GetAsync(bool force = false, CancellationToken cancellationToken = default)
        { Token = cancellationToken; Started.TrySetResult(); return Response.Task; }
    }
    private sealed class GatedDetailsCatalog : Catalog
    {
        internal readonly TaskCompletionSource<bool> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public override Task<bool> EnsureDetailsAsync(SteamCatalogItem item, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); Started.TrySetResult(); return Response.Task; }
    }
}
