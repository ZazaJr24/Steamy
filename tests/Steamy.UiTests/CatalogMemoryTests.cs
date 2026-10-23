using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed partial class CatalogMemoryTests
{
    [Fact]
    public async Task RecommendationsStayOfflineAndDoNotMaterializeThePublicAppList()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-recommendations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cache = Path.Combine(folder, "app-list.json");
        await File.WriteAllTextAsync(cache, "unrelated catalog sentinel");
        using var handler = new OfflineHandler(); using var http = new HttpClient(handler);
        using var service = new SteamCatalogService(http, folder);
        try
        {
            var candidates = await service.GetRecommendationCandidatesAsync();
            Assert.NotEmpty(candidates); Assert.InRange(candidates.Count, 1, 80);
            Assert.All(candidates, item => Assert.Equal(SteamCatalogAppType.Game, item.AppType));
            Assert.Same(candidates, await service.GetRecommendationCandidatesAsync());
            Assert.Equal(0, handler.Requests);
            Assert.Equal("unrelated catalog sentinel", await File.ReadAllTextAsync(cache));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetRecommendationCandidatesAsync(cancelled.Token));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task FullCatalogRetainsArtworkAndCustomProviderSemantics()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-lazy-urls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "app-list.json"), JsonSerializer.Serialize(new
        {
            FetchedAt = DateTimeOffset.UtcNow, ETag = (string?)null, LastModified = (string?)null,
            Items = new[] { new { AppId = 480, Name = "Fixture" } }
        }));
        using var handler = new OfflineHandler(); using var http = new HttpClient(handler);
        using var service = new SteamCatalogService(http, folder);
        try
        {
            var snapshot = await service.GetCatalogAsync();
            var game = Assert.Single(snapshot.Items);
            const string root = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/480/";
            Assert.Equal(root + "header.jpg", game.HeaderImageUrl);
            Assert.Equal(root + "capsule_616x353.jpg", game.CapsuleImageUrl);
            Assert.Equal(root + "library_600x900.jpg", game.PortraitImageUrl);
            Assert.Same(game.PortraitImageUrl, game.LibraryImageUrl);
            game.LibraryImageUrl = "https://example.test/custom.jpg";
            Assert.Equal(root + "library_600x900.jpg", game.PortraitImageUrl);
            Assert.Equal("https://example.test/custom.jpg", game.LibraryImageUrl);
            game.HeaderImageUrl = ""; Assert.Equal("", game.HeaderImageUrl);
            Assert.Same(snapshot.Items, await service.GetRecommendationCandidatesAsync());
            Assert.Equal(0, handler.Requests);
            var custom = new SteamCatalogItem { AppId = 480, LibraryImageUrl = "custom" };
            Assert.Equal("", custom.PortraitImageUrl); Assert.Equal("", custom.HeaderImageUrl);
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
    }
}
