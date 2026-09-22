using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using SteamContentManager.Models;

namespace SteamContentManager.Services;

public interface ISteamCatalogService : IDisposable
{
    Task<SteamCatalogSnapshot> GetCatalogAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
    Task<SteamCatalogDetails?> GetDetailsAsync(int appId, CancellationToken cancellationToken = default);
    Task<bool> EnsureDetailsAsync(SteamCatalogItem item, CancellationToken cancellationToken = default);
    Task<bool> EnsureArtworkAsync(SteamCatalogItem item, bool includeHeader = false, CancellationToken cancellationToken = default);
    Task<bool> EnsureScreenshotsAsync(SteamCatalogItem item, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only access to public Steam catalog and Store metadata. No credentials, account data,
/// ownership checks, manifests or downloads are handled here.
/// </summary>
public sealed class SteamCatalogService : ISteamCatalogService
{
    public const string AppListUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";
    public const string GithubAppIdsUrl = "https://raw.githubusercontent.com/Dev12434/AppID-List/refs/heads/main/appids.txt";
    private const string DetailsUrl = "https://store.steampowered.com/api/appdetails?appids={0}&l=english&cc=us";
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan DetailsLifetime = TimeSpan.FromHours(12);
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly string? _bundledAppIdJsonPath;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private readonly SemaphoreSlim _detailsGate = new(4, 4);
    private readonly SemaphoreSlim _artworkGate = new(4, 4);
    private bool _disposed;

    public SteamCatalogService(HttpClient? httpClient = null, string? cacheDirectory = null, string? bundledAppIdJsonPath = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(12);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SteamContentManager/1.0");
        }

        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamContentManager",
            "steam-catalog");
        _bundledAppIdJsonPath = bundledAppIdJsonPath;
    }

    public async Task<SteamCatalogSnapshot> GetCatalogAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cachePath = Path.Combine(_cacheDirectory, "app-list.json");
            var cached = await ReadCatalogCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
            if (!forceRefresh && cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < CatalogLifetime)
            {
                return new SteamCatalogSnapshot(true, CreateItems(cached.Items), cached.FetchedAt, true, $"Loaded {cached.Items.Count:N0} public apps from local cache.");
            }

            // 1) Try GitHub appids.txt on every start (forceRefresh also re-fetches). This is the
            //    authoritative AppID list the user asked for. It is ~2.2 MB plain-text with one AppID
            //    per line; names come from the bundled appid.json fallback / Store details lazily.
            if (!forceRefresh || true)
            {
                try
                {
                    var githubIds = await FetchGithubAppIdsAsync(cancellationToken).ConfigureAwait(false);
                    if (githubIds.Count > 0)
                    {
                        var names = await LoadBundledNamesAsync(cancellationToken).ConfigureAwait(false);
                        var entries = MergeGithubWithNames(githubIds, names);
                        var fresh = new CatalogCache(DateTimeOffset.UtcNow, null, null,
                            entries.Select(item => new CatalogEntry(item.AppId, item.Name)).ToList());
                        await WriteJsonAsync(cachePath, fresh, cancellationToken).ConfigureAwait(false);
                        return new SteamCatalogSnapshot(true, CreateItems(fresh.Items), fresh.FetchedAt, false,
                            $"Loaded {fresh.Items.Count:N0} public apps from GitHub.");
                    }
                }
                catch (Exception exception) when (IsNetworkException(exception))
                {
                    // fall through to Steam API / bundled json
                }
            }

            // 2) Offline fallback: bundled Data/appid.json (28 MB) shipped with the app.
            var bundled = await TryLoadBundledAppIdJsonAsync(cancellationToken).ConfigureAwait(false);
            if (bundled is not null && bundled.Count > 0)
            {
                var fresh = new CatalogCache(DateTimeOffset.UtcNow, null, null,
                    bundled.Select(item => new CatalogEntry(item.AppId, item.Name)).ToList());
                await WriteJsonAsync(cachePath, fresh, cancellationToken).ConfigureAwait(false);
                return new SteamCatalogSnapshot(true, CreateItems(fresh.Items), fresh.FetchedAt, false,
                    $"Loaded {fresh.Items.Count:N0} public apps from bundled data (offline).");
            }

            try
            {
                using var response = await SendWithRetryAsync(
                    () =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, AppListUrl);
                        if (!string.IsNullOrWhiteSpace(cached?.ETag)) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
                        if (!string.IsNullOrWhiteSpace(cached?.LastModified)) request.Headers.TryAddWithoutValidation("If-Modified-Since", cached.LastModified);
                        return request;
                    },
                    cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
                {
                    cached = cached with { FetchedAt = DateTimeOffset.UtcNow };
                    await WriteJsonAsync(cachePath, cached, cancellationToken).ConfigureAwait(false);
                    return new SteamCatalogSnapshot(true, CreateItems(cached.Items), cached.FetchedAt, true, "Steam catalog cache validated by the public endpoint.");
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var entries = ParseAppList(json);
                var apiFresh = new CatalogCache(
                    DateTimeOffset.UtcNow,
                    response.Headers.ETag?.Tag,
                    response.Content.Headers.LastModified?.ToString(),
                    entries.Select(item => new CatalogEntry(item.AppId, item.Name)).ToList());
                await WriteJsonAsync(cachePath, apiFresh, cancellationToken).ConfigureAwait(false);
                return new SteamCatalogSnapshot(true, CreateItems(apiFresh.Items), apiFresh.FetchedAt, false, $"Loaded {apiFresh.Items.Count:N0} public apps from Steam.");
            }
            catch (Exception exception) when (IsNetworkException(exception))
            {
                if (cached is not null)
                {
                    return new SteamCatalogSnapshot(true, CreateItems(cached.Items), cached.FetchedAt, true, $"Steam is unavailable; showing {cached.Items.Count:N0} cached public apps.");
                }

                return SteamCatalogSnapshot.Failure($"Steam catalog is unavailable ({exception.GetType().Name}).");
            }
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public async Task<SteamCatalogDetails?> GetDetailsAsync(int appId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (appId <= 0) return null;

        var cachePath = Path.Combine(_cacheDirectory, "details", $"{appId.ToString(CultureInfo.InvariantCulture)}.json");
        var cached = await ReadDetailsCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
        if (cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < DetailsLifetime) return cached.Details;

        await _detailsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = await ReadDetailsCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
            if (cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < DetailsLifetime) return cached.Details;

            try
            {
                using var response = await SendWithRetryAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, string.Format(CultureInfo.InvariantCulture, DetailsUrl, appId)),
                    cancellationToken).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var details = ParseDetails(json, appId);
                if (details is null) return cached?.Details;
                await WriteJsonAsync(cachePath, new DetailsCache(DateTimeOffset.UtcNow, details), cancellationToken).ConfigureAwait(false);
                return details;
            }
            catch (Exception exception) when (IsNetworkException(exception))
            {
                return cached?.Details;
            }
        }
        finally
        {
            _detailsGate.Release();
        }
    }

    public async Task<bool> EnsureDetailsAsync(SteamCatalogItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.HasDetails && item.DetailsUpdatedAt is { } updated && DateTimeOffset.UtcNow - updated < DetailsLifetime) return true;

        item.IsDetailsLoading = true;
        item.DetailStatus = "Loading public Store details…";
        try
        {
            var details = await GetDetailsAsync(item.AppId, cancellationToken).ConfigureAwait(false);
            if (details is null)
            {
                item.DetailStatus = "Public Store details unavailable";
                return false;
            }

            item.Apply(details);
            item.DetailsUpdatedAt = DateTimeOffset.UtcNow;
            item.HasDetails = true;
            item.DetailStatus = "Public Store details loaded";
            return true;
        }
        catch (OperationCanceledException)
        {
            item.DetailStatus = "Details loading cancelled";
            throw;
        }
        catch (Exception exception) when (IsNetworkException(exception))
        {
            item.DetailStatus = "Public Store details unavailable";
            return false;
        }
        finally
        {
            item.IsDetailsLoading = false;
        }
    }

    public async Task<bool> EnsureArtworkAsync(SteamCatalogItem item, bool includeHeader = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ArtworkImage is not null && (!includeHeader || item.HeaderImage is not null)) return true;

        await _artworkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        item.IsArtworkLoading = true;
        try
        {
            var capsuleTask = item.ArtworkImage is null
                ? LoadArtworkWithFallbackAsync(item, cancellationToken)
                : Task.FromResult<BitmapImage?>(item.ArtworkImage);
            var headerTask = includeHeader && item.HeaderImage is null
                ? LoadImageAsync(item.HeaderImageUrl, Path.Combine(_cacheDirectory, "artwork", $"{item.AppId}_header.jpg"), cancellationToken)
                : Task.FromResult<BitmapImage?>(item.HeaderImage);

            var images = await Task.WhenAll(capsuleTask, headerTask).ConfigureAwait(false);
            item.ArtworkImage ??= images[0];
            if (includeHeader) item.HeaderImage ??= images[1];
            return item.ArtworkImage is not null || (!includeHeader || item.HeaderImage is not null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            item.IsArtworkLoading = false;
            _artworkGate.Release();
        }
    }

    private async Task<BitmapImage?> LoadArtworkWithFallbackAsync(SteamCatalogItem item, CancellationToken cancellationToken)
    {
        var urls = new[]
        {
            item.PortraitImageUrl,
            item.LibraryImageUrl,
            $"https://cdn.akamai.steamstatic.com/steam/apps/{item.AppId}/library_600x900_2x.jpg",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{item.AppId}/library_600x900.jpg",
            item.CapsuleImageUrl,
            $"https://cdn.akamai.steamstatic.com/steam/apps/{item.AppId}/capsule_616x353.jpg",
            item.HeaderImageUrl,
            $"https://cdn.akamai.steamstatic.com/steam/apps/{item.AppId}/header.jpg"
        }.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase);

        var cachePath = Path.Combine(_cacheDirectory, "artwork", $"{item.AppId}_portrait_v2.jpg");
        foreach (var url in urls)
        {
            var image = await LoadImageAsync(url, cachePath, cancellationToken).ConfigureAwait(false);
            if (image is not null) return image;
        }
        return null;
    }

    public async Task<bool> EnsureScreenshotsAsync(SteamCatalogItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ScreenshotsLoaded) return item.Screenshots.Any(screenshot => screenshot.Image is not null);
        if (!item.HasDetails && !await EnsureDetailsAsync(item, cancellationToken).ConfigureAwait(false)) return false;

        item.IsScreenshotsLoading = true;
        item.ScreenshotStatus = item.Screenshots.Count == 0
            ? "No public screenshots are listed"
            : "Loading public screenshots…";
        try
        {
            if (item.Screenshots.Count == 0)
            {
                item.ScreenshotsLoaded = true;
                return false;
            }

            await Task.WhenAll(item.Screenshots.Select((screenshot, index) =>
                LoadScreenshotAsync(item.AppId, screenshot, index, cancellationToken))).ConfigureAwait(false);
            item.ScreenshotsLoaded = true;
            var loaded = item.Screenshots.Count(screenshot => screenshot.Image is not null);
            item.ScreenshotStatus = loaded == 0
                ? "Public screenshots could not be loaded"
                : $"{loaded} public screenshot(s) loaded";
            return loaded > 0;
        }
        catch (OperationCanceledException)
        {
            item.ScreenshotStatus = "Screenshot loading cancelled";
            throw;
        }
        finally
        {
            item.IsScreenshotsLoading = false;
        }
    }

    private async Task LoadScreenshotAsync(int appId, SteamCatalogScreenshot screenshot, int index, CancellationToken cancellationToken)
    {
        if (screenshot.Image is not null) return;
        var url = string.IsNullOrWhiteSpace(screenshot.ThumbnailUrl) ? screenshot.FullUrl : screenshot.ThumbnailUrl;
        var image = await LoadImageAsync(
            url,
            Path.Combine(_cacheDirectory, "screenshots", $"{appId}_{index}.jpg"),
            cancellationToken).ConfigureAwait(false);
        screenshot.Image = image;
    }

    private async Task<BitmapImage?> LoadImageAsync(string url, string cachePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            if (File.Exists(cachePath))
            {
                var cached = Decode(await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false));
                if (cached is not null) return cached;
            }

            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var image = Decode(bytes);
            if (image is null) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken).ConfigureAwait(false);
            return image;
        }
        catch (Exception exception) when (IsNetworkException(exception) || exception is InvalidDataException or IOException)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = createRequest();
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotModified) return response;

            var retryable = response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500;
            if (!retryable || attempt >= 2)
            {
                response.Dispose();
                response.EnsureSuccessStatusCode();
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
        }
    }

    private static List<CatalogEntry> ParseAppList(string json)
    {
        using var document = JsonDocument.Parse(json);
        var apps = document.RootElement.GetProperty("applist").GetProperty("apps");
        return apps.EnumerateArray()
            .Select(element => new CatalogEntry(
                element.TryGetProperty("appid", out var appId) && appId.TryGetInt32(out var parsedAppId) ? parsedAppId : 0,
                element.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty))
            .Where(item => item.AppId > 0 && !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.AppId)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static SteamCatalogDetails? ParseDetails(string json, int appId)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var envelope)
            || !envelope.TryGetProperty("success", out var success) || !success.GetBoolean()
            || !envelope.TryGetProperty("data", out var data)) return null;

        var type = data.TryGetProperty("type", out var typeElement) ? ParseType(typeElement.GetString()) : SteamCatalogAppType.Unknown;
        var header = GetString(data, "header_image") ?? string.Empty;
        var capsule = GetString(data, "capsule_image") ?? header;
        var portrait = GetString(data, "library_capsule") ?? $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_600x900_2x.jpg";
        var description = GetString(data, "short_description") ?? string.Empty;
        var developers = GetStringArray(data, "developers");
        var publishers = GetStringArray(data, "publishers");
        var releaseDate = data.TryGetProperty("release_date", out var release) ? GetString(release, "date") ?? string.Empty : string.Empty;
        var free = data.TryGetProperty("is_free", out var freeElement) && freeElement.ValueKind == JsonValueKind.True;
        var price = free ? "Free to Play" : "Price unavailable";
        if (!free && data.TryGetProperty("price_overview", out var priceOverview)) price = GetString(priceOverview, "final_formatted") ?? price;
        var genres = GetGenres(data);
        var screenshots = GetScreenshots(data);
        var requirements = GetSystemRequirements(data);
        var storeUrl = $"https://store.steampowered.com/app/{appId.ToString(CultureInfo.InvariantCulture)}/";
        return new SteamCatalogDetails(type, header, capsule, portrait, portrait, description, developers, publishers, releaseDate, price, free, genres, screenshots, requirements, storeUrl);
    }

    private static SteamCatalogAppType ParseType(string? type) => type?.ToLowerInvariant() switch
    {
        "game" => SteamCatalogAppType.Game,
        "dlc" => SteamCatalogAppType.Dlc,
        "software" => SteamCatalogAppType.Software,
        "video" => SteamCatalogAppType.Video,
        "hardware" => SteamCatalogAppType.Hardware,
        "music" => SteamCatalogAppType.Music,
        _ => SteamCatalogAppType.Unknown
    };

    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray()
        : Array.Empty<string>();

    private static string GetGenres(JsonElement data)
    {
        if (!data.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array) return "Genres unavailable";
        var values = genres.EnumerateArray()
            .Select(genre => GetString(genre, "description"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return values.Length == 0 ? "Genres unavailable" : string.Join(" · ", values);
    }

    private static IReadOnlyList<SteamCatalogScreenshotInfo> GetScreenshots(JsonElement data)
    {
        if (!data.TryGetProperty("screenshots", out var screenshots) || screenshots.ValueKind != JsonValueKind.Array) return Array.Empty<SteamCatalogScreenshotInfo>();
        return screenshots.EnumerateArray()
            .Select(screenshot => new SteamCatalogScreenshotInfo(
                GetString(screenshot, "path_thumbnail") ?? string.Empty,
                GetString(screenshot, "path_full") ?? string.Empty))
            .Where(screenshot => !string.IsNullOrWhiteSpace(screenshot.ThumbnailUrl) || !string.IsNullOrWhiteSpace(screenshot.FullUrl))
            .Take(12)
            .ToArray();
    }

    private static string GetSystemRequirements(JsonElement data)
    {
        if (!data.TryGetProperty("pc_requirements", out var requirements) || requirements.ValueKind != JsonValueKind.Object) return "System requirements unavailable";
        var lines = new List<string>();
        AddRequirement(lines, "Minimum", requirements, "minimum");
        AddRequirement(lines, "Recommended", requirements, "recommended");
        return lines.Count == 0 ? "System requirements unavailable" : string.Join(Environment.NewLine, lines);
    }

    private static void AddRequirement(List<string> lines, string label, JsonElement requirements, string propertyName)
    {
        var value = GetString(requirements, propertyName);
        if (string.IsNullOrWhiteSpace(value)) return;
        var plain = Regex.Replace(System.Net.WebUtility.HtmlDecode(value), "<[^>]+>", " ");
        plain = Regex.Replace(plain, @"\\s+", " ").Trim();
        if (!string.IsNullOrWhiteSpace(plain)) lines.Add($"{label}: {plain}");
    }

    private static List<SteamCatalogItem> CreateItems(IEnumerable<CatalogEntry> entries) => entries.Select(entry => new SteamCatalogItem
    {
        AppId = entry.AppId,
        Name = entry.Name,
        AppType = SteamCatalogAppType.Unknown,
        HeaderImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{entry.AppId}/header.jpg",
        CapsuleImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{entry.AppId}/capsule_616x353.jpg",
        PortraitImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{entry.AppId}/library_600x900_2x.jpg",
        LibraryImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{entry.AppId}/library_600x900_2x.jpg"
    }).ToList();

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return default;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions { WriteIndented = false }, cancellationToken).ConfigureAwait(false);
    }

    private static Task<CatalogCache?> ReadCatalogCacheAsync(string path, CancellationToken cancellationToken) =>
        ReadJsonAsync<CatalogCache>(path, cancellationToken);

    private static Task<DetailsCache?> ReadDetailsCacheAsync(string path, CancellationToken cancellationToken) =>
        ReadJsonAsync<DetailsCache>(path, cancellationToken);

    private async Task<IReadOnlyList<int>> FetchGithubAppIdsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, GithubAppIdsUrl), cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<int>();
        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            if (line.Contains('{') || line.Contains('[')) continue;

            // Accept one-ID-per-line and common variants such as `12345|Title` or
            // `12345 Title`; the first positive integer is the App ID.
            var match = Regex.Match(line, @"(?<!\d)(\d{1,10})(?!\d)");
            if (match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
                ids.Add(id);
        }
        return ids.OrderBy(id => id).ToArray();
    }

    private async Task<Dictionary<int, string>> LoadBundledNamesAsync(CancellationToken cancellationToken)
    {
        var path = ResolveBundledPath();
        if (path is null || !File.Exists(path)) return new Dictionary<int, string>();
        try
        {
            await using var stream = File.OpenRead(path);
            // Supports both JSON array [{appid,name}] and NDJSON one-per-line.
            var first = new byte[1];
            var read = await stream.ReadAsync(first, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            if (read == 0) return new Dictionary<int, string>();
            // Peek: if starts with '[' it's a JSON array; otherwise treat as NDJSON / JSONL.
            while (read > 0 && (first[0] == (byte)' ' || first[0] == (byte)'\n' || first[0] == (byte)'\r' || first[0] == (byte)'\t'))
            {
                read = await stream.ReadAsync(first, cancellationToken).ConfigureAwait(false);
            }
            var isArray = first[0] == (byte)'[';
            stream.Position = 0;
            if (isArray)
            {
                var items = await JsonSerializer.DeserializeAsync<List<BundledEntry>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (items is null) return new Dictionary<int, string>();
                return items.Where(e => e.AppId > 0 && !string.IsNullOrWhiteSpace(e.Name))
                    .GroupBy(e => e.AppId).ToDictionary(g => g.Key, g => g.First().Name!);
            }
            else
            {
                var dict = new Dictionary<int, string>();
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var appId = doc.RootElement.TryGetProperty("appid", out var a) && a.TryGetInt32(out var v) ? v : 0;
                        var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (appId > 0 && !string.IsNullOrWhiteSpace(name)) dict.TryAdd(appId, name!);
                    }
                    catch (JsonException) { }
                }
                return dict;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new Dictionary<int, string>(); }
    }

    private async Task<List<CatalogEntry>?> TryLoadBundledAppIdJsonAsync(CancellationToken cancellationToken)
    {
        var path = ResolveBundledPath();
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var names = await LoadBundledNamesAsync(cancellationToken).ConfigureAwait(false);
            if (names.Count == 0) return null;
            return names.Select(kv => new CatalogEntry(kv.Key, kv.Value)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return null; }
    }

    private string? ResolveBundledPath()
    {
        // An explicitly injected path is authoritative: if it is set, never silently fall back
        // to ambient locations. This keeps tests (and any host that injects a path) hermetic.
        if (!string.IsNullOrWhiteSpace(_bundledAppIdJsonPath))
        {
            return File.Exists(_bundledAppIdJsonPath) ? _bundledAppIdJsonPath : null;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "appid.json"),
            Path.Combine(AppContext.BaseDirectory, "appid.json"),
        };
        foreach (var p in candidates) if (File.Exists(p)) return p;
        return null;
    }

    private static List<CatalogEntry> MergeGithubWithNames(IReadOnlyList<int> ids, Dictionary<int, string> names) =>
        ids.Select(id => new CatalogEntry(id, names.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : $"App {id}"))
           .GroupBy(e => e.AppId).Select(g => g.First())
           .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
           .ToList();

    private sealed record BundledEntry([property: JsonPropertyName("appid")] int AppId, [property: JsonPropertyName("name")] string? Name);

    private static bool IsNetworkException(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException;

    private static BitmapImage? Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return null;
        }
    }    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _catalogLock.Dispose();
        _detailsGate.Dispose();
        _artworkGate.Dispose();
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed record CatalogEntry(int AppId, string Name);
    private sealed record CatalogCache(DateTimeOffset FetchedAt, string? ETag, string? LastModified, List<CatalogEntry> Items);
    private sealed record DetailsCache(DateTimeOffset FetchedAt, SteamCatalogDetails Details);
}

internal static class SteamCatalogItemExtensions

{
    public static void Apply(this SteamCatalogItem item, SteamCatalogDetails details)
    {
        item.AppType = details.AppType;
        item.ShortDescription = details.ShortDescription;
        item.DevelopersDisplay = string.Join(", ", details.Developers);
        item.PublishersDisplay = string.Join(", ", details.Publishers);
        item.ReleaseDate = details.ReleaseDate;
        item.PriceLabel = details.PriceLabel;
        item.IsFreeToPlay = details.IsFreeToPlay;
        item.GenresDisplay = details.GenresDisplay;
        item.SystemRequirements = details.SystemRequirements;
        item.StoreUrl = details.StoreUrl;
        item.Screenshots.Clear();
        foreach (var screenshot in details.Screenshots ?? Array.Empty<SteamCatalogScreenshotInfo>())
        {
            item.Screenshots.Add(new SteamCatalogScreenshot
            {
                ThumbnailUrl = screenshot.ThumbnailUrl,
                FullUrl = screenshot.FullUrl
            });
        }
        item.ScreenshotsLoaded = false;
        item.ScreenshotStatus = item.Screenshots.Count == 0 ? "No public screenshots are listed" : "Screenshots ready to load";
        if (!string.IsNullOrWhiteSpace(details.HeaderImageUrl)) item.HeaderImageUrl = details.HeaderImageUrl;
        if (!string.IsNullOrWhiteSpace(details.CapsuleImageUrl)) item.CapsuleImageUrl = details.CapsuleImageUrl;
        if (!string.IsNullOrWhiteSpace(details.PortraitImageUrl)) item.PortraitImageUrl = details.PortraitImageUrl;
        if (!string.IsNullOrWhiteSpace(details.LibraryImageUrl)) item.LibraryImageUrl = details.LibraryImageUrl;
    }
}
