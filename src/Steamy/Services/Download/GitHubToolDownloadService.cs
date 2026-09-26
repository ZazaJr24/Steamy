using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Steamy.Services;

public sealed record ToolDownloadResult(
    bool Succeeded,
    string Message,
    string? CachedPath,
    string? Version);

public sealed record GitHubToolDefinition(
    string RepoOwner,
    string RepoName,
    string CacheSubfolder,
    string TargetExecutableName,
    Func<string, bool>? AssetFilter = null);

public interface IGitHubToolDownloadService
{
    string? GetCachedPath(GitHubToolDefinition tool);
    bool HasCached(GitHubToolDefinition tool);
    Task<ToolDownloadResult> DownloadLatestAsync(GitHubToolDefinition tool, CancellationToken cancellationToken = default);
}

public sealed class GitHubToolDownloadService : IGitHubToolDownloadService, IDisposable
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/{0}/{1}/releases/latest";

    private readonly HttpClient _httpClient;
    private readonly string _baseCacheDir;
    private readonly bool _ownsHttpClient;

    public GitHubToolDownloadService(HttpClient? httpClient = null, string? baseCacheDir = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/0.1");

        _baseCacheDir = baseCacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steamy",
            "ToolCache");
    }

    public string? GetCachedPath(GitHubToolDefinition tool)
    {
        var path = ResolveCachedExe(tool);
        return File.Exists(path) ? path : null;
    }

    public bool HasCached(GitHubToolDefinition tool) => GetCachedPath(tool) is not null;

    public async Task<ToolDownloadResult> DownloadLatestAsync(GitHubToolDefinition tool, CancellationToken cancellationToken = default)
    {
        try
        {
            var cacheDir = Path.Combine(_baseCacheDir, tool.CacheSubfolder);
            Directory.CreateDirectory(cacheDir);
            var targetExe = Path.Combine(cacheDir, tool.TargetExecutableName);

            var release = await FetchLatestReleaseAsync(tool, cancellationToken).ConfigureAwait(false);
            if (release is null)
                return new ToolDownloadResult(false, $"Could not reach GitHub releases for {tool.RepoOwner}/{tool.RepoName}.", GetCachedPath(tool), null);

            var asset = SelectAsset(release.Assets, tool);
            if (asset is null)
                return new ToolDownloadResult(false, $"No matching asset found in {release.TagName} for {tool.RepoName}.", GetCachedPath(tool), release.TagName);

            var assetName = asset.Name.ToLowerInvariant();

            if (assetName.EndsWith(".zip") || assetName.EndsWith(".7z"))
            {
                var zipBytes = await DownloadBytesAsync(asset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
                if (zipBytes is null)
                    return new ToolDownloadResult(false, "Download failed.", GetCachedPath(tool), release.TagName);

                ExtractArchive(zipBytes, cacheDir);

                var exePath = FindExecutableInCache(cacheDir, tool.TargetExecutableName);
                if (exePath is null)
                    return new ToolDownloadResult(false, $"{tool.TargetExecutableName} not found in the downloaded archive.", GetCachedPath(tool), release.TagName);

                return new ToolDownloadResult(true, $"{tool.RepoName} {release.TagName} ready.", exePath, release.TagName);
            }
            else
            {
                var bytes = await DownloadBytesAsync(asset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
                if (bytes is null)
                    return new ToolDownloadResult(false, "Download failed.", GetCachedPath(tool), release.TagName);

                await File.WriteAllBytesAsync(targetExe, bytes, cancellationToken).ConfigureAwait(false);
                return new ToolDownloadResult(true, $"{tool.RepoName} {release.TagName} ready.", targetExe, release.TagName);
            }
        }
        catch (OperationCanceledException)
        {
            return new ToolDownloadResult(false, "Download cancelled.", GetCachedPath(tool), null);
        }
        catch (Exception ex)
        {
            return new ToolDownloadResult(false, $"Download failed: {ex.Message}", GetCachedPath(tool), null);
        }
    }

    private string ResolveCachedExe(GitHubToolDefinition tool)
    {
        var cacheDir = Path.Combine(_baseCacheDir, tool.CacheSubfolder);
        var direct = Path.Combine(cacheDir, tool.TargetExecutableName);
        if (File.Exists(direct)) return direct;

        var found = FindExecutableInCache(cacheDir, tool.TargetExecutableName);
        return found ?? direct;
    }

    private static string? FindExecutableInCache(string cacheDir, string targetName)
    {
        if (!Directory.Exists(cacheDir)) return null;

        var direct = Path.Combine(cacheDir, targetName);
        if (File.Exists(direct)) return direct;

        foreach (var file in Directory.EnumerateFiles(cacheDir, targetName, SearchOption.AllDirectories))
            return file;

        return null;
    }

    private async Task<GhRelease?> FetchLatestReleaseAsync(GitHubToolDefinition tool, CancellationToken ct)
    {
        try
        {
            var url = string.Format(ReleaseApiUrl, tool.RepoOwner, tool.RepoName);
            var release = await _httpClient.GetFromJsonAsync<GhRelease>(url, ct).ConfigureAwait(false);
            return release;
        }
        catch
        {
            return null;
        }
    }

    private static GhAsset? SelectAsset(IReadOnlyList<GhAsset>? assets, GitHubToolDefinition tool)
    {
        if (assets is null) return null;

        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Name)) continue;
            if (tool.AssetFilter is not null && tool.AssetFilter(asset.Name)) return asset;
        }

        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Name)) continue;
            var lower = asset.Name.ToLowerInvariant();
            if (lower.Contains("linux") || lower.Contains("macos") || lower.Contains("darwin")) continue;
            if (lower.EndsWith(".exe") || lower.EndsWith(".zip") || lower.EndsWith(".7z")) return asset;
        }

        return null;
    }

    private async Task<byte[]?> DownloadBytesAsync(string url, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static void ExtractArchive(byte[] archiveBytes, string destDir)
    {
        try
        {
            using var stream = new MemoryStream(archiveBytes);
            using var archive = ArchiveFactory.Open(stream);
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                entry.WriteToDirectory(destDir, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });
            }
        }
        catch
        {
            // Not a valid archive — might be a self-extracting exe, just save as-is
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed record GhRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] IReadOnlyList<GhAsset>? Assets);

    private sealed record GhAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}
