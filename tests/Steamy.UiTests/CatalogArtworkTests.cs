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
            var rows = new[] { 480, 481 }.Select(id => new {
                appid = id, assets = new { asset_url_format = $"steam/apps/{id}/${{FILENAME}}",
                    library_capsule_2x = "verified/capsule.jpg", header_2x = "verified/header.jpg" }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { response = new { store_items = rows } }), Encoding.UTF8, "application/json") });
        }
    }
}
