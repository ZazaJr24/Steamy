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
        Assert.Empty(ReleaseCountdown.Upcoming([new() { ComingSoon = true, ReleaseLabel = "Q2 2026" }, new() { ComingSoon = true, ReleaseLabel = "Late 2025" }], new(2026,10,1)));
        Assert.Equal([8,7,6,5], ReleaseCountdown.Upcoming(games, new(2026,10,1)).Select(game => game.AppId));
    }
    [Fact]
    public void ConfirmedClockTicksSecondsAndDoesNotInventTimesForDateOnlyGames()
    {
        var game = new SpotlightGame { ReleaseTime = Now.AddDays(2).AddHours(3).AddMinutes(4).AddSeconds(5) };
        Assert.Equal(new ReleaseCountdownParts(2, 3, 4, 5), ReleaseCountdown.Parts(game, Now));
        Assert.Equal(new ReleaseCountdownParts(2, 3, 4, 4), ReleaseCountdown.Parts(game, Now.AddSeconds(1)));
        Assert.Equal(new ReleaseCountdownParts(0, 0, 0, 1), ReleaseCountdown.Parts(game, game.ReleaseTime!.Value.AddMilliseconds(-100)));
        Assert.Null(ReleaseCountdown.Parts(game, game.ReleaseTime.Value));
        Assert.Null(ReleaseCountdown.Parts(new() { ReleaseDate = new(2026, 10, 2) }, Now));
    }

    [Fact]
    public void UpcomingUsesConfirmedTimeAcrossStoreDateAndTimezoneBoundaries()
    {
        SpotlightGame[] games = [
            new() { AppId = 1, ComingSoon = true, ReleaseDate = new(2026, 9, 30), ReleaseTime = Now.AddHours(1).ToUniversalTime() },
            new() { AppId = 2, ComingSoon = true, ReleaseDate = new(2026, 10, 2), ReleaseTime = Now.AddSeconds(-1) },
            new() { AppId = 3, ComingSoon = false, ReleaseTime = Now.AddHours(1) },
            new() { AppId = 4, ComingSoon = true, ReleaseTime = Now.ToUniversalTime() }];
        Assert.Equal(1, Assert.Single(ReleaseCountdown.UpcomingAt(games, Now)).AppId);
    }

    [Fact]
    public void HeadlineGamesLeadTheGalleryButReleasedAndExpiredFlagsStillExcludeThem()
    {
        SpotlightGame[] games = [
            new() { AppId = 1, ComingSoon = true, ReleaseTime = Now.AddHours(1) },
            new() { AppId = 2, ComingSoon = true, Featured = true, ReleaseTime = Now.AddDays(4) },
            new() { AppId = 3, ComingSoon = true, Featured = true, ReleaseTime = Now.AddDays(2) },
            new() { AppId = 4, ComingSoon = false, Featured = true, ReleaseTime = Now.AddDays(1) },
            new() { AppId = 5, ComingSoon = true, Featured = true, ReleaseTime = Now.AddSeconds(-1) }];
        Assert.Equal([3, 2, 1], ReleaseCountdown.UpcomingAt(games, Now).Select(game => game.AppId));
    }

}
