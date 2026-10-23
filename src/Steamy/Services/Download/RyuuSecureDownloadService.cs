using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http;

namespace Steamy.Services;

/// <summary>
/// Downloads a Ryuu archive through the Steamy relay without distributing its provider key.
/// An explicitly supplied personal key uses the provider directly. Only complete, validated
/// ZIPs are published atomically to the one-hour local cache.
/// </summary>
public interface IRyuuSecureDownloadService
{
    /// <summary>
    /// Returns the per-application download folder used as cache for Ryuu archives.
    /// </summary>
    string CacheFolder { get; }

    /// <summary>
    /// Downloads the Ryuu archive. An empty authCode selects the Steamy relay.
    /// A fresh, verified cached archive is reused without a network request.
    /// </summary>
    Task<RyuuSecureDownloadResult> DownloadAsync(
        int appId,
        string authCode,
        string gameName,
        IProgress<RyuuSecureDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record RyuuSecureDownloadProgress(
    double Percent,
    string CurrentFile,
    string Downloaded,
    string Total,
    string Speed,
    string Eta);

public sealed record RyuuSecureDownloadResult(
    bool Succeeded,
    string ArchivePath,
    string Message);

/// <summary>
/// Concrete implementation using a dedicated <see cref="HttpClient"/> with the same conventions
/// as the rest of the app: timeout, User-Agent, read-only GET, streaming.
/// </summary>
public sealed class RyuuSecureDownloadService : IRyuuSecureDownloadService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _cacheFolder;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private const long MaximumArchiveBytes = 128L * 1024 * 1024;

    private static HttpClient CreateClient()
    {
        var handler = StableDnsHandler.Create();
        if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
        return new HttpClient(handler);
    }

    public RyuuSecureDownloadService(ISettingsService settings, HttpClient? httpClient = null, string? cacheFolder = null)
    {
        _settings = settings;
        _httpClient = httpClient ?? CreateClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromMinutes(20);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0 (Ryuu secure download)");

        _cacheFolder = cacheFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy", "ryuu-downloads");
        TryEnsureFolder(_cacheFolder);
    }

    public string CacheFolder => _cacheFolder;

    public async Task<RyuuSecureDownloadResult> DownloadAsync(
        int appId,
        string authCode,
        string gameName,
        IProgress<RyuuSecureDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (appId <= 0) return new(false, string.Empty, "Choose a valid app ID.");
        var archivePath = Path.Combine(_cacheFolder, $"{appId}.zip");
        var partialPath = Path.Combine(_cacheFolder, $"{appId}-{Guid.NewGuid():N}.partial");
        TryEnsureFolder(_cacheFolder);
        var acquired = false;
        try
        {
            await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            if (File.Exists(archivePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(archivePath) < TimeSpan.FromHours(1)
                && IsValidArchive(archivePath))
                return new(true, archivePath, "Using the verified cached Ryuu archive.");

            var useRelay = string.IsNullOrWhiteSpace(authCode);
            using var request = useRelay ? ProviderRelay.CreateRequest("ryuu", appId, "archive")
                : new HttpRequestMessage(HttpMethod.Get, $"https://generator.ryuu.lol/api/download/{appId}");
            if (!useRelay) request.Headers.TryAddWithoutValidation("X-Auth-Key", authCode);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new RyuuSecureDownloadResult(false, string.Empty, $"Ryuu returned HTTP {(int)response.StatusCode}.");

            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes > MaximumArchiveBytes) return new(false, string.Empty, "Ryuu archive exceeds the size limit.");
            await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            var totalRead = 0L;
            var lastPercent = 0.0;
            var startTime = DateTimeOffset.UtcNow;
            var lastReport = Stopwatch.GetTimestamp();
            var currentFile = $"appid {appId}.zip";

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await networkStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
                if (totalRead > MaximumArchiveBytes) throw new InvalidDataException("Ryuu archive exceeds the size limit.");
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                // A fast transfer must not queue thousands of dispatcher callbacks.
                if (Stopwatch.GetElapsedTime(lastReport) < TimeSpan.FromMilliseconds(100)) continue;
                lastReport = Stopwatch.GetTimestamp();

                var percent = totalBytes.HasValue && totalBytes.Value > 0
                    ? 100.0 * totalRead / totalBytes.Value
                    : Math.Min(99.0, lastPercent + (read / 1_048_576.0));
                lastPercent = percent;

                var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
                var speed = elapsed > 0 ? totalRead / elapsed : 0;
                var downloadedLabel = FormatSize(totalRead);
                var totalLabel = totalBytes.HasValue ? FormatSize(totalBytes.Value) : "—";
                var etaLabel = speed > 0 && totalBytes.HasValue && totalBytes.Value > totalRead
                    ? $"{(totalBytes.Value - totalRead) / speed / 60.0:F0} min"
                    : "—";
                var speedLabel = FormatSize((long)speed) + "/s";

                progress?.Report(new RyuuSecureDownloadProgress(
                    Math.Clamp(percent, 0, 100),
                    currentFile,
                    downloadedLabel,
                    totalLabel,
                    speedLabel,
                    etaLabel));
            }

            await fileStream.DisposeAsync().ConfigureAwait(false);
            if (totalRead == 0 || totalBytes.HasValue && totalRead != totalBytes.Value || !IsValidArchive(partialPath))
                return new(false, string.Empty, "Ryuu returned an incomplete or invalid ZIP archive.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, archivePath, overwrite: true);

            progress?.Report(new RyuuSecureDownloadProgress(100, currentFile, FormatSize(totalRead), FormatSize(totalRead), "—", "Done"));
            return new RyuuSecureDownloadResult(true, archivePath, $"Downloaded {FormatSize(totalRead)} to {archivePath}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RyuuSecureDownloadResult(false, string.Empty, "Download cancelled.");
        }
        catch (Exception exception)
        {
            return new RyuuSecureDownloadResult(false, string.Empty, $"Download failed: {exception.GetType().Name}.");
        }
        finally
        {
            TryDelete(partialPath);
            if (acquired) _downloadGate.Release();
        }
    }

    private static bool IsValidArchive(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaximumArchiveBytes) return false;
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Count is > 0 and <= 10000;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    private static void TryEnsureFolder(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
