using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using SteamContentManager.Models;

namespace SteamContentManager.Services;

public interface IHubcapCatalogService
{
    Task<SteamCatalogSnapshot> GetGamesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

public sealed class HubcapCatalogService : IHubcapCatalogService, IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(4);

    private readonly ISettingsService _settings;
    private readonly ISecureCredentialService _credentials;
    private readonly HttpClient _httpClient;
    private readonly string _cachePath;

    public HubcapCatalogService(ISettingsService settings, ISecureCredentialService credentials)
    {
        _settings = settings;
        _credentials = credentials;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SteamContentManager/1.0");

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamContentManager", "hubcap-catalog");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "games.json");
    }

    public async Task<SteamCatalogSnapshot> GetGamesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        TryReadCache(out var cached, out var cachedAt);

        if (!forceRefresh && cached.Count > 0 && DateTimeOffset.UtcNow - cachedAt < CacheLifetime)
            return new SteamCatalogSnapshot(true, cached, cachedAt, true, $"Loaded {cached.Count:N0} Hubcap games from cache.");

        var key = await _credentials.ReadAsync("hubcap-api-key");
        if (string.IsNullOrWhiteSpace(key))
        {
            if (cached.Count > 0)
                return new SteamCatalogSnapshot(true, cached, cachedAt, true, $"No Hubcap API key — showing {cached.Count:N0} cached games.");
            return SteamCatalogSnapshot.Failure("No Hubcap API key configured. Set it in Settings → Hubcap API Key.");
        }

        var baseUrl = _settings.Load().HubcapBaseUrl?.TrimEnd('/') ?? "https://hubcapmanifest.com";

        try
        {
            var items = await FetchAllGamesAsync(baseUrl, key, cancellationToken);
            if (items.Count > 0)
            {
                SaveCache(items);
                return new SteamCatalogSnapshot(true, items, DateTimeOffset.UtcNow, false,
                    $"Loaded {items.Count:N0} games from Hubcap.");
            }

            if (cached is { Count: > 0 })
                return new SteamCatalogSnapshot(true, cached, cachedAt, true, "Hubcap returned no games; showing cached data.");
            return SteamCatalogSnapshot.Failure("Hubcap returned no games.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (cached is { Count: > 0 })
                return new SteamCatalogSnapshot(true, cached, cachedAt, true,
                    $"Hubcap unavailable; showing {cached.Count:N0} cached games.");
            return SteamCatalogSnapshot.Failure($"Hubcap unavailable: {ex.GetType().Name}");
        }
    }

    private async Task<List<SteamCatalogItem>> FetchAllGamesAsync(string baseUrl, string key, CancellationToken ct)
    {
        var all = new Dictionary<int, SteamCatalogItem>();

        // Hubcap search requires min 3 chars. Search with common broad terms + letter sweeps
        // to collect as many games as possible.
        var queries = new[] { "the", "call", "war", "dark", "dead", "star", "world", "sim", "city",
            "red", "age", "god", "life", "last", "final", "grand", "need", "far", "tom",
            "pro", "street", "race", "dragon", "king", "battle", "doom", "evil", "night",
            "shadow", "fire", "ice", "steel", "craft", "over", "under", "super", "mega",
            "ultra", "space", "cyber", "auto", "euro", "truck", "farm", "train", "flight",
            "assassin", "hitman", "resident", "monster", "hunter", "rise", "fall", "prey" };

        foreach (var q in queries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var url = $"{baseUrl}/api/v1/search?q={Uri.EscapeDataString(q)}&limit=50";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
                using var resp = await _httpClient.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                var arr = doc.RootElement.TryGetProperty("results", out var r) ? r
                    : doc.RootElement.TryGetProperty("games", out var g) ? g
                    : (JsonElement?)null;
                if (arr is null) continue;

                foreach (var item in arr.Value.EnumerateArray())
                {
                    var id = item.TryGetProperty("game_id", out var gid)
                        ? (int.TryParse(gid.ToString(), out var p1) ? p1 : 0)
                        : (item.TryGetProperty("app_id", out var aid)
                            ? (int.TryParse(aid.ToString(), out var p2) ? p2 : 0) : 0);
                    if (id <= 0 || all.ContainsKey(id)) continue;

                    var name = item.TryGetProperty("game_name", out var gn)
                        ? gn.GetString() ?? $"App {id}"
                        : (item.TryGetProperty("name", out var n) ? n.GetString() ?? $"App {id}" : $"App {id}");

                    all[id] = new SteamCatalogItem
                    {
                        AppId = id,
                        Name = name,
                        AppType = SteamCatalogAppType.Game,
                        HeaderImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{id}/header.jpg",
                        CapsuleImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{id}/capsule_616x353.jpg",
                        PortraitImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{id}/library_600x900_2x.jpg",
                        LibraryImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{id}/library_600x900_2x.jpg"
                    };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        return all.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool TryReadCache(out List<SteamCatalogItem> items, out DateTimeOffset writtenAt)
    {
        items = new List<SteamCatalogItem>();
        writtenAt = DateTimeOffset.MinValue;
        try
        {
            if (!File.Exists(_cachePath)) return false;
            writtenAt = File.GetLastWriteTimeUtc(_cachePath);
            using var stream = File.OpenRead(_cachePath);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(stream);
            if (entries is null) return false;

            var seen = new HashSet<int>();
            foreach (var e in entries)
            {
                if (e.AppId <= 0 || !seen.Add(e.AppId)) continue;
                items.Add(new SteamCatalogItem
                {
                    AppId = e.AppId,
                    Name = e.Name ?? $"App {e.AppId}",
                    AppType = SteamCatalogAppType.Game,
                    HeaderImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{e.AppId}/header.jpg",
                    CapsuleImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{e.AppId}/capsule_616x353.jpg",
                    PortraitImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{e.AppId}/library_600x900_2x.jpg",
                    LibraryImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{e.AppId}/library_600x900_2x.jpg"
                });
            }
            return items.Count > 0;
        }
        catch { return false; }
    }

    private void SaveCache(List<SteamCatalogItem> items)
    {
        try
        {
            var entries = items.Select(x => new CacheEntry(x.AppId, x.Name)).ToList();
            using var stream = File.Create(_cachePath);
            JsonSerializer.Serialize(stream, entries, new JsonSerializerOptions { WriteIndented = false });
        }
        catch { }
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record CacheEntry(int AppId, string Name);
}
