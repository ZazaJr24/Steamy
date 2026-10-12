using Steamy.Playtime;
using Steamy.Services;

namespace Steamy.Tests;

public sealed class SteamPlayTimeTests
{
    [Fact]
    public void ImportsHistoricalSteamTimeWithoutAddingTheAlreadyObservedSessionTwice()
    {
        var now = DateTimeOffset.UtcNow;
        var steam = new SteamPlayTimes("A", new Dictionary<int, long> { [42] = 3600 });
        var first = SteamPlayTimeMerger.Merge(new(1, now, [new(42, 120, false)]), steam, null);
        Assert.Equal(3600, Assert.Single(first.Snapshot.Games).Seconds);
        var afterMinute = SteamPlayTimeMerger.Merge(new(1, now, [new(42, 180, true)]), steam, first.State);
        Assert.Equal(3660, Assert.Single(afterMinute.Snapshot.Games).Seconds);
        var steamCaughtUp = SteamPlayTimeMerger.Merge(new(1, now, [new(42, 180, true)]),
            new("A", new Dictionary<int, long> { [42] = 3660 }), first.State);
        Assert.Equal(3660, Assert.Single(steamCaughtUp.Snapshot.Games).Seconds);
        Assert.True(Assert.Single(steamCaughtUp.Snapshot.Games).IsRunning);
    }

    [Fact]
    public void KeepsStandaloneGamesAndDoesNotMixAccountBaselines()
    {
        var local = new PlayTimeSnapshot(1, DateTimeOffset.UtcNow, [new(-42, 240, true)]);
        var merged = SteamPlayTimeMerger.Merge(local, new("B", new Dictionary<int, long> { [42] = 60 }),
            new("A", [new(42, 3600, 0)]));
        Assert.Equal(240, merged.Snapshot.Games.Single(g => g.AppId == -42).Seconds);
        Assert.Equal(60, merged.Snapshot.Games.Single(g => g.AppId == 42).Seconds);
        var tracker = new PlayTimeAccumulator(local);
        tracker.Sample([-42], TimeSpan.Zero);
        tracker.Sample([-42], TimeSpan.FromSeconds(5));
        Assert.Equal(245, Assert.Single(tracker.Snapshot(DateTimeOffset.UtcNow).Games).Seconds);
    }

    [Fact]
    public void ReadsMinutesForMostRecentAccountOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-steam-time-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "loginusers.vdf"), "\"users\" { \"76561197960265729\" { \"MostRecent\" \"1\" } \"76561197960265730\" { \"MostRecent\" \"0\" } }");
            foreach (var id in new[] { "1", "2" })
            {
                var directory = Path.Combine(root, "userdata", id, "config");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "localconfig.vdf"), "\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"apps\" { \"42\" { \"Playtime\" \"" + (id == "1" ? "205" : "9999") + "\" \"LastPlayed\" \"1700000000\" } \"43\" { \"Playtime\" \"-1\" \"LastPlayed\" \"999999999999\" } } } } } }");
            }
            var read = SteamPlayTimeReader.Read(root)!;
            Assert.Equal("1", read.Account);
            Assert.Equal(205 * 60, Assert.Single(read.Seconds).Value);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), Assert.Single(read.LastPlayed).Value);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
