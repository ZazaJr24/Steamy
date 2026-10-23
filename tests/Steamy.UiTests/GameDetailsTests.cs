using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media.Imaging;
using Steamy.Models;
using Steamy.Playtime;
using Steamy.Services;
using Steamy.ViewModels;
using Steamy.Controls;
using System.Windows.Media;

namespace Steamy.UiTests;

public sealed partial class GameDetailsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicGameInformationLoadsWithoutCredentialsAndSurvivesNewsOutage(bool newsOffline)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-info-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new OverviewHandler { NewsOffline = newsOffline };
            using var http = new HttpClient(handler);
            using var catalog = new SteamCatalogService(http, directory);
            using var service = new SteamGameDetailsService(new Settings(), new Credentials(), http, catalog);
            var value = await service.ReadOverviewAsync(1808500);
            Assert.Equal("Actual store summary", value.Description);
            Assert.Equal("Studio", value.Developer); Assert.Equal("30 Oct, 2025", value.ReleaseDate);
            Assert.Contains("Explore the surface", value.DetailedDescription);
            Assert.DoesNotContain("<", value.DetailedDescription); Assert.DoesNotContain("script", value.DetailedDescription);
            Assert.Contains("Windows 10\n• Memory: 16 GB", value.SystemRequirements);
            Assert.Equal("English, German", value.Languages);
            Assert.Single(value.Screenshots); Assert.Equal("cdn.akamai.steamstatic.com", value.Screenshots[0].Thumbnail.Host);
            Assert.Equal(newsOffline ? 0 : 1, value.News.Count);
            Assert.All(handler.Requests, uri => Assert.DoesNotContain("key=", uri.Query));
            Assert.NotNull(value.Reviews); Assert.Equal(95, value.Reviews.PositivePercent); Assert.Equal(1000, value.Reviews.Total);
            Assert.Equal(value, await service.ReadOverviewAsync(1808500)); Assert.Equal(3, handler.Requests.Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class OverviewHandler : HttpMessageHandler
    {
        public bool NewsOffline;
        public System.Collections.Concurrent.ConcurrentBag<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath.StartsWith("/appreviews/", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"success":1,"query_summary":{"total_positive":950,"total_reviews":1000,"review_score_desc":"Very Positive"}}""") });
            var news = request.RequestUri!.AbsolutePath.Contains("GetNewsForApp", StringComparison.Ordinal);
            if (news && NewsOffline) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var json = news
                ? """{"appnews":{"newsitems":[{"title":"Official update","contents":"<p>New content</p>","date":1700000000,"url":"https://store.steampowered.com/news/app/1808500"},{"title":"Invalid link","url":"javascript:alert(1)"}]}}"""
                : """{"1808500":{"success":true,"data":{"type":"game","short_description":"Actual store summary","about_the_game":"<h2>Explore the surface</h2><p>Prepare your loadout.</p><script>ignore this script</script>","developers":["Studio"],"publishers":["Publisher"],"release_date":{"date":"30 Oct, 2025","coming_soon":false},"supported_languages":"English, <b>German</b>","genres":[{"description":"Action"}],"categories":[{"description":"Co-op"}],"pc_requirements":{"minimum":"<b>Windows 10</b><ul><li>Memory: 16 GB</li></ul>"},"screenshots":[{"path_thumbnail":"https://cdn.akamai.steamstatic.com/apps/1808500/thumb.jpg","path_full":"https://cdn.akamai.steamstatic.com/apps/1808500/full.jpg"},{"path_thumbnail":"https://example.com/foreign.jpg","path_full":"https://example.com/foreign-full.jpg"}]}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
    [Fact]
    public void SteamLogoPositioningCanvasIsRemovedWithoutChangingLogoPixels()
    {
        var pixels = new byte[100 * 80 * 4];
        for (var y = 50; y < 70; y++) for (var x = 20; x < 80; x++)
        { pixels[(y * 100 + x) * 4] = 200; pixels[(y * 100 + x) * 4 + 3] = 255; }
        var source = BitmapSource.Create(100, 80, 96, 96, PixelFormats.Bgra32, null, pixels, 400); source.Freeze();
        var logo = GameLogoLayout.Trim(source)!;
        Assert.Equal(60, logo.PixelWidth); Assert.Equal(20, logo.PixelHeight); Assert.True(logo.IsFrozen);
        Assert.Equal(100, source.PixelWidth);
        var output = new byte[60 * 20 * 4]; logo.CopyPixels(output, 240, 0);
        Assert.Equal((byte)200, output[0]); Assert.Equal((byte)255, output[3]);
    }
    [Fact]
    public async Task SteamAchievementsUseActualPlayerUnlocksAndSchemaIcons()
    {
        var configuration = new Settings(); var credentials = new Credentials { Value = new string('A', 32) };
        using var handler = new ApiHandler(); using var http = new HttpClient(handler);
        using var service = new SteamGameDetailsService(configuration, credentials, http);
        var snapshot = await service.ReadAchievementsAsync(1808500);
        Assert.True(snapshot.Available); Assert.Equal(2, snapshot.Items.Count);
        Assert.True(snapshot.Items[0].Unlocked); Assert.Equal("First extraction", snapshot.Items[0].Name);
        Assert.Equal("https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/1808500/unlocked.jpg", snapshot.Items[0].IconUrl);
        Assert.False(snapshot.Items[1].Unlocked); Assert.Equal("", snapshot.Items[1].IconUrl); // Foreign schema icon URL is rejected.
        Assert.All(handler.Requests, request => Assert.Equal("api.steampowered.com", request.Host));
        Assert.Equal(2, handler.Requests.Count);
    }
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(-42, true, false)]
    [InlineData(42, false, false)]
    [InlineData(42, true, true)]
    public async Task MissingOrPrivateDataDoesNotMasqueradeAsZeroUnlocked(int appId, bool connected, bool privateProfile)
    {
        using var handler = new ApiHandler { Private = privateProfile }; using var http = new HttpClient(handler);
        using var service = new SteamGameDetailsService(new Settings(), new Credentials { Value = connected ? new string('A', 32) : null }, http);
        var result = await service.ReadAchievementsAsync(appId);
        Assert.False(result.Available); Assert.Empty(result.Items); Assert.NotEmpty(result.Message);
        Assert.Equal(privateProfile ? 1 : 0, handler.Requests.Count);
    }
    [Fact]
    public async Task CancelingPlayerLookupPropagatesCancellation()
    {
        using var http = new HttpClient(new ApiHandler()); using var service = new SteamGameDetailsService(new Settings(), new Credentials { Value = new string('A', 32) }, http);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAchievementsAsync(42, cancellation.Token));
    }
    [Fact]
    public async Task GameSwitchDiscardsLateDescriptionAndKeepsEachGamesNotes()
    {
        var preferences = new Preferences(); var data = new DeferredData();
        var model = Model(data, preferences);
        model.SelectGame(Game(42)); var first = model.ActivateAsync();
        await WaitForPreferencesAsync(model);
        Assert.True(model.CanEditPreferences);
        model.Notes = "Notes for the first game";
        model.SelectGame(Game(-42, local: true)); await model.ActivateAsync();
        Assert.Equal("Second game description", model.Description); Assert.Empty(model.Notes);
        data.First.SetResult(new("Late first game description")); await first;
        Assert.Equal("Second game description", model.Description); Assert.Equal(-42, model.Game!.AppId);
        Assert.Equal("Notes for the first game", preferences.Values[42].Notes);
        model.Notes = "Notes for local game"; await model.DeactivateAsync();
        Assert.Equal("Notes for local game", preferences.Values[-42].Notes);
    }
    [Theory]
    [InlineData(-42)]
    [InlineData(1808500)]
    public async Task LauncherPassesLocalArgumentsDirectlyAndRecordsOnlyAcceptedRequests(int appId)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-launch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "game.exe"); await File.WriteAllTextAsync(executable, "fixture; never executed");
            var game = new Game { AppId = appId, Name = "Local game", InstallState = GameInstallState.Installed, IsLocalDownload = true, LaunchExecutable = executable, InstallFolder = directory };
            var store = new AppDataStore(); store.Games.Add(game);
            var preferences = new Preferences(); preferences.Values[appId] = new("", "--profile \"a&b\"");
            var starter = new Starter(); var time = new Time(); var activity = new JsonGameActivityService(Path.Combine(directory, "activity.json"));
            var launcher = new GameLaunchService(store, new Settings(directory), time, activity, preferences, starter, Log());
            Assert.True((await launcher.LaunchAsync(game)).Succeeded);
            Assert.Equal(executable, starter.Request!.FileName); Assert.Equal(directory, starter.Request.WorkingDirectory);
            Assert.Equal("--profile \"a&b\"", starter.Request.Arguments); Assert.Equal(1, time.Configurations);
            Assert.Equal(appId, Assert.Single((await activity.GetAsync()).Launches).AppId);
            var alternate = Path.Combine(directory, "alternate.exe"); await File.WriteAllTextAsync(alternate, "fixture; never executed");
            preferences.Values[appId] = new("", "--profile 2", alternate);
            Assert.True((await launcher.LaunchAsync(game)).Succeeded);
            Assert.Equal(alternate, starter.Request.FileName);
            Assert.Null(GameLaunchService.BuildRequest(game, "", executableOverride: Path.Combine(directory, "..", "outside.exe")));
            store.Games.Clear(); starter.Request = null;
            Assert.False((await launcher.LaunchAsync(game)).Succeeded); Assert.Null(starter.Request);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void SteamLaunchArgumentsUseSteamExecutableAndOrdinaryLaunchUsesProtocol()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-steam-launch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "steam.exe"); File.WriteAllText(executable, "fixture; never executed");
            var ordinary = GameLaunchService.BuildRequest(Game(1808500), "");
            Assert.Equal("steam://rungameid/1808500", ordinary!.FileName);
            var options = GameLaunchService.BuildRequest(Game(1808500), "-windowed --profile \"a&b\"", directory);
            Assert.Equal(executable, options!.FileName); Assert.Equal("-applaunch 1808500 -windowed --profile \"a&b\"", options.Arguments);
            Assert.Null(GameLaunchService.BuildRequest(Game(1808500), "-windowed", Path.Combine(directory, "missing")));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task DeveloperRecommendationsPreserveCompanyNamesAndLoadOnlySelectedArtwork()
    {
        using var catalog = new RelatedCatalog();
        var data = new RelatedData();
        var model = Model(data, new Preferences(), catalog);
        model.SelectGame(Game(42));
        await model.ActivateAsync();
        await catalog.ArtworkReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 43 }, model.DeveloperGames.Select(item => item.AppId));
        Assert.Equal(new[] { 45 }, model.SimilarGames.Select(item => item.AppId));
        Assert.Equal(new[] { 43, 45 }, catalog.ArtworkRequests.Order());
        Assert.Contains("Studio%2C%20Ltd.", model.DeveloperStoreUri!.AbsoluteUri);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<SteamCatalogItem>)model.OpenRelatedGameCommand).ExecuteAsync(model.DeveloperGames.Single());
        Assert.Equal(43, model.Game!.AppId);
        Assert.Equal("Description", model.Description);
        Assert.True(model.CanEditPreferences); // Same-page navigation must activate the new game.
        await model.DeactivateAsync();
    }
    private sealed class RelatedData : IGameDetailsDataService
    {
        public Task<GameOverview> ReadOverviewAsync(int appId, CancellationToken token = default) => Task.FromResult(new GameOverview("Description", "Studio, Ltd.", "Publisher") { Genres = "Action" });
        public Task<GameAchievementSnapshot> ReadAchievementsAsync(int appId, CancellationToken token = default) => Task.FromResult(GameAchievementSnapshot.Unavailable("Unavailable"));
    }
    private class RelatedCatalog : ISteamCatalogService
    {
        public List<int> ArtworkRequests { get; } = [];
        public TaskCompletionSource ArtworkReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<int> DetailRequests { get; } = [];
        public SteamCatalogDetails? Details { get; set; }
        public Task<SteamCatalogSnapshot> GetCatalogAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Game recommendations must not request the full catalog.");
        public virtual Task<IReadOnlyList<SteamCatalogItem>> GetRecommendationCandidatesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SteamCatalogItem>>(
            [new() { AppId = 42, DevelopersDisplay = "Studio, Ltd.", AppType = SteamCatalogAppType.Game },
             new() { AppId = 43, DevelopersDisplay = "Studio, Ltd.", AppType = SteamCatalogAppType.Game },
             new() { AppId = 44, DevelopersDisplay = "Studio, Corp.", AppType = SteamCatalogAppType.Game },
             new() { AppId = 45, DevelopersDisplay = "Another", GenresDisplay = "Action · Adventure", AppType = SteamCatalogAppType.Game },
             new() { AppId = 46, DevelopersDisplay = "Studio, Ltd.", AppType = SteamCatalogAppType.Dlc }]);
        public Task<bool> EnsureArtworkAsync(SteamCatalogItem item, bool includeHeader = false, CancellationToken cancellationToken = default)
        { ArtworkRequests.Add(item.AppId); if (ArtworkRequests.Count >= 2) ArtworkReady.TrySetResult(); return Task.FromResult(true); }
        public virtual Task<SteamCatalogDetails?> GetDetailsAsync(int appId, CancellationToken cancellationToken = default)
        { DetailRequests.Add(appId); return Task.FromResult(Details); }
        public Task<bool> EnsureDetailsAsync(SteamCatalogItem item, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> EnsureScreenshotsAsync(SteamCatalogItem item, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task PrepareReleaseStatusAsync(IReadOnlyList<SteamCatalogItem> items, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }
    private static Game Game(int appId, bool local = false) => new() { AppId = appId, Name = appId.ToString(), IsLocalDownload = local, InstallState = GameInstallState.Installed };
    private static ILoggingService Log() => (ILoggingService)OfflineServiceProxy.Create(typeof(ILoggingService));
    private static GameDetailsViewModel Model(IGameDetailsDataService data, IGamePreferencesStore preferences, ISteamCatalogService? catalog = null, IArtworkService? artwork = null, IPlayTimeService? playtime = null, IGameActivityService? activity = null) => new(new AppDataStore(), new NavigationService(), Log(), artwork ?? new Artwork(), data, preferences,
        (IGameLaunchService)OfflineServiceProxy.Create(typeof(IGameLaunchService)), playtime ?? new Time(), activity ?? new Activity(), new Credentials(), catalog);
    private sealed class Settings(string? directory = null) : ISettingsService
    {
        private readonly AppSettings _value = new() { SteamId64 = "76561197960265729", SteamLibraryPath = directory ?? "" };
        public AppSettings Load() => _value;
        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Credentials : ISecureCredentialService
    {
        public string? Value;
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default) { Value = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { Value = null; return Task.CompletedTask; }
    }
    private sealed class ApiHandler : HttpMessageHandler
    {
        public bool Private;
        public System.Collections.Concurrent.ConcurrentBag<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri!);
            var json = request.RequestUri!.AbsolutePath.Contains("GetSchemaForGame", StringComparison.Ordinal)
                ? """{"game":{"availableGameStats":{"achievements":[{"name":"first","icon":"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/1808500/unlocked.jpg","icongray":"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/1808500/locked.jpg"},{"name":"second","icon":"https://example.com/icon.jpg","icongray":"https://example.com/locked.jpg"}]}}}"""
                : Private ? """{"playerstats":{"success":false,"error":"Profile is not public"}}"""
                : """{"playerstats":{"success":true,"achievements":[{"apiname":"first","achieved":1,"unlocktime":1700000000,"name":"First extraction","description":"Extract once"},{"apiname":"second","achieved":0,"unlocktime":0,"name":"Return","description":"Return to the surface"}]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
    private sealed class Preferences : IGamePreferencesStore
    {
        public Dictionary<int, GamePreferences> Values { get; } = new();
        public Task<GamePreferences> ReadAsync(int appId, CancellationToken token = default) => Task.FromResult(Values.GetValueOrDefault(appId) ?? new());
        public Task SaveAsync(int appId, GamePreferences value, CancellationToken token = default) { Values[appId] = value; return Task.CompletedTask; }
    }
    private sealed class DeferredData : IGameDetailsDataService
    {
        public TaskCompletionSource<GameOverview> First { get; } = new();
        public Task<GameOverview> ReadOverviewAsync(int appId, CancellationToken token = default) => appId == 42 ? First.Task : Task.FromResult(new GameOverview("Second game description"));
        public Task<GameAchievementSnapshot> ReadAchievementsAsync(int appId, CancellationToken token = default) => Task.FromResult(GameAchievementSnapshot.Unavailable("Unavailable"));
    }
    private sealed class Artwork : IArtworkService
    {
        public Task<BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default) => Task.FromResult<BitmapImage?>(null);
        public Task LoadAsync(Game game, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadHeadersAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadManyAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Time : IPlayTimeService
    {
        public int Configurations;
        public Task<PlayTimeSnapshot> ReadAsync(CancellationToken token = default) => Task.FromResult(PlayTimeSnapshot.Empty);
        public Task ConfigureAsync(AppSettings settings, IEnumerable<Game> games) { Configurations++; return Task.CompletedTask; }
    }
    private sealed class Activity : IGameActivityService
    {
        public event EventHandler? Changed { add { } remove { } }
        public Task<GameActivitySnapshot> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(GameActivitySnapshot.Empty);
        public Task<bool> ToggleFavoriteAsync(int appId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task RecordLaunchAsync(int appId, DateTimeOffset? launchedAt = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Starter : IGameProcessStarter { public ProcessStartInfo? Request; public void Start(ProcessStartInfo request) => Request = request; }
}
