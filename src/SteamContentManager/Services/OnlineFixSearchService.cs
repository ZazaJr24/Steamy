using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using SteamContentManager.Models;

namespace SteamContentManager.Services;

public sealed record OnlineFixSearchResult(string Url, string Source, double Score, int AppId = 0, string ArtworkUrl = "");

public interface IOnlineFixSearchService
{
    /// <summary>Searches online-fix.me for the game and opens the best match in the default browser.</summary>
    Task<OnlineFixSearchResult> SearchAndOpenAsync(string gameName, CancellationToken cancellationToken = default);

    /// <summary>Runs the search steps and returns the chosen URL without opening a browser.</summary>
    Task<OnlineFixSearchResult> SearchAsync(string gameName, int appId = 0, string artworkUrl = "", CancellationToken cancellationToken = default);
}

/// <summary>
/// From-scratch multiplayer-fix helper modeled on the general idea of searching online-fix.me
/// and opening the result in the browser. It performs read-only HTTP GET requests against
/// public search pages, never logs in, never stores credentials, downloads no files and
/// writes nothing to disk or game folders. All account/download steps stay on the website.
/// </summary>
public sealed partial class OnlineFixSearchService : IOnlineFixSearchService, IDisposable
{
    public const string BaseUrl = "https://online-fix.me";
    private const int MatchThreshold = 60;

    private readonly HttpClient _httpClient;
    private readonly Action<string> _openInBrowser;
    private readonly bool _ownsHttpClient;

    public OnlineFixSearchService(HttpClient? httpClient = null, Action<string>? openInBrowser = null)
    {
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SteamContentManager/0.1 (multiplayer fix search)");
        }
        _openInBrowser = openInBrowser ?? (url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
    }

    public async Task<OnlineFixSearchResult> SearchAndOpenAsync(string gameName, CancellationToken cancellationToken = default)
    {
        var result = await SearchAsync(gameName).ConfigureAwait(false);
        _openInBrowser(result.Url);
        return result;
    }

    /// <summary>Runs the search steps and returns the chosen URL without opening a browser.</summary>
    public async Task<OnlineFixSearchResult> SearchAsync(string gameName, int appId = 0, string artworkUrl = "", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return new OnlineFixSearchResult(BaseUrl, "Fallback", 0, appId, artworkUrl);
        }

        // Step 1: the site's own search endpoint.
        var direct = await SearchDirectAsync(gameName, cancellationToken).ConfigureAwait(false);
        if (direct is not null) return direct with { AppId = appId, ArtworkUrl = artworkUrl };

        // Step 2: search-engine fallback limited to the site's game pages.
        var engine = await SearchViaEnginesAsync(gameName, cancellationToken).ConfigureAwait(false);
        if (engine is not null) return engine with { AppId = appId, ArtworkUrl = artworkUrl };

        // Step 3: fall back to the site itself so the user can search there.
        return new OnlineFixSearchResult(BaseUrl, "Fallback", 0, appId, artworkUrl);
    }

    private async Task<OnlineFixSearchResult?> SearchDirectAsync(string gameName, CancellationToken cancellationToken)
    {
        var searchUrl = $"{BaseUrl}/index.php?do=search&subaction=search&story={Uri.EscapeDataString(gameName)}";
        var html = await FetchAsync(searchUrl, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(html)) return null;

        return PickBest(gameName, ExtractGameLinks(html), "online-fix.me search");
    }

    private async Task<OnlineFixSearchResult?> SearchViaEnginesAsync(string gameName, CancellationToken cancellationToken)
    {
        var query = $"site:online-fix.me/games {gameName} online fix";
        var urls = new[]
        {
            $"https://www.google.com/search?q={Uri.EscapeDataString(query)}",
            $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}"
        };
        var best = new OnlineFixSearchResult(string.Empty, string.Empty, 0);
        foreach (var url in urls)
        {
            var html = await FetchAsync(url, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(html)) continue;
            var candidate = PickBest(gameName, ExtractGameLinks(html), "Search engine fallback");
            if (candidate is not null && candidate.Score > best.Score) best = candidate;
        }

        return best.Score >= MatchThreshold ? best : null;
    }

    private async Task<string> FetchAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return string.Empty;
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return string.Empty;
        }
    }

    /// <summary>Picks the highest-scoring online-fix.me game link, or null below the match threshold.</summary>
    public OnlineFixSearchResult? PickBest(string gameName, IEnumerable<string> links, string source, int appId = 0, string artworkUrl = "")
    {
        OnlineFixSearchResult? best = null;
        foreach (var url in links)
        {
            var score = ScoreMatch(gameName, url);
            if (best is null || score > best.Score) best = new OnlineFixSearchResult(url, source, score, appId, artworkUrl);
        }
        return best is not null && best.Score >= MatchThreshold ? best : null;
    }

    /// <summary>
    /// Searches online-fix.me for a specific installed game identified by its name and Steam AppId.
    /// The returned result preserves the AppId and the standard Steam library artwork URL so the UI
    /// can show the game tile without inventing metadata.
    /// </summary>
    public async Task<OnlineFixSearchResult?> FindBestForAppAsync(Game game, CancellationToken cancellationToken = default)
    {
        if (game is null || game.AppId <= 0)
        {
            return null;
        }

        var result = await SearchAsync(game.Name, game.AppId, game.ArtworkUrl, cancellationToken).ConfigureAwait(false);
        return result with { AppId = game.AppId, ArtworkUrl = game.ArtworkUrl };
    }

    /// <summary>
    /// Self-written relevance score combining coverage (share of game-name words found in the
    /// URL path) with precision (share of path words that belong to the game name), balanced
    /// by their geometric mean. A path with many extra unrelated words therefore scores lower
    /// than an exact match. No external matching libraries.
    /// </summary>
    public static int ScoreMatch(string gameName, string url)
    {
        var want = Normalize(gameName);
        var hay = Normalize(Uri.UnescapeDataString(PathSegmentOf(url)));
        if (want.Length == 0 || hay.Length == 0) return 0;

        var queryWords = want.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToArray();
        if (queryWords.Length == 0) queryWords = new[] { want };
        var pathWords = hay.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToArray();

        var coverage = (int)Math.Round(100.0 * queryWords.Count(hay.Contains) / queryWords.Length);
        var precision = pathWords.Length == 0 ? 0 : (int)Math.Round(100.0 * pathWords.Count(queryWords.Contains) / pathWords.Length);
        return (int)Math.Round(Math.Sqrt(coverage * (double)precision));
    }

    /// <summary>Returns the file name segment of a URL without query, fragment or extension.</summary>
    private static string PathSegmentOf(string url)
    {
        var cleaned = url;
        var cutoff = cleaned.IndexOfAny(new[] { '?', '#' });
        if (cutoff >= 0) cleaned = cleaned[..cutoff];
        var slash = cleaned.LastIndexOf('/');
        var segment = slash >= 0 ? cleaned[(slash + 1)..] : cleaned;
        var dot = segment.LastIndexOf('.');
        return dot > 0 ? segment[..dot] : segment;
    }

    private static string Normalize(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant())
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>
    /// Extracts absolute online-fix.me /games/... links from HTML and unwraps common
    /// search-engine redirect wrappers. Kept intentionally small and self-written.
    /// </summary>
    public static IReadOnlyList<string> ExtractGameLinks(string html)
    {
        if (string.IsNullOrEmpty(html)) return Array.Empty<string>();

        var results = new List<string>();
        foreach (var attribute in HrefPattern().EnumerateMatches(html))
        {
            var raw = html.Substring(attribute.Index + 6, attribute.Length - 7);
            if (!TryBuildGameUrl(raw, out var url)) continue;
            if (!results.Contains(url)) results.Add(url);
        }
        return results;
    }

    [GeneratedRegex("href\\s*=\\s*\"([^\"]+)\"|href\\s*=\\s*'([^']+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HrefPattern();

    private static bool TryBuildGameUrl(string raw, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var candidate = raw.Trim();
        if (candidate.StartsWith("/url?", StringComparison.OrdinalIgnoreCase))
        {
            // Google wraps results as /url?q=<encoded target>&...
            var match = GoogleRedirectPattern().Match(candidate);
            if (!match.Success) return false;
            candidate = Uri.UnescapeDataString(match.Groups[1].Value);
        }
        else
        {
            candidate = Uri.UnescapeDataString(candidate);
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.EndsWith("online-fix.me", StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.AbsolutePath.StartsWith("/games/", StringComparison.OrdinalIgnoreCase)) return false;

        url = $"https://{uri.Host}{uri.AbsolutePath}";
        return true;
    }

    [GeneratedRegex("[?&]q=([^&]+)")]
    private static partial Regex GoogleRedirectPattern();

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
