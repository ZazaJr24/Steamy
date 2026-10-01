// GPL-2.0; see ../LICENSE. Steamy modification, 2026-10-01.
using System.Diagnostics;
using DepotDownloader;

namespace DepotDownloaderMod.Tests;

public sealed class RateLimitTests
{
    [Fact]
    public async Task ConcurrentConnectionsShareOneRateAndOneBurst()
    {
        const int rate = 96 * 1024;
        var limiter = new DownloadBandwidthLimiter(rate);
        var timer = Stopwatch.StartNew();
        var total = 0;
        var readers = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var stream = new RateLimitedReadStream(new MemoryStream(new byte[rate / 8]), limiter);
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer)) != 0)
            {
                Assert.InRange(count, 1, limiter.MaximumReadBytes);
                var observed = Interlocked.Add(ref total, count);
                var possible = rate * timer.Elapsed.TotalSeconds + limiter.MaximumReadBytes;
                Assert.True(observed <= possible + 32, $"{observed} bytes exceeded the shared budget {possible:F0}.");
            }
        });
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(rate, total);
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(900), timer.Elapsed.ToString());
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CancellationStopsAReadWaitingForTokensWithoutTouchingTheInnerStream()
    {
        var limiter = new DownloadBandwidthLimiter(1);
        Assert.Equal(1, await limiter.ReserveAsync(1, CancellationToken.None));
        using var inner = new CountingStream();
        using var stream = new RateLimitedReadStream(inner, limiter);
        using var cancellation = new CancellationTokenSource();
        var read = stream.ReadAsync(new byte[8], cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, inner.ReadCount);
    }

    [Fact]
    public async Task ClosingConnectionCancelsItsBudgetWait()
    {
        var limiter = new DownloadBandwidthLimiter(1);
        await limiter.ReserveAsync(1, CancellationToken.None);
        var inner = new CountingStream();
        var stream = new RateLimitedReadStream(inner, limiter);
        var read = stream.ReadAsync(new byte[8]).AsTask();
        await stream.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, inner.ReadCount);
        Assert.True(inner.IsDisposed);
    }

    [Fact]
    public async Task ShortReadsReturnUnusedBudgetAndWritesRemainUnrestricted()
    {
        var limiter = new DownloadBandwidthLimiter(20);
        using var inner = new MemoryStream();
        using var stream = new RateLimitedReadStream(inner, limiter);
        await stream.WriteAsync(new byte[64]);
        Assert.Equal(64, inner.Length);
        inner.Position = inner.Length;
        Assert.Equal(0, await stream.ReadAsync(new byte[64]));
        // EOF must not consume the one-byte token and delay the next connection.
        using var other = new RateLimitedReadStream(new MemoryStream([42]), limiter);
        var read = other.ReadAsync(new byte[1]).AsTask();
        Assert.True(read.IsCompletedSuccessfully);
        Assert.Equal(1, await read);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20, 1)]
    [InlineData(1024, 51)]
    [InlineData(1024 * 1024, 52428)]
    [InlineData(long.MaxValue, 65536)]
    public void PerReadBudgetIsBounded(long rate, int expected) =>
        Assert.Equal(expected, new DownloadBandwidthLimiter(rate).MaximumReadBytes);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidLimitsAreRejected(long rate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DownloadBandwidthLimiter(rate));

    private sealed class CountingStream : MemoryStream
    {
        public int ReadCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return base.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
