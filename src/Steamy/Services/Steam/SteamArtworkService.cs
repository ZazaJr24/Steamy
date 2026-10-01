using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using Steamy.Models;

namespace Steamy.Services;

public interface IArtworkService
{
    Task<BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default);
    Task<BitmapImage?> LoadSpotlightHeroAsync(SpotlightGame game, CancellationToken cancellationToken = default) => LoadHeroAsync(game.AppId, cancellationToken);
    async Task<BitmapImage?> LoadSpotlightHeaderAsync(SpotlightGame item, CancellationToken cancellationToken = default)
    {
        var game = new Game { AppId = item.AppId, Name = item.Name };
        await LoadHeadersAsync(new[] { game }, cancellationToken).ConfigureAwait(false);
        return game.HeaderImage as BitmapImage;
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

    public SteamArtworkService(HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _httpClient = httpClient ?? new HttpClient(StableDnsHandler.Create());
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(8);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/0.1");
        }
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steamy",
            "artwork");
    }

    public Task<BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default) =>
        LoadFirstAvailableAsync(new[]
        {
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero.jpg",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_hero.jpg",
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"
        }, Path.Combine(_cacheDirectory, $"{appId}_hero.jpg"), cancellationToken);

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

    private async Task<BitmapImage?> LoadFirstAvailableAsync(string[] urls, string cachePath, CancellationToken cancellationToken)
    {
        if (File.Exists(cachePath))
        {
            try
            {
                var cached = await File.ReadAllBytesAsync(cachePath, cancellationToken);
                var cachedImage = Decode(cached);
                if (cachedImage is not null) return cachedImage;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* stale cache, re-fetch */ }
        }

        foreach (var url in urls)
        {
            var image = await FetchAndCacheAsync(url, cachePath, cancellationToken);
            if (image is not null) return image;
        }
        return null;
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

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var nativeWidth = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = Math.Min(1600, nativeWidth);
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
