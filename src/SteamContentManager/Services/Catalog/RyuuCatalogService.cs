using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamContentManager.Models;

namespace SteamContentManager.Services;

/// <summary>
/// Loads the list of games available through the configured Ryuu generator
/// (<c>&lt;base&gt;/files/games.json</c>) and maps it onto the same <see cref="SteamCatalogItem"/>
/// the library grid already renders, so the "available games" browse reuses the paged grid,
/// lazy artwork and search without any new gallery code.
/// </summary>
public interface IRyuuCatalogService
{
    Task<SteamCatalogSnapshot> GetGamesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

public sealed class RyuuCatalogService : IRyuuCatalogService, IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);

    private readonly ISettingsService _settings;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _cachePath;

    public RyuuCatalogService(ISettingsService settings, HttpClient? httpClient = null, string? cacheDirectory = null)
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
            "ryuu-catalog");
        Directory.CreateDirectory(directory);
        _cachePath = Path.Combine(directory, "games.json");
    }

    public async Task<SteamCatalogSnapshot> GetGamesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        // 1) Fresh-enough disk cache.
        if (!forceRefresh && TryGetCache(out var cachedAt) && DateTimeOffset.UtcNow - cachedAt < CacheLifetime)
        {
            var cached = await ReadItemsAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            if (cached.Count > 0)
                return new SteamCatalogSnapshot(true, cached, cachedAt, true, $"Loaded {cached.Count:N0} available games from local cache.");
        }

        // 2) Download from the configured Ryuu base URL.
        try
        {
            var url = BuildGamesUrl();
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var fileStream = File.Create(_cachePath))
            {
                await networkStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            var items = await ReadItemsAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            if (items.Count > 0)
                return new SteamCatalogSnapshot(true, items, DateTimeOffset.UtcNow, false, $"Loaded {items.Count:N0} available games from the Ryuu repository.");
            return SteamCatalogSnapshot.Failure("The Ryuu game list was empty or could not be read.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 3) Stale cache beats nothing.
            var stale = await ReadItemsAsync(_cachePath, cancellationToken).ConfigureAwait(false);
            if (stale.Count > 0)
                return new SteamCatalogSnapshot(true, stale, TryGetCache(out var at) ? at : DateTimeOffset.MinValue, true,
                    $"Ryuu is unavailable; showing {stale.Count:N0} cached available games.");
            return SteamCatalogSnapshot.Failure($"The Ryuu game list is unavailable ({exception.GetType().Name}).");
        }
    }

    private string BuildGamesUrl()
    {
        var baseUrl = _settings.Load().RyuuBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "https://generator.ryuu.lol/";
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        return baseUrl + "files/games.json";
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

    private static async Task<List<SteamCatalogItem>> ReadItemsAsync(string path, CancellationToken cancellationToken)
    {
        var items = new List<SteamCatalogItem>();
        try
        {
            if (!File.Exists(path)) return items;
            await using var stream = File.OpenRead(path);
            var games = await JsonSerializer.DeserializeAsync<List<RyuuGame>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (games is null) return items;

            var seen = new HashSet<int>();
            foreach (var game in games)
            {
                if (game is null || string.IsNullOrWhiteSpace(game.AppId)) continue;
                if (!int.TryParse(game.AppId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) || appId <= 0) continue;
                if (!seen.Add(appId)) continue;

                items.Add(new SteamCatalogItem
                {
                    AppId = appId,
                    Name = string.IsNullOrWhiteSpace(game.Name) ? $"App {appId}" : game.Name,
                    AppType = MapType(game.Type),
                    Nsfw = game.Nsfw,
                    // Real header from the feed; portrait/library covers come from Steam's CDN by
                    // app id, which the artwork loader already falls back through.
                    HeaderImageUrl = string.IsNullOrWhiteSpace(game.HeaderImage)
                        ? $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"
                        : game.HeaderImage,
                    CapsuleImageUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_616x353.jpg",
                    PortraitImageUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900_2x.jpg",
                    LibraryImageUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900_2x.jpg"
                });
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A partial/corrupt cache yields whatever parsed before the failure.
        }

        return items;
    }

    private static SteamCatalogAppType MapType(string? type) => (type ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "game" => SteamCatalogAppType.Game,
        "dlc" => SteamCatalogAppType.Dlc,
        "software" or "application" or "tool" => SteamCatalogAppType.Software,
        "video" or "movie" or "series" => SteamCatalogAppType.Video,
        "music" => SteamCatalogAppType.Music,
        "hardware" => SteamCatalogAppType.Hardware,
        _ => SteamCatalogAppType.Game
    };

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed class RyuuGame
    {
        [JsonPropertyName("appid")] public string? AppId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("header_image")] public string? HeaderImage { get; set; }
        [JsonPropertyName("nsfw")] public bool Nsfw { get; set; }
        [JsonPropertyName("drm")] public bool Drm { get; set; }
    }
}
