using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Steamy.Controls;
using Steamy.Models;
using Steamy.Services;
using Wpf.Ui.Controls;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckLoggingBatches()
    {
        var dispatcher = new QueuedLogDispatcher();
        var database = new LogDatabaseFixture(Thread.CurrentThread.ManagedThreadId);
        var store = new AppDataStore();
        var logger = new InMemoryLoggingService(store, database, dispatcher);
        var changes = 0;
        store.Logs.CollectionChanged += (_, _) => changes++;
        Task.Run(() =>
        {
            logger.Add(LogLevel.Error, "Fixture", "Preserved critical failure");
            for (var index = 0; index < 10_000; index++)
                logger.Add(LogLevel.Info, "Fixture", $"Progress {index}");
        }).GetAwaiter().GetResult();
        Assert.Equal(1, dispatcher.PendingCount);
        dispatcher.FlushOne();
        Assert.Equal(1, changes); // One collection reset for a whole queued output burst.
        Assert.Equal(InMemoryLoggingService.VisibleHistoryLimit, store.Logs.Count);
        Assert.Contains(store.Logs, entry => entry.Message == "Preserved critical failure");
        Assert.Contains(store.Logs, entry => entry.Component == "Logging" && entry.Message.Contains("omitted"));
        PumpUntil(() => database.BatchCount > 0);
        Assert.False(database.WroteOnUiThread);
        Assert.Equal(0, database.SingleEntryWrites);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => logger.FlushAsync(cancelled.Token).GetAwaiter().GetResult());
        database.ReleaseWrites.SetResult();
        // Complete with no collection dispatcher pumping: shutdown flush has no UI dependency.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        logger.FlushAsync(deadline.Token).GetAwaiter().GetResult();
        Assert.False(database.WroteOnUiThread);
        Assert.Contains(database.Entries, entry => entry.Message == "Preserved critical failure");
        Assert.Contains(database.Entries, entry => entry.Component == "Logging" && entry.Message.Contains("omitted"));
        dispatcher.FlushAll();
        Assert.True(store.Logs.Count <= InMemoryLoggingService.VisibleHistoryLimit);
        Assert.Contains(store.Logs, entry => entry.Message == "Preserved critical failure");

        database.FailWrites = true;
        logger.Add(LogLevel.Warning, "Fixture", "Database failure fixture");
        logger.FlushAsync(deadline.Token).GetAwaiter().GetResult();
        dispatcher.FlushAll();
        Assert.Contains(store.Logs, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Could not save"));
        Assert.True(store.Logs.Count <= InMemoryLoggingService.VisibleHistoryLimit);

        database.FailWrites = false;
        logger.Add(LogLevel.Info, "Fixture", "api_key=private auth_key:private Authorization: Bearer private");
        logger.FlushAsync(deadline.Token).GetAwaiter().GetResult();
        dispatcher.FlushAll();
        Assert.DoesNotContain("private", store.Logs[0].Message);
        Assert.Contains("[redacted]", store.Logs[0].Message);
    }

    private static void CheckReducedEffectsNativeWindow()
    {
        var app = Application.Current;
        var previous = app.MainWindow;
        var reduceEffects = MotionPreferences.ReduceEffects;
        var settings = ((ISettingsService)App.Services.GetService(typeof(ISettingsService))!).Load();
        var savedReduceEffects = settings.ReduceEffects;
        var window = new FluentWindow { Width = 400, Height = 300, ShowInTaskbar = false };
        app.MainWindow = window;
        try
        {
            UiThemeService.ApplyBackdrop("Mica");
            window.Background = Brushes.Transparent; // Reproduce the local value installed by native backdrops.
            settings.ReduceEffects = true;
            MotionPreferences.Configure(true);
            UiThemeService.ApplyBackdrop("Mica");
            Assert.Equal(WindowBackdropType.None, window.WindowBackdropType);
            Assert.Equal(255, Assert.IsType<SolidColorBrush>(window.Background).Color.A);
            Assert.Same(app.Resources["AppBackgroundBrush"], window.Background);
            Assert.False(MotionPreferences.AnimationsEnabled);
            Assert.False(MotionPreferences.BackdropBlurEnabled);
            UiThemeService.Apply("Light");
            Assert.Same(app.Resources["AppBackgroundBrush"], window.Background);
            Assert.Equal(255, Assert.IsType<SolidColorBrush>(window.Background).Color.A);
            UiThemeService.Apply("Dark");
            Assert.Equal(255, Assert.IsType<SolidColorBrush>(window.Background).Color.A);
        }
        finally
        {
            UiThemeService.ApplyBackdrop("None");
            settings.ReduceEffects = savedReduceEffects;
            MotionPreferences.Configure(reduceEffects);
            window.Close();
            app.MainWindow = previous;
        }
    }

    private sealed class QueuedLogDispatcher : ILogDispatcher
    {
        private readonly ConcurrentQueue<Action> _pending = new();
        public bool IsCurrent => false;
        public int PendingCount => _pending.Count;
        public void Post(Action action) => _pending.Enqueue(action);
        public void FlushOne() { if (_pending.TryDequeue(out var action)) action(); }
        public void FlushAll() { while (_pending.TryDequeue(out var action)) action(); }
    }

    private sealed class LogDatabaseFixture(int uiThreadId) : ILocalDatabase, ILogBatchDatabase
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        private int _batchCount;
        private int _singleEntryWrites;
        private int _wroteOnUiThread;
        public TaskCompletionSource ReleaseWrites { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BatchCount => Volatile.Read(ref _batchCount);
        public int SingleEntryWrites => Volatile.Read(ref _singleEntryWrites);
        public bool WroteOnUiThread => Volatile.Read(ref _wroteOnUiThread) != 0;
        public bool FailWrites { get; set; }
        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();
        public async Task AppendLogsAsync(IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken = default)
        {
            if (Thread.CurrentThread.ManagedThreadId == uiThreadId) Interlocked.Exchange(ref _wroteOnUiThread, 1);
            Interlocked.Increment(ref _batchCount);
            await ReleaseWrites.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (FailWrites) throw new IOException("Fixture disk unavailable");
            foreach (var entry in entries) _entries.Enqueue(entry);
        }
        public Task AppendLogAsync(LogEntry entry, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _singleEntryWrites);
            throw new InvalidOperationException("The batching adapter must be used.");
        }
        public void Initialize() => throw new NotSupportedException();
        public Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveDownloadJobAsync(PersistedDownloadJob job, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDownloadJobAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearAllDownloadJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PersistedDownloadJob>> LoadDownloadJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
