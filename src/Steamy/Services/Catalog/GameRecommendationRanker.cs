using System.Text.RegularExpressions;

namespace Steamy.Services;

public sealed record RecommendationProfile(int AppId, string Name, IReadOnlyList<string> Genres);
public sealed record GameRecommendation(int AppId, double Score, string Reason);

/// <summary>Local, explainable similarity. Owned titles are excluded; unavailable genres are never invented.</summary>
public static class GameRecommendationRanker
{
    public static string[] Genres(string? value) => (value ?? "").Split(['·', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(part => !part.Contains("unavailable", StringComparison.OrdinalIgnoreCase) && !part.Contains("unknown", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static string Series(string name)
    {
        var words = Regex.Replace(name.ToLowerInvariant(), @"[^\p{L}\p{N} ]", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => word is not ("the" or "a" or "an") && !word.All(char.IsDigit)).ToArray();
        if (words.Length < 2) return "";
        return string.Join(' ', words.Take(2));
    }
    public static IReadOnlyList<GameRecommendation> Rank(IReadOnlyList<RecommendationProfile> owned, IEnumerable<RecommendationProfile> candidates)
    {
        var ids = owned.Select(game => game.AppId).ToHashSet();
        var genres = owned.SelectMany(game => game.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(genre => genre, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var series = owned.Select(game => Series(game.Name)).Where(key => key.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.Where(game => !ids.Contains(game.AppId)).Select(game =>
        {
            var key = Series(game.Name);
            var sameSeries = key.Length > 0 && series.Contains(key);
            var matches = game.Genres.Where(genres.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(genre => genres[genre]).ToArray();
            // A series match has priority. Broad genres cannot overwhelm it just because a library is large.
            var score = (sameSeries ? 100 : 0) + matches.Sum(genre => 12d * genres[genre] / Math.Max(1, owned.Count));
            var reason = sameSeries ? "More from a series in your library" : matches.Length > 0 ? $"Because you play {string.Join(" · ", matches.Take(2))}" : "";
            return new GameRecommendation(game.AppId, score, reason);
        }).Where(result => result.Score > 0).OrderByDescending(result => result.Score).ThenBy(result => result.AppId).ToArray();
    }
}
