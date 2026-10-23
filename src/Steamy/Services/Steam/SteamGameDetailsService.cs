using System.Net.Http;
using System.Text.Json;
using System.IO;
using System.Runtime.CompilerServices;

namespace Steamy.Services;

public sealed record GameAchievement(string Id, string Name, string Description, bool Unlocked, string IconUrl, DateTimeOffset? UnlockedAt)
{
    public Uri? IconUri => Uri.TryCreate(IconUrl, UriKind.Absolute, out var uri) ? uri : null;
    public string StateLabel => Unlocked ? (UnlockedAt is { } date ? $"Unlocked · {date.LocalDateTime:d}" : "Unlocked") : "Locked";
}
public sealed record GameAchievementSnapshot(IReadOnlyList<GameAchievement> Items, bool Available, string Message)
{
    public static GameAchievementSnapshot Unavailable(string message) => new([], false, message);
}
public sealed record GameScreenshot(Uri Thumbnail, Uri Full);
public sealed record GameNews(string Title, string Summary, string DateLabel, Uri Link);
public sealed record GameReviewSummary(long Positive, long Total, string Description)
{
    public double PositivePercent => Total > 0 ? Math.Clamp(Positive * 100d / Total, 0, 100) : 0;
}
public sealed record GameOverview(string Description = "", string Developer = "", string Publisher = "")
{
    public GameReviewSummary? Reviews { get; init; }
    public string DeveloperStoreName { get; init; } = "";
    public string DetailedDescription { get; init; } = "";
    public string ReleaseDate { get; init; } = "";
    public string Genres { get; init; } = "";
    public string SystemRequirements { get; init; } = "";
    public string Languages { get; init; } = "";
    public string Features { get; init; } = "";
    public IReadOnlyList<GameScreenshot> Screenshots { get; init; } = [];
    public IReadOnlyList<GameNews> News { get; init; } = [];
}
public interface IGameDetailsDataService
{
    Task<GameOverview> ReadOverviewAsync(int appId, CancellationToken token = default);
    Task<GameAchievementSnapshot> ReadAchievementsAsync(int appId, CancellationToken token = default);
}
public interface IProgressiveGameDetailsDataService
{
    IAsyncEnumerable<GameOverview> ReadOverviewUpdatesAsync(int appId, CancellationToken token = default);
}

/// <summary>Only actual Steam player data is used for achievement counts and unlock states.</summary>
public sealed class SteamGameDetailsService(ISettingsService settings, ISecureCredentialService credentials, HttpClient? httpClient = null, ISteamCatalogService? catalog = null) : IGameDetailsDataService, IProgressiveGameDetailsDataService, IDisposable
{
    public const string CredentialKey = "steam-api-key";
    private readonly HttpClient _http = httpClient ?? new HttpClient(StableDnsHandler.Create()) { Timeout = TimeSpan.FromSeconds(12) };
    private const int OverviewCapacity = 64;
    private readonly object _overviewGate = new();
    private readonly Dictionary<int, LinkedListNode<KeyValuePair<int, GameOverview>>> _overviews = [];
    private readonly LinkedList<KeyValuePair<int, GameOverview>> _overviewLru = [];
    private bool TryOverview(int appId, out GameOverview overview)
    {
        lock (_overviewGate)
        {
            if (!_overviews.TryGetValue(appId, out var node)) { overview = null!; return false; }
            _overviewLru.Remove(node); _overviewLru.AddLast(node); overview = node.Value.Value; return true;
        }
    }
    private void RememberOverview(int appId, GameOverview overview)
    {
        lock (_overviewGate)
        {
            if (_overviews.Remove(appId, out var old)) _overviewLru.Remove(old);
            _overviews[appId] = _overviewLru.AddLast(new KeyValuePair<int, GameOverview>(appId, overview));
            if (_overviews.Count <= OverviewCapacity) return;
            _overviews.Remove(_overviewLru.First!.Value.Key); _overviewLru.RemoveFirst();
        }
    }
    public async Task<GameOverview> ReadOverviewAsync(int appId, CancellationToken token = default)
    {
        var latest = new GameOverview();
        await foreach (var overview in ReadOverviewUpdatesAsync(appId, token).ConfigureAwait(false)) latest = overview;
        return latest;
    }
    public async IAsyncEnumerable<GameOverview> ReadOverviewUpdatesAsync(int appId, [EnumeratorCancellation] CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (appId <= 0) { yield return new(); yield break; }
        if (TryOverview(appId, out var cached)) { yield return cached; yield break; }
        var overview = await Task.Run(() => ReadBaseOverviewAsync(appId, token), token).ConfigureAwait(false);
        if (overview is null) { yield return new(); yield break; }
        using var requests = CancellationTokenSource.CreateLinkedTokenSource(token);
        var requestToken = requests.Token;
        var news = Task.Run(() => ReadNewsAsync(appId, requestToken), requestToken);
        var reviews = Task.Run(() => ReadReviewsAsync(appId, requestToken), requestToken);
        try
        {
            // Publish the ready Store data before waiting for optional servers.
            yield return overview;
            var pending = new List<Task> { news, reviews };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(token).ConfigureAwait(false);
                pending.Remove(completed);
                overview = ReferenceEquals(completed, news)
                    ? overview with { News = await news.ConfigureAwait(false) }
                    : overview with { Reviews = await reviews.ConfigureAwait(false) };
                token.ThrowIfCancellationRequested();
                if (pending.Count == 0) RememberOverview(appId, overview);
                yield return overview;
            }
        }
        finally
        {
            requests.Cancel();
            _ = ObserveCompletionAsync(news, reviews);
        }
    }
    private static async Task ObserveCompletionAsync(Task news, Task reviews)
    {
        try { await Task.WhenAll(news, reviews).ConfigureAwait(false); }
        catch (Exception) { /* Observe abandoned optional requests after navigation/cancellation. */ }
    }
    private async Task<GameOverview?> ReadBaseOverviewAsync(int appId, CancellationToken token)
    {
        try
        {
            if (catalog is not null)
            {
                if (await catalog.GetDetailsAsync(appId, token).ConfigureAwait(false) is { } details)
                    return new(details.ShortDescription, string.Join(", ", details.Developers), string.Join(", ", details.Publishers))
                    {
                        DetailedDescription = details.DetailedDescription, ReleaseDate = details.ReleaseDate,
                        Genres = details.GenresDisplay, SystemRequirements = details.SystemRequirements,
                        Languages = details.Languages, Features = details.Features,
                        DeveloperStoreName = details.Developers.FirstOrDefault() ?? "",
                        Screenshots = (details.Screenshots ?? []).Take(6).Select(item =>
                            SafeSteamUri(item.ThumbnailUrl, true) is { } thumb && SafeSteamUri(item.FullUrl, true) is { } full
                                ? new GameScreenshot(thumb, full) : null).OfType<GameScreenshot>().ToArray()
                    };
            }
            var query = JsonSerializer.Serialize(new { ids = new[] { new { appid = appId } },
                context = new { language = "english", country_code = "US", steam_realm = 1 }, data_request = new { include_basic_info = true } });
            using var json = await GetJsonAsync("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(query), token);
            var item = json.RootElement.GetProperty("response").GetProperty("store_items").EnumerateArray()
                .FirstOrDefault(row => row.TryGetProperty("appid", out var id) && id.GetInt32() == appId);
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("basic_info", out var info)) return null;
            string Names(string property) => info.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array
                ? string.Join(", ", values.EnumerateArray().Take(8).Select(value => Text(value, "name"))) : "";
            return new(Text(info, "short_description"), Names("developers"), Names("publishers"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException) { return null; }
    }
    private async Task<GameReviewSummary?> ReadReviewsAsync(int appId, CancellationToken token)
    {
        try
        {
            using var json = await GetJsonAsync($"https://store.steampowered.com/appreviews/{appId}?json=1&language=all&purchase_type=all&num_per_page=1", token);
            var root = json.RootElement;
            if (!root.TryGetProperty("success", out var success) || !success.TryGetInt32(out var code) || code != 1 || !root.TryGetProperty("query_summary", out var summary)) return null;
            if (!summary.TryGetProperty("total_reviews", out var totalValue) || !totalValue.TryGetInt64(out var total) || total <= 0
                || !summary.TryGetProperty("total_positive", out var positiveValue) || !positiveValue.TryGetInt64(out var positive) || positive < 0 || positive > total) return null;
            return new(positive, total, Text(summary, "review_score_desc"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException) { return null; }
    }
    private async Task<IReadOnlyList<GameNews>> ReadNewsAsync(int appId, CancellationToken token)
    {
        try
        {
            using var json = await GetJsonAsync($"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={appId}&count=3&maxlength=600&feeds=steam_community_announcements", token);
            if (!json.RootElement.TryGetProperty("appnews", out var news) || !news.TryGetProperty("newsitems", out var rows) || rows.ValueKind != JsonValueKind.Array) return [];
            var items = new List<GameNews>();
            foreach (var row in rows.EnumerateArray().Take(3))
            {
                var link = SafeSteamUri(Text(row, "url"), false);
                var title = SteamDisplayText.Plain(Text(row, "title"));
                if (link is null || title.Length == 0) continue;
                var date = row.TryGetProperty("date", out var value) && value.TryGetInt64(out var seconds) && seconds is > 0 and < 253402300800
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("d MMM yyyy") : "";
                items.Add(new(title, SteamDisplayText.Plain(Text(row, "contents")), date, link));
            }
            return items;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException) { return []; }
    }
    private static Uri? SafeSteamUri(string value, bool image)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
        return (image ? uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
            : uri.Host is "store.steampowered.com" or "steamcommunity.com" or "steamstore-a.akamaihd.net") ? uri : null;
    }
    public Task<GameAchievementSnapshot> ReadAchievementsAsync(int appId, CancellationToken token = default) =>
        Task.Run(() => ReadAchievementsCoreAsync(appId, token), token);

    private async Task<GameAchievementSnapshot> ReadAchievementsCoreAsync(int appId, CancellationToken token)
    {
        if (appId <= 0) return GameAchievementSnapshot.Unavailable("Steam achievements are not available for this local game.");
        var configuration = settings.Load();
        var profile = configuration.SteamId64.Trim();
        if (profile.Length == 0) profile = SteamIdLookup.FindLocalSteamId(configuration.SteamLibraryPath)?.ToString() ?? "";
        if (!ulong.TryParse(profile, out var steamId) || steamId < 76561197960265728)
            return GameAchievementSnapshot.Unavailable("Add your Steam ID in Settings to see your achievements.");
        string? key;
        try { key = await credentials.ReadAsync(CredentialKey, token) ?? await credentials.ReadAsync("SteamWebApi", token); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { return GameAchievementSnapshot.Unavailable("Your saved Steam API key could not be read. Connect it again below."); }
        if (string.IsNullOrWhiteSpace(key)) return GameAchievementSnapshot.Unavailable("Connect a Steam Web API key below to load your achievements.");
        try
        {
            // Never log either URL: the API key is transmitted over HTTPS only.
            var encodedKey = Uri.EscapeDataString(key);
            using var player = await GetJsonAsync($"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?appid={appId}&steamid={steamId}&key={encodedKey}&l=english", token);
            if (!player.RootElement.TryGetProperty("playerstats", out var stats)
                || !stats.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                return GameAchievementSnapshot.Unavailable("Steam has no readable achievements for this profile and game. Check your game-details privacy setting.");
            if (!stats.TryGetProperty("achievements", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return new([], true, "This game has no Steam achievements.");
            var icons = new Dictionary<string, (string Locked, string Unlocked)>();
            try
            {
                using var schema = await GetJsonAsync($"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?appid={appId}&key={encodedKey}&l=english", token);
                if (schema.RootElement.TryGetProperty("game", out var game) && game.TryGetProperty("availableGameStats", out var available)
                    && available.TryGetProperty("achievements", out var schemaRows))
                    foreach (var row in schemaRows.EnumerateArray().Take(5000)) icons[Text(row, "name")] = (SafeIcon(Text(row, "icongray")), SafeIcon(Text(row, "icon")));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException) { }
            var items = rows.EnumerateArray().Take(5000).Select(row =>
            {
                var id = Text(row, "apiname");
                var unlocked = row.TryGetProperty("achieved", out var value) && value.TryGetInt32(out var achieved) && achieved == 1;
                DateTimeOffset? date = row.TryGetProperty("unlocktime", out var time) && time.TryGetInt64(out var seconds) && seconds is > 0 and < 253402300800
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
                var icon = icons.GetValueOrDefault(id);
                return new GameAchievement(id, Text(row, "name") is { Length: > 0 } name ? name : id, Text(row, "description"), unlocked,
                    unlocked ? icon.Unlocked ?? "" : icon.Locked ?? "", date);
            }).Where(item => item.Id.Length > 0).DistinctBy(item => item.Id).OrderByDescending(item => item.Unlocked).ThenByDescending(item => item.UnlockedAt).ToArray();
            return new(items, true, items.Length == 0 ? "This game has no Steam achievements." : "Synced with Steam");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
        { return GameAchievementSnapshot.Unavailable("Achievements are unavailable right now. Check your Steam API key and profile privacy, then try again."); }
    }
    private static string Text(JsonElement item, string property) => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string SafeIcon(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && (uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".steamcommunity.com", StringComparison.OrdinalIgnoreCase)) ? value : "";
    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new IOException("Response too large.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > 8 * 1024 * 1024) throw new IOException("Response too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        output.Position = 0;
        return await JsonDocument.ParseAsync(output, cancellationToken: token).ConfigureAwait(false);
    }
    public void Dispose() { if (httpClient is null) _http.Dispose(); }
}
