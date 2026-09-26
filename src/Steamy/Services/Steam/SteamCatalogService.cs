using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Steamy.Models;

namespace Steamy.Services;

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
    private readonly object _catalogItemsLock = new();
    private readonly object _bundledNamesLock = new();
    private readonly object _backgroundRefreshLock = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _backgroundCatalogRefreshCancellation;
    private CatalogCache? _catalogCache;
    private CatalogCache? _catalogItemsSource;
    private SteamCatalogItem[]? _catalogItems;
    private Task<Dictionary<int, string>>? _bundledNamesTask;
    private int _catalogCacheLoaded;
    private bool _disposed;

    public SteamCatalogService(HttpClient? httpClient = null, string? cacheDirectory = null, string? bundledAppIdJsonPath = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(12);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");
        }

        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steamy",
            "steam-catalog");
        _bundledAppIdJsonPath = bundledAppIdJsonPath;
    }

    public async Task<SteamCatalogSnapshot> GetCatalogAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Yield();
        var cachePath = Path.Combine(_cacheDirectory, "app-list.json");
        var cached = Volatile.Read(ref _catalogCache);

        if (Volatile.Read(ref _catalogCacheLoaded) == 0)
        {
            await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _catalogCacheLoaded) == 0)
                {
                    cached = await Task.Run(async () =>
                    {
                        // Preserve the metadata and freshness timestamp in our current cache format.
                        var diskCache = await ReadCatalogCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
                        if (diskCache is { Items.Count: > 0 }) return diskCache;

                        // Older versions wrote a plain { items: [{ appid, name }] } catalog.
                        diskCache = await ReadCatalogFileCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
                        if (diskCache is { Items.Count: > 0 }) return diskCache;

                        return forceRefresh
                            ? null
                            : await TryLoadBundledCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
                    }, cancellationToken).ConfigureAwait(false);

                    if (Volatile.Read(ref _catalogCacheLoaded) == 0)
                    {
                        Volatile.Write(ref _catalogCache, cached);
                        Volatile.Write(ref _catalogCacheLoaded, 1);
                    }
                    else
                    {
                        cached = Volatile.Read(ref _catalogCache);
                    }
                }
                else
                {
                    cached = Volatile.Read(ref _catalogCache);
                }
            }
            finally
            {
                _catalogLock.Release();
            }
        }
        else
        {
            cached = Volatile.Read(ref _catalogCache);
        }

        if (!forceRefresh && cached is { RequiresRefresh: true })
        {
            ScheduleCatalogRefresh(cachePath);
            return new SteamCatalogSnapshot(true, await GetCatalogItemsAsync(cached, cancellationToken).ConfigureAwait(false),
                cached.FetchedAt, true, $"Loaded {cached.Items.Count:N0} games from cache; refreshing in the background.");
        }

        if (!forceRefresh && cached is not null)
        {
            if (IsCatalogCacheFresh(cached))
            {
                return new SteamCatalogSnapshot(true, await GetCatalogItemsAsync(cached, cancellationToken).ConfigureAwait(false), cached.FetchedAt, true,
                    $"Loaded {cached.Items.Count:N0} public apps from local cache.");
            }

            ScheduleCatalogRefresh(cachePath);
            return new SteamCatalogSnapshot(true, await GetCatalogItemsAsync(cached, cancellationToken).ConfigureAwait(false), cached.FetchedAt, true,
                $"Loaded {cached.Items.Count:N0} public apps from cache; refreshing in the background.");
        }

        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed the catalog while this request waited for the lock.
            cached = Volatile.Read(ref _catalogCache);
            if (!forceRefresh && cached is not null)
            {
                if (IsCatalogCacheFresh(cached))
                {
                    return new SteamCatalogSnapshot(true, await GetCatalogItemsAsync(cached, cancellationToken).ConfigureAwait(false), cached.FetchedAt, true,
                        $"Loaded {cached.Items.Count:N0} public apps from local cache.");
                }

                ScheduleCatalogRefresh(cachePath);
                return new SteamCatalogSnapshot(true, await GetCatalogItemsAsync(cached, cancellationToken).ConfigureAwait(false), cached.FetchedAt, true,
                    $"Loaded {cached.Items.Count:N0} public apps from cache; refreshing in the background.");
            }

            var result = await RefreshCatalogFromSourcesAsync(cachePath, cached, cancellationToken).ConfigureAwait(false);
            return result.Cache is null
                ? SteamCatalogSnapshot.Failure(result.Message)
                : new SteamCatalogSnapshot(true, await GetCatalogItemsAsync(result.Cache, cancellationToken).ConfigureAwait(false), result.Cache.FetchedAt, result.FromCache, result.Message);
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    private async Task<List<CatalogEntry>?> LoadBundledEntriesAsync(CancellationToken cancellationToken)
    {
        var entries = await TryLoadBundledAppIdJsonAsync(cancellationToken).ConfigureAwait(false);
        return entries?.ToList();
    }

    private async Task<CatalogCache?> TryLoadBundledCacheAsync(string cachePath, CancellationToken cancellationToken)
    {
        var bundled = await LoadBundledEntriesAsync(cancellationToken).ConfigureAwait(false);
        if (bundled is not { Count: > 0 }) return null;

        var cache = new CatalogCache(
            DateTimeOffset.UtcNow,
            null,
            null,
            bundled.Select(item => new CatalogEntry(item.AppId, item.Name)).ToList())
        {
            RequiresRefresh = true
        };
        try
        {
            await StoreCatalogCacheAsync(cachePath, cache, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref _catalogCache, cache);
            Volatile.Write(ref _catalogCacheLoaded, 1);
        }

        ScheduleCatalogRefresh(cachePath);
        return cache;
    }

    private void ScheduleCatalogRefresh(string cachePath)
    {
        CancellationTokenSource cancellation;
        lock (_backgroundRefreshLock)
        {
            if (_disposed || _backgroundCatalogRefreshCancellation is not null) return;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            _backgroundCatalogRefreshCancellation = cancellation;
        }

        _ = RefreshCatalogInBackgroundAsync(cachePath, cancellation);
    }

    private async Task RefreshCatalogInBackgroundAsync(string cachePath, CancellationTokenSource cancellation)
    {
        try
        {
            await _catalogLock.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                var cached = Volatile.Read(ref _catalogCache);
                if (cached is null && Volatile.Read(ref _catalogCacheLoaded) == 0)
                {
                    cached = await ReadCatalogFileCacheAsync(cachePath, cancellation.Token).ConfigureAwait(false);
                    Volatile.Write(ref _catalogCache, cached);
                    Volatile.Write(ref _catalogCacheLoaded, 1);
                }

                if (cached is not null && IsCatalogCacheFresh(cached)) return;
                await RefreshCatalogFromSourcesAsync(cachePath, cached, cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                _catalogLock.Release();
            }
        }
        catch (Exception)
        {
            // A background refresh must never interrupt the cached catalog already shown to the user.
        }
        finally
        {
            lock (_backgroundRefreshLock)
            {
                if (ReferenceEquals(_backgroundCatalogRefreshCancellation, cancellation))
                    _backgroundCatalogRefreshCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task<CatalogRefreshResult> RefreshCatalogFromSourcesAsync(
        string cachePath,
        CatalogCache? cached,
        CancellationToken cancellationToken)
    {
        try
        {
            var githubIds = await FetchGithubAppIdsAsync(cancellationToken).ConfigureAwait(false);
            if (githubIds.Count > 0)
            {
                var cacheIds = cached is { RequiresRefresh: true, Items.Count: > 0 }
                    ? cached.Items.Select(item => item.AppId).ToHashSet()
                    : new HashSet<int>();
                if (cacheIds.Count > 0) githubIds = githubIds.Concat(cacheIds).Distinct().OrderBy(id => id).ToArray();

                var names = await LoadBundledNamesAsync(cancellationToken).ConfigureAwait(false);
                var entries = MergeGithubWithNames(githubIds, names);
                var fresh = new CatalogCache(DateTimeOffset.UtcNow, null, null,
                    entries.Select(item => new CatalogEntry(item.AppId, item.Name)).ToList());
                await StoreCatalogCacheAsync(cachePath, fresh, cancellationToken).ConfigureAwait(false);
                return new CatalogRefreshResult(fresh, false, $"Loaded {fresh.Items.Count:N0} public apps from GitHub.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsNetworkException(exception))
        {
            // Continue with Steam's public endpoint or the bundled offline list.
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
                var validated = cached with { FetchedAt = DateTimeOffset.UtcNow, RequiresRefresh = false };
                await StoreCatalogCacheAsync(cachePath, validated, cancellationToken).ConfigureAwait(false);
                return new CatalogRefreshResult(validated, true, "Steam catalog cache validated by the public endpoint.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var entries = ParseAppList(json);
            if (entries.Count > 0)
            {
                var fresh = new CatalogCache(
                    DateTimeOffset.UtcNow,
                    response.Headers.ETag?.Tag,
                    response.Content.Headers.LastModified?.ToString(),
                    entries.Select(item => new CatalogEntry(item.AppId, item.Name)).ToList());
                await StoreCatalogCacheAsync(cachePath, fresh, cancellationToken).ConfigureAwait(false);
                return new CatalogRefreshResult(fresh, false, $"Loaded {fresh.Items.Count:N0} public apps from Steam.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsNetworkException(exception))
        {
            // Preserve a previous good cache if both public endpoints are unavailable.
        }

        if (cached is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bundled = await TryLoadBundledAppIdJsonAsync(cancellationToken).ConfigureAwait(false);
            if (bundled is { Count: > 0 })
            {
                var fresh = new CatalogCache(DateTimeOffset.UtcNow, null, null,
                    bundled.Select(item => new CatalogEntry(item.AppId, item.Name)).ToList());
                await StoreCatalogCacheAsync(cachePath, fresh, cancellationToken).ConfigureAwait(false);
                return new CatalogRefreshResult(fresh, false,
                    $"Loaded {fresh.Items.Count:N0} public apps from bundled data (offline).");
            }
        }

        return cached is not null
            ? new CatalogRefreshResult(cached, true, $"Public catalog is unavailable; showing {cached.Items.Count:N0} cached apps.")
            : new CatalogRefreshResult(null, false, "Steam catalog and offline app list are unavailable.");
    }

    private static bool IsCatalogCacheFresh(CatalogCache cache) =>
        !cache.RequiresRefresh && DateTimeOffset.UtcNow - cache.FetchedAt < CatalogLifetime;

    private async Task StoreCatalogCacheAsync(string cachePath, CatalogCache cache, CancellationToken cancellationToken)
    {
        await WriteJsonAsync(cachePath, cache, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _catalogCache, cache);
        Volatile.Write(ref _catalogCacheLoaded, 1);
    }

    private Task<IReadOnlyList<SteamCatalogItem>> GetCatalogItemsAsync(CatalogCache cache, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<SteamCatalogItem>>(() =>
        {
            lock (_catalogItemsLock)
            {
                if (ReferenceEquals(_catalogItemsSource?.Items, cache.Items) && _catalogItems is not null)
                    return _catalogItems;

                _catalogItems = CreateItems(cache.Items);
                _catalogItemsSource = cache;
                return _catalogItems;
            }
        }, cancellationToken);

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
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{item.AppId}/library_600x900_2x.jpg",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{item.AppId}/library_600x900_2x.jpg",
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{item.AppId}/library_600x900.jpg",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{item.AppId}/library_600x900.jpg",
            item.CapsuleImageUrl,
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{item.AppId}/capsule_616x353.jpg",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{item.AppId}/capsule_616x353.jpg",
            item.HeaderImageUrl,
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{item.AppId}/header.jpg",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{item.AppId}/header.jpg"
        }.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase);

        var cachePath = Path.Combine(_cacheDirectory, "artwork", $"{item.AppId}_portrait_v2.jpg");
        foreach (var url in urls)
        {
            var image = await LoadImageAsync(url, cachePath, cancellationToken, decodeWidth: 400).ConfigureAwait(false);
            if (image is not null) return image;
        }

        var storeImageUrl = await FetchStoreImageUrlAsync(item.AppId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(storeImageUrl))
        {
            var image = await LoadImageAsync(storeImageUrl, cachePath, cancellationToken, decodeWidth: 400).ConfigureAwait(false);
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

    private async Task<string?> FetchStoreImageUrlAsync(int appId, CancellationToken cancellationToken)
    {
        if (appId <= 0) return null;
        try
        {
            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, string.Format(CultureInfo.InvariantCulture, DetailsUrl, appId)),
                cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var envelope)
                && envelope.TryGetProperty("success", out var success) && success.GetBoolean()
                && envelope.TryGetProperty("data", out var data))
            {
                return GetString(data, "header_image")
                    ?? GetString(data, "capsule_image")
                    ?? GetString(data, "capsule_imagev5");
            }
        }
        catch (Exception exception) when (IsNetworkException(exception) || exception is IOException)
        {
        }
        return null;
    }

    private async Task<BitmapImage?> LoadImageAsync(string url, string cachePath, CancellationToken cancellationToken, int decodeWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            if (File.Exists(cachePath))
            {
                var cached = Decode(await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false), decodeWidth);
                if (cached is not null) return cached;
            }

            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var image = Decode(bytes, decodeWidth);
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
        var portrait = GetString(data, "library_capsule") ?? $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900_2x.jpg";
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

    private static SteamCatalogItem[] CreateItems(IEnumerable<CatalogEntry> entries) => entries.Select(entry => new SteamCatalogItem
    {
        AppId = entry.AppId,
        Name = entry.Name,
        AppType = SteamCatalogAppType.Unknown,
        HeaderImageUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{entry.AppId}/header.jpg",
        CapsuleImageUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{entry.AppId}/capsule_616x353.jpg",
        PortraitImageUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{entry.AppId}/library_600x900_2x.jpg",
        LibraryImageUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{entry.AppId}/library_600x900_2x.jpg"
    }).ToArray();

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
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value,
                    new JsonSerializerOptions { WriteIndented = false }, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Task<CatalogCache?> ReadCatalogCacheAsync(string path, CancellationToken cancellationToken) =>
        ReadJsonAsync<CatalogCache>(path, cancellationToken);

    private static async Task<CatalogCache?> ReadCatalogFileCacheAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var hasItems = root.TryGetProperty("Items", out var items)
                || root.TryGetProperty("items", out items);
            if (!hasItems || items.ValueKind != JsonValueKind.Array) return null;

            var entries = new List<CatalogEntry>();
            foreach (var item in items.EnumerateArray())
            {
                var hasAppId = item.TryGetProperty("AppId", out var appId)
                    || item.TryGetProperty("appid", out appId)
                    || item.TryGetProperty("appId", out appId);
                if (!hasAppId || !appId.TryGetInt32(out var id) || id <= 0) continue;
                var name = item.TryGetProperty("Name", out var nameProperty)
                    || item.TryGetProperty("name", out nameProperty)
                    ? nameProperty.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(name)) entries.Add(new CatalogEntry(id, name));
            }

            if (entries.Count == 0) return null;
            return new CatalogCache(DateTimeOffset.UtcNow, null, null, entries) { RequiresRefresh = true };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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

    private Task<Dictionary<int, string>> LoadBundledNamesAsync(CancellationToken cancellationToken)
    {
        Task<Dictionary<int, string>> namesTask;
        lock (_bundledNamesLock)
        {
            namesTask = _bundledNamesTask ??= LoadBundledNamesFromDiskAsync();
        }

        return cancellationToken.CanBeCanceled ? namesTask.WaitAsync(cancellationToken) : namesTask;
    }

    private async Task<Dictionary<int, string>> LoadBundledNamesFromDiskAsync()
    {
        var cancellationToken = CancellationToken.None;
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
        ids.Select(id => new CatalogEntry(id, names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : $"App {id}"))
           .GroupBy(entry => entry.AppId).Select(group => group.First())
           .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
           .ToList();


    private sealed record BundledEntry([property: JsonPropertyName("appid")] int AppId, [property: JsonPropertyName("name")] string? Name);

    private static bool IsNetworkException(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException;

    private static BitmapImage? Decode(byte[] bytes, int decodeWidth = 0)
    {
        if (bytes.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_backgroundRefreshLock)
        {
            if (_disposed) return;
            _disposed = true;
            _backgroundCatalogRefreshCancellation?.Cancel();
        }

        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        // Active catalog, detail and artwork requests still need to release these gates.
        // SemaphoreSlim is managed and can be left for collection after the service is disposed.
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed record CatalogEntry(int AppId, string Name);
    private sealed record CatalogRefreshResult(CatalogCache? Cache, bool FromCache, string Message);
    private sealed record CatalogCache(DateTimeOffset FetchedAt, string? ETag, string? LastModified, List<CatalogEntry> Items)
    {
        public bool RequiresRefresh { get; init; }
    }
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
