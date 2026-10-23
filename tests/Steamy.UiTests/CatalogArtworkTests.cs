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
    public async Task CachedFallbackBypassesBusyWorkersEvenWhenThePreferredImageIsMissing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-cached-fallback-queue-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new CoverHandler { PreferredMissing = true };
            using var http = new HttpClient(handler); using var service = new SteamCatalogService(http, folder);
            var initial = Item(480); Assert.True(await service.EnsureArtworkAsync(initial));
            handler.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var slow = Enumerable.Range(481, 4).Select(id => service.EnsureArtworkAsync(Item(id))).ToArray();
            using var lookup = new CancellationTokenSource();
            Task<bool>? cached = null;
            try
            {
                await handler.FourBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var requests = handler.Requests; var restored = Item(480);
                cached = service.EnsureArtworkAsync(restored, cancellationToken: lookup.Token);
                Assert.True(await cached.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Same(initial.ArtworkImage, restored.ArtworkImage);
                Assert.Equal(requests, handler.Requests);
                Assert.All(slow, task => Assert.False(task.IsCompleted));
            }
            finally
            {
                lookup.Cancel(); handler.Hold.TrySetResult(); await Task.WhenAll(slow);
                if (cached is not null) try { await cached; } catch (OperationCanceledException) { }
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task CachedCoverDoesNotWaitForFourSlowNetworkRequests()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-cache-queue-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new CoverHandler(); using var http = new HttpClient(handler);
            using var service = new SteamCatalogService(http, folder);
            var initial = Item(480); await service.EnsureArtworkAsync(initial);
            handler.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var slow = Enumerable.Range(481, 4).Select(id => service.EnsureArtworkAsync(Item(id))).ToArray();
            try
            {
                await handler.FourBlocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var cached = Item(480);
                Assert.True(await service.EnsureArtworkAsync(cached).WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Same(initial.ArtworkImage, cached.ArtworkImage);
            }
            finally { handler.Hold.TrySetResult(); await Task.WhenAll(slow); }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task MissingCoverReturnsFailure()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-missing-cover-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new MissingCoverHandler()); using var service = new SteamCatalogService(http, folder);
            var item = Item(480); Assert.False(await service.EnsureArtworkAsync(item)); Assert.Null(item.ArtworkImage);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private sealed class MissingCoverHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
    [Fact]
    public async Task CachedFallbackLoadsBeforePreferredCdnIsContacted()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-cover-fallback-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new CoverHandler { PreferredMissing = true };
            using var http = new HttpClient(handler);
            using (var first = new SteamCatalogService(http, folder))
            { var item = Item(480); await first.EnsureArtworkAsync(item); Assert.NotNull(item.ArtworkImage); }
            var requests = handler.Requests; Assert.True(requests > 1); handler.Offline = true;
            using var restarted = new SteamCatalogService(http, folder);
            var restored = Item(480); await restarted.EnsureArtworkAsync(restored);
            Assert.NotNull(restored.ArtworkImage); Assert.Equal(requests, handler.Requests);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task DecodedCoverIsSharedAcrossNewItemsWithoutDiskOrNetworkReload()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-cover-memory-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new CoverHandler();
            using var http = new HttpClient(handler);
            using var service = new SteamCatalogService(http, folder);
            var first = Item(480);
            await service.EnsureArtworkAsync(first);
            Assert.NotNull(first.ArtworkImage);
            Assert.True(first.ArtworkImage.IsFrozen);
            foreach (var path in Directory.GetFiles(Path.Combine(folder, "artwork"))) await File.WriteAllBytesAsync(path, Array.Empty<byte>());
            handler.Offline = true;
            var second = Item(480);
            await service.EnsureArtworkAsync(second);
            Assert.Same(first.ArtworkImage, second.ArtworkImage);
            Assert.Equal(1, handler.Requests);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private sealed class CoverHandler : HttpMessageHandler
    {
        public int Requests;
        public bool Offline;
        public bool PreferredMissing;
        public TaskCompletionSource? Hold;
        public TaskCompletionSource FourBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            if (Hold is { } hold) { if (Interlocked.Increment(ref _blocked) == 4) FourBlocked.TrySetResult(); await hold.Task.WaitAsync(cancellationToken); }
            if (Offline) throw new HttpRequestException("Offline");
            if (PreferredMissing && request.RequestUri!.AbsolutePath.Contains("library_600x900")) return new HttpResponseMessage(HttpStatusCode.NotFound);
            // A valid one-pixel 24-bit BMP, with a padded four-byte scanline.
            var bytes = new byte[58];
            using (var writer = new BinaryWriter(new MemoryStream(bytes)))
            {
                writer.Write((ushort)0x4D42); writer.Write(58); writer.Write(0); writer.Write(54);
                writer.Write(40); writer.Write(1); writer.Write(1); writer.Write((ushort)1); writer.Write((ushort)24);
                writer.Write(0); writer.Write(4); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                writer.Write(new byte[] { 80, 140, 200, 0 });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }

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
