using Steamy.Services;

namespace Steamy.Tests;

public sealed class GameActivityStoreTests
{
    [Fact]
    public async Task FavoritesPersistWithoutInventingLaunches()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "activity.json");
        var store = new JsonGameActivityService(path);
        Assert.True(await store.ToggleFavoriteAsync(730));
        Assert.True(await store.ToggleFavoriteAsync(570));
        Assert.False(await store.ToggleFavoriteAsync(730));
        var reopened = await new JsonGameActivityService(path).GetAsync();
        Assert.Equal(new[] { 570 }, reopened.FavoriteAppIds);
        Assert.Empty(reopened.Launches);
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
    }

    [Fact]
    public async Task LaunchesAreDistinctAndNewestRequestWins()
    {
        using var folder = new TempFolder();
        var store = new JsonGameActivityService(Path.Combine(folder.Path, "activity.json"));
        var now = DateTimeOffset.UtcNow;
        await store.RecordLaunchAsync(730, now.AddDays(-3));
        await store.RecordLaunchAsync(570, now.AddDays(-2));
        await store.RecordLaunchAsync(730, now.AddDays(-1));
        await store.RecordLaunchAsync(730, now.AddDays(-5));
        var snapshot = await store.GetAsync();
        Assert.Equal(new[] { 730, 570 }, snapshot.Launches.Select(item => item.AppId));
        Assert.Equal(now.AddDays(-1), snapshot.Launches[0].OpenedAt);
        Assert.Empty(snapshot.FavoriteAppIds);
    }

    [Fact]
    public async Task ConcurrentFavoriteTogglesDoNotLoseChanges()
    {
        using var folder = new TempFolder();
        var store = new JsonGameActivityService(Path.Combine(folder.Path, "activity.json"));
        await Task.WhenAll(Enumerable.Range(1, 30).Select(appId => store.ToggleFavoriteAsync(appId)));
        Assert.Equal(30, (await store.GetAsync()).FavoriteAppIds.Count);
        await Task.WhenAll(Enumerable.Range(1, 30).Select(appId => store.ToggleFavoriteAsync(appId)));
        Assert.Empty((await store.GetAsync()).FavoriteAppIds);
    }

    [Fact]
    public async Task CanceledMutationPreservesSavedAndCachedPreferences()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "activity.json");
        var store = new JsonGameActivityService(path);
        await store.ToggleFavoriteAsync(730);
        var saved = await File.ReadAllTextAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ToggleFavoriteAsync(570, cancellation.Token));
        Assert.Equal(saved, await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { 730 }, (await store.GetAsync()).FavoriteAppIds);
    }

    [Fact]
    public async Task FailedAtomicWriteDoesNotReportUnsavedFavorite()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "activity.json");
        var store = new JsonGameActivityService(path);
        await store.ToggleFavoriteAsync(730);
        File.Delete(path);
        Directory.CreateDirectory(path); // A directory cannot be replaced by the settings file.
        var failure = await Record.ExceptionAsync(() => store.ToggleFavoriteAsync(570));
        // Replacing a directory produces IOException on Unix and UnauthorizedAccessException
        // on Windows. Both failures must leave the saved favorite state untouched.
        Assert.True(failure is IOException or UnauthorizedAccessException,
            $"Expected a filesystem write failure, but received {failure?.GetType().FullName ?? "no exception"}.");
        Assert.Equal(new[] { 730 }, (await store.GetAsync()).FavoriteAppIds);
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
    }

    [Fact]
    public async Task CorruptPreferencesDoNotPreventBrowsingAndCanBeReplaced()
    {
        using var folder = new TempFolder();
        var path = folder.Write("activity.json", "{broken");
        var store = new JsonGameActivityService(path);
        Assert.Equal(GameActivitySnapshot.Empty, await store.GetAsync());
        await store.ToggleFavoriteAsync(730);
        Assert.Equal(new[] { 730 }, (await new JsonGameActivityService(path).GetAsync()).FavoriteAppIds);
    }

    [Fact]
    public async Task InvalidAndDuplicateImportedEntriesAreDiscarded()
    {
        using var folder = new TempFolder();
        var path = folder.Write("activity.json", """
            {"schemaVersion":1,"favorites":[-1,0,730,730,"bad"],"launches":[
                {"appId":730,"openedAt":"2020-02-01T00:00:00Z"},
                {"appId":730,"openedAt":"2020-03-01T00:00:00Z"},
                {"appId":570,"openedAt":"2999-01-01T00:00:00Z"},
                {"appId":-1,"openedAt":"2020-03-01T00:00:00Z"},
                {"appId":570,"openedAt":123}]}
            """);
        var snapshot = await new JsonGameActivityService(path).GetAsync();
        Assert.Equal(new[] { 730 }, snapshot.FavoriteAppIds);
        Assert.Equal(730, Assert.Single(snapshot.Launches).AppId);
        Assert.Equal(3, snapshot.Launches[0].OpenedAt.Month);
    }

    [Fact]
    public async Task InvalidAppIdsAndFutureLaunchDatesAreRejected()
    {
        using var folder = new TempFolder();
        var store = new JsonGameActivityService(Path.Combine(folder.Path, "activity.json"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ToggleFavoriteAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RecordLaunchAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RecordLaunchAsync(730, DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Equal(GameActivitySnapshot.Empty, await store.GetAsync());
    }
}
