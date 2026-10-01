using Steamy.Services;

namespace Steamy.Tests;

public class ReleaseCountdownTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2));
    [Fact]
    public void DatesNeverImplyDownloadAvailability()
    {
        Assert.Equal("TBA", ReleaseCountdown.Label(new() { ReleaseLabel = "October 2026" }, Now));
        Assert.Equal("Today", ReleaseCountdown.Label(new() { ReleaseDate = new(2026, 10, 1) }, Now));
        Assert.Equal("1 day", ReleaseCountdown.Label(new() { ReleaseDate = new(2026, 10, 2) }, Now));
        Assert.Equal("Awaiting release confirmation", ReleaseCountdown.Label(new() { ReleaseDate = new(2026, 9, 30) }, Now));
        Assert.Equal("0d 01h 00m 00s", ReleaseCountdown.Label(new() { ReleaseTime = Now.AddHours(1) }, Now));
        Assert.Equal("Awaiting release confirmation", ReleaseCountdown.Label(new() { ReleaseTime = Now.AddSeconds(-1) }, Now));
    }
    [Fact]
    public void StaleFeedsExcludeReleasedGamesAndExpiredDatesAndSortNearDatesFirst()
    {
        SpotlightGame[] games = [
            new() { AppId = 1, ComingSoon = false, ReleaseDate = new(2026, 10, 2) },
            new() { AppId = 2, ComingSoon = true, ReleaseDate = new(2026, 9, 30) },
            new() { AppId = 3, ComingSoon = true, ReleaseLabel = "September 2026" },
            new() { AppId = 4, ComingSoon = true, ReleaseLabel = "2025" },
            new() { AppId = 5, ComingSoon = true, ReleaseLabel = "TBA" },
            new() { AppId = 6, ComingSoon = true, ReleaseDate = new(2026, 11, 1) },
            new() { AppId = 7, ComingSoon = true, ReleaseDate = new(2026, 10, 2) },
            new() { AppId = 8, ComingSoon = true, ReleaseDate = new(2026, 10, 1) }];
        Assert.Equal([8,7,6,5], ReleaseCountdown.Upcoming(games, new(2026,10,1)).Select(game => game.AppId));
    }
}
