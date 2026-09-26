using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Steamy.Services;

/// <summary>
/// Downloads the latest CreamInstaller build from GitHub and keeps a local cache so the
/// CreamInstaller page can run without the user having to browse for the executable.
/// </summary>
public interface ICreamInstallerDownloadService
{
    /// <summary>
    /// Returns the path to a cached CreamInstaller.exe or null when nothing usable is cached.
    /// </summary>
    string? CachedPath { get; }

    /// <summary>True when a usable cached executable exists.</summary>
    bool HasCachedExecutable { get; }

    /// <summary>
    /// Fetches the latest release from GitHub, downloads the first matching CreamInstaller .exe
    /// and writes it to the local cache. Reports progress and the result.
    /// </summary>
    Task<CreamInstallerDownloadResult> DownloadLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>Clears the cached executable. The file is never touched if it does not exist.</summary>
    void ClearCache();
}

public sealed record CreamInstallerDownloadResult(
    bool Succeeded,
    string Message,
    string? CachedPath,
    string? Version,
    int? DownloadedBytes);

/// <summary>
/// Minimal GitHub release payload only for what this service needs.
/// </summary>
public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);

public sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

public sealed class CreamInstallerDownloadService : ICreamInstallerDownloadService, IDisposable
{
    private const string RepoOwner = "FroggMaster";
    private const string RepoName = "CreamInstaller";
    private const string ReleaseApiUrl = "https://api.github.com/repos/{0}/{1}/releases/latest";

    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly bool _ownsHttpClient;
    private readonly string _cachedExecutablePath;

    public CreamInstallerDownloadService(HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/0.1 (CreamInstaller downloader)");
        }

        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steamy",
            "CreamInstallerCache");

        Directory.CreateDirectory(_cacheDirectory);
        _cachedExecutablePath = Path.Combine(_cacheDirectory, "CreamInstaller.exe");
    }

    public string? CachedPath => File.Exists(_cachedExecutablePath) ? _cachedExecutablePath : null;

    public bool HasCachedExecutable => File.Exists(_cachedExecutablePath);

    public async Task<CreamInstallerDownloadResult> DownloadLatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await FetchLatestReleaseAsync(cachedTag: HasCachedExecutable
                ? GitHubReleaseFileNameFromPath(_cachedExecutablePath)
                : null, cancellationToken).ConfigureAwait(false);

            if (release is null)
                return new CreamInstallerDownloadResult(false, "The GitHub release could not be reached.", null, null, null);

            var asset = SelectAsset(release.Assets);
            if (asset is null)
                return new CreamInstallerDownloadResult(false, "No suitable CreamInstaller .exe was found in the latest release.", null, release.TagName, null);

            var downloaded = await DownloadAssetAsync(asset.BrowserDownloadUrl, _cachedExecutablePath, cancellationToken).ConfigureAwait(false);
            if (downloaded is null)
                return new CreamInstallerDownloadResult(false, "The executable could not be downloaded.", null, release.TagName, null);

            return new CreamInstallerDownloadResult(true, $"CreamInstaller saved to {_cachedExecutablePath}.", _cachedExecutablePath, release.TagName, downloaded);
        }
        catch (OperationCanceledException)
        {
            return new CreamInstallerDownloadResult(false, "The download was cancelled.", CachedPath, null, null);
        }
        catch (Exception exception)
        {
            return new CreamInstallerDownloadResult(false, $"The download failed: {exception.Message}.", CachedPath, null, null);
        }
    }

    public void ClearCache()
    {
        if (File.Exists(_cachedExecutablePath))
            TryDelete(_cachedExecutablePath);
    }

    private async Task<GitHubRelease?> FetchLatestReleaseAsync(string? cachedTag, CancellationToken cancellationToken)
    {
        var url = string.Format(ReleaseApiUrl, RepoOwner, RepoName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (!string.IsNullOrWhiteSpace(cachedTag))
        {
            request.Headers.IfNoneMatch.Clear();
            request.Headers.TryAddWithoutValidation("If-None-Match", cachedTag);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return null;

        if (!response.IsSuccessStatusCode)
            return null;

        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken).ConfigureAwait(false);
        if (release is null) return null;

        return release with { TagName = release.TagName.TrimStart('v') };
    }

    private static GitHubAsset? SelectAsset(IReadOnlyList<GitHubAsset> assets)
    {
        if (assets is null) return null;

        for (var i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];
            if (string.IsNullOrWhiteSpace(asset.Name)) continue;
            if (IsCreamInstallerExe(asset.Name)) return asset;
        }

        return null;
    }

    private static bool IsCreamInstallerExe(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("creaminstaller") && lower.EndsWith(".exe");
    }

    private async Task<int?> DownloadAssetAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
            return null;

        var totalBytes = (int?)null;
        var bytesRead = 0L;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            bytesRead += read;
        }

        totalBytes = (int)bytesRead;
        return totalBytes;
    }

    private static string GitHubReleaseFileNameFromPath(string path)
    {
        var name = Path.GetFileName(path);
        return "v" + (name?.TrimEnd('.') ?? "cached");
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Cache cleanup must never block the application.
        }
    }
}
