using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Steamy.Services;

public enum ImagePriority { Visible, Prefetch, CacheOnly }
public enum ImageCachePolicy { PublicArtwork, MemoryOnly }
public sealed record ImageRequest(Uri Source, int PixelWidth, int PixelHeight = 0,
    ImagePriority Priority = ImagePriority.Visible, ImageCachePolicy CachePolicy = ImageCachePolicy.PublicArtwork,
    string? CachePath = null);
public interface IImagePipeline
{
    Task<BitmapImage?> LoadAsync(ImageRequest request, CancellationToken cancellationToken = default);
    void Prioritize(Uri source) { }
}

/// <summary>Shared, bounded static images. Callers cancel their subscriptions, not other consumers.</summary>
public sealed class ImagePipeline : IImagePipeline, IDisposable
{
    public const long MemoryBudget = 32 * 1024 * 1024;
    public const long DiskBudget = 512 * 1024 * 1024;
    internal const int MaxDownloadBytes = 8 * 1024 * 1024;
    private static readonly Lazy<ImagePipeline> Instance = new(() =>
    {
        return new ImagePipeline { _sweepLegacyCaches = true };
    });
    public static ImagePipeline Shared => Instance.Value;
    private readonly HttpClient _http;
    private readonly Func<string, bool> _fileExists;
    private readonly bool _ownsHttp;
    private readonly string _directory;
    private string DiskDirectory => _directory.Length > 0 ? _directory : Path.Combine(Storage.AppDataPaths.Root, "image-cache");
    private bool _sweepLegacyCaches, _diskSweepStarted;
    private readonly long _memoryLimit, _diskLimit;
    private readonly object _sync = new();
    private readonly object _diskSync = new();
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<ImageSource, DerivedOwner> _derivedOwners = new();
    private int _nextDerivedOwner;
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Transfer> _transfers = new(StringComparer.Ordinal);
    private readonly PriorityQueue<Flight, int> _queue = new();
    private readonly SemaphoreSlim _signal = new(0), _decode = new(2), _network = new(4);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, (long Bytes, long Access)> _diskFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scannedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDiskTouches = new(StringComparer.OrdinalIgnoreCase);
    private bool _diskTouchWorker;
    private long _diskBytes, _access;
    private long _bytes;
    private int _activePrefetch;
    private bool _started, _disposed;
    private sealed record Entry(BitmapSource Image, long Bytes, LinkedListNode<string> Node);
    private sealed record DerivedOwner(int Id);
    private sealed class Transfer
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<byte[]?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Subscribers;
    }
    private sealed class Flight(string key, string cacheKey, ImageRequest request)
    {
        internal readonly string Key = key;
        internal readonly string CacheKey = cacheKey;
        internal readonly ImageRequest Request = request;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<BitmapImage?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Subscribers;
        internal bool Started;
        internal bool Probing;
        internal bool Released;
        internal ImagePriority Priority = request.Priority;
    }
    public ImagePipeline(HttpClient? http = null, string? directory = null, long memoryBudget = MemoryBudget, long diskBudget = DiskBudget)
        : this(http, directory, memoryBudget, diskBudget, File.Exists) { }

    internal ImagePipeline(HttpClient? http, string? directory, long memoryBudget, long diskBudget, Func<string, bool> fileExists)
    {
        _fileExists = fileExists;
        _ownsHttp = http is null;
        if (http is null)
        {
            var handler = StableDnsHandler.Create();
            if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
            if (handler is HttpClientHandler client) client.AllowAutoRedirect = false;
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/0.8");
        }
        _http = http;
        _directory = directory ?? string.Empty;
        _memoryLimit = Math.Max(0, memoryBudget); _diskLimit = Math.Max(0, diskBudget);
    }
    internal long CachedBytes { get { lock (_sync) return _bytes; } }
    internal int CachedCount { get { lock (_sync) return _cache.Count; } }
    public void Prioritize(Uri source)
    {
        lock (_sync)
            foreach (var flight in _flights.Values.Where(flight => !flight.Started && flight.Request.Source == source && flight.Priority == ImagePriority.Prefetch))
            { flight.Priority = ImagePriority.Visible; if (!flight.Probing) { _queue.Enqueue(flight, 0); _signal.Release(); } }
    }
    public async Task<BitmapImage?> LoadAsync(ImageRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Source.IsAbsoluteUri || request.Source.Scheme is not ("https" or "file")) return null;
        request = request with { PixelWidth = Math.Clamp(request.PixelWidth, 1, 2560), PixelHeight = Math.Clamp(request.PixelHeight, 0, 2560) };
        var cacheKey = $"{request.Source.AbsoluteUri}|{request.PixelWidth}|{request.PixelHeight}|{request.CachePolicy}";
        var key = cacheKey + "|" + (request.Priority == ImagePriority.CacheOnly);
        Flight flight;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sweepLegacyCaches && !_diskSweepStarted && request.CachePolicy == ImageCachePolicy.PublicArtwork)
            { _diskSweepStarted = true; _ = Task.Run(SweepPublicDiskCaches); }
            if (_cache.TryGetValue(cacheKey, out var hit)) { _lru.Remove(hit.Node); _lru.AddFirst(hit.Node); return hit.Image as BitmapImage; }
            // A private memory-only miss has no disk or network work to schedule.
            // Still check the shared decoded cache first, including differently
            // prioritized callers of the same source and target size.
            if (request.Priority == ImagePriority.CacheOnly && request.CachePolicy == ImageCachePolicy.MemoryOnly && !request.Source.IsFile)
                return null;
            if (!_flights.TryGetValue(key, out flight!))
            {
                flight = new Flight(key, cacheKey, request); _flights.Add(key, flight);
                if (request.Source.IsFile || request.CachePolicy == ImageCachePolicy.PublicArtwork)
                { flight.Probing = true; _ = Task.Run(() => ProbeCacheAsync(flight)); }
                else Enqueue(flight);
            }
            else if (!flight.Started && request.Priority == ImagePriority.Visible)
            { flight.Priority = ImagePriority.Visible; if (!flight.Probing) { _queue.Enqueue(flight, 0); _signal.Release(); } }
            flight.Subscribers++;
        }
        try { return await flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                if (--flight.Subscribers == 0 && !flight.Completion.Task.IsCompleted)
                { if (_flights.TryGetValue(key, out var current) && current == flight) _flights.Remove(key); flight.Cancellation.Cancel(); }
                if (flight.Subscribers == 0 && flight.Completion.Task.IsCompleted && !flight.Released)
                { flight.Released = true; flight.Cancellation.Dispose(); }
            }
        }
    }
    // Called while holding _sync; only scheduling happens here, never filesystem I/O.
    private void Enqueue(Flight flight)
    {
        _queue.Enqueue(flight, (int)flight.Priority); _signal.Release();
        if (!_started) { _started = true; for (var i = 0; i < 4; i++) _ = Task.Run(WorkerAsync); }
    }
    private async Task ProbeCacheAsync(Flight flight)
    {
        try
        {
            lock (_sync)
            {
                if (_disposed || flight.Released || flight.Cancellation.IsCancellationRequested)
                { FinishProbe(flight, cancelled: true); return; }
            }
            // A slow cache disk must not hold the shared image-cache lock or
            // block the caller. Existing files still bypass occupied network workers.
            var exists = _fileExists(CacheFile(flight.Request));
            lock (_sync)
            {
                flight.Probing = false;
                if (_disposed || flight.Released || flight.Cancellation.IsCancellationRequested)
                { FinishProbe(flight, cancelled: true); return; }
                // A cache-only miss must finish here. Queuing it behind busy
                // network workers also delayed the caller's next, cached fallback.
                if (!exists && flight.Request.Priority == ImagePriority.CacheOnly)
                { FinishProbe(flight, cancelled: false); return; }
                if (exists) flight.Started = true;
                else { Enqueue(flight); return; }
            }
            await ProcessAsync(flight).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or TimeoutException)
        { lock (_sync) FinishProbe(flight, cancelled: false); }
    }
    private void FinishProbe(Flight flight, bool cancelled)
    {
        flight.Probing = false;
        if (_flights.TryGetValue(flight.Key, out var current) && current == flight) _flights.Remove(flight.Key);
        if (cancelled) flight.Completion.TrySetCanceled(); else flight.Completion.TrySetResult(null);
        if (flight.Subscribers == 0 && !flight.Released) { flight.Released = true; flight.Cancellation.Dispose(); }
    }
    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                Flight? flight;
                bool prefetch;
                lock (_sync)
                {
                    if (!_queue.TryDequeue(out flight, out _) || flight.Started || flight.Released) continue;
                    // Cancelled prefetches must drain even while the two real prefetch
                    // workers are occupied, rather than retaining recycled tile requests.
                    prefetch = flight.Priority == ImagePriority.Prefetch && !flight.Cancellation.IsCancellationRequested;
                    // Reserve workers and transfer capacity for newly visible artwork.
                    if (prefetch && _activePrefetch >= 2) { _queue.Enqueue(flight, 1); continue; }
                    if (prefetch) _activePrefetch++;
                    flight.Started = true;
                }
                try { await ProcessAsync(flight).ConfigureAwait(false); }
                finally { lock (_sync) { if (prefetch) _activePrefetch--; if (_queue.Count > 0) _signal.Release(); } }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task ProcessAsync(Flight flight)
    {
                CancellationToken cancellation;
                lock (_sync) { if (flight.Released || _disposed) { flight.Completion.TrySetCanceled(); return; } cancellation = flight.Cancellation.Token; }
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellation);
                try
                {
                    var image = await ReadAsync(flight.Request, linked.Token).ConfigureAwait(false);
                    if (image is not null && !linked.IsCancellationRequested) Remember(flight.CacheKey, image);
                    flight.Completion.TrySetResult(image);
                }
                catch (OperationCanceledException) { if (linked.IsCancellationRequested) flight.Completion.TrySetCanceled(); else flight.Completion.TrySetResult(null); }
                catch (Exception error) when (error is IOException or HttpRequestException or ArgumentException or NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
                { flight.Completion.TrySetResult(null); }
                finally
                {
                    lock (_sync)
                    {
                        if (_flights.TryGetValue(flight.Key, out var current) && current == flight) _flights.Remove(flight.Key);
                        if (flight.Subscribers == 0 && !flight.Released) { flight.Released = true; flight.Cancellation.Dispose(); }
                    }
                }
    }
    private string CacheFile(ImageRequest request) => request.Source.IsFile ? request.Source.LocalPath
        : request.CachePath ?? Path.Combine(DiskDirectory, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Source.AbsoluteUri))) + ".image");
    internal void RememberDerived(ImageSource owner, int variant, BitmapSource image)
    {
        var id = _derivedOwners.GetValue(owner, _ => new(Interlocked.Increment(ref _nextDerivedOwner)));
        Remember($"continuation:{id.Id}:{variant}", image);
    }
    private void Remember(string key, BitmapSource image)
    {
        // WIC can preserve 48/64-bit artwork. Account for its actual format while
        // reserving at least the 32-bit surface used by ordinary rendered images.
        var stride = ((long)image.PixelWidth * Math.Max(32, image.Format.BitsPerPixel) + 7) / 8;
        var bytes = stride * image.PixelHeight;
        lock (_sync)
        {
            if (_disposed || bytes > _memoryLimit) return;
            if (_cache.TryGetValue(key, out var hit))
            {
                if (ReferenceEquals(hit.Image, image)) { _lru.Remove(hit.Node); _lru.AddFirst(hit.Node); return; }
                // A weak ambient variant can be regenerated while its previous version
                // is still cached. Retain the surface the caller actually uses.
                _cache.Remove(key); _lru.Remove(hit.Node); _bytes -= hit.Bytes;
            }
            while (_bytes + bytes > _memoryLimit && _lru.Last is { } oldest)
            { _bytes -= _cache[oldest.Value].Bytes; _cache.Remove(oldest.Value); _lru.RemoveLast(); }
            _cache.Add(key, new(image, bytes, _lru.AddFirst(key))); _bytes += bytes;
        }
    }
    private async Task<BitmapImage?> ReadAsync(ImageRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Source.IsFile && string.Equals(Path.GetExtension(request.Source.LocalPath), ".exe", StringComparison.OrdinalIgnoreCase))
            return LocalGameArtwork.Read(request.Source.LocalPath, request.PixelWidth, request.PixelHeight);
        var disk = request.CachePolicy == ImageCachePolicy.PublicArtwork;
        var file = disk || request.Source.IsFile ? CacheFile(request) : string.Empty;
        if ((disk || request.Source.IsFile) && File.Exists(file))
        {
            try
            {
                if (new FileInfo(file).Length <= MaxDownloadBytes)
                {
                    var bytes = await File.ReadAllBytesAsync(file, token).ConfigureAwait(false);
                    var image = await DecodeAsync(bytes, request, token).ConfigureAwait(false);
                    if (image is not null) { if (disk && !request.Source.IsFile) QueueDiskTouch(file); return image; }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (request.Source.IsFile || request.Priority == ImagePriority.CacheOnly) return null;
        var content = await LoadTransientContentAsync(request.Source, token).ConfigureAwait(false);
        if (content is null) return null;
        var decoded = await DecodeAsync(content, request, token).ConfigureAwait(false);
        if (decoded is not null && disk)
        {
            var temporary = file + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(temporary, content, token).ConfigureAwait(false);
                File.Move(temporary, file, overwrite: true); TrackDisk(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        return decoded;
    }
    // Compressed data is shared only while consumers are using a transfer. In particular,
    // GIFs and private attachments never enter a persistent byte cache.
    internal async Task<byte[]?> LoadTransientContentAsync(Uri uri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!uri.IsAbsoluteUri || uri.Scheme != "https") return null;
        var key = uri.AbsoluteUri; Transfer transfer; var start = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_transfers.TryGetValue(key, out transfer!)) { transfer = new(); _transfers.Add(key, transfer); start = true; }
            transfer.Subscribers++;
        }
        var transferToken = transfer.Cancellation.Token;
        if (start) _ = Task.Run(async () =>
        {
            if (transfer.Completion.Task.IsCompleted) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, transferToken);
            try
            {
                await _network.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDownloadBytes) { transfer.Completion.TrySetResult(null); return; }
                    await using var source = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                    using var memory = new MemoryStream(); var buffer = new byte[16384]; int read;
                    while ((read = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) != 0)
                    { if (memory.Length + read > MaxDownloadBytes) { transfer.Completion.TrySetResult(null); return; } memory.Write(buffer, 0, read); }
                    transfer.Completion.TrySetResult(memory.ToArray());
                }
                finally { _network.Release(); }
            }
            catch (OperationCanceledException) { if (linked.IsCancellationRequested) transfer.Completion.TrySetCanceled(); else transfer.Completion.TrySetResult(null); }
            catch (Exception error) { transfer.Completion.TrySetException(error); }
            finally
            {
                lock (_sync)
                {
                    if (_transfers.TryGetValue(key, out var current) && current == transfer) _transfers.Remove(key);
                }
            }
        });
        try { return await transfer.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
                if (--transfer.Subscribers == 0)
                {
                    if (_transfers.TryGetValue(key, out var current) && current == transfer) _transfers.Remove(key);
                    if (!transfer.Completion.Task.IsCompleted) transfer.Cancellation.Cancel();
                    // The worker may still be unwinding a cancelled HTTP call.
                    _ = transfer.Completion.Task.ContinueWith(_ => transfer.Cancellation.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
        }
    }
    private async Task<BitmapImage?> DecodeAsync(byte[] bytes, ImageRequest request, CancellationToken token)
    {
        await _decode.WaitAsync(token).ConfigureAwait(false);
        try { return Decode(bytes, request.PixelWidth, request.PixelHeight); }
        finally { _decode.Release(); }
    }
    internal static BitmapImage? Decode(byte[] bytes, int width, int height = 0)
    {
        if (bytes.Length == 0 || bytes.Length > MaxDownloadBytes) return null;
        if (bytes.Length <= 8192 && Convert.ToHexString(SHA256.HashData(bytes)) is
            "574128283765E00DD4EEF95EFAA40F54AA60FDA25AD01A56B265A8B1D0C655CD" or
            "9EA238901BE13C138C061365CC705840BB757FD076A66EC3731C1948BB2B6A44" or
            "3514810F0A5A3F0D55D756924B16F33D5AAFAFC8996DBC501631ED534798F3A8" or
            "CF39EAA2E57FE66C0B7B92F371F3E945BE5631CB96D9002F4DED1417E13843B8") return null;
        try
        {
            using var stream = new ReleasingImageStream(bytes);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if ((long)frame.PixelWidth * frame.PixelHeight > 40_000_000 || Math.Max(frame.PixelWidth, frame.PixelHeight) > 32768) return null;
            var horizontal = Math.Min(1, Math.Clamp(width, 1, 2560) / (double)Math.Max(1, frame.PixelWidth));
            var vertical = Math.Min(1, (height > 0 ? height : 2560) / (double)Math.Max(1, frame.PixelHeight));
            var scale = Math.Min(horizontal, vertical);
            stream.Position = 0;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            if (vertical < horizontal) image.DecodePixelHeight = Math.Max(1, (int)(frame.PixelHeight * scale));
            else image.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * scale));
            image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.ExternalException) { return null; }
    }
    private void QueueDiskTouch(string file)
    {
        // Access-order maintenance is best effort. A decoded image must not wait
        // for a directory scan, timestamp write or another cache's eviction pass.
        lock (_sync)
        {
            if (_disposed || _pendingDiskTouches.Count >= 128 || !_pendingDiskTouches.Add(file)) return;
            if (_diskTouchWorker) return;
            _diskTouchWorker = true;
            _ = Task.Run(ProcessDiskTouches);
        }
    }
    private void ProcessDiskTouches()
    {
        while (true)
        {
            string file;
            lock (_sync)
            {
                if (_disposed || _pendingDiskTouches.Count == 0)
                { _pendingDiskTouches.Clear(); _diskTouchWorker = false; return; }
                file = _pendingDiskTouches.First();
                _pendingDiskTouches.Remove(file);
            }
            TrackDisk(file);
        }
    }
    private void TrackDisk(string file)
    {
        lock (_diskSync)
        {
            try
            {
                var directory = Path.GetDirectoryName(file)!;
                // Scan only this application's public artwork cache, never the source of a local image.
                ScanDiskDirectory(directory);
                if (_diskFiles.Remove(file, out var previous)) _diskBytes -= previous.Bytes;
                var length = new FileInfo(file).Length; _diskFiles[file] = (length, ++_access); _diskBytes += length;
                File.SetLastAccessTimeUtc(file, DateTime.UtcNow);
                while (_diskBytes > _diskLimit && _diskFiles.Count > 0)
                {
                    var oldest = _diskFiles.MinBy(pair => pair.Value.Access);
                    File.Delete(oldest.Key); _diskBytes -= oldest.Value.Bytes; _diskFiles.Remove(oldest.Key);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private void ScanDiskDirectory(string directory)
    {
        if (!_scannedDirectories.Add(directory) || !Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory).Where(path => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".png" or ".image").OrderBy(File.GetLastAccessTimeUtc))
        { var info = new FileInfo(path); if (_diskFiles.TryAdd(path, (info.Length, ++_access))) _diskBytes += info.Length; }
    }
    private void SweepPublicDiskCaches()
    {
        lock (_diskSync)
        {
            if (_disposed) return;
            try
            {
                var directory = DiskDirectory; var root = Path.GetDirectoryName(directory)!;
                foreach (var path in new[] { directory, Path.Combine(root, "artwork"), Path.Combine(root, "steam-catalog", "artwork") }) ScanDiskDirectory(path);
                while (_diskBytes > _diskLimit && _diskFiles.Count > 0)
                { var oldest = _diskFiles.MinBy(pair => pair.Value.Access); File.Delete(oldest.Key); _diskBytes -= oldest.Value.Bytes; _diskFiles.Remove(oldest.Key); }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return; _disposed = true; _lifetime.Cancel();
            foreach (var flight in _flights.Values) { if (!flight.Released) flight.Cancellation.Cancel(); flight.Completion.TrySetCanceled(); }
            foreach (var transfer in _transfers.Values) { transfer.Cancellation.Cancel(); transfer.Completion.TrySetCanceled(); }
            _flights.Clear(); _cache.Clear(); _lru.Clear(); _pendingDiskTouches.Clear(); _bytes = 0;
        }
        if (_ownsHttp) _http.Dispose();
    }
}
