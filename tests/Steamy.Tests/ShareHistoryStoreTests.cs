using Steamy.Services;

namespace Steamy.Tests;

public class ShareHistoryStoreTests
{
    [Fact]
    public void Shares_are_remembered_per_target_across_instances()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "history.json");
        var when = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);

        new ShareHistoryStore(path).Record(new[] { new ShareHistoryEntry(730, "abc", "Owner/Dumps", when, "dumps/730/x.zip") });

        var reloaded = new ShareHistoryStore(path);
        Assert.NotNull(reloaded.Find("owner/dumps", "abc"));
        Assert.Null(reloaded.Find("someone/else", "abc"));
        Assert.Null(reloaded.Find("owner/dumps", "other"));
        Assert.Equal(when, reloaded.LastSharedUtc);
        Assert.Equal(1, reloaded.Count);
    }

    [Fact]
    public void A_damaged_history_file_starts_empty_instead_of_failing()
    {
        using var temp = new TempFolder();
        var path = temp.Write("history.json", "{ not json");

        var store = new ShareHistoryStore(path);

        Assert.Equal(0, store.Count);
        Assert.Null(store.LastSharedUtc);
        store.Record(new[] { new ShareHistoryEntry(1, "f", "a/b", DateTime.UtcNow, null) });
        Assert.Equal(1, new ShareHistoryStore(path).Count);
    }
}
