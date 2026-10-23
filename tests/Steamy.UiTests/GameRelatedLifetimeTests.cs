using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class GameDetailsTests
{
    [Fact]
    public async Task RelatedGameSelectionDoesNotEnumerateOnTheCallingThread()
    {
        var items = new ProbedCandidates();
        using var catalog = new ProbedRelatedCatalog(items);
        var model = Model(new RelatedData(), new Preferences(), catalog);
        var called = new TaskCompletionSource<(int Thread, Task Work)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            try { called.TrySetResult((Environment.CurrentManagedThreadId, SelectRelated(model, new GameOverview(Developer: "Missing studio") { Genres = "Action" }))); }
            catch (Exception error) { called.TrySetException(error); }
        }) { IsBackground = true };
        caller.SetApartmentState(ApartmentState.STA); caller.Start();
        var request = await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await request.Work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(caller.Join(TimeSpan.FromSeconds(5)));
        Assert.NotEmpty(items.Threads);
        Assert.DoesNotContain(request.Thread, items.Threads);
        Assert.Equal(Enumerable.Range(100, 6), model.SimilarGames.Select(item => item.AppId));
    }

    [Fact]
    public async Task CancellationWhileScanningRecommendationsPreventsPublicationAndArtworkRequests()
    {
        using var cancellation = new CancellationTokenSource();
        var items = new ProbedCandidates { Visiting = index => { if (index == 128) cancellation.Cancel(); } };
        using var catalog = new ProbedRelatedCatalog(items);
        var model = Model(new RelatedData(), new Preferences(), catalog);
        await SelectRelated(model, new GameOverview(Developer: "Missing studio") { Genres = "Action" }, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Empty(model.DeveloperGames);
        Assert.Empty(model.SimilarGames);
        Assert.Empty(catalog.ArtworkRequests);
    }

    private static Task SelectRelated(GameDetailsViewModel model, GameOverview overview, CancellationToken token = default) =>
        (Task)typeof(GameDetailsViewModel).GetMethod("LoadRelatedGamesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [42, overview, 0, token])!;

    private sealed class ProbedRelatedCatalog(IReadOnlyList<SteamCatalogItem> items) : RelatedCatalog
    {
        public override Task<IReadOnlyList<SteamCatalogItem>> GetRecommendationCandidatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(items);
    }

    private sealed class ProbedCandidates : IReadOnlyList<SteamCatalogItem>
    {
        private readonly SteamCatalogItem _item = new() { AppId = 999, AppType = SteamCatalogAppType.Game, DevelopersDisplay = "Other studio", GenresDisplay = "Strategy · Simulation" };
        internal ConcurrentBag<int> Threads { get; } = [];
        internal Action<int>? Visiting { get; init; }
        public int Count => 20000;
        public SteamCatalogItem this[int index]
        {
            get
            {
                Threads.Add(Environment.CurrentManagedThreadId); Visiting?.Invoke(index);
                return index < 6 ? new SteamCatalogItem { AppId = index + 100, AppType = SteamCatalogAppType.Game, GenresDisplay = "Action · Adventure" } : _item;
            }
        }
        public IEnumerator<SteamCatalogItem> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

