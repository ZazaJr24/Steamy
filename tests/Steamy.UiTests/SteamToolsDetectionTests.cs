using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed class SteamToolsDetectionTests
{
    [Fact]
    public async Task BackgroundResultsPublishOnTheOwningDispatcherWithoutCreatingAnyWindow()
    {
        using var service = new BlockingDetection();
        var ready = new TaskCompletionSource<(BetterSteamToolsViewModel Model, System.Windows.Threading.Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() =>
        {
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                var model = new BetterSteamToolsViewModel(service, new Settings(), new NavigationService(), dispatcher);
                model.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(model.BackendInstalled) && model.BackendInstalled) published.TrySetResult(Environment.CurrentManagedThreadId);
                };
                ready.TrySetResult((model, dispatcher));
                System.Windows.Threading.Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); published.TrySetException(error); }
        }) { IsBackground = true, Name = "Windowless detection publication check" };
        owner.SetApartmentState(ApartmentState.STA); owner.Start();
        var context = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = await context.Dispatcher.InvokeAsync(() => context.Model.RefreshDetectionAsync());
            service.Release.Set();
            Assert.Equal(owner.ManagedThreadId, await published.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotEqual(owner.ManagedThreadId, service.DetectionThread);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            service.Release.Set();
            await context.Dispatcher.InvokeAsync(() => context.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background));
            Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
        }
    }
    [Fact]
    public async Task ConstructingTheToolModelDoesNotWaitForFileDetection()
    {
        using var service = new BlockingDetection();
        var creation = Task.Run(() => new BetterSteamToolsViewModel(service, new Settings(), new NavigationService()));
        try
        {
            await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var finished = await Task.WhenAny(creation, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(creation, finished);
        }
        finally { service.Release.Set(); var model = await creation.WaitAsync(TimeSpan.FromSeconds(5)); await model.RefreshDetectionAsync(); }
    }
    [Fact]
    public async Task ConcurrentRefreshesShareOnePendingDetection()
    {
        using var service = new BlockingDetection();
        var model = new BetterSteamToolsViewModel(service, new Settings(), new NavigationService());
        try
        {
            await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var first = model.RefreshDetectionAsync(); var second = model.RefreshDetectionAsync();
            Assert.Same(first, second); Assert.False(first.IsCompleted);
            Assert.True(model.IsDetecting); Assert.False(model.BackendSetupRequired);
            Assert.Equal("Checking Steam…", model.ConnectionLabel);
            Assert.Equal(1, service.Calls);
            service.Release.Set(); await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(model.BackendInstalled); Assert.Single(model.AddedGames);
            Assert.False(model.IsDetecting);
            Assert.Equal(1, service.Calls);
        }
        finally { service.Release.Set(); await model.RefreshDetectionAsync(); }
    }
    [Fact]
    public async Task AChangedSteamFolderRejectsTheLatePreviousResult()
    {
        using var service = new BlockingDetection();
        var model = new BetterSteamToolsViewModel(service, new Settings(), new NavigationService());
        var previous = model.RefreshDetectionAsync();
        try
        {
            await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.SteamRoot = "OtherSteam";
            await model.RefreshDetectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("OtherSteam", model.SteamRoot); Assert.False(model.BackendInstalled);
            service.Release.Set(); await previous.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("OtherSteam", model.SteamRoot); Assert.False(model.BackendInstalled);
            Assert.Equal(2, service.Calls);
        }
        finally { service.Release.Set(); await previous; }
    }
    [Fact]
    public async Task UnchangedDetectionRetainsRowsAndBatchesCommandUpdates()
    {
        using var service = new BlockingDetection(); service.Release.Set();
        var model = new BetterSteamToolsViewModel(service, new Settings(), new NavigationService());
        await model.RefreshDetectionAsync();
        var original = Assert.Single(model.AddedGames);
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        model.AddedGames.CollectionChanged += (_, args) => changes.Add(args.Action);
        var commandUpdates = 0; model.AddGameCommand.CanExecuteChanged += (_, _) => commandUpdates++;
        await model.RefreshDetectionAsync();
        Assert.Same(original, Assert.Single(model.AddedGames)); Assert.Empty(changes); Assert.Equal(1, commandUpdates);
        service.Ids = [480, 481]; await model.RefreshDetectionAsync();
        Assert.Same(original, model.AddedGames[0]); Assert.Equal(481, model.AddedGames[1].AppId);
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Add], changes);
        changes.Clear(); service.Ids = [480, 480, 481, 0, -1]; await model.RefreshDetectionAsync();
        Assert.Equal(new[] { 480, 481 }, model.AddedGames.Select(item => item.AppId));
        Assert.Same(original, model.AddedGames[0]); Assert.Empty(changes);
    }
    private sealed class Settings : ISettingsService
    {
        public AppSettings Load() => new();
        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class BlockingDetection : IBetterSteamToolsService, IDisposable
    {
        internal int Calls;
        internal int DetectionThread;
        internal IReadOnlyList<int> Ids = [480];
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public BetterSteamToolsState Detect(string? root = null)
        {
            var call = Interlocked.Increment(ref Calls);
            DetectionThread = Environment.CurrentManagedThreadId;
            if (call == 1)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Detection test was not released.");
            }
            return new(string.IsNullOrWhiteSpace(root) ? "FixtureSteam" : root, true, root != "OtherSteam", Ids, "Ready");
        }
        public Task<BetterSteamToolsResult> InstallBackendAsync(string steamRoot, IProgress<string>? progress = null, CancellationToken token = default) => throw new InvalidOperationException("No installation is allowed in this check.");
        public Task<BetterSteamToolsResult> ImportAsync(string steamRoot, IReadOnlyList<string> paths, int? appId = null, CancellationToken token = default) => throw new InvalidOperationException("No import is allowed in this check.");
        public Task<BetterSteamToolsResult> AddFromSourceAsync(string steamRoot, int appId, ManifestSource? source, IProgress<string>? progress = null, CancellationToken token = default) => throw new InvalidOperationException("No import is allowed in this check.");
        public void Dispose() => Release.Dispose();
    }
}
