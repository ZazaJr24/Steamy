using Steamy.Playtime;

namespace Steamy.Tests;

public sealed class PlayTimeTests
{
    [Fact]
    public void CountsOnlyObservedRuntimeAndDeduplicatesProcesses()
    {
        var tracker = new PlayTimeAccumulator();
        tracker.Sample([], TimeSpan.Zero);
        tracker.Sample([42, 42], TimeSpan.FromSeconds(5));
        Assert.Equal(0, Assert.Single(tracker.Snapshot(DateTimeOffset.UtcNow).Games).Seconds);
        tracker.Sample([42, 42], TimeSpan.FromSeconds(10));
        var running = Assert.Single(tracker.Snapshot(DateTimeOffset.UtcNow).Games);
        Assert.Equal(5, running.Seconds);
        Assert.True(running.IsRunning);
        tracker.Sample([], TimeSpan.FromSeconds(15));
        tracker.Sample([], TimeSpan.FromMinutes(15));
        var stopped = Assert.Single(tracker.Snapshot(DateTimeOffset.UtcNow).Games);
        Assert.Equal(5, stopped.Seconds);
        Assert.False(stopped.IsRunning);
    }

    [Fact]
    public void RestartsKeepTotalsWithoutCountingOfflineGapsOrOldRunningFlags()
    {
        var saved = new PlayTimeSnapshot(1, DateTimeOffset.UtcNow.AddDays(-1), [new(42, 19200, true)]);
        var tracker = new PlayTimeAccumulator(saved);
        Assert.False(Assert.Single(tracker.Snapshot(DateTimeOffset.UtcNow).Games).IsRunning);
        tracker.Sample([42], TimeSpan.FromHours(10));
        tracker.Sample([42], TimeSpan.FromHours(10).Add(TimeSpan.FromSeconds(5)));
        Assert.Equal(19205, Assert.Single(tracker.Snapshot(DateTimeOffset.UtcNow).Games).Seconds);
        tracker.Sample([42], TimeSpan.FromHours(11)); // A long sampling interruption adds no runtime.
        Assert.Equal(19205, Assert.Single(tracker.Snapshot(DateTimeOffset.UtcNow).Games).Seconds);
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(59, "0m")]
    [InlineData(60, "1m")]
    [InlineData(19200, "5h 20m")]
    public void FormatsBannerTime(long seconds, string expected) => Assert.Equal(expected, PlayTimeFiles.Format(seconds));

    [Fact]
    public void MatchingRequiresInstalledDirectoryBoundaryAndIgnoresLaunchers()
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-game");
        TrackedGame[] games = [new(42, root)];
        Assert.Equal(42, GameProcessMatcher.Match(Path.Combine(root, "bin", "game.exe"), games));
        Assert.Equal(42, GameProcessMatcher.Match(Path.Combine(root, "CrashBandicoot.exe"), games));
        Assert.Equal(42, GameProcessMatcher.Match(Path.Combine(root, "BeachGame.exe"), games));
        Assert.Null(GameProcessMatcher.Match(Path.Combine(root + "Other", "game.exe"), games));
        Assert.Null(GameProcessMatcher.Match(Path.Combine(root, "GameLauncher.exe"), games));
        Assert.Null(GameProcessMatcher.Match(Path.Combine(root, "CrashReport.exe"), games));
        Assert.Null(GameProcessMatcher.Match(Path.Combine(root, "PlayTimeTracker.exe"), games));
        Assert.Null(GameProcessMatcher.Match("game.exe", games));
    }

    [Fact]
    public async Task AtomicFilePersistsAcrossReadersAndRejectsMalformedData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-playtime-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "playtime.json");
        try
        {
            await PlayTimeFiles.WriteAsync(path, new PlayTimeSnapshot(1, DateTimeOffset.UtcNow, [new(42, 19200, false)]));
            var loaded = await PlayTimeFiles.ReadAsync<PlayTimeSnapshot>(path);
            Assert.Equal(19200, Assert.Single(loaded!.Games).Seconds);
            Assert.Single(Directory.GetFiles(directory));
            await File.WriteAllTextAsync(path, "bad json");
            Assert.Null(await PlayTimeFiles.ReadAsync<PlayTimeSnapshot>(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
