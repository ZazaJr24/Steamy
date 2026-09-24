using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace SteamContentManager.Services;

/// <summary>
/// Downloads the steam-ticket-generator release from GitHub, extracts the bundled
/// steam-ticket-generator.exe and steam_api64.dll into a local cache folder, and
/// exposes the executable path so the Denuvo Activation page can run without asking
/// the user to download anything manually.
/// </summary>
public interface IDenuvoGeneratorDownloadService
{
    /// <summary>Path to the cached steam-ticket-generator.exe, or null when nothing is cached.</summary>
    string? CachedPath { get; }

    /// <summary>True when a usable cached executable exists.</summary>
    bool HasCachedExecutable { get; }

    /// <summary>
    /// Downloads the latest release from GitHub, extracts the tool and the Steam API DLL to the
    /// local cache, and returns the executable path. Reports progress and the result.
    /// </summary>
    Task<DenuvoGeneratorDownloadResult> DownloadLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>Clears the cached files. Nothing is deleted if the files do not exist.</summary>
    void ClearCache();
}

public sealed record DenuvoGeneratorDownloadResult(
    bool Succeeded,
    string Message,
    string? CachedPath,
    string? Version);

public sealed class DenuvoGeneratorDownloadService : IDenuvoGeneratorDownloadService, IDisposable
{
    private const string RepoOwner = "denuvosanctuary";
    private const string RepoName = "steam-ticket-generator";
    private const string GithubApiUrl = "https://api.github.com/repos/{0}/{1}/releases/latest";

    /// <summary>Version behind <see cref="KnownWindowsZipUrl"/>; only used when the API is unreachable.</summary>
    private const string KnownVersion = "v1.2.1";

    // Release-Asset-URL für die Windows-ZIP-Datei. Diese URL kann sich ändern, wenn neue Releases
    // veröffentlicht werden. In einem späteren Schritt könnte der Download-Service die Asset-Liste
    // abfragen und automatisch die passende ZIP-Datei auswählen.
    //
    // Aktuell: wir nutzen die bekannte direkte Download-URL aus der Aufgabenstellung.
    private const string KnownWindowsZipUrl =
        "https://github.com/denuvosanctuary/steam-ticket-generator/releases/download/" + KnownVersion + "/steam-ticket-generator-windows.zip";

    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly string _cachedExePath;
    private readonly string _cachedDllPath;
    private bool _ownsHttpClient;

    public DenuvoGeneratorDownloadService(HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SteamContentManager/0.1 (Denuvo generator downloader)");
        }

        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamContentManager",
            "DenuvoGeneratorCache");

        Directory.CreateDirectory(_cacheDirectory);
        _cachedExePath = Path.Combine(_cacheDirectory, "steam-ticket-generator.exe");
        _cachedDllPath = Path.Combine(_cacheDirectory, "steam_api64.dll");
    }

    public string? CachedPath => File.Exists(_cachedExePath) ? _cachedExePath : null;
    public bool HasCachedExecutable => File.Exists(_cachedExePath);

    public async Task<DenuvoGeneratorDownloadResult> DownloadLatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Stelle sicher, dass der Cache-Ordner existiert.
            Directory.CreateDirectory(_cacheDirectory);

            // Die Asset-URL trägt die Version im Pfad, eine fest verdrahtete URL veraltet also mit
            // dem nächsten Release. Deshalb zuerst die Release-API fragen und nur bei Fehlschlag
            // auf die bekannte URL zurückfallen.
            var release = await ResolveLatestWindowsAssetAsync(cancellationToken).ConfigureAwait(false);
            var downloadUrl = release?.Url ?? KnownWindowsZipUrl;
            var version = release?.Version ?? KnownVersion;

            var zipBytes = await DownloadZipAsync(downloadUrl, cancellationToken).ConfigureAwait(false);
            if (zipBytes is null || zipBytes.Length == 0)
                return new DenuvoGeneratorDownloadResult(false, "The tool ZIP could not be downloaded.", CachedPath, null);

            // Entpacke die ZIP in den Cache-Ordner.
            ExtractZip(zipBytes);

            if (!File.Exists(_cachedExePath))
                return new DenuvoGeneratorDownloadResult(false, "The expected steam-ticket-generator.exe was not found in the downloaded archive.", CachedPath, null);

            return new DenuvoGeneratorDownloadResult(true, $"Tool saved to {_cachedExePath}.", _cachedExePath, version);
        }
        catch (OperationCanceledException)
        {
            return new DenuvoGeneratorDownloadResult(false, "The download was cancelled.", CachedPath, null);
        }
        catch (Exception exception)
        {
            return new DenuvoGeneratorDownloadResult(false, $"The download failed: {exception.Message}.", CachedPath, null);
        }
    }

    public void ClearCache()
    {
        TryDelete(_cachedExePath);
        TryDelete(_cachedDllPath);
    }

    /// <summary>
    /// Reads the latest release and returns the Windows ZIP asset with the release tag. Returns
    /// null when the API is unreachable, rate limited or the release carries no Windows asset.
    /// </summary>
    private async Task<(string Url, string Version)?> ResolveLatestWindowsAssetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var apiUrl = string.Format(GithubApiUrl, RepoOwner, RepoName);
            using var response = await _httpClient
                .GetAsync(apiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("tag_name", out var tagElement))
                return null;

            var version = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(version))
                return null;

            if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameElement)) continue;

                var name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!name.Contains("windows", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                if (!asset.TryGetProperty("browser_download_url", out var urlElement)) continue;

                var url = urlElement.GetString();
                if (string.IsNullOrWhiteSpace(url)) continue;

                return (url, version);
            }

            return null;
        }
        catch (Exception)
        {
            // Any failure here just means: fall back to the known release URL.
            return null;
        }
    }

    private async Task<byte[]?> DownloadZipAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[81920];
        var bytesRead = 0L;

        await using var memoryStream = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await memoryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            bytesRead += read;
        }

        return memoryStream.ToArray();
    }

    private void ExtractZip(byte[] zipBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;

            var destPath = Path.Combine(_cacheDirectory, entry.Name);
            var entryDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrWhiteSpace(entryDir))
                Directory.CreateDirectory(entryDir);

            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cache cleanup must never block the application.
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
