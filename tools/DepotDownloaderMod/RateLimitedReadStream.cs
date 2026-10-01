// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.
// Steamy modification, 2026-09-30: shared download bandwidth limit.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DepotDownloader
{
    /// <summary>A single token budget shared by every HTTP connection in this process.</summary>
    internal sealed class DownloadBandwidthLimiter
    {
        private readonly object gate = new();
        private double available;
        private long replenishedAt;

        public long BytesPerSecond { get; }
        public int MaximumReadBytes { get; }

        public DownloadBandwidthLimiter(long bytesPerSecond)
        {
            if (bytesPerSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
            }

            BytesPerSecond = bytesPerSecond;
            // At most 50 ms of the chosen rate, bounded to 64 KiB across all connections.
            MaximumReadBytes = (int)Math.Clamp(bytesPerSecond / 20, 1L, 65536L);
            available = MaximumReadBytes;
            replenishedAt = Stopwatch.GetTimestamp();
        }

        public async ValueTask<int> ReserveAsync(int requested, CancellationToken cancellationToken)
        {
            if (requested <= 0)
            {
                return 0;
            }

            var count = Math.Min(requested, MaximumReadBytes);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan delay;
                lock (gate)
                {
                    Replenish();
                    if (available >= count)
                    {
                        available -= count;
                        return count;
                    }

                    delay = TimeSpan.FromMilliseconds(Math.Max(1, (count - available) * 1000 / BytesPerSecond));
                }

                // No lock or thread is held while the network budget replenishes.
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        public void ReturnUnused(int bytes)
        {
            if (bytes <= 0)
            {
                return;
            }

            lock (gate)
            {
                Replenish();
                available = Math.Min(MaximumReadBytes, available + bytes);
            }
        }

        private void Replenish()
        {
            var now = Stopwatch.GetTimestamp();
            available = Math.Min(MaximumReadBytes, available + Stopwatch.GetElapsedTime(replenishedAt, now).TotalSeconds * BytesPerSecond);
            replenishedAt = now;
        }
    }

    /// <summary>Limits real socket reads; HTTP/TLS and CDN clients use the same process budget.</summary>
    internal sealed class RateLimitedReadStream : Stream
    {
        private readonly Stream inner;
        private readonly DownloadBandwidthLimiter limiter;
        private readonly CancellationTokenSource closed = new();
        private int disposed;

        public RateLimitedReadStream(Stream inner, DownloadBandwidthLimiter limiter)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanTimeout => inner.CanTimeout;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int ReadTimeout { get => inner.ReadTimeout; set => inner.ReadTimeout = value; }
        public override int WriteTimeout { get => inner.WriteTimeout; set => inner.WriteTimeout = value; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var reserved = limiter.ReserveAsync(buffer.Length, closed.Token).AsTask().GetAwaiter().GetResult();
            var received = 0;
            try
            {
                received = inner.Read(buffer[..reserved]);
                return received;
            }
            finally
            {
                limiter.ReturnUnused(reserved - received);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closed.Token);
            var reserved = await limiter.ReserveAsync(buffer.Length, linked.Token).ConfigureAwait(false);
            var received = 0;
            try
            {
                received = await inner.ReadAsync(buffer[..reserved], linked.Token).ConfigureAwait(false);
                return received;
            }
            finally
            {
                limiter.ReturnUnused(reserved - received);
            }
        }

        public override int ReadByte()
        {
            Span<byte> byteBuffer = stackalloc byte[1];
            return Read(byteBuffer) == 0 ? -1 : byteBuffer[0];
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override void WriteByte(byte value) => inner.WriteByte(value);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                closed.Cancel();
                inner.Dispose();
                closed.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                closed.Cancel();
                await inner.DisposeAsync().ConfigureAwait(false);
                closed.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}
