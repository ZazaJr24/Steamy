using Steamy.Services;

namespace Steamy.Tests;

public sealed partial class GameSearchIndexTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _allocationOutput;
    public GameSearchIndexTests(Xunit.Abstractions.ITestOutputHelper output) => _allocationOutput = output;

    [Fact]
    public void ExactSearchIndexConstructionStaysWithinItsAllocationBudget()
    {
        const int count = 20_000;
        var apps = Enumerable.Range(1, count).Select(id => new App(id, $"Offline Library fixture{id:D5}")).ToArray();
        _ = Index(new App(1, "Warmup"));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var index = new GameSearchIndex<App>(apps, app => app.Id, app => app.Name);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _allocationOutput.WriteLine($"20,000-title construction: {allocated} allocated bytes.");
        Assert.InRange(allocated, 0, count * 500L);
        Assert.Equal(12345, Assert.Single(index.Find("fixture12345").Items).Id);
        GC.KeepAlive(index);
    }

    [Fact]
    public void ExactQueryDoesNotAllocatePerCatalogEntry()
    {
        var index = new GameSearchIndex<App>(Enumerable.Range(1, 20_000).Select(id => new App(id, $"Offline Library fixture{id:D5}")), app => app.Id, app => app.Name);
        _ = index.Find("offline fixture12345");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = index.Find("offline fixture12345");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _allocationOutput.WriteLine($"20,000-title exact query: {allocated} allocated bytes.");
        Assert.InRange(allocated, 0, 128 * 1024);
        Assert.Equal(12345, Assert.Single(result.Items).Id);
        Assert.False(result.IsTypoMatch);
    }

    [Fact]
    public async Task ConcurrentFirstTypoQueriesKeepTheSameMatchingAndOrdering()
    {
        var index = new GameSearchIndex<App>(Enumerable.Range(1, 2000).Select(id => new App(id, $"Offline Library fixture{id:D5}")), app => app.Id, app => app.Name);
        var queries = Enumerable.Range(0, 8).Select(_ => Task.Run(() => index.Find("offlien fixture00999"))).ToArray();
        var results = await Task.WhenAll(queries);
        foreach (var result in results)
        {
            // Two changed terms are supported: close numbered fixtures are valid
            // matches too, alongside the exact fixture number and misspelled word.
            Assert.True(result.IsTypoMatch);
            Assert.Equal(29, result.Items.Count);
            Assert.Contains(result.Items, app => app.Id == 999);
        }
        Assert.Equal(results[0].Items, index.Find("offlien fixture00999").Items);
    }

    [Fact]
    public void CancellationBeforeFirstTypoFallbackDoesNotPoisonTheIndex()
    {
        var index = Index(new App(1, "Offline Library"), new App(2, "Other game"));
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => index.Find("offlien library", cancellation.Token, app =>
        {
            if (app.Id == 2) cancellation.Cancel();
            return true;
        }));
        Assert.Equal(1, Assert.Single(index.Find("offline library").Items).Id);
        var corrected = index.Find("offlien library");
        Assert.True(corrected.IsTypoMatch); Assert.Equal(1, Assert.Single(corrected.Items).Id);
    }
}
