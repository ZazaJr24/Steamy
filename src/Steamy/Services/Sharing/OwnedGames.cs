using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Xml;
using System.Xml.Linq;

namespace Steamy.Services;

// The app and the test project both define LogLevel/ILoggingService; the alias keeps this file
// compiling against either one.
using SupportedLogLevel = Steamy.Models.LogLevel;

/// <summary>One game of the user's account library: its id, its name and where it came from.</summary>
public sealed record OwnedGame(int AppId, string Name, bool Installed);

/// <summary>Result of loading the account library: the games and how they were obtained.</summary>
public sealed record OwnedGamesResult(bool Succeeded, string Message, IReadOnlyList<OwnedGame> Games)
{
    public static OwnedGamesResult Failure(string message) => new(false, message, Array.Empty<OwnedGame>());
}

/// <summary>Loads every game of the user's Steam account — not only the installed ones.</summary>
public interface IOwnedGamesService
{
    /// <summary>The whole account library (purchased, F2P, family-shared), installed or not.</summary>
    Task<OwnedGamesResult> LoadAsync(CancellationToken cancellationToken = default);
}


/// <summary>
/// Folds the account library into scanned Share-page items: every owned game the scan has not
/// found a manifest or Lua for becomes its own candidate with an empty file list — sharing it
/// fetches the manifests from a source.
/// </summary>
public static class OwnedGamesMerge
{
    public static IReadOnlyList<ShareCandidate> MergeOwned(
        IReadOnlyList<ShareCandidate> scanned, IReadOnlyList<OwnedGame> owned)
    {
        if (owned.Count == 0) return scanned;

        var byApp = scanned.ToDictionary(candidate => candidate.AppId);
        var merged = new List<ShareCandidate>(scanned);

        foreach (var game in owned)
        {
            if (byApp.ContainsKey(game.AppId)) continue;
            if (ManifestLibraryScanner.IgnoredApp(game.AppId)) continue;

            var candidate = new ShareCandidate(game.AppId, game.Name, ShareSourceKind.Manifests,
                string.Empty, Array.Empty<ShareFile>(), Array.Empty<DepotManifestRef>(), DateTime.MinValue);
            byApp[game.AppId] = candidate;
            merged.Add(candidate);
        }

        return merged
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// Loads the user's whole account library from the public profile XML (gameslist), which needs no
/// key and no login — only a SteamID and a profile whose game details are public.
/// </summary>
public sealed class OwnedGamesService : IOwnedGamesService
{
    private readonly HttpClient _http;
    private readonly ILoggingService _logging;

    public OwnedGamesService(ILoggingService logging, HttpClient? httpClient = null)
    {
        _logging = logging;
        _http = httpClient ?? new HttpClient(StableDnsHandler.Create()) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<OwnedGamesResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var steamId = await Task.Run(() => SteamIdLookup.FindLocalSteamId(), cancellationToken).ConfigureAwait(false);
        if (steamId is null)
            return OwnedGamesResult.Failure("No Steam account found on this PC — log into Steam once, then try again.");

        try
        {
            var url = $"https://steamcommunity.com/profiles/{steamId}/games?tab=all&xml=1";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return OwnedGamesResult.Failure($"Steam returned HTTP {(int)response.StatusCode} for your profile.");

            var games = OwnedGamesXml.Parse(text);
            if (games.Count == 0)
                return OwnedGamesResult.Failure(
                    "Your profile keeps game details private, so Steam does not hand out the list. " +
                    "Set \u00E2\u20AC\u0153Game details\u00E2\u20AC\u009D to public in your Steam privacy settings, then load again.");

            _logging.Add(SupportedLogLevel.Info, "Sharing", $"Loaded {games.Count} owned games from the public profile.");
            return new OwnedGamesResult(true, $"Loaded {games.Count} games from your account.", games);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logging.Add(SupportedLogLevel.Warning, "Sharing", $"Loading the account library failed: {exception.Message}");
            return OwnedGamesResult.Failure($"The library could not be loaded: {exception.Message}");
        }
    }
}

/// <summary>Parses the gameslist XML the public profile serves.</summary>
public static class OwnedGamesXml
{
    public static IReadOnlyList<OwnedGame> Parse(string xml)
    {
        var games = new List<OwnedGame>();
        if (string.IsNullOrWhiteSpace(xml)) return games;

        try
        {
            var document = XDocument.Parse(xml);
            foreach (var element in document.Descendants("game"))
            {
                var appId = (int?)element.Element("appID");
                if (appId is not > 0) continue;
                var name = element.Element("name")?.Value.Trim() ?? string.Empty;
                if (name.Length == 0) name = $"App {appId}";
                games.Add(new OwnedGame(appId.Value, name, Installed: false));
            }
        }
        catch (XmlException)
        {
            // A half-loaded page yields no games; the caller reports the empty list.
        }

        return games;
    }
}
