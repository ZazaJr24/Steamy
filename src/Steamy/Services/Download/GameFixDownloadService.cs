using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;

namespace Steamy.Services;

/// <summary>
/// Downloads a single game-fix archive from a remote URL and, when told to, extracts it into
/// a chosen game folder. It performs read-only HTTP GET requests, never stores credentials and
/// only ever writes into the folder the user selected — it does not touch
/// anything else on disk. Downloads land in the application's download folder (or a local fallback
/// temp folder), and extractions go into a subfolder named after the archive file so that a failed
/// run leaves its own folder rather than clobbering unrelated game files.
/// </summary>
public interface IGameFixDownloadService
{
    /// <summary>
    /// Downloads the archive at <paramref name="archiveUrl"/> into the application download folder
    /// and returns the local path of the downloaded file.
    /// </summary>
    Task<GameFixDownloadResult> DownloadAsync(
        string archiveUrl,
        string archiveFileName,
        string gameName,
        int appId,
        IProgress<GameFixDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? authToken = null);

    /// <summary>
    /// Extracts <paramref name="archivePath"/> into <paramref name="targetFolder"/>. The contents
    /// go into a subfolder named after the archive (without extension) to keep each fix isolated.
    /// Returns the extraction root and a best-effort total size of the extracted files.
    /// </summary>
    Task<GameFixApplyResult> ApplyAsync(
        string archivePath,
        string archiveFileName,
        string targetFolder,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the extracted folder created by an earlier <see cref="ApplyAsync"/> call for the
    /// given archive. Returns true when the folder existed and was removed.
    /// </summary>
    Task<bool> ResetAsync(string archiveFileName, string targetFolder, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts a ZIP archive into <paramref name="targetFolder"/>. The contents go into a
    /// subfolder named after the archive (without extension) so each extraction is isolated.
    /// Returns the extraction root and a best-effort total size of the extracted files.
    /// </summary>
    Task<GameFixApplyResult> ApplyArchiveAsync(
        string archivePath,
        string targetFolder,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the application-local folder used for downloads when no explicit download folder is
    /// configured in settings.
    /// </summary>
    string DefaultDownloadFolder { get; }
}

public sealed record GameFixDownloadProgress(
    double Percent,
    string CurrentFile,
    string Downloaded,
    string Total,
    string Speed,
    string Eta);

public sealed record GameFixDownloadResult(
    bool Succeeded,
    string LocalPath,
    string Message);

public sealed record GameFixApplyResult(
    bool Succeeded,
    string ExtractionRoot,
    string ExtractedSize,
    string Message);

/// <summary>
/// Concrete implementation that downloads fix archives via HTTP and extracts them with
/// <see cref="System.IO.Compression.ZipFile"/>. It owns its own <see cref="HttpClient"/> unless one
/// is supplied, and it mirrors the rest of the app's conventions: timeout, User-Agent and error
/// handling are all local and read-only.
/// </summary>
public sealed class GameFixDownloadService : IGameFixDownloadService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _downloadFolder;

    public GameFixDownloadService(ISettingsService settings, HttpClient? httpClient = null)
    {
        _settings = settings;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromMinutes(20);
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream, application/zip, */*;q=0.5");

        var configured = _settings.Load().DownloadFolder;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            _downloadFolder = configured;
        }
        else
        {
            _downloadFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Steamy",
                "game-fix-downloads");
            TryEnsureFolder(_downloadFolder);
        }
    }

    public string DefaultDownloadFolder => _downloadFolder;

    public async Task<GameFixDownloadResult> DownloadAsync(
        string archiveUrl,
        string archiveFileName,
        string gameName,
        int appId,
        IProgress<GameFixDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? authToken = null)
    {
        if (string.IsNullOrWhiteSpace(archiveUrl))
            return new GameFixDownloadResult(false, string.Empty, "No download URL was provided.");

        var localPath = Path.Combine(_downloadFolder, EscapeFileName(archiveFileName));
        TryEnsureFolder(_downloadFolder);

        // If a completed download for this exact file already exists, report it rather than
        // downloading again.
        if (File.Exists(localPath))
        {
            var existing = new FileInfo(localPath);
            if (existing.Length > 0 && !IsLfsPointer(localPath))
                return new GameFixDownloadResult(true, localPath, $"Already downloaded: {FormatSize(existing.Length)}");
        }

        try
        {
            var safeUrl = new Uri(archiveUrl.Contains(' ') ? archiveUrl.Replace(" ", "%20") : archiveUrl);
            using var request = new HttpRequestMessage(HttpMethod.Get, safeUrl);
            if (!string.IsNullOrWhiteSpace(authToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", authToken);
                request.Headers.Accept.ParseAdd("application/octet-stream");
            }
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new GameFixDownloadResult(false, string.Empty, $"Download failed: HTTP {(int)response.StatusCode} ({response.ReasonPhrase?.Trim() ?? "unknown"}).");

            var totalBytes = response.Content.Headers.ContentLength;
            await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            var totalRead = 0L;
            var lastProgress = 0.0;
            var startTime = DateTimeOffset.UtcNow;
            string? currentFile = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await networkStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                totalRead += read;
                var percent = totalBytes.HasValue && totalBytes.Value > 0
                    ? 100.0 * totalRead / totalBytes.Value
                    : Math.Min(99.0, lastProgress + (read / 1_048_576.0));
                lastProgress = percent;

                var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
                var speed = elapsed > 0 ? totalRead / elapsed : 0;
                var downloadedLabel = FormatSize(totalRead);
                var totalLabel = totalBytes.HasValue ? FormatSize(totalBytes.Value) : "—";
                var etaLabel = speed > 0 && totalBytes.HasValue && totalBytes.Value > totalRead
                    ? $"{(totalBytes.Value - totalRead) / speed / 1024.0:F0} min"
                    : "—";
                var speedLabel = FormatSize((long)speed) + "/s";

                progress?.Report(new GameFixDownloadProgress(
                    Math.Clamp(percent, 0, 100),
                    currentFile ?? archiveFileName,
                    downloadedLabel,
                    totalLabel,
                    speedLabel,
                    etaLabel));
            }

            var written = fileStream.Length;
            await fileStream.DisposeAsync().ConfigureAwait(false);
            if (written == 0)
            {
                TryDelete(localPath);
                return new GameFixDownloadResult(false, string.Empty, "Download finished but no data was written.");
            }

            if (IsLfsPointer(localPath))
            {
                TryDelete(localPath);
                return new GameFixDownloadResult(false, string.Empty, "The source returned a Git LFS pointer instead of the archive. Use the GitHub repository URL as the fixes source.");
            }

            progress?.Report(new GameFixDownloadProgress(100, archiveFileName, FormatSize(totalRead), FormatSize(totalRead), "—", "Done"));
            return new GameFixDownloadResult(true, localPath, $"Downloaded {FormatSize(totalRead)}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(localPath);
            return new GameFixDownloadResult(false, string.Empty, "Download cancelled.");
        }
        catch (Exception exception)
        {
            TryDelete(localPath);
            return new GameFixDownloadResult(false, string.Empty, $"Download failed: {exception.GetType().Name}.");
        }
    }

    public async Task<GameFixApplyResult> ApplyAsync(
        string archivePath,
        string archiveFileName,
        string targetFolder,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return new GameFixApplyResult(false, string.Empty, string.Empty, "The downloaded fix file was not found.");

        if (string.IsNullOrWhiteSpace(targetFolder))
            return new GameFixApplyResult(false, string.Empty, string.Empty, "No target folder was provided.");

        TryEnsureFolder(targetFolder);

        // Isolate each fix in its own subfolder so a bad extraction never clobbers the game tree.
        var extension = Path.GetExtension(archiveFileName);
        var baseName = string.IsNullOrWhiteSpace(extension) ? archiveFileName : archiveFileName[..^extension.Length];
        var extractionRoot = Path.Combine(targetFolder, EscapeFileName(baseName));

        if (Directory.Exists(extractionRoot))
        {
            // Be conservative: do not silently overwrite an existing extraction. Report it and reuse
            // the existing folder rather than deleting user files.
            progress?.Report($"Extraction folder already exists: {extractionRoot}");
            var size = FolderSize(extractionRoot);
            return new GameFixApplyResult(true, extractionRoot, FormatSize(size), "Already applied.");
        }

        try
        {
            Directory.CreateDirectory(extractionRoot);
            progress?.Report($"Extracting to {extractionRoot}…");

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipFile.ExtractToDirectory(archivePath, extractionRoot, overwriteFiles: false);
            }, cancellationToken).ConfigureAwait(false);

            var size = FolderSize(extractionRoot);
            progress?.Report($"Extracted {FormatSize(size)} into {extractionRoot}");
            return new GameFixApplyResult(true, extractionRoot, FormatSize(size), "Applied.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteDirectory(extractionRoot);
            return new GameFixApplyResult(false, string.Empty, string.Empty, "Extraction cancelled.");
        }
        catch (Exception exception)
        {
            TryDeleteDirectory(extractionRoot);
            return new GameFixApplyResult(false, string.Empty, string.Empty, $"Extraction failed: {exception.GetType().Name}.");
        }
    }

    public async Task<bool> ResetAsync(string archiveFileName, string targetFolder, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archiveFileName) || string.IsNullOrWhiteSpace(targetFolder))
            return false;

        var extension = Path.GetExtension(archiveFileName);
        var baseName = string.IsNullOrWhiteSpace(extension) ? archiveFileName : archiveFileName[..^extension.Length];
        var folder = Path.Combine(targetFolder, EscapeFileName(baseName));

        if (!Directory.Exists(folder))
            return false;

        await Task.Run(() => TryDeleteDirectory(folder), cancellationToken).ConfigureAwait(false);
        return !Directory.Exists(folder);
    }

    public async Task<GameFixApplyResult> ApplyArchiveAsync(
        string archivePath,
        string targetFolder,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return new GameFixApplyResult(false, string.Empty, string.Empty, "The archive was not found.");

        if (string.IsNullOrWhiteSpace(targetFolder))
            return new GameFixApplyResult(false, string.Empty, string.Empty, "No target folder was provided.");

        TryEnsureFolder(targetFolder);

        var archiveFileName = Path.GetFileName(archivePath);
        if (string.IsNullOrWhiteSpace(archiveFileName))
            archiveFileName = "fix.zip";

        var baseName = Path.GetFileNameWithoutExtension(archiveFileName);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "fix";

        var extractionRoot = Path.Combine(targetFolder, EscapeFileName(baseName));

        if (Directory.Exists(extractionRoot))
        {
            progress?.Report($"Extraction folder already exists: {extractionRoot}");
            var size = FolderSize(extractionRoot);
            return new GameFixApplyResult(true, extractionRoot, FormatSize(size), "Already applied.");
        }

        try
        {
            Directory.CreateDirectory(extractionRoot);
            progress?.Report($"Extracting to {extractionRoot}…");

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipFile.ExtractToDirectory(archivePath, extractionRoot, overwriteFiles: false);
            }, cancellationToken).ConfigureAwait(false);

            var size = FolderSize(extractionRoot);
            progress?.Report($"Extracted {FormatSize(size)} into {extractionRoot}");
            return new GameFixApplyResult(true, extractionRoot, FormatSize(size), "Applied.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteDirectory(extractionRoot);
            return new GameFixApplyResult(false, string.Empty, string.Empty, "Extraction cancelled.");
        }
        catch (Exception exception)
        {
            TryDeleteDirectory(extractionRoot);
            return new GameFixApplyResult(false, string.Empty, string.Empty, $"Extraction failed: {exception.GetType().Name}.");
        }
    }

    private static bool IsLfsPointer(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 1024) return false;
            return File.ReadAllText(path).StartsWith("version https://git-lfs.github.com/spec/", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "fix";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new StringBuilder(name.Length);
        foreach (var ch in name)
            safe.Append(invalid.Contains(ch) ? '_' : ch);
        return safe.ToString();
    }

    private static void TryEnsureFolder(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch { /* If we cannot create the folder, downloads will fail explicitly. */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    private static long FolderSize(string folder)
    {
        var info = new DirectoryInfo(folder);
        long size = 0;
        try
        {
            foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
                size += file.Length;
        }
        catch { }
        return size;
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
