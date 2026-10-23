using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed partial class ImagePipelineTests
{
    private static byte[] Image(int width = 16, int height = 16)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var memory = new MemoryStream(); encoder.Save(memory); return memory.ToArray();
    }
    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        internal int Calls;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? Gate;
        internal Func<Uri, bool>? WaitOn;
        internal TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls); Started.TrySetResult();
            try { if (Gate is not null && (WaitOn?.Invoke(request.RequestUri!) ?? true)) await Gate.Task.WaitAsync(token); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
    [Fact]
    public async Task SharedRequestSurvivesOneConsumerLeaving()
    {
        using var handler = new Handler(Image()) { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var http = new HttpClient(handler); using var pipeline = new ImagePipeline(http);
        using var cancellation = new CancellationTokenSource();
        var request = new ImageRequest(new Uri("https://shared.fastly.steamstatic.com/a.png"), 16, CachePolicy: ImageCachePolicy.MemoryOnly);
        var first = pipeline.LoadAsync(request, cancellation.Token); var second = pipeline.LoadAsync(request);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(handler.Cancelled.Task.IsCompleted);
        handler.Gate.SetResult(); var image = await second;
        Assert.NotNull(image); Assert.True(image.IsFrozen); Assert.Equal(1, handler.Calls);
        Assert.Same(image, await pipeline.LoadAsync(request)); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task DifferentDecodeSizesShareTheTransferAndCancelIndependently()
    {
        using var handler = new Handler(Image(128, 64)) { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var http = new HttpClient(handler); using var pipeline = new ImagePipeline(http);
        using var cancellation = new CancellationTokenSource();
        var uri = new Uri("https://shared.fastly.steamstatic.com/same-source.png");
        var first = pipeline.LoadAsync(new(uri, 32, CachePolicy: ImageCachePolicy.MemoryOnly), cancellation.Token);
        var second = pipeline.LoadAsync(new(uri, 64, CachePolicy: ImageCachePolicy.MemoryOnly));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(40); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        handler.Gate.SetResult();
        var result = await second;
        Assert.NotNull(result); Assert.Equal(64, result.PixelWidth); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task SlowPrefetchLeavesCapacityForVisibleImages()
    {
        using var handler = new Handler(Image()) { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously), WaitOn = uri => uri.AbsolutePath.Contains("prefetch") };
        using var http = new HttpClient(handler); using var pipeline = new ImagePipeline(http);
        var pending = Enumerable.Range(0, 6).Select(i => pipeline.LoadAsync(new(new Uri($"https://shared.fastly.steamstatic.com/prefetch-{i}.png"), 16, Priority: ImagePriority.Prefetch, CachePolicy: ImageCachePolicy.MemoryOnly))).ToArray();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var visible = await pipeline.LoadAsync(new(new Uri("https://shared.fastly.steamstatic.com/visible.png"), 16, CachePolicy: ImageCachePolicy.MemoryOnly)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(visible); Assert.Contains(pending, task => !task.IsCompleted);
        handler.Gate.SetResult(); await Task.WhenAll(pending);
    }
    [Fact]
    public async Task LastConsumerCancelsTheActualTransfer()
    {
        using var handler = new Handler(Image()) { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var http = new HttpClient(handler); using var pipeline = new ImagePipeline(http);
        using var cancellation = new CancellationTokenSource();
        var task = pipeline.LoadAsync(new(new Uri("https://shared.fastly.steamstatic.com/b.png"), 16, CachePolicy: ImageCachePolicy.MemoryOnly), cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, pipeline.CachedBytes);
    }
    [Fact]
    public async Task ByteBudgetEvictsOldImagesAndPrivateImagesNeverReachDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-image-budget-" + Guid.NewGuid().ToString("N"));
        using var handler = new Handler(Image()); using var http = new HttpClient(handler);
        using var pipeline = new ImagePipeline(http, directory, memoryBudget: 1100);
        for (var i = 0; i < 8; i++)
            Assert.NotNull(await pipeline.LoadAsync(new(new Uri($"https://shared.fastly.steamstatic.com/{i}.png"), 16, CachePolicy: ImageCachePolicy.MemoryOnly)));
        Assert.InRange(pipeline.CachedBytes, 1, 1100); Assert.Equal(1, pipeline.CachedCount); Assert.False(Directory.Exists(directory));
    }
    [Fact]
    public async Task DiskBudgetIsEnforcedAndCachedImagesWorkOffline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-image-disk-" + Guid.NewGuid().ToString("N")); var bytes = Image();
        try
        {
            using var handler = new Handler(bytes); using var http = new HttpClient(handler);
            using (var pipeline = new ImagePipeline(http, directory, diskBudget: bytes.Length * 2))
                for (var i = 0; i < 5; i++) Assert.NotNull(await pipeline.LoadAsync(new(new Uri($"https://shared.fastly.steamstatic.com/{i}.png"), 16)));
            Assert.True(Directory.GetFiles(directory).Sum(path => new FileInfo(path).Length) <= bytes.Length * 2);
            var count = handler.Calls;
            using var restart = new ImagePipeline(http, directory);
            Assert.NotNull(await restart.LoadAsync(new(new Uri("https://shared.fastly.steamstatic.com/4.png"), 16, Priority: ImagePriority.CacheOnly)));
            Assert.Equal(count, handler.Calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void AmbientDerivativesShareTheSameByteBudget()
    {
        using var pipeline = new ImagePipeline(memoryBudget: 1100);
        var source = ImagePipeline.Decode(Image(), 16)!;
        for (var variant = 0; variant < 8; variant++) pipeline.RememberDerived(source, variant, ImagePipeline.Decode(Image(), 16)!);
        Assert.InRange(pipeline.CachedBytes, 1, 1100); Assert.Equal(1, pipeline.CachedCount);
    }
    [Fact]
    public void HighPrecisionDerivedImagesUseTheirFullPixelStorage()
    {
        using var pipeline = new ImagePipeline(memoryBudget: 1100);
        var source = ImagePipeline.Decode(Image(), 16)!;
        var highPrecision = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Rgba64, null, new byte[16 * 16 * 8], 16 * 8);
        highPrecision.Freeze();
        pipeline.RememberDerived(source, 0, highPrecision);
        Assert.Equal(0, pipeline.CachedBytes); Assert.Equal(0, pipeline.CachedCount);
    }
    [Fact]
    public void RegeneratedAmbientVariantReplacesTheStaleSurfaceAndByteCount()
    {
        using var pipeline = new ImagePipeline(memoryBudget: 4096);
        var source = ImagePipeline.Decode(Image(), 16)!;
        pipeline.RememberDerived(source, 0, ImagePipeline.Decode(Image(), 16)!);
        Assert.Equal(1024, pipeline.CachedBytes);
        pipeline.RememberDerived(source, 0, ImagePipeline.Decode(Image(32, 16), 32)!);
        Assert.Equal(2048, pipeline.CachedBytes); Assert.Equal(1, pipeline.CachedCount);
    }
    [Fact]
    public void FrozenBitmapDoesNotRetainTheCompressedStreamBuffer()
    {
        var (image, bytes) = DecodeAndForgetBytes();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(bytes.TryGetTarget(out _));
        Assert.Equal(16, image.PixelWidth); Assert.True(image.IsFrozen);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (BitmapImage, WeakReference<byte[]>) DecodeAndForgetBytes()
    {
        var bytes = Image(); return (ImagePipeline.Decode(bytes, 16)!, new(bytes));
    }
    [Fact]
    public void DecodeFitsBothDimensionsAndRejectsCorruptData()
    {
        var image = ImagePipeline.Decode(Image(600, 900), 360, 200);
        Assert.NotNull(image); Assert.InRange(image.PixelWidth, 132, 134); Assert.Equal(200, image.PixelHeight); Assert.True(image.IsFrozen);
        Assert.Null(ImagePipeline.Decode([1, 2, 3], 360));
    }
}
