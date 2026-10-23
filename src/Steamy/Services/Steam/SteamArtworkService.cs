using System.IO;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Steamy.Models;

namespace Steamy.Services;

public interface IArtworkService
{
    Task<BitmapImage?> LoadGameLogoAsync(Game game, CancellationToken cancellationToken = default) => LoadSpotlightLogoAsync(new SpotlightGame { AppId = game.AppId, Name = game.Name }, cancellationToken);
    Task<BitmapImage?> LoadSpotlightLogoAsync(SpotlightGame game, CancellationToken cancellationToken = default) => Task.FromResult<BitmapImage?>(null);
    Task<BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default);
    Task<BitmapImage?> LoadSpotlightHeroAsync(SpotlightGame game, CancellationToken cancellationToken = default) => LoadHeroAsync(game.AppId, cancellationToken);
    async Task<BitmapImage?> LoadSpotlightHeaderAsync(SpotlightGame item, CancellationToken cancellationToken = default)
    {
        var game = new Game { AppId = item.AppId, Name = item.Name };
        await LoadHeadersAsync(new[] { game }, cancellationToken).ConfigureAwait(false);
        return game.HeaderImage as BitmapImage;
    }
    async Task<BitmapImage?> LoadSpotlightPortraitAsync(SpotlightGame metadata, CancellationToken cancellationToken = default)
    {
        var game = new Game { AppId = metadata.AppId, Name = metadata.Name };
        await LoadAsync(game, cancellationToken).ConfigureAwait(false);
        return game.ArtworkImage as BitmapImage;
    }
    Task LoadAsync(Game game, CancellationToken cancellationToken = default);
    Task LoadHeadersAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default);
    Task LoadManyAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default);
    Task PrefetchAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => LoadManyAsync(games, cancellationToken);
    async Task LoadSpotlightCoverAsync(SpotlightGame metadata, Game destination, ImagePriority priority, CancellationToken cancellationToken = default)
    {
        var header = LoadSpotlightHeaderAsync(metadata, cancellationToken); var portrait = LoadSpotlightPortraitAsync(metadata, cancellationToken);
        await Task.WhenAll(header, portrait).ConfigureAwait(false);
        destination.HeaderImage = header.Result; destination.ArtworkImage = portrait.Result;
    }
}

/// <summary>
/// Loads public Steam store artwork without credentials and keeps a local cache so
/// the library remains useful when the CDN is unavailable.
/// </summary>
public sealed class SteamArtworkService : IArtworkService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly bool _ownsHttpClient;
    private readonly ImagePipeline _images;
    private readonly bool _ownsImages;
    private readonly AsyncLocal<ImagePriority> _priority = new();
    private static readonly Lazy<IReadOnlyDictionary<int, string>> BundledLogos = new(() =>
        SpotlightCatalogService.LoadBundled().Games.Where(game => SpotlightCatalogService.IsArtworkUrl(game.LogoUrl))
            .ToDictionary(game => game.AppId, game => game.LogoUrl));

    public SteamArtworkService(HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _httpClient = httpClient ?? new HttpClient(StableDnsHandler.Create());
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(8);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/0.1");
        }
        _cacheDirectory = cacheDirectory ?? Path.Combine(Steamy.Storage.AppDataPaths.Root,
            "artwork");
        _ownsImages = httpClient is not null || cacheDirectory is not null;
        _images = _ownsImages ? new ImagePipeline(_httpClient, _cacheDirectory) : ImagePipeline.Shared;
    }

    public async Task<BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default)
    {
        if (appId <= 0) return null;
        var cache = Path.Combine(_cacheDirectory, $"{appId}_hero.jpg");
        var image = await LoadFirstAvailableAsync(new[]
        {
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero.jpg",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_hero.jpg"
        }, cache, cancellationToken).ConfigureAwait(false);
        if (image is not null) return image;
        var assets = await GetStoreAssetUrlsAsync(appId, cancellationToken).ConfigureAwait(false);
        return await LoadFirstAvailableAsync(new[] { assets.Hero, assets.Header }.Where(SpotlightCatalogService.IsArtworkUrl).ToArray(), cache, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BitmapImage?> LoadGameLogoAsync(Game game, CancellationToken cancellationToken = default)
    {
        if (game.AppId <= 0) return null;
        var path = Path.Combine(_cacheDirectory, $"{game.AppId}_game_logo.png");
        var cached = await LoadFirstAvailableAsync([], path, cancellationToken).ConfigureAwait(false);
        if (cached is not null) return cached;
        if (BundledLogos.Value.TryGetValue(game.AppId, out var bundled))
        {
            var bundledImage = await LoadFirstAvailableAsync([bundled], path, cancellationToken).ConfigureAwait(false);
            if (bundledImage is not null) return bundledImage;
        }
        try
        {
            // The same public SteamCMD metadata mirror is already used for app launch/depot
            // metadata. Only a validated relative logo filename is accepted; image bytes
            // always come from Steam's CDN. Legacy /logo.png can actually return a banner.
            using var response = await _httpClient.GetAsync($"https://api.steamcmd.net/v1/info/{game.AppId}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 4 * 1024 * 1024) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty(game.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var app)
                || !app.TryGetProperty("common", out var common) || !common.TryGetProperty("library_assets_full", out var assets)
                || !assets.TryGetProperty("library_logo", out var logo)) return null;
            foreach (var size in new[] { "image2x", "image" })
            {
                if (!logo.TryGetProperty(size, out var languages) || !languages.TryGetProperty("english", out var english) || english.ValueKind != JsonValueKind.String) continue;
                var filename = english.GetString() ?? "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(filename, @"\A(?:[a-fA-F0-9]{40}/)?logo(?:_2x)?\.png\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) continue;
                var image = await LoadFirstAvailableAsync([$"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{game.AppId}/{filename}"], path, cancellationToken).ConfigureAwait(false);
                if (image is not null) return image;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OperationCanceledException) { }
        return null;
    }

    public Task<BitmapImage?> LoadSpotlightHeroAsync(SpotlightGame game, CancellationToken cancellationToken = default) =>
        LoadFirstAvailableAsync(new[] { game.HeroUrl, game.HeaderUrl }.Where(SpotlightCatalogService.IsArtworkUrl).ToArray(),
            SpotlightCachePath(game.AppId, "hero", game.HeroUrl), cancellationToken);

    public Task<BitmapImage?> LoadSpotlightHeaderAsync(SpotlightGame game, CancellationToken cancellationToken = default) =>
        LoadFirstAvailableAsync(new[] { game.HeaderUrl }.Where(SpotlightCatalogService.IsArtworkUrl).ToArray(),
            SpotlightCachePath(game.AppId, "header", game.HeaderUrl), cancellationToken);

    private string SpotlightCachePath(int appId, string kind, string url)
    {
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..12];
        return Path.Combine(_cacheDirectory, $"{appId}_spotlight_{kind}_{fingerprint}.jpg");
    }

    public async Task<BitmapImage?> LoadSpotlightLogoAsync(SpotlightGame game, CancellationToken cancellationToken = default)
    {
        // Older cached or online feeds omit logo URLs. Keep using the hashed Steam logo
        // shipped with the app instead of falling back only to a nonexistent legacy URL.
        var bundledUrl = BundledLogos.Value.GetValueOrDefault(game.AppId, string.Empty);
        var urls = new[] { game.LogoUrl, bundledUrl }
            .Where(SpotlightCatalogService.IsArtworkUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (urls.Length > 0)
        {
            var explicitLogo = await LoadFirstAvailableAsync(urls, SpotlightCachePath(game.AppId, "logo", urls[0]), cancellationToken).ConfigureAwait(false);
            if (explicitLogo is not null) return explicitLogo;
        }
        // Online spotlight entries need the same hashed library-logo lookup as game pages.
        // The old /logo.png route can return a rectangular banner instead of a wordmark.
        return await LoadGameLogoAsync(new Game { AppId = game.AppId }, cancellationToken).ConfigureAwait(false);
    }

    public Task<BitmapImage?> LoadSpotlightPortraitAsync(SpotlightGame game, CancellationToken cancellationToken = default) =>
        LoadFirstAvailableAsync(new[]
        {
            game.PortraitUrl,
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{game.AppId}/library_600x900.jpg",
            game.HeaderUrl
        }.Where(SpotlightCatalogService.IsArtworkUrl).ToArray(), SpotlightCachePath(game.AppId, "portrait", string.IsNullOrEmpty(game.PortraitUrl) ? game.HeaderUrl : game.PortraitUrl), cancellationToken);

    public async Task LoadHeadersAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default)
    {
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(games.Where(game => game.AppId > 0).Select(async game =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                game.HeaderImage = await LoadFirstAvailableAsync(new[]
                {
                    $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{game.AppId}/header.jpg",
                    $"https://cdn.akamai.steamstatic.com/steam/apps/{game.AppId}/header.jpg"
                }, Path.Combine(_cacheDirectory, $"{game.AppId}_header.jpg"), cancellationToken).ConfigureAwait(false);
                if (game.HeaderImage is null)
                    await LoadAsync(game, cancellationToken).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
    }

    public async Task LoadManyAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default)
    {
        // A real library can hold hundreds of apps, so the CDN requests are kept to a small
        // number of parallel downloads instead of one burst per app.
        var selectedGames = games.Where(game => game.AppId > 0).ToArray();
        if (selectedGames.Length == 0) return;

        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(selectedGames.Select(async game =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await LoadAsync(game, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    public async Task PrefetchAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default)
    {
        var previous = _priority.Value; _priority.Value = ImagePriority.Prefetch;
        try { await LoadManyAsync(games.Take(24), cancellationToken).ConfigureAwait(false); }
        finally { _priority.Value = previous; }
    }
    public async Task LoadSpotlightCoverAsync(SpotlightGame metadata, Game destination, ImagePriority priority, CancellationToken cancellationToken = default)
    {
        var previous = _priority.Value; _priority.Value = priority;
        try
        {
            var header = PublishImageAsync(LoadSpotlightHeaderAsync(metadata, cancellationToken), image => destination.HeaderImage = image, cancellationToken);
            var portrait = PublishImageAsync(LoadSpotlightPortraitAsync(metadata, cancellationToken), image => destination.ArtworkImage = image, cancellationToken);
            await Task.WhenAll(header, portrait).ConfigureAwait(false);
        }
        finally { _priority.Value = previous; }
    }

    public async Task LoadAsync(Game game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.AppId <= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (game.ArtworkImage is null && !string.IsNullOrWhiteSpace(game.LaunchExecutable) && Uri.TryCreate(game.LaunchExecutable, UriKind.Absolute, out var local) && local.IsFile)
                game.ArtworkImage = await _images.LoadAsync(new ImageRequest(local, 256, CachePolicy: ImageCachePolicy.MemoryOnly), cancellationToken).ConfigureAwait(false);
            game.HeaderImage ??= game.ArtworkImage;
            return;
        }
        game.IsArtworkLoading = true;

        try
        {
            var portraitCachePath = Path.Combine(_cacheDirectory, $"{game.AppId}_library.jpg");
            var headerCachePath = Path.Combine(_cacheDirectory, $"{game.AppId}_header.jpg");

            var portraitUrls = new[]
            {
                $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{game.AppId}/library_600x900_2x.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{game.AppId}/library_600x900_2x.jpg",
                $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{game.AppId}/library_600x900.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{game.AppId}/library_600x900.jpg",
                $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{game.AppId}/capsule_616x353.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{game.AppId}/capsule_616x353.jpg",
            };

            var headerUrls = new[]
            {
                $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{game.AppId}/header.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{game.AppId}/header.jpg",
            };

            void PublishPortrait(BitmapImage image) { game.ArtworkImage = image; game.HeaderImage ??= image; }
            void PublishHeader(BitmapImage image) { game.HeaderImage = image; game.ArtworkImage ??= image; }
            var portraitTask = PublishImageAsync(LoadFirstAvailableAsync(portraitUrls, portraitCachePath, cancellationToken), PublishPortrait, cancellationToken);
            var headerTask = PublishImageAsync(LoadFirstAvailableAsync(headerUrls, headerCachePath, cancellationToken), PublishHeader, cancellationToken);

            await Task.WhenAll(portraitTask, headerTask).ConfigureAwait(false);
            var portraitImage = portraitTask.Result;
            var headerImage = headerTask.Result;

            // Store art filenames can be versioned (hash-prefixed) for newer games.
            // Steam's public store metadata supplies the current asset URLs when the
            // stable legacy filenames above are no longer available.
            if (portraitImage is null || headerImage is null)
            {
                var storeAssets = await GetStoreAssetUrlsAsync(game.AppId, cancellationToken).ConfigureAwait(false);
                var storePortrait = portraitImage is null && storeAssets.Portrait.Length > 0
                    ? PublishImageAsync(LoadFirstAvailableAsync(new[] { storeAssets.Portrait }, portraitCachePath, cancellationToken), PublishPortrait, cancellationToken)
                    : Task.FromResult<BitmapImage?>(null);
                var storeHeader = headerImage is null && storeAssets.Header.Length > 0
                    ? PublishImageAsync(LoadFirstAvailableAsync(new[] { storeAssets.Header }, headerCachePath, cancellationToken), PublishHeader, cancellationToken)
                    : Task.FromResult<BitmapImage?>(null);
                await Task.WhenAll(storePortrait, storeHeader).ConfigureAwait(false);
                portraitImage ??= storePortrait.Result;
                headerImage ??= storeHeader.Result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            portraitImage ??= headerImage is null ? LocalGameArtwork.Read(game.LaunchExecutable) : null;
            game.ArtworkImage = portraitImage ?? headerImage;
            game.HeaderImage = headerImage ?? portraitImage;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Artwork is optional. The view keeps its local color/glyph fallback.
        }
        finally
        {
            game.IsArtworkLoading = false;
        }
    }

    internal static async Task<BitmapImage?> PublishImageAsync(Task<BitmapImage?> request, Action<BitmapImage> publish, CancellationToken token)
    {
        var image = await request.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (image is not null) publish(image);
        return image;
    }

    private async Task<(string Portrait, string Header, string Hero)> GetStoreAssetUrlsAsync(int appId, CancellationToken cancellationToken)
    {
        try
        {
            var query = JsonSerializer.Serialize(new
            {
                ids = new[] { new { appid = appId } },
                context = new { language = "english", country_code = "US", steam_realm = 1 },
                data_request = new { include_assets = true }
            });
            using var response = await _httpClient.GetAsync(
                "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(query),
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (string.Empty, string.Empty, string.Empty);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("response", out var root)
                || !root.TryGetProperty("store_items", out var rows)) return (string.Empty, string.Empty, string.Empty);

            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("appid", out var id) || !id.TryGetInt32(out var resultId) || resultId != appId
                    || !row.TryGetProperty("assets", out var assets)
                    || !assets.TryGetProperty("asset_url_format", out var formatValue)) continue;

                var format = formatValue.GetString();
                if (string.IsNullOrWhiteSpace(format)
                    || !format.StartsWith($"steam/apps/{appId}/", StringComparison.Ordinal)
                    || !format.Contains("${FILENAME}", StringComparison.Ordinal)) continue;

                string Resolve(params string[] names)
                {
                    foreach (var name in names)
                    {
                        if (!assets.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) continue;
                        var filename = value.GetString();
                        if (!string.IsNullOrWhiteSpace(filename))
                            return "https://shared.akamai.steamstatic.com/store_item_assets/" + format.Replace("${FILENAME}", filename, StringComparison.Ordinal);
                    }
                    return string.Empty;
                }

                return (Resolve("library_capsule_2x", "library_capsule"), Resolve("header_2x", "header"), Resolve("library_hero_2x", "library_hero"));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Current Steam asset metadata is optional; keep glyph fallback. */ }

        return (string.Empty, string.Empty, string.Empty);
    }

    private async Task<BitmapImage?> LoadFirstAvailableAsync(string[] urls, string cachePath, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(cachePath);
        var width = name.Contains("hero", StringComparison.OrdinalIgnoreCase) ? 2560
            : name.Contains("logo", StringComparison.OrdinalIgnoreCase) ? 900
            : name.Contains("header", StringComparison.OrdinalIgnoreCase) ? 600 : 360;
        if (urls.Length == 0)
            return await _images.LoadAsync(new ImageRequest(new Uri(Path.GetFullPath(cachePath)), width, Priority: _priority.Value, CachePath: cachePath), cancellationToken).ConfigureAwait(false);
        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            var image = await _images.LoadAsync(new ImageRequest(uri, width, Priority: _priority.Value, CachePath: cachePath), cancellationToken).ConfigureAwait(false);
            if (image is not null) return image;
        }
        return null;
    }

    public void Dispose()
    {
        if (_ownsImages) _images.Dispose();
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
