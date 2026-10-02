using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Steamy.Services;

public sealed record SpotlightGame
{
    public int AppId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Genres { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public bool ComingSoon { get; init; }
    public string ReleaseLabel { get; init; } = string.Empty;
    public DateOnly? ReleaseDate { get; init; }
    public DateTimeOffset? ReleaseTime { get; init; }
    public string StoreUrl { get; init; } = string.Empty;
    public string HeroUrl { get; init; } = string.Empty;
    public string HeaderUrl { get; init; } = string.Empty;
    public string PortraitUrl { get; init; } = string.Empty;
}

public sealed record SpotlightSnapshot(DateTimeOffset UpdatedAt, IReadOnlyList<SpotlightGame> Games)
{
    public static SpotlightSnapshot Empty { get; } = new(DateTimeOffset.MinValue, Array.Empty<SpotlightGame>());
}

public interface ISpotlightService
{
    SpotlightSnapshot Cached { get; }
    Task<SpotlightSnapshot> GetAsync(bool force = false, CancellationToken cancellationToken = default);
}

/// <summary>A small, daily Steam feed. Concurrent callers share a refresh; failed requests retain the last good data.</summary>
public sealed class SpotlightCatalogService : ISpotlightService, IDisposable
{
    private const int MaxBytes = 256 * 1024;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly Uri FeedUri = new("https://raw.githubusercontent.com/ZazaJr24/Steamy/main/src/Steamy/Data/spotlight.json");
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cachePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private Task<SpotlightSnapshot>? _refresh;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private SpotlightSnapshot _cached;

    public SpotlightCatalogService(HttpClient? httpClient = null, string? cachePath = null,
        Func<DateTimeOffset>? clock = null, SpotlightSnapshot? bundled = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ownsHttp = httpClient is null;
        _cachePath = cachePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy", "spotlight.json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _cached = bundled ?? LoadBundled();
        try
        {
            if (File.Exists(_cachePath) && new FileInfo(_cachePath).Length <= MaxBytes)
            {
                var saved = Parse(File.ReadAllBytes(_cachePath));
                if (saved.UpdatedAt >= _cached.UpdatedAt) _cached = saved;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
    }

    public SpotlightSnapshot Cached { get { lock (_sync) return _cached; } }

    public Task<SpotlightSnapshot> GetAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<SpotlightSnapshot> task;
        lock (_sync)
        {
            if (_refresh is { IsCompleted: false }) task = _refresh;
            else if (!force && _clock() - _lastAttempt < RefreshInterval) return Task.FromResult(_cached);
            else
            {
                _lastAttempt = _clock();
                task = _refresh = RefreshAsync();
            }
        }
        // Cancelling a page does not cancel another page's shared request.
        return task.WaitAsync(cancellationToken);
    }

    private async Task<SpotlightSnapshot> RefreshAsync()
    {
        try
        {
            using var response = await _http.GetAsync(FeedUri, HttpCompletionOption.ResponseHeadersRead, _lifetime.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Spotlight response is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(_lifetime.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + count > MaxBytes) throw new InvalidDataException("Spotlight response is too large.");
                memory.Write(buffer, 0, count);
            }
            var bytes = memory.ToArray();
            var snapshot = Parse(bytes);
            lock (_sync)
            {
                if (snapshot.UpdatedAt < _cached.UpdatedAt) return _cached;
                _cached = snapshot;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cachePath))!);
                var temp = _cachePath + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes, _lifetime.Token).ConfigureAwait(false);
                File.Move(temp, _cachePath, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException) { }
        return Cached;
    }

    public static SpotlightSnapshot LoadBundled()
    {
        try
        {
            using var stream = typeof(SpotlightCatalogService).Assembly.GetManifestResourceStream("Steamy.spotlight.json");
            if (stream is null) return SpotlightSnapshot.Empty;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return Parse(memory.ToArray());
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException) { return SpotlightSnapshot.Empty; }
    }

    public static SpotlightSnapshot Parse(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Spotlight response is too large.");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1
            || !root.TryGetProperty("updatedAt", out var timestamp) || timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTimeOffset(out var updated)
            || !root.TryGetProperty("games", out var list) || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() is < 1 or > 80)
            throw new InvalidDataException("Invalid spotlight feed.");
        var games = new List<SpotlightGame>();
        var ids = new HashSet<int>();
        foreach (var item in list.EnumerateArray())
        {
            var game = item.Deserialize<SpotlightGame>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Invalid spotlight game.");
            if (game.AppId <= 0 || !ids.Add(game.AppId) || string.IsNullOrWhiteSpace(game.Name) || game.Name.Length > 200
                || game.Description is null || game.Description.Length > 1000 || game.Publisher is null || game.Publisher.Length > 250
                || game.Genres is null || game.Genres.Length > 250 || game.ReleaseLabel is null || game.ReleaseLabel.Length > 100
                || game.StoreUrl != $"https://store.steampowered.com/app/{game.AppId}/"
                || !IsArtworkUrl(game.HeroUrl) || !IsArtworkUrl(game.HeaderUrl)
                || (!string.IsNullOrEmpty(game.PortraitUrl) && !IsArtworkUrl(game.PortraitUrl)))
                throw new InvalidDataException("Invalid spotlight game metadata.");
            games.Add(game);
        }
        return new(updated, games.AsReadOnly());
    }

    public static bool IsArtworkUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host.Equals("steamstatic.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
