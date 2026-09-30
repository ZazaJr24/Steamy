using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.Tests;

public sealed class DownloadPresentationTests
{
    [Theory]
    [InlineData(DownloadJobState.Downloading, "Active", true)]
    [InlineData(DownloadJobState.Preparing, "Active", true)]
    [InlineData(DownloadJobState.Verifying, "Active", true)]
    [InlineData(DownloadJobState.Paused, "Active", false)]
    [InlineData(DownloadJobState.Paused, "Paused", true)]
    [InlineData(DownloadJobState.Queued, "Queued", true)]
    [InlineData(DownloadJobState.Failed, "Completed", false)]
    [InlineData(DownloadJobState.Completed, "Completed", true)]
    [InlineData(DownloadJobState.Failed, "Failed", true)]
    [InlineData(DownloadJobState.Cancelled, "Cancelled", true)]
    [InlineData(DownloadJobState.Failed, "All downloads", true)]
    public void FilterUsesTheActualJobState(DownloadJobState state, string filter, bool expected) =>
        Assert.Equal(expected, DownloadPresentation.Matches("Portal 2", 620, state, "", filter));

    [Theory]
    [InlineData(" portal ", "Paused", true)]
    [InlineData("PORTAL", "Paused", true)]
    [InlineData("620", "Paused", true)]
    [InlineData("Portal", "Active", false)]
    [InlineData("Half-Life", "All downloads", false)]
    public void SearchAndStatusAreCombined(string search, string filter, bool expected) =>
        Assert.Equal(expected, DownloadPresentation.Matches("Portal 2", 620, DownloadJobState.Paused, search, filter));

    [Fact]
    public void ChangedStatusLeavesTheActiveFilter()
    {
        Assert.True(DownloadPresentation.Matches("Game", 1, DownloadJobState.Downloading, "", "Active"));
        Assert.False(DownloadPresentation.Matches("Game", 1, DownloadJobState.Completed, "", "Active"));
    }

    [Fact]
    public void UnknownEstimateDoesNotPromiseAnEarlyFinish()
    {
        Assert.Null(DownloadPresentation.RemainingSeconds(new double?[] { 10, null, 90 }));
        Assert.Null(DownloadPresentation.RemainingSeconds(Array.Empty<double?>()));
        Assert.Equal(90d, DownloadPresentation.RemainingSeconds(new double?[] { 10, 90, 40 }));
        Assert.Equal(0d, DownloadPresentation.RemainingSeconds(new double?[] { 0 }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidEstimatesRemainUnknown(double seconds) =>
        Assert.Null(DownloadPresentation.RemainingSeconds(new double?[] { 10, seconds }));

    [Fact]
    public void SynchronizationPreservesExistingRowsWithoutResetting()
    {
        var first = new object();
        var second = new object();
        var third = new object();
        var added = new object();
        var rows = new ObservableCollection<object> { first, second, third };
        var actions = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, args) => actions.Add(args.Action);

        DownloadPresentation.Synchronize(rows, new[] { third, second, added });

        Assert.Equal(new[] { third, second, added }, rows);
        Assert.Same(second, rows[1]);
        Assert.Contains(NotifyCollectionChangedAction.Move, actions);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
    }

    [Fact]
    public void UnchangedLargeHistoryDoesNotRecreateRows()
    {
        var history = Enumerable.Range(0, 10_000).ToArray();
        var rows = new ObservableCollection<int>(history);
        var notifications = 0;
        rows.CollectionChanged += (_, _) => notifications++;
        DownloadPresentation.Synchronize(rows, history);
        Assert.Equal(0, notifications);
        Assert.Equal(history, rows);
    }

    [Fact]
    public void ActiveAndFailedJobsAppearBeforeCompletedHistory()
    {
        var states = new[] { DownloadJobState.Completed, DownloadJobState.Failed, DownloadJobState.Downloading, DownloadJobState.Queued };
        Assert.Equal(new[] { DownloadJobState.Downloading, DownloadJobState.Queued, DownloadJobState.Failed, DownloadJobState.Completed },
            states.OrderBy(DownloadPresentation.StateOrder));
    }
}
