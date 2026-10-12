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
    private readonly ConcurrentDictionary<string, WeakReference<BitmapImage>> _decodedImages = new(StringComparer.OrdinalIgnoreCase);
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

    public async Task LoadAsync(Game game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.AppId <= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            game.ArtworkImage ??= LocalGameArtwork.Read(game.LaunchExecutable);
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

            var portraitTask = LoadFirstAvailableAsync(portraitUrls, portraitCachePath, cancellationToken);
            var headerTask = LoadFirstAvailableAsync(headerUrls, headerCachePath, cancellationToken);

            await Task.WhenAll(portraitTask, headerTask);
            var portraitImage = await portraitTask;
            var headerImage = await headerTask;

            // Store art filenames can be versioned (hash-prefixed) for newer games.
            // Steam's public store metadata supplies the current asset URLs when the
            // stable legacy filenames above are no longer available.
            if (portraitImage is null || headerImage is null)
            {
                var storeAssets = await GetStoreAssetUrlsAsync(game.AppId, cancellationToken).ConfigureAwait(false);
                var storePortrait = portraitImage is null && storeAssets.Portrait.Length > 0
                    ? await LoadFirstAvailableAsync(new[] { storeAssets.Portrait }, portraitCachePath, cancellationToken).ConfigureAwait(false)
                    : null;
                var storeHeader = headerImage is null && storeAssets.Header.Length > 0
                    ? await LoadFirstAvailableAsync(new[] { storeAssets.Header }, headerCachePath, cancellationToken).ConfigureAwait(false)
                    : null;
                portraitImage ??= storePortrait;
                headerImage ??= storeHeader;
            }

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
        cancellationToken.ThrowIfCancellationRequested();
        if (_decodedImages.TryGetValue(cachePath, out var reference) && reference.TryGetTarget(out var retained))
            return retained;
        if (File.Exists(cachePath))
        {
            try
            {
                var cached = await File.ReadAllBytesAsync(cachePath, cancellationToken);
                var cachedImage = Decode(cached);
                if (cachedImage is not null) return Remember(cachePath, cachedImage);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* stale cache, re-fetch */ }
        }

        foreach (var url in urls)
        {
            var image = await FetchAndCacheAsync(url, cachePath, cancellationToken);
            if (image is not null) return Remember(cachePath, image);
        }
        return null;
    }

    private BitmapImage Remember(string path, BitmapImage image)
    {
        // Frozen bitmaps can be shared across pages; weak references release unused art.
        if (_decodedImages.Count >= 1024)
            foreach (var entry in _decodedImages)
                if (!entry.Value.TryGetTarget(out _)) _decodedImages.TryRemove(entry.Key, out _);
        if (_decodedImages.Count < 1024) _decodedImages[path] = new(image);
        return image;
    }

    private async Task<BitmapImage?> FetchAndCacheAsync(string url, string cachePath, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            const int maxImageBytes = 8 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maxImageBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (memory.Length + count > maxImageBytes) return null;
                memory.Write(buffer, 0, count);
            }
            var bytes = memory.ToArray();
            var image = Decode(bytes);
            if (image is null) return null;

            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { /* A valid image remains usable when the optional disk cache is unavailable. */ }
            return image;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static BitmapImage? Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        // Steam returns these generic gray JPEGs with HTTP 200 for obsolete asset URLs.
        // Treat both downloaded and already-cached placeholders as missing artwork so
        // the current hashed URLs from StoreBrowse can replace them. Exact fingerprints
        // deliberately preserve legitimate minimal or monochrome game artwork.
        if (bytes.Length <= 8192 && Convert.ToHexString(SHA256.HashData(bytes)) is
            "574128283765E00DD4EEF95EFAA40F54AA60FDA25AD01A56B265A8B1D0C655CD" or
            "9EA238901BE13C138C061365CC705840BB757FD076A66EC3731C1948BB2B6A44" or
            "3514810F0A5A3F0D55D756924B16F33D5AAFAFC8996DBC501631ED534798F3A8" or
            "CF39EAA2E57FE66C0B7B92F371F3E945BE5631CB96D9002F4DED1417E13843B8") return null;

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var nativeWidth = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = Math.Min(2048, nativeWidth);
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
