using Steamy.Services;
using System.Net.Http;
using System.IO;

namespace Steamy.UiTests;

public sealed partial class ImagePipelineTests
{
    [Fact]
    public async Task PrivateCacheOnlyLookupsStillReturnAnAlreadyDecodedImage()
    {
        using var handler = new Handler(Image()); using var http = new HttpClient(handler);
        using var pipeline = new ImagePipeline(http);
        var request = new ImageRequest(new Uri("https://shared.fastly.steamstatic.com/private-hit.png"), 16, CachePolicy: ImageCachePolicy.MemoryOnly);
        var loaded = await pipeline.LoadAsync(request); Assert.NotNull(loaded);
        Assert.Same(loaded, await pipeline.LoadAsync(request with { Priority = ImagePriority.CacheOnly }));
        Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData(ImageCachePolicy.PublicArtwork)]
    [InlineData(ImageCachePolicy.MemoryOnly)]
    public async Task CacheOnlyMissCompletesWithoutWaitingForOccupiedNetworkWorkers(ImageCachePolicy policy)
    {
        using var handler = new Handler(Image()) { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-cache-miss-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(handler); using var pipeline = new ImagePipeline(http, folder);
        var pending = Enumerable.Range(0, 4).Select(index => pipeline.LoadAsync(new(
            new Uri($"https://shared.fastly.steamstatic.com/blocked-{index}.png"), 16,
            CachePolicy: ImageCachePolicy.MemoryOnly))).ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        try
        {
            while (Volatile.Read(ref handler.Calls) < 4 && DateTime.UtcNow < deadline) await Task.Delay(5);
            Assert.Equal(4, handler.Calls);
            var missing = await pipeline.LoadAsync(new(new Uri("https://shared.fastly.steamstatic.com/cache-miss.png"), 16,
                Priority: ImagePriority.CacheOnly, CachePolicy: policy)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(missing); Assert.Equal(4, handler.Calls);
            Assert.All(pending, operation => Assert.False(operation.IsCompleted));
        }
        finally { handler.Gate.TrySetResult(); await Task.WhenAll(pending); }
    }
}
