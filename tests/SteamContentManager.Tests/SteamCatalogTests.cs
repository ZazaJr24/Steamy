using System.Net;
using System.Net.Http;
using System.Text;
using SteamContentManager.Models;
using SteamContentManager.Services;
using Xunit;

namespace SteamContentManager.Tests;

public sealed class SteamCatalogTests
{
    [Fact]
    public async Task GetCatalogAsync_ParsesAndDeduplicatesPublicAppList()
    {
        const string json = """
            {
              "applist": {
                "apps": [
                  { "appid": 730, "name": "Counter-Strike 2" },
                  { "appid": 730, "name": "Duplicate title" },
                  { "appid": 570, "name": "Dota 2" },
                  { "appid": 0, "name": "Invalid" },
                  { "appid": 1, "name": "" }
                ]
              }
            }
            """;
        using var handler = new QueueHandler(_ => JsonResponse(json));
        using var client = new HttpClient(handler);
        var cache = CreateTempDirectory();
        // Hermetic: the bundled Data/appid.json shipped with the app now takes precedence over
        // the stubbed HTTP handler during catalog initialization. Use a real bundled JSON in the
        // temp cache so the service is still fully hermetic.
        var bundledPath = Path.Combine(cache, "appid.json");
        await File.WriteAllTextAsync(bundledPath, "[{\"appid\":730,\"name\":\"Counter-Strike 2\"},{\"appid\":570,\"name\":\"Dota 2\"}]", CancellationToken.None);
        using var service = new SteamCatalogService(client, cache, bundledAppIdJsonPath: bundledPath);

        var snapshot = await service.GetCatalogAsync(forceRefresh: true);

        Assert.True(snapshot.Succeeded);
        Assert.False(snapshot.FromCache);
        Assert.Equal(2, snapshot.Items.Count);
        Assert.Equal(new[] { "Counter-Strike 2", "Dota 2" }, snapshot.Items.Select(item => item.Name));
        Assert.Equal(730, snapshot.Items[0].AppId);
        Assert.Contains("header.jpg", snapshot.Items[0].HeaderImageUrl, StringComparison.Ordinal);
        DeleteDirectory(cache);
    }

    [Fact]
    public async Task GetCatalogAsync_UsesFreshCacheWithoutAnotherHttpRequest()
    {
        const string json = "{\"applist\":{\"apps\":[{\"appid\":730,\"name\":\"Counter-Strike 2\"}]}}";
        using var handler = new QueueHandler(request => IsGithubRequest(request)
            ? throw new HttpRequestException("github offline")
            : JsonResponse(json));
        using var client = new HttpClient(handler);
        var cache = CreateTempDirectory();
        var bundledPath = Path.Combine(cache, "appid.json");
        await File.WriteAllTextAsync(bundledPath, "[{\"appid\":730,\"name\":\"Counter-Strike 2\"}]", CancellationToken.None);
        using var service = new SteamCatalogService(client, cache, bundledAppIdJsonPath: bundledPath);

        var first = await service.GetCatalogAsync(forceRefresh: true);
        var second = await service.GetCatalogAsync();

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.True(second.FromCache);
        Assert.Single(second.Items);
        // GitHub fails, then the bundled file answers before the Steam endpoint is contacted.
        // The fresh second call is served from cache without any further HTTP request.
        Assert.Equal(1, handler.RequestCount);
        DeleteDirectory(cache);
    }

    [Fact]
    public async Task GetCatalogAsync_FallsBackToCacheWhenSteamIsUnavailable()
    {
        const string json = "{\"applist\":{\"apps\":[{\"appid\":570,\"name\":\"Dota 2\"}]}}";
        var requestNumber = 0;
        using var handler = new QueueHandler(request =>
        {
            if (IsGithubRequest(request)) throw new HttpRequestException("github offline");
            requestNumber++;
            return requestNumber == 1
                ? JsonResponse(json)
                : throw new HttpRequestException("offline");
        });
        using var client = new HttpClient(handler);
        var cache = CreateTempDirectory();
        using var service = new SteamCatalogService(client, cache, bundledAppIdJsonPath: Path.Combine(cache, "no-bundled.json"));

        var first = await service.GetCatalogAsync(forceRefresh: true);
        var fallback = await service.GetCatalogAsync(forceRefresh: true);

        Assert.True(first.Succeeded);
        Assert.True(fallback.Succeeded);
        Assert.True(fallback.FromCache);
        Assert.Contains("cached", fallback.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(570, Assert.Single(fallback.Items).AppId);
        // When the bundled appid.json path is missing, the service falls through to the Steam API
        // handler: first forced call succeeds, second forced call uses the cache. GitHub attempts
        // fail and are retried once per call, so exactly two Steam requests are ever made.
        Assert.Equal(2, requestNumber);
        DeleteDirectory(cache);
    }

    [Fact]
    public async Task GetCatalogAsync_RetriesTransientServerFailures()
    {
        const string json = "{\"applist\":{\"apps\":[{\"appid\":1245620,\"name\":\"Elden Ring\"}]}}";
        var attempt = 0;
        using var handler = new QueueHandler(request =>
        {
            if (IsGithubRequest(request)) throw new HttpRequestException("github offline");
            attempt++;
            return attempt < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : JsonResponse(json);
        });
        using var client = new HttpClient(handler);
        var cache = CreateTempDirectory();
        using var service = new SteamCatalogService(client, cache, bundledAppIdJsonPath: Path.Combine(cache, "no-bundled.json"));

        var snapshot = await service.GetCatalogAsync(forceRefresh: true);

        Assert.True(snapshot.Succeeded);
        Assert.Equal(3, attempt);
        Assert.Equal(1245620, Assert.Single(snapshot.Items).AppId);
        DeleteDirectory(cache);
    }

    [Fact]
    public async Task GetDetailsAsync_ParsesPublicStoreMetadata()
    {
        const string json = """
            {
              "730": {
                "success": true,
                "data": {
                  "type": "game",
                  "header_image": "https://cdn.example.test/730/header.jpg",
                  "capsule_image": "https://cdn.example.test/730/capsule.jpg",
                  "library_capsule": "https://cdn.example.test/730/library.jpg",
                  "short_description": "A competitive first-person shooter.",
                  "developers": ["Valve"],
                  "publishers": ["Valve"],
                  "release_date": { "date": "21 Aug, 2012" },
                  "is_free": true
                }
              }
            }
            """;
        using var handler = new QueueHandler(_ => JsonResponse(json));
        using var client = new HttpClient(handler);
        var cache = CreateTempDirectory();
        using var service = new SteamCatalogService(client, cache);

        var details = await service.GetDetailsAsync(730);

        Assert.NotNull(details);
        Assert.Equal(SteamCatalogAppType.Game, details!.AppType);
        Assert.Equal("A competitive first-person shooter.", details.ShortDescription);
        Assert.Equal(new[] { "Valve" }, details.Developers);
        Assert.Equal("Free to Play", details.PriceLabel);
        Assert.True(details.IsFreeToPlay);
        DeleteDirectory(cache);
    }

    [Fact]
    public async Task EnsureDetailsAsync_AppliesDetailsToCatalogItem()
    {
        const string json = "{\"730\":{\"success\":true,\"data\":{\"type\":\"game\",\"short_description\":\"Public details\",\"developers\":[\"Valve\"],\"publishers\":[\"Valve\"],\"is_free\":true}}}";
        using var handler = new QueueHandler(_ => JsonResponse(json));
        using var client = new HttpClient(handler);
        var cache = CreateTempDirectory();
        using var service = new SteamCatalogService(client, cache);
        var item = new SteamCatalogItem { AppId = 730, Name = "Counter-Strike 2" };

        var loaded = await service.EnsureDetailsAsync(item);

        Assert.True(loaded);
        Assert.True(item.HasDetails);
        Assert.Equal(SteamCatalogAppType.Game, item.AppType);
        Assert.Equal("Public details", item.ShortDescription);
        Assert.Equal("Valve", item.DevelopersDisplay);
        Assert.Equal("Public Store details loaded", item.DetailStatus);
        DeleteDirectory(cache);
    }

    [Fact]
    public void Query_FiltersSearchesSortsAndPagesCatalogItems()
    {
        var items = new[]
        {
            new SteamCatalogItem { AppId = 730, Name = "Counter-Strike 2", AppType = SteamCatalogAppType.Game, IsInstalled = true },
            new SteamCatalogItem { AppId = 570, Name = "Dota 2", AppType = SteamCatalogAppType.Game },
            new SteamCatalogItem { AppId = 123, Name = "Counter DLC", AppType = SteamCatalogAppType.Dlc }
        };

        var filtered = SteamCatalogQuery.FilterAndSort(items, "counter", "Games", "Name A–Z");
        var page = SteamCatalogPaging.Page(filtered, page: 1, pageSize: 1);

        Assert.Single(filtered);
        Assert.Equal(730, filtered[0].AppId);
        Assert.Single(page);
        Assert.Equal(1, SteamCatalogPaging.TotalPages(filtered.Count, 1));
        Assert.True(SteamCatalogPaging.TotalPages(3, 2) == 2);
    }

    [Fact]
    public async Task GetDetailsAsync_ParsesExtendedPublicStoreFields()
    {
        const string json = """
            {
              "730": {
                "success": true,
                "data": {
                  "type": "game",
                  "short_description": "Public details",
                  "developers": ["Valve"],
                  "publishers": ["Valve"],
                  "is_free": false,
                  "genres": [{ "description": "Action" }, { "description": "Shooter" }],
                  "screenshots": [
                    { "path_thumbnail": "https://example.test/a_thumb.jpg", "path_full": "https://example.test/a.jpg" },
                    { "path_thumbnail": "", "path_full": "https://example.test/b.jpg" }
                  ],
                  "pc_requirements": { "minimum": "<strong>Minimum:</strong><br>CPU Intel", "recommended": "<strong>Recommended:</strong><br>GPU NVIDIA" }
                }
              }
            }
            """;
        using var handler = new QueueHandler(_ => JsonResponse(json));
        using var client = new HttpClient(handler);
        var cache = CreateTempDirectory();
        using var service = new SteamCatalogService(client, cache);

        var details = await service.GetDetailsAsync(730);

        Assert.NotNull(details);
        Assert.Equal("Action · Shooter", details!.GenresDisplay);
        Assert.Equal(2, details.Screenshots.Count);
        Assert.Equal("https://example.test/a_thumb.jpg", details.Screenshots[0].ThumbnailUrl);
        Assert.Equal("https://store.steampowered.com/app/730/", details.StoreUrl);
        Assert.Contains("Minimum:", details.SystemRequirements, StringComparison.Ordinal);
        Assert.Contains("Recommended:", details.SystemRequirements, StringComparison.Ordinal);
        DeleteDirectory(cache);
    }

    [Fact]
    public async Task EnsureDetailsAsync_AppliesExtendedFieldsToCatalogItem()
    {
        const string json = "{\"730\":{\"success\":true,\"data\":{\"type\":\"game\",\"short_description\":\"x\",\"is_free\":true,\"genres\":[{\"description\":\"Action\"}],\"screenshots\":[{\"path_thumbnail\":\"https://example.test/a_thumb.jpg\",\"path_full\":\"https://example.test/a.jpg\"}],\"pc_requirements\":{\"minimum\":\"CPU Intel\"}}}}";
        using var handler = new QueueHandler(_ => JsonResponse(json));
        using var client = new HttpClient(handler);
        var cache = CreateTempDirectory();
        using var service = new SteamCatalogService(client, cache);
        var item = new SteamCatalogItem { AppId = 730, Name = "Counter-Strike 2" };

        var loaded = await service.EnsureDetailsAsync(item);

        Assert.True(loaded);
        Assert.Equal("Action", item.GenresDisplay);
        Assert.Equal("https://store.steampowered.com/app/730/", item.StoreUrl);
        Assert.Single(item.Screenshots);
        Assert.Contains("CPU Intel", item.SystemRequirements, StringComparison.Ordinal);
        DeleteDirectory(cache);
    }

    [Fact]
    public async Task GetCatalogAsync_ReturnsFailureWithoutCacheWhenRequestFails()
    {
        using var handler = new QueueHandler(request =>
        {
            if (IsGithubRequest(request)) throw new HttpRequestException("github offline");
            throw new HttpRequestException("offline");
        });
        using var client = new HttpClient(handler);
        var cache = CreateTempDirectory();
        using var service = new SteamCatalogService(client, cache, bundledAppIdJsonPath: Path.Combine(cache, "no-bundled.json"));

        var snapshot = await service.GetCatalogAsync(forceRefresh: true);

        Assert.False(snapshot.Succeeded);
        Assert.Empty(snapshot.Items);
        Assert.Contains("unavailable", snapshot.Message, StringComparison.OrdinalIgnoreCase);
        DeleteDirectory(cache);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    /// <summary>
    /// The service consults the GitHub app-id list first and the Steam applist endpoint second.
    /// Stub handlers must therefore route by URL: the GitHub endpoint answers "unreachable"
    /// so the fixture on the Steam endpoint decides the outcome.
    /// </summary>
    private static bool IsGithubRequest(HttpRequestMessage request) =>
        request.RequestUri?.Host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase) == true;

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SteamContentManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public QueueHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = _handler(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
