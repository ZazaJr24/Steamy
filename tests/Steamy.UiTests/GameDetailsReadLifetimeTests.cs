using System.Collections.Concurrent;
using System.Net.Http;
using Steamy.Models;
using Steamy.Playtime;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed partial class GameDetailsTests
{
    [Fact]
    public async Task DispatcherStillProcessesInputWhilePreferenceFileSetupIsBlocked()
    {
        using var preferences = new BlockingPreferences();
        var model = Model(new RelatedData(), preferences); model.SelectGame(Game(42));
        var ready = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            ready.TrySetResult(dispatcher);
            try { activation.TrySetResult(model.ActivateAsync()); System.Windows.Threading.Dispatcher.Run(); }
            catch (Exception error) { activation.TrySetException(error); }
        }) { IsBackground = true };
        caller.SetApartmentState(ApartmentState.STA); caller.Start();
        var dispatcher = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await preferences.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => handled.TrySetResult()));
            await handled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(preferences.Release.IsSet);
            Assert.False((await activation.Task).IsCompleted);
        }
        finally
        {
            preferences.Release.Set();
            var work = await activation.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await work.WaitAsync(TimeSpan.FromSeconds(5));
            await dispatcher.InvokeAsync(model.DeactivateAsync).Task.Unwrap();
            dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
            Assert.True(caller.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task PreferenceFilesDoNotStartOnTheCallingThread()
    {
        var preferences = new ProbedPreferences();
        var model = Model(new RelatedData(), preferences);
        model.SelectGame(Game(42));
        var caller = await CallOnSta(model.ActivateAsync);
        await caller.Work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(preferences.ReadThreads);
        Assert.DoesNotContain(caller.Thread, preferences.ReadThreads);
        await model.DeactivateAsync();
    }

    [Fact]
    public async Task PreferenceWritesKeepTheirSnapshotAndDoNotStartOnTheCallingThread()
    {
        var preferences = new ProbedPreferences();
        var model = Model(new RelatedData(), preferences);
        model.SelectGame(Game(42)); await model.ActivateAsync();
        model.Notes = "Keep this note";
        var caller = await CallOnSta(model.FlushAsync);
        await caller.Work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(preferences.WriteThreads);
        Assert.DoesNotContain(caller.Thread, preferences.WriteThreads);
        Assert.Equal("Keep this note", Assert.Single(preferences.Saved).Notes);
        await model.DeactivateAsync();
    }

    [Fact]
    public async Task AchievementSetupReadsDoNotRunOnTheCallingThread()
    {
        var credentials = new ProbedCredentials();
        using var http = new HttpClient(new ApiHandler());
        using var service = new SteamGameDetailsService(new Settings(), credentials, http);
        var caller = await CallOnSta(() => service.ReadAchievementsAsync(42));
        await caller.Work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(credentials.Threads);
        Assert.DoesNotContain(caller.Thread, credentials.Threads);
    }

    [Fact]
    public async Task OverviewMetadataStartsAwayFromTheCallingThread()
    {
        var catalog = new ProbedOverviewCatalog();
        using var http = new HttpClient(new ApiHandler());
        using var service = new SteamGameDetailsService(new Settings(), new Credentials(), http, catalog);
        var caller = await CallOnSta(() => service.ReadOverviewAsync(42));
        await caller.Work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(catalog.Threads);
        Assert.DoesNotContain(caller.Thread, catalog.Threads);
    }

    [Fact]
    public async Task ReturningToTheSameGameStartsNewTimeWorkWithoutWaitingForTheAbandonedRead()
    {
        var time = new RestartedTime();
        var model = Model(new RelatedData(), new Preferences(), playtime: time);
        model.SelectGame(Game(42));
        var first = model.ActivateAsync();
        Task? second = null;
        try
        {
            await time.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.DeactivateAsync();
            second = model.ActivateAsync();
            await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, time.Calls);
            Assert.Equal(PlayTimeFiles.Format(7200), model.PlayTimeLabel);
            await first.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(time.First.Task.IsCompleted);
            time.First.TrySetResult(Snapshot(60));
            Assert.Equal(PlayTimeFiles.Format(7200), model.PlayTimeLabel);
        }
        finally
        {
            time.First.TrySetResult(Snapshot(60));
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5));
            await model.DeactivateAsync();
        }
    }

    [Fact]
    public async Task PlaytimeFileProvidersDoNotStartOnTheCallingThread()
    {
        var time = new ProbedTime(); var activity = new ProbedActivity();
        var model = Model(new RelatedData(), new Preferences(), playtime: time, activity: activity);
        model.SelectGame(Game(42));
        var caller = await CallOnSta(model.RefreshPlayTimeAsync);
        await caller.Work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(time.Threads); Assert.NotEmpty(activity.Threads);
        Assert.DoesNotContain(caller.Thread, time.Threads); Assert.DoesNotContain(caller.Thread, activity.Threads);
        Assert.Equal(PlayTimeFiles.Format(7200), model.PlayTimeLabel);
        await model.DeactivateAsync();
    }

    private static async Task<(int Thread, Task Work)> CallOnSta(Func<Task> start)
    {
        var called = new TaskCompletionSource<(int Thread, Task Work)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { called.TrySetResult((Environment.CurrentManagedThreadId, start())); }
            catch (Exception error) { called.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var result = await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        return result;
    }
    private static async Task WaitForPreferencesAsync(Steamy.ViewModels.GameDetailsViewModel model)
    {
        if (model.CanEditPreferences) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler changed = (_, change) =>
        { if (change.PropertyName == nameof(model.CanEditPreferences) && model.CanEditPreferences) ready.TrySetResult(); };
        model.PropertyChanged += changed;
        try { if (!model.CanEditPreferences) await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { model.PropertyChanged -= changed; }
    }
    private static PlayTimeSnapshot Snapshot(long seconds) => new(1, DateTimeOffset.UtcNow, [new GamePlayTime(42, seconds, false)]);
    private sealed class ProbedCredentials : ISecureCredentialService
    {
        internal readonly ConcurrentBag<int> Threads = [];
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
        { Threads.Add(Environment.CurrentManagedThreadId); return Task.FromResult<string?>(null); }
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class ProbedPreferences : IGamePreferencesStore
    {
        internal readonly ConcurrentBag<int> ReadThreads = [], WriteThreads = [];
        internal readonly ConcurrentBag<GamePreferences> Saved = [];
        public Task<GamePreferences> ReadAsync(int appId, CancellationToken token = default)
        { ReadThreads.Add(Environment.CurrentManagedThreadId); return Task.FromResult(new GamePreferences()); }
        public Task SaveAsync(int appId, GamePreferences preferences, CancellationToken token = default)
        { WriteThreads.Add(Environment.CurrentManagedThreadId); Saved.Add(preferences); return Task.CompletedTask; }
    }
    private sealed class BlockingPreferences : IGamePreferencesStore, IDisposable
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        public Task<GamePreferences> ReadAsync(int appId, CancellationToken token = default)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Preference setup fixture was not released.");
            return Task.FromResult(new GamePreferences());
        }
        public Task SaveAsync(int appId, GamePreferences preferences, CancellationToken token = default) => Task.CompletedTask;
        public void Dispose() => Release.Dispose();
    }
    private sealed class ProbedOverviewCatalog : RelatedCatalog
    {
        internal readonly ConcurrentBag<int> Threads = [];
        public override Task<SteamCatalogDetails?> GetDetailsAsync(int appId, CancellationToken cancellationToken = default)
        { Threads.Add(Environment.CurrentManagedThreadId); return Task.FromResult<SteamCatalogDetails?>(ReadyDetails); }
    }
    private sealed class RestartedTime : IPlayTimeService
    {
        internal int Calls;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<PlayTimeSnapshot> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PlayTimeSnapshot> ReadAsync(CancellationToken token = default)
        {
            if (Interlocked.Increment(ref Calls) != 1) return Task.FromResult(Snapshot(7200));
            Started.TrySetResult(); return First.Task; // Deliberately ignore cancellation.
        }
        public Task ConfigureAsync(AppSettings settings, IEnumerable<Game> games) => Task.CompletedTask;
    }
    private sealed class ProbedTime : IPlayTimeService
    {
        internal readonly ConcurrentBag<int> Threads = [];
        public Task<PlayTimeSnapshot> ReadAsync(CancellationToken token = default)
        { Threads.Add(Environment.CurrentManagedThreadId); return Task.FromResult(Snapshot(7200)); }
        public Task<DateTimeOffset?> ReadLastPlayedAsync(int appId, CancellationToken token = default)
        { Threads.Add(Environment.CurrentManagedThreadId); return Task.FromResult<DateTimeOffset?>(null); }
        public Task ConfigureAsync(AppSettings settings, IEnumerable<Game> games) => Task.CompletedTask;
    }
    private sealed class ProbedActivity : IGameActivityService
    {
        internal readonly ConcurrentBag<int> Threads = [];
        public event EventHandler? Changed { add { } remove { } }
        public Task<GameActivitySnapshot> GetAsync(CancellationToken cancellationToken = default)
        { Threads.Add(Environment.CurrentManagedThreadId); return Task.FromResult(GameActivitySnapshot.Empty); }
        public Task<bool> ToggleFavoriteAsync(int appId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task RecordLaunchAsync(int appId, DateTimeOffset? launchedAt = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
