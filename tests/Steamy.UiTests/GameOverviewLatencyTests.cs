using System.IO;
using System.Net;
using System.Net.Http;
using Steamy.Services;
using Steamy.Models;
using System.Runtime.CompilerServices;

namespace Steamy.UiTests;

public sealed partial class GameDetailsTests
{
    [Fact]
    public async Task GameDescriptionAndPreviewDoNotWaitForNewsOrReviewServers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-progressive-info-" + Guid.NewGuid().ToString("N"));
        using var handler = new DelayedOverviewHandler(); using var http = new HttpClient(handler);
        using var catalog = new SteamCatalogService(http, directory);
        using var service = new SteamGameDetailsService(new Settings(), new Credentials(), http, catalog);
        Assert.NotNull(await catalog.GetDetailsAsync(1808500)); // Seed the real disk cache before opening the page model.
        var model = Model(service, new Preferences());
        model.SelectGame(Game(1808500));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, _) => { if (model.Description == "Ready cached description" && model.Screenshots.Count == 1) ready.TrySetResult(); };
        var activation = model.ActivateAsync();
        try
        {
            await Task.WhenAll(handler.NewsStarted.Task, handler.ReviewsStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Ready cached description", model.Description);
            Assert.Single(model.Screenshots);
            Assert.False(activation.IsCompleted);
            await WaitForPreferencesAsync(model);
            Assert.True(model.CanEditPreferences);
            Assert.Equal(1, handler.CatalogRequests);
            var galleryChanges = 0;
            model.Screenshots.CollectionChanged += (_, _) => galleryChanges++;
            var reviewsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            model.PropertyChanged += (_, _) => { if (model.Reviews is not null) reviewsReady.TrySetResult(); };
            handler.ReleaseReviews.TrySetResult();
            await reviewsReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(95, model.Reviews!.PositivePercent);
            Assert.Empty(model.News); Assert.False(activation.IsCompleted);
            Assert.Equal(0, galleryChanges);
            handler.ReleaseNews.TrySetResult();
            await activation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(model.News); Assert.Equal(0, galleryChanges);
        }
        finally
        {
            handler.ReleaseNews.TrySetResult(); handler.ReleaseReviews.TrySetResult();
            await activation.WaitAsync(TimeSpan.FromSeconds(5)); await model.DeactivateAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
    [Fact]
    public async Task LeavingTheOverviewCancelsBothOptionalRequests()
    {
        using var handler = new DelayedOverviewHandler(); using var http = new HttpClient(handler);
        var catalog = new RelatedCatalog { Details = ReadyDetails };
        using var service = new SteamGameDetailsService(new Settings(), new Credentials(), http, catalog);
        var updates = service.ReadOverviewUpdatesAsync(1808500).GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("Ready details", updates.Current.Description);
        await Task.WhenAll(handler.NewsStarted.Task, handler.ReviewsStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await updates.DisposeAsync();
        await Task.WhenAll(handler.NewsCancelled.Task, handler.ReviewsCancelled.Task).WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task CompletedOverviewCacheIsBoundedAndRecentlyViewedGamesStayWarm()
    {
        using var handler = new DelayedOverviewHandler();
        handler.ReleaseNews.TrySetResult(); handler.ReleaseReviews.TrySetResult();
        using var http = new HttpClient(handler);
        var catalog = new RelatedCatalog { Details = ReadyDetails };
        using var service = new SteamGameDetailsService(new Settings(), new Credentials(), http, catalog);
        for (var appId = 1; appId <= 64; appId++) await service.ReadOverviewAsync(appId);
        Assert.Equal(64, catalog.DetailRequests.Count);
        await service.ReadOverviewAsync(1); await service.ReadOverviewAsync(65); await service.ReadOverviewAsync(1);
        Assert.Equal(65, catalog.DetailRequests.Count);
        await service.ReadOverviewAsync(2); Assert.Equal(66, catalog.DetailRequests.Count);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadOverviewAsync(1, cancelled.Token));
        Assert.Equal(66, catalog.DetailRequests.Count);
    }
    [Fact]
    public async Task LateProgressiveUpdatesCannotReplaceTheSelectedGamesInformation()
    {
        var data = new DelayedProgressiveData(); var model = Model(data, new Preferences());
        model.SelectGame(Game(42)); var first = model.ActivateAsync();
        Assert.Equal("First game", model.Description);
        var developerLink = model.DeveloperStoreUri;
        Assert.NotNull(developerLink);
        model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(model.DeveloperStoreUri)) developerLink = model.DeveloperStoreUri; };
        model.SelectGame(Game(-42, local: true)); await model.ActivateAsync();
        Assert.Equal("Second game", model.Description);
        Assert.Null(developerLink);
        data.Release.TrySetResult(); await first;
        Assert.Equal("Second game", model.Description); Assert.Empty(model.News); Assert.Null(model.Reviews);
        await model.DeactivateAsync();
    }
    private static SteamCatalogDetails ReadyDetails => new(SteamCatalogAppType.Game, "", "", "", "", "Ready details", ["Studio"], ["Publisher"], "", "", false, "Action", [], "", "");
    private sealed class DelayedProgressiveData : IGameDetailsDataService, IProgressiveGameDetailsDataService
    {
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GameOverview> ReadOverviewAsync(int appId, CancellationToken token = default) => throw new InvalidOperationException("Progressive data must be used.");
        public Task<GameAchievementSnapshot> ReadAchievementsAsync(int appId, CancellationToken token = default)
            => Task.FromResult(GameAchievementSnapshot.Unavailable("No fixture achievements"));
        public async IAsyncEnumerable<GameOverview> ReadOverviewUpdatesAsync(int appId, [EnumeratorCancellation] CancellationToken token = default)
        {
            if (appId != 42) { yield return new("Second game"); yield break; }
            yield return new("First game", "Studio") { DeveloperStoreName = "Studio" };
            // Deliberately ignore cancellation to exercise the view model's own revision guard.
            await Release.Task;
            yield return new("Late first game") { Reviews = new(1, 2, "Mixed") };
        }
    }
    private sealed class DelayedOverviewHandler : HttpMessageHandler
    {
        internal int CatalogRequests;
        internal TaskCompletionSource NewsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReviewsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseNews { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseReviews { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource NewsCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReviewsCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string json;
            if (request.RequestUri!.AbsolutePath.Contains("GetNewsForApp", StringComparison.Ordinal))
            {
                NewsStarted.TrySetResult();
                try { await ReleaseNews.Task.WaitAsync(token); } catch (OperationCanceledException) { NewsCancelled.TrySetResult(); throw; }
                json = """{"appnews":{"newsitems":[{"title":"Public update","contents":"New content","date":1700000000,"url":"https://store.steampowered.com/news/app/1808500"}]}}""";
            }
            else if (request.RequestUri.AbsolutePath.Contains("appreviews", StringComparison.Ordinal))
            {
                ReviewsStarted.TrySetResult();
                try { await ReleaseReviews.Task.WaitAsync(token); } catch (OperationCanceledException) { ReviewsCancelled.TrySetResult(); throw; }
                json = """{"success":1,"query_summary":{"total_positive":95,"total_reviews":100,"review_score_desc":"Very Positive"}}""";
            }
            else
            {
                Interlocked.Increment(ref CatalogRequests);
                json = """{"1808500":{"success":true,"data":{"type":"game","short_description":"Ready cached description","developers":["Studio"],"screenshots":[{"path_thumbnail":"https://cdn.akamai.steamstatic.com/apps/1808500/thumb.jpg","path_full":"https://cdn.akamai.steamstatic.com/apps/1808500/full.jpg"}]}}}""";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
