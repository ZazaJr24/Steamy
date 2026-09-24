using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamContentManager.Models;

namespace SteamContentManager.Services;

/// <summary>Loads the fixes catalog (<c>fixes.json</c>) from the fixes source set in Settings.</summary>
public interface IFixCatalogService
{
    Task<FixFeedSnapshot> GetFixesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

public sealed record FixFeedSnapshot(
    bool Succeeded,
    IReadOnlyList<FixGame> Games,
    DateTimeOffset UpdatedAt,
    bool FromCache,
    string Message)
{
    public static FixFeedSnapshot Failure(string message) =>
        new(false, Array.Empty<FixGame>(), DateTimeOffset.MinValue, false, message);
}

/// <summary>GitHub repos: catalog from the raw host, LFS archives from the media host (raw only serves LFS pointers, media 404s non-LFS files).</summary>
public sealed record FixSource(string FeedUrl, string FilesBaseUrl)
{
    public const string TokenCredentialName = "fix-mirror-token";

    public static FixSource? Resolve(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        var text = configured.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;

            var owner = parts[0];
            var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
            var branch = parts.Length >= 4 && parts[2].Equals("tree", StringComparison.OrdinalIgnoreCase) ? parts[3] : "main";
            return new FixSource(
                $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/fixes.json",
                $"https://media.githubusercontent.com/media/{owner}/{repo}/{branch}/");
        }

        var root = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        return new FixSource(root + "fixes.json", root);
    }

    public string FileUrl(FixEntry entry)
    {
        var relative = string.IsNullOrWhiteSpace(entry.Path) ? $"fixes/{entry.Filename}" : entry.Path;
        var segments = relative.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment is not "." and not "..")
            .Select(Uri.EscapeDataString);
        return FilesBaseUrl + string.Join('/', segments);
    }
}

public sealed class FixCatalogService : IFixCatalogService, IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    private readonly ISettingsService _settings;
    private readonly ISecureCredentialService _credentials;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _cachePath;

    public FixCatalogService(ISettingsService settings, ISecureCredentialService credentials, HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _settings = settings;
        _credentials = credentials;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(40);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ResonanceTools/1.0");

        var directory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamContentManager",
            "fix-catalog");
        Directory.CreateDirectory(directory);
        _cachePath = Path.Combine(directory, "fixes.json");
    }

    public async Task<FixFeedSnapshot> GetFixesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var source = FixSource.Resolve(_settings.Load().FixMirrorUrl);
        if (source is null)
            return FixFeedSnapshot.Failure("No fixes source is set. Add it in Settings → Fixes source.");

        if (!forceRefresh && TryGetCache(out var cachedAt) && DateTimeOffset.UtcNow - cachedAt < CacheLifetime)
        {
            var cached = await ReadGamesAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            if (cached.Count > 0)
                return new FixFeedSnapshot(true, cached, cachedAt, true, $"Loaded {cached.Count:N0} games from local cache.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.FeedUrl);
            var token = await _credentials.ReadAsync(FixSource.TokenCredentialName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("token", token);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // Download next to the cache first so a failed transfer never replaces a good cache.
            var partialPath = _cachePath + ".part";
            await using (var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var fileStream = File.Create(partialPath))
            {
                await networkStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            var items = await ReadGamesAsync(partialPath, cancellationToken).ConfigureAwait(false);
            if (items.Count == 0)
            {
                File.Delete(partialPath);
                return FixFeedSnapshot.Failure("The fixes catalog was empty or could not be read.");
            }

            File.Move(partialPath, _cachePath, overwrite: true);
            return new FixFeedSnapshot(true, items, DateTimeOffset.UtcNow, false, $"Loaded {items.Count:N0} games from the fixes source.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var reason = exception is HttpRequestException { StatusCode: { } status }
                ? $"HTTP {(int)status}"
                : exception.GetType().Name;
            var stale = await ReadGamesAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            if (stale.Count > 0)
                return new FixFeedSnapshot(true, stale, TryGetCache(out var at) ? at : DateTimeOffset.MinValue, true,
                    $"The fixes source is unavailable ({reason}); showing {stale.Count:N0} cached games.");
            return FixFeedSnapshot.Failure($"The fixes source is unavailable ({reason}). Check the URL and access token in Settings.");
        }
    }

    private bool TryGetCache(out DateTimeOffset writtenAt)
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                writtenAt = File.GetLastWriteTimeUtc(_cachePath);
                return true;
            }
        }
        catch
        {
            // Treat an unreadable cache as no cache.
        }

        writtenAt = DateTimeOffset.MinValue;
        return false;
    }

    private static async Task<List<FixGame>> ReadGamesAsync(string path, CancellationToken cancellationToken)
    {
        var games = new List<FixGame>();
        try
        {
            if (!File.Exists(path)) return games;
            await using var stream = File.OpenRead(path);
            var feed = await JsonSerializer.DeserializeAsync<List<FeedGame>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (feed is null) return games;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in feed)
            {
                var appId = entry?.AppId?.Trim() ?? string.Empty;
                if (appId.Length == 0 || !seen.Add(appId)) continue;

                var fixes = new List<FixEntry>();
                foreach (var f in entry!.Fixes ?? Array.Empty<FeedEntry>())
                {
                    if (f is null) continue;
                    fixes.Add(new FixEntry
                    {
                        Path = f.Path?.Trim() ?? string.Empty,
                        Filename = f.Filename?.Trim() ?? string.Empty,
                        Size = f.Size?.Trim() ?? string.Empty,
                        Badges = f.Badges?.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToList() ?? new List<string>()
                    });
                }

                games.Add(new FixGame
                {
                    AppId = appId,
                    Name = string.IsNullOrWhiteSpace(entry.Name) ? appId : entry.Name.Trim(),
                    Fixes = fixes
                });
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A partial or corrupt file yields whatever parsed before the failure.
        }

        return games;
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed class FeedGame
    {
        [JsonPropertyName("appid")]
        public string? AppId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("fixes")]
        public IReadOnlyList<FeedEntry>? Fixes { get; set; }
    }

    private sealed class FeedEntry
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("size")]
        public string? Size { get; set; }

        [JsonPropertyName("badges")]
        public IReadOnlyList<string>? Badges { get; set; }
    }
}
