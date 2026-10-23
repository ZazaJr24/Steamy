using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class SteamArtworkTests
{
    [Fact]
    public async Task LibraryCoversDecodeAtDisplayResolutionAndReuseTheDecodedImage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-cover-memory-" + Guid.NewGuid().ToString("N"));
        var bitmap = BitmapSource.Create(600, 900, 96, 96, PixelFormats.Bgra32, null, new byte[600 * 900 * 4], 600 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output);
        using var handler = new ImageHandler(output.ToArray()); using var http = new HttpClient(handler);
        using var artwork = new SteamArtworkService(http, folder);
        try
        {
            var game = new Game { AppId = 42 }; await artwork.LoadAsync(game);
            Assert.NotNull(game.ArtworkImage); Assert.Equal(360, game.ArtworkImage.PixelWidth); Assert.Equal(540, game.ArtworkImage.PixelHeight);
            Assert.True(game.ArtworkImage.IsFrozen);
            var second = new Game { AppId = 42 }; await artwork.LoadAsync(second);
            Assert.Same(game.ArtworkImage, second.ArtworkImage); Assert.Equal(2, handler.Requests);
            Assert.Equal(.36, game.ArtworkImage.PixelWidth * (double)game.ArtworkImage.PixelHeight / (600 * 900), 2);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private sealed class ImageHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Requests); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }
    }
    [Fact]
    public async Task OnlineSpotlightWithoutBundledLogoResolvesSteamWordmarkAndCachesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-online-logo-" + Guid.NewGuid().ToString("N"));
        var expected = "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1808500/" + new string('a', 40) + "/logo_2x.png";
        using var handler = new LogoHandler(expected, "logo"); using var http = new HttpClient(handler);
        try
        {
            using var service = new SteamArtworkService(http, root);
            var game = new SpotlightGame { AppId = 1808500 };
            Assert.NotNull(await service.LoadSpotlightLogoAsync(game));
            Assert.Contains(expected, handler.Requests);
            Assert.DoesNotContain(handler.Requests, url => url.EndsWith("/1808500/logo.png", StringComparison.Ordinal));
            var count = handler.Requests.Count;
            using var restartHttp = new HttpClient(handler, disposeHandler: false);
            using var restart = new SteamArtworkService(restartHttp, root);
            Assert.NotNull(await restart.LoadSpotlightLogoAsync(game));
            Assert.Equal(count, handler.Requests.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GameDetailsUseCurrentSteamHeroAndLogoInsteadOfLegacyBanner(bool hero)
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-detail-art-" + Guid.NewGuid().ToString("N"));
        var filename = new string('a', 40) + (hero ? "/library_hero_2x.jpg" : "/logo_2x.png");
        var expected = $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1808500/{filename}";
        using var handler = new LogoHandler(expected, hero ? "hero" : "logo"); using var http = new HttpClient(handler);
        try
        {
            using var service = new SteamArtworkService(http, root);
            var image = hero ? await service.LoadHeroAsync(1808500) : await service.LoadGameLogoAsync(new Game { AppId = 1808500 });
            Assert.NotNull(image); Assert.True(image.IsFrozen);
            Assert.Contains(handler.Requests, url => url.EndsWith(filename, StringComparison.Ordinal));
            Assert.DoesNotContain(handler.Requests, url => url.EndsWith("/1808500/logo.png", StringComparison.Ordinal));
            var requests = handler.Requests.Count;
            using var restartHttp = new HttpClient(handler, disposeHandler: false);
            using var restart = new SteamArtworkService(restartHttp, root);
            Assert.NotNull(hero ? await restart.LoadHeroAsync(1808500) : await restart.LoadGameLogoAsync(new Game { AppId = 1808500 }));
            Assert.Equal(requests, handler.Requests.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(2769570, false)]
    [InlineData(4435490, true)]
    public async Task SpotlightUsesBundledSteamLogoWhenFeedOmitsItOrHasExpiredUrl(int appId, bool expiredUrl)
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-logo-" + Guid.NewGuid().ToString("N"));
        var expected = SpotlightCatalogService.LoadBundled().Games.Single(game => game.AppId == appId).LogoUrl;
        using var handler = new LogoHandler(expected);
        using var http = new HttpClient(handler);
        try
        {
            using var artwork = new SteamArtworkService(http, root);
            var metadata = new SpotlightGame { AppId = appId, LogoUrl = expiredUrl
                ? $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/expired/logo.png" : string.Empty };
            var logo = await artwork.LoadSpotlightLogoAsync(metadata);
            Assert.NotNull(logo);
            Assert.True(logo.IsFrozen);
            Assert.Contains(expected, handler.Requests);
            var count = handler.Requests.Count;
            using var restartHttp = new HttpClient(handler, disposeHandler: false);
            using var restart = new SteamArtworkService(restartHttp, root);
            Assert.NotNull(await restart.LoadSpotlightLogoAsync(metadata));
            Assert.Equal(count, handler.Requests.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class LogoHandler(string expectedUrl, string? detailsKind = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (detailsKind == "logo" && request.RequestUri!.Host == "api.steamcmd.net")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"data":{"1808500":{"common":{"library_assets_full":{"library_logo":{"image2x":{"english":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/logo_2x.png"}}}}}}}
                    """) });
            if (detailsKind == "hero" && request.RequestUri!.Host == "api.steampowered.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"response":{"store_items":[{"appid":1808500,"assets":{"asset_url_format":"steam/apps/1808500/${FILENAME}","library_hero_2x":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/library_hero_2x.jpg"}}]}}
                    """) });
            if (url != expectedUrl && !(detailsKind == "hero" && request.RequestUri!.AbsolutePath.EndsWith("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/library_hero_2x.jpg", StringComparison.Ordinal)))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var image = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Repeat((byte)200, 16 * 16 * 4).ToArray(), 64);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = new MemoryStream();
            encoder.Save(output);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.ToArray()) });
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReplacesCachedSteamGrayPlaceholdersWithCurrentAssets(bool headerOnly, bool smallPortrait)
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-artwork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var (kind, file) in new[] { ("library", "portrait"), ("header", "header") })
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"steam-placeholder-{(file == "portrait" && smallPortrait ? "portrait-small" : file)}.jpg"), Path.Combine(root, $"2807960_{kind}.jpg"));
            using var handler = new AssetHandler();
            using var client = new HttpClient(handler);
            using var artwork = new SteamArtworkService(client, root);
            var game = new Game { AppId = 2807960, Name = "Battlefield 6" };
            if (headerOnly) await artwork.LoadHeadersAsync([game]);
            else await artwork.LoadAsync(game);
            Assert.NotNull(game.HeaderImage);
            Assert.NotNull(game.ArtworkImage);
            Assert.Equal(16, game.ArtworkImage.PixelWidth);
            Assert.True(game.ArtworkImage.IsFrozen);
            Assert.Equal(2, handler.CurrentAssets);
            var requestCount = handler.Requests;
            using var revisitClient = new HttpClient(handler, disposeHandler: false);
            using var revisited = new SteamArtworkService(revisitClient, root);
            await revisited.LoadAsync(new Game { AppId = 2807960 });
            Assert.Equal(requestCount, handler.Requests); // Repaired cache survives restart.
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class AssetHandler : HttpMessageHandler
    {
        public int CurrentAssets, Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            var url = request.RequestUri!;
            if (url.Host == "api.steampowered.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"response":{"store_items":[{"appid":2807960,"assets":{"asset_url_format":"steam/apps/2807960/${FILENAME}","library_capsule":"current/portrait.jpg","header":"current/header.jpg"}}]}}
                    """, Encoding.UTF8, "application/json") });
            byte[] bytes;
            if (url.AbsolutePath.Contains("/current/"))
            {
                Interlocked.Increment(ref CurrentAssets);
                var pixels = Enumerable.Repeat((byte)200, 16 * 16 * 4).ToArray();
                var image = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 64);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(image));
                using var output = new MemoryStream();
                png.Save(output);
                bytes = output.ToArray();
            }
            else
            {
                var file = url.AbsolutePath.Contains("header") ? "header"
                    : url.AbsolutePath.Contains("capsule_616") ? "capsule"
                    : url.AbsolutePath.Contains("_2x") ? "portrait" : "portrait-small";
                bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"steam-placeholder-{file}.jpg"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
