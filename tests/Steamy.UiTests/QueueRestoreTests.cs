using System.Collections.ObjectModel;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed class QueueRestoreTests
{
    [Fact]
    public async Task RestoringALargeQueuePublishesOnceAndKeepsExistingLiveJobs()
    {
        var store = new AppDataStore(); var database = new NullLocalDatabase();
        var live = new DownloadJob { GameName = "Already running", State = DownloadJobState.Downloading, Progress = 12 };
        store.Downloads.Add(live);
        await database.SaveDownloadJobAsync(DownloadQueueStore.ToRow(new DownloadJob { Id = live.Id, GameName = "Older saved row", State = DownloadJobState.Paused }));
        for (var index = 0; index < 1000; index++)
            await database.SaveDownloadJobAsync(DownloadQueueStore.ToRow(new DownloadJob { GameName = "Saved " + index, State = DownloadJobState.Downloading, Progress = 37 }));
        using var shell = new ShellDownloadViewModel(store, (IArtworkService)OfflineServiceProxy.Create(typeof(IArtworkService)));
        var changes = 0; store.Downloads.CollectionChanged += (_, _) => changes++;
        var queue = new DownloadQueueStore(database, new Log());
        await queue.RestoreAsync(store.Downloads, autoResume: false);
        Assert.Equal(1001, store.Downloads.Count); Assert.Equal(1, changes);
        Assert.Same(live, store.Downloads[0]); Assert.Same(live, shell.Job); Assert.Equal(12, live.Progress);
        Assert.All(store.Downloads.Skip(1), job => { Assert.Equal(DownloadJobState.Paused, job.State); Assert.Equal(37, job.Progress); });
        await queue.RestoreAsync(store.Downloads, autoResume: false);
        Assert.Equal(1, changes); Assert.Equal(1001, store.Downloads.Count);
        live.State = DownloadJobState.Completed;
        Assert.Same(store.Downloads[1], shell.Job);
        store.Downloads[1].State = DownloadJobState.Completed;
        Assert.Same(store.Downloads[2], shell.Job); // Reset subscribed newly restored jobs too.
    }

    [Fact]
    public async Task PlainCollectionsRemainSupportedAndCancelledReadsPublishNothing()
    {
        var database = new NullLocalDatabase();
        var row = DownloadQueueStore.ToRow(new DownloadJob { GameName = "Saved", State = DownloadJobState.Paused });
        await database.SaveDownloadJobAsync(row);
        var queue = new DownloadQueueStore(database, new Log());
        var jobs = new ObservableCollection<DownloadJob>();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.RestoreAsync(jobs, false, cancelled.Token));
        Assert.Empty(jobs);
        await queue.RestoreAsync(jobs, false); Assert.Single(jobs);
        await queue.RestoreAsync(jobs, false); Assert.Single(jobs);
    }

    private sealed class Log : ILoggingService
    {
        public void Add(LogLevel level, string component, string message, int? appId = null, Guid? jobId = null) { }
    }
}
