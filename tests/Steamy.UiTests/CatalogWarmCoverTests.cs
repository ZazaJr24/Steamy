using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed partial class CatalogMemoryTests
{
    [Fact]
    public void ReleaseFeedOverridesStaleFlagsWithoutMutatingSharedCatalogModels()
    {
        var released = new SteamCatalogItem { AppId = 1, Name = "Released", IsUpcoming = true };
        var upcoming = new SteamCatalogItem { AppId = 2, Name = "Upcoming", IsUpcoming = false };
        var status = new Dictionary<int, bool> { [1] = false, [2] = true };
        Assert.Same(released, Assert.Single(SteamCatalogQuery.FilterAndSort([released, upcoming], null, null, "App ID", releaseStatus: status)));
        Assert.True(released.IsUpcoming); Assert.False(upcoming.IsUpcoming);
        Assert.Equal(2, SteamCatalogQuery.FilterAndSort([released, upcoming], null, null, "App ID", includeUpcoming: true, releaseStatus: status).Count);
    }

    [Fact]
    public async Task DecodedFallbackCoverWinsWithoutWaitingForAnEarlierMissingCdnImage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-warm-cover-" + Guid.NewGuid().ToString("N"));
        var cachedUrl = "https://cdn.akamai.steamstatic.com/steam/apps/42/library_600x900_2x.jpg";
        using var handler = new WarmCoverHandler(cachedUrl); using var http = new HttpClient(handler);
        using var service = new SteamCatalogService(http, folder);
        var pipeline = (ImagePipeline)typeof(SteamCatalogService).GetField("_images", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var cache = Path.Combine(folder, "seed.png");
        var original = await pipeline.LoadAsync(new(new Uri(cachedUrl), 400, CachePath: cache));
        Assert.NotNull(original);
        File.Delete(cache); // Decoded images can outlive their public disk-cache entry.
        var item = new SteamCatalogItem { AppId = 42, PortraitImageUrl = "https://shared.akamai.steamstatic.com/steam/apps/42/missing.jpg", LibraryImageUrl = cachedUrl };
        var loading = service.EnsureArtworkAsync(item);
        try
        {
            Assert.True(await loading.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Same(original, item.ArtworkImage);
            Assert.Equal(1, handler.Requests); // Only seeding; no missing-image network request.
        }
        finally
        {
            handler.Release.TrySetResult();
            await loading.WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private sealed class WarmCoverHandler(string cachedUrl) : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            if (request.RequestUri!.AbsoluteUri != cachedUrl)
            {
                await Release.Task.WaitAsync(token);
                return new(HttpStatusCode.NotFound);
            }
            var bitmap = BitmapSource.Create(4, 6, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 6 * 4], 4 * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var bytes = new MemoryStream(); encoder.Save(bytes);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) };
        }
    }
}
