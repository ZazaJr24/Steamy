using System.IO;
using System.Net.Http;
using System.Text;

namespace SteamContentManager.Services;

/// <summary>
/// Downloads the full Ryuu archive for an app id from
/// <c>https://generator.ryuu.lol/secure_download?appid=&lt;appid&gt;&amp;auth_code=&lt;key&gt;</c>.
/// The response is a ZIP containing the game's Lua, manifests and keys. The archive is cached
/// locally under the application's Ryuu download folder so the same app id is never downloaded
/// twice in a row. This is the only place that touches the Ryuu secure endpoint; the Game Fixes
/// workflow calls this, then hands the local archive path to DepotDownloaderMod/Zip extraction.
/// </summary>
public interface IRyuuSecureDownloadService
{
    /// <summary>
    /// Returns the per-application download folder used as cache for Ryuu archives.
    /// </summary>
    string CacheFolder { get; }

    /// <summary>
    /// Downloads the Ryuu archive for <paramref name="appId"/> using <paramref name="authCode"/>.
    /// If the archive already exists in the cache, the existing path is returned without a new
    /// network request.
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

    public RyuuSecureDownloadService(ISettingsService settings, HttpClient? httpClient = null)
    {
        _settings = settings;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromMinutes(20);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SteamContentManager/1.0 (Ryuu secure download)");

        _cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamContentManager",
            "ryuu-downloads");
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
        if (string.IsNullOrWhiteSpace(authCode))
            return new RyuuSecureDownloadResult(false, string.Empty, "No Ryuu auth code configured.");

        var archivePath = Path.Combine(_cacheFolder, $"{appId}.zip");
        TryEnsureFolder(_cacheFolder);

        if (File.Exists(archivePath))
            return new RyuuSecureDownloadResult(true, archivePath, $"Already cached: {archivePath}");

        var url = $"https://generator.ryuu.lol/secure_download?appid={appId}&auth_code={Uri.EscapeDataString(authCode)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new RyuuSecureDownloadResult(false, string.Empty, $"Ryuu returned HTTP {(int)response.StatusCode}.");

            var totalBytes = response.Content.Headers.ContentLength;
            await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            var totalRead = 0L;
            var lastPercent = 0.0;
            var startTime = DateTimeOffset.UtcNow;
            var currentFile = $"appid {appId}.zip";

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await networkStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                totalRead += read;

                var percent = totalBytes.HasValue && totalBytes.Value > 0
                    ? 100.0 * totalRead / totalBytes.Value
                    : Math.Min(99.0, lastPercent + (read / 1_048_576.0));
                lastPercent = percent;

                var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
                var speed = elapsed > 0 ? totalRead / elapsed : 0;
                var downloadedLabel = FormatSize(totalRead);
                var totalLabel = totalBytes.HasValue ? FormatSize(totalBytes.Value) : "—";
                var etaLabel = speed > 0 && totalBytes.HasValue && totalBytes.Value > totalRead
                    ? $"{(totalBytes.Value - totalRead) / speed / 1024.0:F0} min"
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

            if (fileStream.Length == 0)
            {
                TryDelete(archivePath);
                return new RyuuSecureDownloadResult(false, string.Empty, "Download finished but no data was written.");
            }

            progress?.Report(new RyuuSecureDownloadProgress(100, currentFile, FormatSize(totalRead), FormatSize(totalRead), "—", "Done"));
            return new RyuuSecureDownloadResult(true, archivePath, $"Downloaded {FormatSize(totalRead)} to {archivePath}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(archivePath);
            return new RyuuSecureDownloadResult(false, string.Empty, "Download cancelled.");
        }
        catch (Exception exception)
        {
            TryDelete(archivePath);
            return new RyuuSecureDownloadResult(false, string.Empty, $"Download failed: {exception.GetType().Name}.");
        }
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
