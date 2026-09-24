using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamContentManager.Models;

namespace SteamContentManager.Services;

/// <summary>
/// Loads the list of available game fixes from the configured Ryuu generator
/// (<c>&lt;base&gt;/files/fixes.json</c>) and maps it onto concrete feed models. It performs
/// read-only HTTP GET requests, never logs in, never stores credentials and never writes into
/// game folders — the actual download/extract workflow is handled by <see cref=\"IGameFixDownloadService\"/>.
/// </summary>
public interface IRyuuFixesService
{
    Task<RyuuFixFeedSnapshot> GetFixesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

public sealed record RyuuFixFeedSnapshot(
    bool Succeeded,
    IReadOnlyList<RyuuFixGame> Games,
    DateTimeOffset UpdatedAt,
    bool FromCache,
    string Message)
{
    public static RyuuFixFeedSnapshot Failure(string message) =>
        new(false, Array.Empty<RyuuFixGame>(), DateTimeOffset.MinValue, false, message);
}

/// <summary>
/// Reads <c>fixes.json</c> from the same Ryuu host that already provides <c>games.json</c>.
/// The feed is cached to disk under the same application data folder as the catalog cache, with
/// a 12-hour freshness window and a stale-cache fallback when the network is unavailable.
/// </summary>
public sealed class RyuuFixesService : IRyuuFixesService, IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    private readonly ISettingsService _settings;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _cachePath;

    public RyuuFixesService(ISettingsService settings, HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _settings = settings;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(40);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SteamContentManager/1.0");

        var directory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamContentManager",
            "ryuu-fixes");
        Directory.CreateDirectory(directory);
        _cachePath = Path.Combine(directory, "fixes.json");
    }

    public async Task<RyuuFixFeedSnapshot> GetFixesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && TryGetCache(out var cachedAt) && DateTimeOffset.UtcNow - cachedAt < CacheLifetime)
        {
            var cached = await ReadGamesAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            if (cached.Count > 0)
                return new RyuuFixFeedSnapshot(true, cached, cachedAt, true, $"Loaded {cached.Count:N0} games from local cache.");
        }

        try
        {
            var mirrorResult = await TryFetchFromMirrorAsync(cancellationToken).ConfigureAwait(false);
            if (mirrorResult is not null)
                return mirrorResult;

            var url = BuildFeedUrl();
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var fileStream = File.Create(_cachePath))
            {
                await networkStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            var items = await ReadGamesAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            if (items.Count > 0)
                return new RyuuFixFeedSnapshot(true, items, DateTimeOffset.UtcNow, false, $"Loaded {items.Count:N0} games from the Ryuu generator.");
            return RyuuFixFeedSnapshot.Failure("The Ryuu fixes feed was empty or could not be read.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var stale = await ReadGamesAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            if (stale.Count > 0)
                return new RyuuFixFeedSnapshot(true, stale, TryGetCache(out var at) ? at : DateTimeOffset.MinValue, true,
                    $"The Ryuu generator is unavailable; showing {stale.Count:N0} cached games.");
            return RyuuFixFeedSnapshot.Failure($"The Ryuu fixes feed is unavailable ({exception.GetType().Name}).");
        }
    }

    private async Task<RyuuFixFeedSnapshot?> TryFetchFromMirrorAsync(CancellationToken cancellationToken)
    {
        var mirrorUrl = _settings.Load().FixMirrorUrl;
        if (string.IsNullOrWhiteSpace(mirrorUrl))
            return null;

        try
        {
            if (!mirrorUrl.EndsWith('/')) mirrorUrl += "/";
            var feedUrl = mirrorUrl + "fixes.json";

            using var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
            var token = await ReadMirrorTokenAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", token);
            request.Headers.Accept.ParseAdd("application/vnd.github.v3.raw");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using (var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var fileStream = File.Create(_cachePath))
            {
                await networkStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            var items = await ReadGamesAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            if (items.Count > 0)
                return new RyuuFixFeedSnapshot(true, items, DateTimeOffset.UtcNow, false, $"Loaded {items.Count:N0} games from the fix mirror.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Mirror failed silently; fall through to Ryuu.
        }

        return null;
    }

    private async Task<string?> ReadMirrorTokenAsync()
    {
        try
        {
            var credentials = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetService<ISecureCredentialService>(App.Services);
            return credentials is not null ? await credentials.ReadAsync("fix-mirror-token").ConfigureAwait(false) : null;
        }
        catch
        {
            return null;
        }
    }

    private string BuildFeedUrl()
    {
        var baseUrl = _settings.Load().RyuuBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "https://generator.ryuu.lol/";
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        return baseUrl + "files/fixes.json";
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
            // Ignore; treat as no cache.
        }

        writtenAt = DateTimeOffset.MinValue;
        return false;
    }

    private static async Task<List<RyuuFixGame>> ReadGamesAsync(string path, CancellationToken cancellationToken)
    {
        var games = new List<RyuuFixGame>();
        try
        {
            if (!File.Exists(path)) return games;
            await using var stream = File.OpenRead(path);
            var feed = await JsonSerializer.DeserializeAsync<List<RyuuFixFeedGame>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (feed is null) return games;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in feed)
            {
                if (entry is null) continue;
                var appId = string.IsNullOrWhiteSpace(entry.AppId) ? string.Empty : entry.AppId.Trim();
                if (string.IsNullOrEmpty(appId)) continue;
                if (!seen.Add(appId)) continue;

                var fixes = new List<RyuuFixEntry>();
                if (entry.Fixes is not null)
                {
                    foreach (var f in entry.Fixes)
                    {
                        if (f is null) continue;
                        fixes.Add(new RyuuFixEntry
                        {
                            Href = string.IsNullOrWhiteSpace(f.Href) ? string.Empty : f.Href.Trim(),
                            Filename = string.IsNullOrWhiteSpace(f.Filename) ? string.Empty : f.Filename.Trim(),
                            Size = string.IsNullOrWhiteSpace(f.Size) ? string.Empty : f.Size.Trim(),
                            Badges = BuildBadges(f.Badges)
                        });
                    }
                }

                games.Add(new RyuuFixGame
                {
                    AppId = appId,
                    Name = string.IsNullOrWhiteSpace(entry.Name) ? appId : entry.Name.Trim(),
                    Fixes = fixes
                });
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A partial/corrupt cache yields whatever parsed before the failure.
        }

        return games;
    }

    private static IReadOnlyList<string> BuildBadges(IReadOnlyList<string>? badges)
    {
        if (badges is null) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var b in badges)
            if (!string.IsNullOrWhiteSpace(b))
                list.Add(b.Trim());
        return list;
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed class RyuuFixFeedGame
    {
        [JsonPropertyName("appid")]
        public string? AppId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("fixes")]
        public IReadOnlyList<RyuuFixFeedEntry>? Fixes { get; set; }
    }

    private sealed class RyuuFixFeedEntry
    {
        [JsonPropertyName("href")]
        public string? Href { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("size")]
        public string? Size { get; set; }

        [JsonPropertyName("badges")]
        public IReadOnlyList<string>? Badges { get; set; }
    }
}
