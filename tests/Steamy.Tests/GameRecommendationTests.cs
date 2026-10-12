using Steamy.Services;

namespace Steamy.Tests;

public sealed class GameRecommendationTests
{
    [Fact]
    public void OwnedGamesAreExcludedAndSeriesMatchesLeadGenreMatches()
    {
        var owned = new[] { new RecommendationProfile(1, "Resident Evil 4", ["Action", "Adventure"]), new RecommendationProfile(2, "ELDEN RING", ["Action", "RPG"]) };
        var result = GameRecommendationRanker.Rank(owned, [owned[0], new(3, "Resident Evil Village", ["Action"]), new(4, "Other RPG", ["Action", "RPG"]), new(5, "Football", ["Sports"])]);
        Assert.Equal(new[] { 3, 4 }, result.Select(item => item.AppId));
        Assert.Contains("series", result[0].Reason); Assert.Contains("RPG", result[1].Reason);
    }
    [Fact]
    public void MissingMetadataDoesNotFabricateGenresOrRecommendations()
    {
        Assert.Empty(GameRecommendationRanker.Genres("Genres unavailable"));
        Assert.Empty(GameRecommendationRanker.Rank([], [new(10, "Game title", ["RPG"])]));
        Assert.Equal(new[] { "Action", "RPG" }, GameRecommendationRanker.Genres("Action · RPG · Action"));
    }
}
