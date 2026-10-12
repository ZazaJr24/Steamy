using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DownloadGates = new(StringComparer.OrdinalIgnoreCase);

    public GitHubToolDownloadService(HttpClient? httpClient = null, string? baseCacheDir = null)
    {
        _httpClient = httpClient ?? new HttpClient(StableDnsHandler.Create());
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/0.1");

        _baseCacheDir = baseCacheDir ?? Path.Combine(Steamy.Storage.AppDataPaths.Root,
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
        var gate = DownloadGates.GetOrAdd(Path.GetFullPath(Path.Combine(_baseCacheDir, tool.CacheSubfolder)), _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith($"/{tool.RepoOwner}/{tool.RepoName}/releases/download/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unexpected tool release URL.");

            if (assetName.EndsWith(".zip") || assetName.EndsWith(".7z"))
            {
                var zipBytes = await DownloadBytesAsync(asset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
                if (zipBytes is null)
                    return new ToolDownloadResult(false, "Download failed.", GetCachedPath(tool), release.TagName);

                VerifyDigest(zipBytes, asset.Digest);
                var stage = Path.Combine(_baseCacheDir, tool.CacheSubfolder + ".preparing-" + Guid.NewGuid().ToString("N"));
                var backup = stage + ".previous";
                var committed = false;
                try
                {
                    Directory.CreateDirectory(stage); ExtractArchive(zipBytes, stage);
                    var stagedTarget = tool.TargetExecutableName.Equals("steamclient64.dll", StringComparison.OrdinalIgnoreCase)
                        ? FullSteamClient.Find(stage) : FindExecutableInCache(stage, tool.TargetExecutableName);
                    if (stagedTarget is null) throw new InvalidDataException($"{tool.TargetExecutableName} not found in the complete tool archive.");
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectLinks(cacheDir);
                    Directory.Move(cacheDir, backup);
                    try { Directory.Move(stage, cacheDir); committed = true; }
                    catch { Directory.Move(backup, cacheDir); throw; }
                    return new ToolDownloadResult(true, $"{tool.RepoName} {release.TagName} ready.", GetCachedPath(tool), release.TagName);
                }
                finally { CleanStage(stage); if (committed) CleanStage(backup); }
            }
            else
            {
                var bytes = await DownloadBytesAsync(asset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
                if (bytes is null)
                    return new ToolDownloadResult(false, "Download failed.", GetCachedPath(tool), release.TagName);

                VerifyDigest(bytes, asset.Digest);
                var temporary = targetExe + ".preparing-" + Guid.NewGuid().ToString("N");
                try
                {
                    RejectLinks(targetExe);
                    await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, targetExe, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
        finally { gate.Release(); }
    }

    private string ResolveCachedExe(GitHubToolDefinition tool)
    {
        var cacheDir = Path.Combine(_baseCacheDir, tool.CacheSubfolder);
        if (tool.TargetExecutableName.Equals("steamclient64.dll", StringComparison.OrdinalIgnoreCase))
            return FullSteamClient.Find(cacheDir) ?? Path.Combine(cacheDir, tool.TargetExecutableName);
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

        if (tool.AssetFilter is not null) return null;

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
        const long maximum = 256L * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Tool archive is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream(); var buffer = new byte[81920]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        { if (bytes.Length + count > maximum) throw new InvalidDataException("Tool archive is too large."); bytes.Write(buffer, 0, count); }
        return bytes.ToArray();
    }

    private static void VerifyDigest(byte[] bytes, string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return;
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tool release checksum failed.");
    }

    private static void ExtractArchive(byte[] archiveBytes, string destDir)
    {
        var root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
            using var stream = new MemoryStream(archiveBytes);
            using var archive = ArchiveFactory.OpenArchive(stream);
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Contains(':')) throw new InvalidDataException("Unsafe tool archive name.");
                var path = Path.GetFullPath(Path.Combine(destDir, (entry.Key ?? "").Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.Size < 0 || (total += entry.Size) > 1024L * 1024 * 1024)
                    throw new InvalidDataException("Unsafe tool archive entry.");
                if (!names.Add(path)) throw new InvalidDataException("Duplicate tool archive entry.");
                for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
                    if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Tool cache contains a symbolic link.");
                if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Tool cache file is a symbolic link.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.WriteToFile(path, new ExtractionOptions { Overwrite = true });
            }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private void CleanStage(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.GetFullPath(_baseCacheDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid tool staging path.");
        if (Directory.Exists(full)) { RejectLinks(full); Directory.Delete(full, true); }
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Tool folder contains a symbolic link.");
    }

    private sealed record GhRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] IReadOnlyList<GhAsset>? Assets);

    private sealed record GhAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest);
}
