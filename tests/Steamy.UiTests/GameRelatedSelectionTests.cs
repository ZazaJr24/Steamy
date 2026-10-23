using Steamy.Models;
using Steamy.Services;
using Xunit.Abstractions;

namespace Steamy.UiTests;

public sealed class GameRelatedSelectionTests(ITestOutputHelper output)
{
    [Fact]
    public void LargeCatalogSelectionDoesNotAllocatePerCandidate()
    {
        var entry = new SteamCatalogItem { AppId = 100, AppType = SteamCatalogAppType.Game, DevelopersDisplay = "Other studio", GenresDisplay = "Strategy · Simulation, Casual" };
        var items = Enumerable.Repeat(entry, 20000).ToArray();
        var overview = new GameOverview(Developer: "Missing studio") { Genres = "Action · Adventure" };
        GameRelatedSelection.Select(items, 42, overview, default);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var selected = GameRelatedSelection.Select(items, 42, overview, default);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"20,000 candidate selection allocated {allocated:N0} bytes.");
        Assert.InRange(allocated, 0, 8192);
        Assert.Empty(selected.Developer); Assert.Empty(selected.Similar);
    }

    [Fact]
    public void SelectionKeepsCompanyNamesAndGenreTokensAndMatchesTheExistingOrder()
    {
        var items = Enumerable.Range(1, 400).Select(index => new SteamCatalogItem
        {
            AppId = index,
            AppType = index % 7 == 0 ? SteamCatalogAppType.Dlc : index % 11 == 0 ? SteamCatalogAppType.Unknown : SteamCatalogAppType.Game,
            DevelopersDisplay = index % 5 == 0 ? " Studio, Ltd. " : "Studio, Other",
            GenresDisplay = index % 3 == 0 ? "Strategy · ACTION, Adventure" : "Actionable · Simulation"
        }).ToArray();
        var overview = new GameOverview(Developer: "Studio, Ltd.") { Genres = "Action · RPG" };
        var candidates = items.Where(item => item.AppId != 42 && item.AppType == SteamCatalogAppType.Game).ToArray();
        var developer = candidates.Where(item => string.Equals(item.DevelopersDisplay.Trim(), overview.Developer.Trim(), StringComparison.OrdinalIgnoreCase)).Take(6).ToArray();
        var ids = developer.Select(item => item.AppId).ToHashSet();
        var genres = overview.Genres.Split([',', '·'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var similar = candidates.Where(item => !ids.Contains(item.AppId) && item.GenresDisplay.Split([',', '·'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(genres.Contains)).Take(6).ToArray();
        var result = GameRelatedSelection.Select(items, 42, overview, default);
        Assert.Equal(developer, result.Developer); Assert.Equal(similar, result.Similar);
        Assert.All(result.Developer.Concat(result.Similar), item => Assert.Equal(SteamCatalogAppType.Game, item.AppType));
    }
}
