using Steamy.Models;

namespace Steamy.Services;

internal static class GameRelatedSelection
{
    internal sealed record Result(SteamCatalogItem[] Developer, SteamCatalogItem[] Similar);

    internal static Result Select(IReadOnlyList<SteamCatalogItem> items, int appId, GameOverview overview, CancellationToken token)
    {
        var developer = overview.Developer.AsSpan().Trim();
        var byDeveloper = new List<SteamCatalogItem>(6);
        var similar = new List<SteamCatalogItem>(6);
        token.ThrowIfCancellationRequested();
        if (!developer.IsEmpty)
        {
            for (var index = 0; index < items.Count && byDeveloper.Count < 6; index++)
            {
                if ((index & 127) == 0) token.ThrowIfCancellationRequested();
                var item = items[index];
                if (item.AppId != appId && item.AppType == SteamCatalogAppType.Game &&
                    item.DevelopersDisplay.AsSpan().Trim().Equals(developer, StringComparison.OrdinalIgnoreCase)) byDeveloper.Add(item);
            }
        }
        var genres = overview.Genres.Split([',', '·'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (genres.Length > 0)
        {
            for (var index = 0; index < items.Count && similar.Count < 6; index++)
            {
                if ((index & 127) == 0) token.ThrowIfCancellationRequested();
                var item = items[index];
                if (item.AppId == appId || item.AppType != SteamCatalogAppType.Game || ContainsApp(byDeveloper, item.AppId)) continue;
                if (SharesGenre(item.GenresDisplay.AsSpan(), genres)) similar.Add(item);
            }
        }
        token.ThrowIfCancellationRequested();
        return new(byDeveloper.ToArray(), similar.ToArray());
    }

    private static bool ContainsApp(List<SteamCatalogItem> items, int appId)
    {
        foreach (var item in items) if (item.AppId == appId) return true;
        return false;
    }

    private static bool SharesGenre(ReadOnlySpan<char> text, string[] genres)
    {
        while (!text.IsEmpty)
        {
            var end = text.IndexOfAny(',', '·');
            var part = (end < 0 ? text : text[..end]).Trim();
            foreach (var genre in genres)
                if (part.Equals(genre.AsSpan(), StringComparison.OrdinalIgnoreCase)) return true;
            if (end < 0) break;
            text = text[(end + 1)..];
        }
        return false;
    }
}
