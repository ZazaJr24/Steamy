using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckDownloadsControls(IServiceProvider provider)
    {
        var originalProvider = App.Services;
        var queue = new DownloadControlsFixture();
        using var isolated = new ServiceCollection().AddSingleton<IDownloadQueueStore>(queue).BuildServiceProvider();
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, isolated);
        try
        {
            var store = new AppDataStore();
            var settings = new TransferSettingsFixture();
            settings.Load().ParallelDownloads = 1;
            var model = new DownloadsViewModel(store, provider.GetRequiredService<INavigationService>(),
                provider.GetRequiredService<ILoggingService>(), queue,
                provider.GetRequiredService<IRyuuGameDownloadService>(), settings);
            DownloadJob[] jobs = Enumerable.Range(1, 4).Select(id => new DownloadJob
            {
                GameName = $"Queue fixture {id}", AppId = id, State = DownloadJobState.Queued,
                DownloadMode = "DepotDownloader", Priority = DownloadPriority.Normal,
                QueuePosition = id, Started = DateTime.Now.AddSeconds(id)
            }).ToArray();
            foreach (var job in jobs) { store.Downloads.Add(job); queue.SaveAsync(job).GetAwaiter().GetResult(); }
            ((IAsyncRelayCommand<DownloadJob>)model.MoveUpCommand).ExecuteAsync(jobs[3]).GetAwaiter().GetResult();
            Assert.Equal(3, jobs[3].QueuePosition);
            Assert.Equal(4, jobs[2].QueuePosition);
            Assert.Equal(3, queue.Saved.Last(row => row.Id == jobs[3].Id).QueuePosition);
            ((IAsyncRelayCommand<DownloadJob>)model.CyclePriorityCommand).ExecuteAsync(jobs[3]).GetAwaiter().GetResult();
            Assert.Equal(DownloadPriority.High, jobs[3].Priority);
            Assert.Equal(DownloadPriority.High, queue.Saved.Last(row => row.Id == jobs[3].Id).Priority);
            var restored = DownloadQueueStore.ToJob(queue.Saved.Last(row => row.Id == jobs[3].Id), false);
            Assert.Equal(jobs[3].QueuePosition, restored.QueuePosition);
            Assert.Equal(jobs[3].Priority, restored.Priority);

            var positionsBefore = jobs.Select(job => job.QueuePosition).ToArray();
            queue.FailSave = true;
            ((IAsyncRelayCommand<DownloadJob>)model.MoveUpCommand).ExecuteAsync(jobs[2]).GetAwaiter().GetResult();
            Assert.Equal(positionsBefore, jobs.Select(job => job.QueuePosition));
            Assert.Contains("order could not be saved", model.QueueStatus);
            ((IAsyncRelayCommand<DownloadJob>)model.CyclePriorityCommand).ExecuteAsync(jobs[2]).GetAwaiter().GetResult();
            Assert.Equal(DownloadPriority.Normal, jobs[2].Priority);
            Assert.Contains("Priority could not be saved", model.QueueStatus);
            ((IAsyncRelayCommand<DownloadJob>)model.RemoveCommand).ExecuteAsync(jobs[2]).GetAwaiter().GetResult();
            Assert.Contains(jobs[2], store.Downloads);
            Assert.Contains("could not be removed", model.QueueStatus);
            queue.FailSave = false;

            var run = model.StartQueuedCommand.ExecuteAsync(null);
            Assert.Equal([jobs[3].Id], queue.Started);
            jobs[0].State = DownloadJobState.Cancelled;
            store.Downloads.Remove(jobs[1]);
            queue.Finish(jobs[3]);
            PumpUntil(() => queue.Started.Count == 2);
            Assert.Equal([jobs[3].Id, jobs[2].Id], queue.Started);
            queue.Finish(jobs[2]);
            PumpUntil(() => run.IsCompleted);
            run.GetAwaiter().GetResult();
            Assert.False(model.IsQueueRunning);

            store.Downloads.Clear();
            var ongoing = new DownloadJob { GameName = "Leave running", State = DownloadJobState.Queued, DownloadMode = "DepotDownloader", QueuePosition = 1 };
            var waiting = new DownloadJob { GameName = "Leave waiting", State = DownloadJobState.Queued, DownloadMode = "DepotDownloader", QueuePosition = 2 };
            store.Downloads.Add(ongoing);
            store.Downloads.Add(waiting);
            var stopped = model.StartQueuedCommand.ExecuteAsync(null);
            model.StopQueueCommand.Execute(null);
            PumpUntil(() => stopped.IsCompleted);
            stopped.GetAwaiter().GetResult();
            Assert.Equal(DownloadJobState.Downloading, ongoing.State);
            Assert.Equal(DownloadJobState.Queued, waiting.State);
            Assert.DoesNotContain(waiting.Id, queue.Started);
            var restarted = model.StartQueuedCommand.ExecuteAsync(null);
            Assert.DoesNotContain(waiting.Id, queue.Started);
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert.DoesNotContain(waiting.Id, queue.Started);
            queue.Finish(ongoing);
            PumpUntil(() => ongoing.State == DownloadJobState.Completed);
            PumpUntil(() => queue.Started.Contains(waiting.Id));
            queue.Finish(waiting);
            PumpUntil(() => restarted.IsCompleted);
            restarted.GetAwaiter().GetResult();

            model.ParallelJobsText = "3";
            model.ConnectionsText = "8";
            model.RateLimitText = "5";
            model.ApplyTransferSettingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(1, settings.Saves);
            Assert.Equal(3, settings.Load().ParallelDownloads);
            Assert.Equal(8, settings.Load().DownloadConnections);
            Assert.Equal(5, settings.Load().DownloadRateLimitMiB);
            model.RateLimitText = "-1";
            model.ApplyTransferSettingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(1, settings.Saves);
            Assert.Equal(5, settings.Load().DownloadRateLimitMiB);
            Assert.Contains("0–1024", model.TransferSettingsHint);
            model.RateLimitText = "10";
            settings.FailSave = true;
            model.ApplyTransferSettingsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(5, settings.Load().DownloadRateLimitMiB);
            Assert.Contains("could not be saved", model.TransferSettingsHint);
        }
        finally { typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, originalProvider); }

        CheckDownloadManagerFailures(provider);
        CheckStoredQueueOrdering(provider);
    }

    private static void CheckDownloadManagerFailures(IServiceProvider provider)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-failure-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "fixture.exe");
            File.WriteAllText(executable, "Fixture: never executed");
            var content = Path.Combine(directory, "existing-game.bin");
            File.WriteAllBytes(content, new byte[32]);
            var settings = new TransferSettingsFixture();
            settings.Load().DepotDownloaderPath = executable;
            settings.Load().VerifyAfterDownload = false;
            settings.Load().RetryCount = 3;
            var queue = new DownloadControlsFixture();
            var adapter = new DownloadFailureFixture();
            using var manager = new DownloadManager(new AppDataStore(), settings, adapter,
                provider.GetRequiredService<IFileVerificationService>(), queue,
                provider.GetRequiredService<ILoggingService>(), new DemoNotificationService());
            var job = new DownloadJob { AppId = 42, GameName = "Interrupted download", TargetFolder = directory,
                AuthorizationConfirmed = true, DownloadMode = "DepotDownloader", State = DownloadJobState.Paused,
                DepotId = 99, Branch = "beta", ManifestId = "1234" };
            adapter.Failure = new HttpRequestException("Fixture offline");
            Assert.False(manager.StartAsync(job).GetAwaiter().GetResult());
            Assert.Equal(DownloadJobState.Failed, job.State);
            Assert.Contains("Network connection interrupted", job.Status);
            Assert.Equal(32, new FileInfo(content).Length);

            adapter.Failure = null;
            adapter.Result = new(1, false, false, string.Empty, "No space left on device");
            var attemptsBefore = adapter.Requests.Count;
            Assert.False(manager.StartAsync(job).GetAwaiter().GetResult());
            Assert.Equal(attemptsBefore + 1, adapter.Requests.Count); // Permanent failures do not spin through retries.
            Assert.Equal(32, new FileInfo(content).Length);

            adapter.Result = new(0, false, false, "Finished", string.Empty);
            Assert.True(manager.StartAsync(job).GetAwaiter().GetResult());
            var request = adapter.Requests.Last();
            Assert.Equal(directory, request.TargetFolder);
            Assert.Equal(99, request.DepotId);
            Assert.Equal("beta", request.Branch);
            Assert.Equal("1234", request.ManifestId);
            Assert.Equal(DownloadJobState.Completed, job.State);

            queue.FailSave = true;
            var startsBeforeSaveFailure = adapter.Requests.Count;
            Assert.False(manager.StartAsync(job).GetAwaiter().GetResult());
            Assert.Equal(startsBeforeSaveFailure, adapter.Requests.Count);
            Assert.Contains("queue could not be saved", job.Status);
            Assert.Equal(32, new FileInfo(content).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class TransferSettingsFixture : ISettingsService
    {
        private readonly AppSettings _settings = new();
        public int Saves;
        public bool FailSave;
        public AppSettings Load() => _settings;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (FailSave) throw new IOException("Fixture save failure");
            Saves++;
            return Task.CompletedTask;
        }
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static void CheckStoredQueueOrdering(IServiceProvider provider)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-queue-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = new SqliteLocalDatabase(Path.Combine(directory, "queue.db"));
            var queue = new DownloadQueueStore(database, provider.GetRequiredService<ILoggingService>());
            var first = new DownloadJob { AppId = 1, GameName = "First", QueuePosition = 1, State = DownloadJobState.Queued };
            var second = new DownloadJob { AppId = 2, GameName = "Second", QueuePosition = 2, State = DownloadJobState.Queued };
            queue.SaveAsync(first).GetAwaiter().GetResult();
            queue.SaveAsync(second).GetAwaiter().GetResult();
            Assert.Throws<DownloadQueuePersistenceException>(() => queue.SavePositionsAsync(
                [new(first.Id, 2), new(Guid.NewGuid(), 1)]).GetAwaiter().GetResult());
            var unchanged = database.LoadDownloadJobsAsync().GetAwaiter().GetResult();
            Assert.Equal(1, unchanged.Single(row => row.Id == first.Id).QueuePosition);
            Assert.Equal(2, unchanged.Single(row => row.Id == second.Id).QueuePosition);

            first.State = DownloadJobState.Downloading;
            first.Progress = 37;
            queue.SaveAsync(first).GetAwaiter().GetResult();
            queue.SavePositionsAsync([new(first.Id, 2), new(second.Id, 1)]).GetAwaiter().GetResult();
            queue.SavePriorityAsync(new(first.Id, DownloadPriority.High)).GetAwaiter().GetResult();
            var ordered = database.LoadDownloadJobsAsync().GetAwaiter().GetResult();
            var updatedFirst = ordered.Single(row => row.Id == first.Id);
            Assert.Equal(2, updatedFirst.QueuePosition);
            Assert.Equal(DownloadPriority.High, updatedFirst.Priority);
            Assert.Equal(nameof(DownloadJobState.Downloading), updatedFirst.State);
            Assert.Equal(37, updatedFirst.Progress);

            using (var blocker = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory, "queue.db")}"))
            {
                blocker.Open();
                using var transaction = blocker.BeginTransaction(deferred: false);
                var third = new DownloadJob { AppId = 3, GameName = "Waiting for storage", QueuePosition = 3, State = DownloadJobState.Queued };
                var save = queue.SaveAsync(third);
                var dispatcherResponded = false;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => dispatcherResponded = true));
                PumpUntil(() => dispatcherResponded);
                Assert.False(save.IsCompleted); // A held database write lock never prevents WPF from processing input.
                transaction.Rollback();
                PumpUntil(() => save.IsCompleted);
                save.GetAwaiter().GetResult();
                Assert.Contains(database.LoadDownloadJobsAsync().GetAwaiter().GetResult(), row => row.Id == third.Id);
            }

            // Real persistence failures are surfaced instead of pretending the queue was saved.
            var unavailable = new DownloadQueueStore(new SqliteLocalDatabase(Path.Combine(directory, "missing-parent-file", "queue.db")),
                provider.GetRequiredService<ILoggingService>());
            File.WriteAllText(Path.Combine(directory, "missing-parent-file"), "not a folder");
            Assert.Throws<DownloadQueuePersistenceException>(() => unavailable.SaveAsync(first).GetAwaiter().GetResult());
            Assert.Throws<DownloadQueuePersistenceException>(() => unavailable.RemoveAsync(first).GetAwaiter().GetResult());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private sealed class DownloadControlsFixture : IDownloadManager, IDownloadQueueStore, IDownloadQueueOrderingStore
    {
        public List<Guid> Started { get; } = [];
        public List<PersistedDownloadJob> Saved { get; } = [];
        public bool FailSave;
        private readonly Dictionary<Guid, TaskCompletionSource<bool>> _operations = [];
        public async Task<bool> StartAsync(DownloadJob job, CancellationToken cancellationToken = default)
        {
            Started.Add(job.Id);
            job.State = DownloadJobState.Downloading;
            var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _operations.Add(job.Id, complete);
            await complete.Task;
            job.State = DownloadJobState.Completed;
            return true;
        }
        public void Finish(DownloadJob job) => _operations[job.Id].SetResult(true);
        public Task ForgetAsync(DownloadJob job, CancellationToken cancellationToken = default) => RemoveAsync(job, cancellationToken);
        public Task PauseAsync(DownloadJob job, CancellationToken cancellationToken = default) { job.State = DownloadJobState.Paused; return Task.CompletedTask; }
        public Task CancelAsync(DownloadJob job, CancellationToken cancellationToken = default) { job.State = DownloadJobState.Cancelled; return Task.CompletedTask; }
        public Task RetryAsync(DownloadJob job, CancellationToken cancellationToken = default) => StartAsync(job, cancellationToken);
        public Task<bool> VerifyAsync(DownloadJob job, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void RegisterJob(Guid id, CancellationTokenSource cancellation) { }
        public void UnregisterJob(Guid id) { }
        public bool IsPauseRequested(Guid id) => false;
        public Task RestoreAsync(ObservableCollection<DownloadJob> jobs, bool autoResume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default)
        {
            CheckSave();
            Saved.Add(DownloadQueueStore.ToRow(job));
            return Task.CompletedTask;
        }
        public Task SavePositionsAsync(IReadOnlyList<DownloadQueuePosition> positions, CancellationToken cancellationToken = default)
        {
            CheckSave();
            foreach (var position in positions)
                Saved.Add(Saved.Last(row => row.Id == position.JobId) with { QueuePosition = position.Position });
            return Task.CompletedTask;
        }
        public Task SavePriorityAsync(DownloadQueuePriority priority, CancellationToken cancellationToken = default)
        {
            CheckSave();
            Saved.Add(Saved.Last(row => row.Id == priority.JobId) with { Priority = priority.Priority });
            return Task.CompletedTask;
        }
        public Task RemoveAsync(DownloadJob job, CancellationToken cancellationToken = default) { CheckSave(); return Task.CompletedTask; }
        private void CheckSave()
        {
            if (FailSave) throw new DownloadQueuePersistenceException("The download queue could not be saved.", new IOException("Fixture save failure"));
        }
    }

    private sealed class DownloadFailureFixture : IDepotDownloaderService
    {
        public Exception? Failure;
        public DepotDownloaderRunResult Result = new(0, false, false, "Finished", string.Empty);
        public List<DepotDownloaderRequest> Requests { get; } = [];
        public Task<DepotDownloaderToolStatus> CheckAsync(string executablePath, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No tool execution permitted");
        public Task<DepotDownloaderRunResult> DownloadAsync(DepotDownloaderRequest request, IProgress<DepotDownloaderProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Failure is null ? Task.FromResult(Result) : Task.FromException<DepotDownloaderRunResult>(Failure);
        }
        public Task StopAsync(Guid jobId, bool pause, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
