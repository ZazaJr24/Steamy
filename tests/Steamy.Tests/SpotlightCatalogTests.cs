using System.Net;
using System.Text;
using System.Text.Json;
using Steamy.Services;

namespace Steamy.Tests;

public sealed class SpotlightCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Steamy-spotlight-tests-" + Guid.NewGuid().ToString("N"));
    private string CachePath => Path.Combine(_directory, "spotlight.json");
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static SpotlightGame Game(int id = 123) => new()
    {
        AppId = id, Name = "An announced game", Description = "An adventure", Publisher = "A studio",
        ComingSoon = true, ReleaseLabel = "To be announced",
        StoreUrl = $"https://store.steampowered.com/app/{id}/",
        HeroUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{id}/hash/page_bg_raw.jpg",
        HeaderUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{id}/hash/header.jpg"
    };
    private static byte[] Feed(DateTimeOffset? updated = null, params SpotlightGame[] games) => JsonSerializer.SerializeToUtf8Bytes(
        new { schemaVersion = 1, updatedAt = updated ?? Now, games = games.Length == 0 ? new[] { Game() } : games },
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    [Fact]
    public void UpcomingMetadataPreservesUnknownDatesAndOfficialArtwork()
    {
        var snapshot = SpotlightCatalogService.Parse(Feed());
        Assert.Equal(Now, snapshot.UpdatedAt);
        var game = Assert.Single(snapshot.Games);
        Assert.True(game.ComingSoon);
        Assert.Null(game.ReleaseDate);
        Assert.Equal("To be announced", game.ReleaseLabel);
        Assert.Contains("/hash/", game.HeaderUrl);
    }

    [Fact]
    public void InvalidOrDuplicateGamesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => SpotlightCatalogService.Parse(Feed(null, Game(), Game())));
        Assert.Throws<InvalidDataException>(() => SpotlightCatalogService.Parse(Feed(null, Game() with { StoreUrl = "https://example.com/" })));
        Assert.Throws<InvalidDataException>(() => SpotlightCatalogService.Parse(Feed(null, Game() with { HeroUrl = "https://steamstatic.com.example.org/art.jpg" })));
        Assert.Throws<InvalidDataException>(() => SpotlightCatalogService.Parse(Encoding.UTF8.GetBytes("null")));
        Assert.Throws<InvalidDataException>(() => SpotlightCatalogService.Parse(Feed(null, Game() with { Description = null! })));
    }

    [Fact]
    public async Task ConcurrentRefreshIsSharedAndCallerCancellationDoesNotCancelTheFeed()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(_ => pending.Task);
        using var http = new HttpClient(handler);
        using var service = new SpotlightCatalogService(http, CachePath, () => Now, SpotlightSnapshot.Empty);
        var first = service.GetAsync();
        using var cancellation = new CancellationTokenSource();
        var second = service.GetAsync(cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        pending.SetResult(Response(Feed()));
        Assert.Single((await first).Games);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(123, Assert.Single((await service.GetAsync()).Games).AppId);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(123, Assert.Single(SpotlightCatalogService.Parse(await File.ReadAllBytesAsync(CachePath)).Games).AppId);
    }

    [Fact]
    public async Task RefreshExpiresAfterSixHoursAndForceBypassesTheInterval()
    {
        var clock = Now;
        var handler = new Handler(_ => Task.FromResult(Response(Feed())));
        using var http = new HttpClient(handler);
        using var service = new SpotlightCatalogService(http, CachePath, () => clock, SpotlightSnapshot.Empty);
        await service.GetAsync();
        clock = clock.AddHours(5);
        await service.GetAsync();
        Assert.Equal(1, handler.Requests);
        await service.GetAsync(force: true);
        Assert.Equal(2, handler.Requests);
        clock = clock.AddHours(6);
        await service.GetAsync();
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task OfflineStartupKeepsTheLastGoodDiskFeed()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(CachePath, Feed(null, Game(456)));
        var handler = new Handler(_ => throw new HttpRequestException("Offline"));
        using var http = new HttpClient(handler);
        using var service = new SpotlightCatalogService(http, CachePath, () => Now,
            new SpotlightSnapshot(Now.AddDays(-1), new[] { Game() }));
        Assert.Equal(456, Assert.Single(service.Cached.Games).AppId);
        Assert.Equal(456, Assert.Single((await service.GetAsync()).Games).AppId);
        await service.GetAsync();
        Assert.Equal(1, handler.Requests); // Failure is also debounced.
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("oversized")]
    [InlineData("older")]
    public async Task FailedRefreshNeverReplacesTheBundledOrSavedFeed(string failure)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(CachePath, "corrupt cached data");
        var response = failure switch
        {
            "oversized" => new byte[300_000],
            "older" => Feed(Now.AddDays(-1), Game(456)),
            _ => Encoding.UTF8.GetBytes("{\"schemaVersion\":\"wrong\"}")
        };
        var handler = new Handler(_ => Task.FromResult(Response(response)));
        using var http = new HttpClient(handler);
        using var service = new SpotlightCatalogService(http, CachePath, () => Now, new SpotlightSnapshot(Now, new[] { Game() }));
        Assert.Equal(123, Assert.Single((await service.GetAsync()).Games).AppId);
        Assert.Equal("corrupt cached data", await File.ReadAllTextAsync(CachePath));
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("raw.githubusercontent.com", request.RequestUri!.Host);
            return respond(cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
