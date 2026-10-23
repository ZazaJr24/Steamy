using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed partial class ImagePipelineTests
{
    [Fact]
    public async Task ACommittedCacheWriteStillEnforcesItsBudgetWhenDisposalRacesMaintenance()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-dispose-budget-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "committed.image");
        using var handler = new Handler(Image()); using var http = new HttpClient(handler);
        using var pipeline = new ImagePipeline(http, folder, diskBudget: 0);
        var maintenanceLock = typeof(ImagePipeline).GetField("_diskSync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pipeline)!;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var maintenance = Task.Run(() => { lock (maintenanceLock) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } });
        Task<BitmapImage?>? load = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            load = pipeline.LoadAsync(new(new Uri("https://cdn.akamai.steamstatic.com/test/dispose-budget.png"), 64, CachePath: file));
            for (var attempt = 0; attempt < 250 && !File.Exists(file); attempt++) await Task.Delay(10);
            Assert.True(File.Exists(file));
            pipeline.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
            release.Set(); await maintenance;
            for (var attempt = 0; attempt < 250 && File.Exists(file); attempt++) await Task.Delay(10);
            Assert.False(File.Exists(file));
        }
        finally
        {
            release.Set(); await maintenance;
            pipeline.Dispose();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task DecodedDiskHitDoesNotWaitForDirectoryMaintenance()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "cached.image");
        await File.WriteAllBytesAsync(file, Image());
        using var handler = new Handler(Image()); using var http = new HttpClient(handler);
        using var pipeline = new ImagePipeline(http, folder);
        var maintenanceLock = typeof(ImagePipeline).GetField("_diskSync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pipeline)!;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var maintenance = Task.Run(() => { lock (maintenanceLock) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } });
        Task<BitmapImage?>? load = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            load = pipeline.LoadAsync(new(new Uri("https://cdn.akamai.steamstatic.com/test/maintenance.png"), 64, Priority: ImagePriority.CacheOnly, CachePath: file));
            var ready = await load.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotNull(ready); Assert.True(ready.IsFrozen);
            Assert.False(maintenance.IsCompleted);
        }
        finally
        {
            release.Set(); await maintenance;
            if (load is not null) await load;
            pipeline.Dispose();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task IdleWorkersDoNotRetainUncachedImagesAfterTheirConsumersLeave()
    {
        using var handler = new Handler(Image(512, 512)); using var http = new HttpClient(handler);
        using var pipeline = new ImagePipeline(http, "unused-memory-only-cache", memoryBudget: 0);
        var images = new List<WeakReference>();
        for (var i = 0; i < 6; i++) images.Add(await LoadWithoutRetainingImage(pipeline, i));
        Assert.Equal(0, pipeline.CachedBytes);
        for (var attempt = 0; attempt < 8 && images.Any(image => image.IsAlive); attempt++)
        {
            await Task.Delay(20);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true); GC.WaitForPendingFinalizers();
        }
        Assert.DoesNotContain(images, image => image.IsAlive);
        GC.KeepAlive(pipeline);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadWithoutRetainingImage(ImagePipeline pipeline, int id)
    {
        var image = await pipeline.LoadAsync(new(new Uri($"https://cdn.akamai.steamstatic.com/test/lifetime-{id}.png"), 512, CachePolicy: ImageCachePolicy.MemoryOnly));
        Assert.NotNull(image); Assert.True(image.IsFrozen);
        return new(image);
    }
    [Fact]
    public async Task SlowDiskProbeDoesNotBlockAnAlreadyDecodedImage()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-warm-probe-" + Guid.NewGuid().ToString("N"));
        using var handler = new Handler(Image()); using var http = new HttpClient(handler);
        using var pipeline = new ImagePipeline(http, folder, ImagePipeline.MemoryBudget, ImagePipeline.DiskBudget, _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cache probe was not released.");
            return false;
        });
        var warm = new ImageRequest(new Uri("https://cdn.akamai.steamstatic.com/test/warm.png"), 64, CachePolicy: ImageCachePolicy.MemoryOnly);
        var original = await pipeline.LoadAsync(warm);
        var pending = pipeline.LoadAsync(new(new Uri("https://cdn.akamai.steamstatic.com/test/slow.png"), 64));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(original, await pipeline.LoadAsync(warm).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.Set(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
    [Fact]
    public async Task CancelledProbeCannotRemoveOrStartTheReplacementRequest()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-probe-retry-" + Guid.NewGuid().ToString("N"));
        using var handler = new Handler(Image()); using var http = new HttpClient(handler);
        using var pipeline = new ImagePipeline(http, folder, ImagePipeline.MemoryBudget, ImagePipeline.DiskBudget, _ =>
        {
            if (Interlocked.Increment(ref probes) == 1)
            {
                entered.TrySetResult();
                try { if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cache probe was not released."); }
                finally { exited.TrySetResult(); }
            }
            return false;
        });
        using var cancelled = new CancellationTokenSource();
        var request = new ImageRequest(new Uri("https://cdn.akamai.steamstatic.com/test/retry.png"), 64);
        var abandoned = pipeline.LoadAsync(request, cancelled.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(0, handler.Calls);
            var replacement = await pipeline.LoadAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(replacement); Assert.Equal(1, handler.Calls);
            release.Set(); await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(replacement, await pipeline.LoadAsync(request));
            Assert.Equal(2, probes); Assert.Equal(1, handler.Calls);
        }
        finally
        {
            release.Set(); await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
    [Fact]
    public async Task PendingProbeIsSharedAndPromotionSurvivesOneSubscriberLeaving()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-probe-shared-" + Guid.NewGuid().ToString("N"));
        using var handler = new Handler(Image()); using var http = new HttpClient(handler);
        using var pipeline = new ImagePipeline(http, folder, ImagePipeline.MemoryBudget, ImagePipeline.DiskBudget, _ =>
        {
            Interlocked.Increment(ref probes); entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cache probe was not released.");
            return false;
        });
        var uri = new Uri("https://cdn.akamai.steamstatic.com/test/shared.png");
        using var cancelled = new CancellationTokenSource();
        var prefetch = pipeline.LoadAsync(new(uri, 64, Priority: ImagePriority.Prefetch), cancelled.Token);
        Task<BitmapImage?>? visible = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            visible = pipeline.LoadAsync(new(uri, 64)); pipeline.Prioritize(uri);
            cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prefetch);
            Assert.Equal(1, probes); Assert.Equal(0, handler.Calls); Assert.False(visible.IsCompleted);
            release.Set(); Assert.NotNull(await visible.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, probes); Assert.Equal(1, handler.Calls);
        }
        finally
        {
            release.Set();
            if (visible is not null) await visible.WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
    [Fact]
    public async Task StartingAnImageRequestDoesNotWaitForASlowDiskCacheProbe()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-probe-" + Guid.NewGuid().ToString("N"));
        using var handler = new Handler(Image()); using var http = new HttpClient(handler);
        using var pipeline = new ImagePipeline(http, folder, ImagePipeline.MemoryBudget, ImagePipeline.DiskBudget, _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cache probe was not released.");
            return false;
        });
        var creation = Task.Factory.StartNew(() => pipeline.LoadAsync(new(new Uri("https://cdn.akamai.steamstatic.com/test/probe.png"), 64)), CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(creation, await Task.WhenAny(creation, Task.Delay(TimeSpan.FromSeconds(1))));
        }
        finally
        {
            release.Set();
            var image = await creation.WaitAsync(TimeSpan.FromSeconds(5)); await image.WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
