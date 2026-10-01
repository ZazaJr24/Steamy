using Steamy.Services;

namespace Steamy.Tests;

public sealed class SearchHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "steamy-search-" + Guid.NewGuid().ToString("N"));
    private string HistoryPath => Path.Combine(_directory, "history.json");

    [Fact]
    public async Task HistoryPersistsMostRecentFirstAndDeduplicatesCaseInsensitive()
    {
        var store = new SearchHistoryStore(HistoryPath);
        await store.RecordAsync(" Elden    Ring ");
        await store.RecordAsync("Cyberpunk");
        await store.RecordAsync("elden ring");
        Assert.Equal(new[] { "elden ring", "Cyberpunk" }, await new SearchHistoryStore(HistoryPath).GetAsync());
        Assert.False(File.Exists(HistoryPath + ".tmp"));
    }

    [Fact]
    public async Task ConcurrentRecordsRemainBoundedAndPersisted()
    {
        var store = new SearchHistoryStore(HistoryPath);
        await Task.WhenAll(Enumerable.Range(0, 30).Select(id => store.RecordAsync("game " + id)));
        var queries = await store.GetAsync();
        Assert.Equal(SearchHistoryStore.Capacity, queries.Count);
        Assert.Equal(queries, await new SearchHistoryStore(HistoryPath).GetAsync());
    }

    [Fact]
    public async Task DamagedFileDoesNotBreakSearchAndClearIsPersistent()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(HistoryPath, "invalid json");
        var store = new SearchHistoryStore(HistoryPath);
        Assert.Empty(await store.GetAsync());
        await store.RecordAsync("Library");
        Assert.Single(await store.GetAsync());
        await store.ClearAsync();
        Assert.Empty(await new SearchHistoryStore(HistoryPath).GetAsync());
    }

    [Fact]
    public async Task InvalidAndOverlongQueriesAreNotStored()
    {
        var store = new SearchHistoryStore(HistoryPath);
        await store.RecordAsync(null);
        await store.RecordAsync("   ");
        await store.RecordAsync(new string('a', 81));
        await store.RecordAsync("secret\0data");
        Assert.Empty(await store.GetAsync());
    }

    [Fact]
    public async Task ReadOnlyStorageStillProvidesSessionHistory()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "file");
        await File.WriteAllTextAsync(blocker, "not a directory");
        var store = new SearchHistoryStore(Path.Combine(blocker, "history.json"));
        Assert.Single(await store.RecordAsync("Library"));
        Assert.Equal("Library", Assert.Single(await store.GetAsync()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
