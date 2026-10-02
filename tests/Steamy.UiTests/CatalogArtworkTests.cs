using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class CatalogArtworkTests
{
    [Fact]
    public async Task VisibleCoverMetadataUsesOneSteamRequestAndSurvivesRestartOffline()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-assets-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new AssetsHandler();
            using var http = new HttpClient(handler);
            using (var service = new SteamCatalogService(http, folder))
            {
                var items = new[] { Item(480), Item(481) };
                await service.PrepareArtworkAsync(items);
                Assert.Equal(1, handler.Requests);
                Assert.All(items, item => Assert.Contains("/verified/capsule.jpg", item.PortraitImageUrl));
                Assert.All(items, item => Assert.Contains("/verified/header.jpg", item.HeaderImageUrl));
                await service.PrepareArtworkAsync(items);
                Assert.Equal(1, handler.Requests);
            }
            handler.Fail = true;
            using var restarted = new SteamCatalogService(http, folder);
            var restored = new[] { Item(480), Item(481) };
            await restarted.PrepareArtworkAsync(restored);
            Assert.Equal(1, handler.Requests);
            Assert.All(restored, item => Assert.Contains("/verified/capsule.jpg", item.PortraitImageUrl));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void PopularOrderingPutsMajorSeriesBeforeOldUnrelatedAppsAndExtras()
    {
        var items = new[] {
            new SteamCatalogItem { AppId = 10, Name = "An unrelated old app" },
            new SteamCatalogItem { AppId = 2_678_790, Name = "Borderlands 4" },
            new SteamCatalogItem { AppId = 20, Name = "Borderlands 4 Soundtrack" },
            new SteamCatalogItem { AppId = 1_171_580, Name = "Assassin's Creed Mirage" }
        };
        var result = SteamCatalogQuery.FilterAndSort(items, null, null, "Popular (AAA)");
        Assert.Equal(new[] { 1_171_580, 2_678_790 }, result.Take(2).Select(item => item.AppId));
        Assert.Equal(4, result.Count); // Other games remain searchable after the major titles.
    }

    [Fact]
    public async Task ReleaseStatusChecksFutureAndUnknownAppsEvenWithExistingArtwork()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-release-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new AssetsHandler();
            using var http = new HttpClient(handler);
            using (var service = new SteamCatalogService(http, folder))
            {
                var items = new[] { Item(480), Item(481), Item(482) };
                foreach (var item in items) item.PortraitImageUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{item.AppId}/existing/capsule.jpg";
                await service.PrepareReleaseStatusAsync(items);
                Assert.Equal(1, handler.Requests);
                Assert.True(items[0].IsReleaseVerified);
                Assert.False(items[0].IsUpcoming);
                Assert.True(items[1].IsReleaseVerified);
                Assert.True(items[1].IsUpcoming);
                Assert.False(items[2].IsReleaseVerified); // A missing Steam result is never assumed released.
                Assert.Single(SteamCatalogQuery.FilterAndSort(items.Take(2), null, null, "Popular (AAA)"));
            }
            handler.Fail = true;
            using var offline = new SteamCatalogService(http, folder);
            var cached = new[] { Item(480), Item(481) };
            await offline.PrepareReleaseStatusAsync(cached);
            Assert.Equal(1, handler.Requests);
            Assert.All(cached, item => Assert.True(item.IsReleaseVerified));
            Assert.True(cached[1].IsUpcoming);
            var unknown = Item(999);
            await offline.PrepareReleaseStatusAsync(new[] { unknown });
            Assert.False(unknown.IsReleaseVerified);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(null, "Popular (AAA)")]
    [InlineData("Fable", "Name A–Z")]
    [InlineData("2769570", "App ID")]
    public void ComingSoonIsExcludedFromEveryCatalogQuery(string? search, string sort)
    {
        var items = new[] {
            new SteamCatalogItem { AppId = 2769570, Name = "Fable", IsUpcoming = true },
            new SteamCatalogItem { AppId = 1245620, Name = "ELDEN RING", IsReleaseVerified = true }
        };
        var result = SteamCatalogQuery.FilterAndSort(items, search, null, sort);
        Assert.DoesNotContain(result, item => item.IsUpcoming);
        if (search is null) Assert.Equal(1245620, Assert.Single(result).AppId);
        else Assert.Empty(result);
    }

    private static SteamCatalogItem Item(int id) => new() { AppId = id, Name = "Fixture", PortraitImageUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{id}/library_600x900_2x.jpg" };
    private sealed class AssetsHandler : HttpMessageHandler
    {
        public int Requests;
        public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Fail) throw new HttpRequestException("Offline");
            Assert.Contains("IStoreBrowseService/GetItems", request.RequestUri!.AbsolutePath);
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Assert.Contains("include_release", query);
            var rows = new[] { 480, 481 }.Select(id => new {
                appid = id, success = 1, release = new { steam_release_date = 1600000000L, is_coming_soon = id == 481 }, assets = new { asset_url_format = $"steam/apps/{id}/${{FILENAME}}",
                    library_capsule_2x = "verified/capsule.jpg", header_2x = "verified/header.jpg" }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { response = new { store_items = rows } }), Encoding.UTF8, "application/json") });
        }
    }
}
