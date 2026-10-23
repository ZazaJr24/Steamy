using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Steamy.Models;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class LibrarySearchLifetimeTests
{
    [Fact]
    public async Task SearchAndLocalResultsBecomeReadyWhileOptionalStoreDetailsAreBlocked()
    {
        var catalog = new GatedDetailsCatalog(); var model = CreateModel(catalog); Seed(model);
        var items = (SteamCatalogItem[])typeof(LibraryViewModel).GetField("_catalogSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        foreach (var item in items) item.HasDetails = false;
        model.Games.Add(new Game { AppId = 999, Name = "Fixture 999" });
        var localReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, change) =>
        { if (change.PropertyName == nameof(model.HasGames) && model.FilteredGames.Any(game => game.AppId == 999)) localReady.TrySetResult(); };
        var search = Search(model, "999");
        try
        {
            await catalog.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await search.WaitAsync(TimeSpan.FromSeconds(1));
            await localReady.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(model.IsSearchBusy);
            Assert.Equal(999, Assert.Single(model.PagedCatalogItems).AppId);
            Assert.False(catalog.Response.Task.IsCompleted);
        }
        finally
        {
            catalog.Response.TrySetResult(true);
            await search.WaitAsync(TimeSpan.FromSeconds(5));
            await model.OnNavigatedFromAsync();
        }
    }

    [Fact]
    public async Task LeavingStopsCardHydrationWaitsEvenIfMetadataIgnoresCancellation()
    {
        var catalog = new GatedDetailsCatalog(); var model = CreateModel(catalog); Seed(model);
        var items = (SteamCatalogItem[])typeof(LibraryViewModel).GetField("_catalogSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        foreach (var item in items) item.HasDetails = false;
        typeof(LibraryViewModel).GetField("_filteredCatalog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, items);
        var hydration = (Task)typeof(LibraryViewModel).GetMethod("RenderCatalogPageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null)!;
        try
        {
            await catalog.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.OnNavigatedFromAsync();
            await hydration.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(catalog.Response.Task.IsCompleted);
            Assert.InRange(catalog.Calls, 1, 4);
        }
        finally
        {
            catalog.Response.TrySetResult(true);
            await hydration.WaitAsync(TimeSpan.FromSeconds(5));
            await model.OnNavigatedFromAsync();
        }
    }

    [Fact]
    public async Task ReleaseMetadataDoesNotWalkTheCatalogOnTheCommandThread()
    {
        var model = CreateModel(); Seed(model);
        var status = new ProbedReleaseStatus();
        typeof(LibraryViewModel).GetField("_discoveryReleaseStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, status);
        var called = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            try { model.SearchText = "999"; model.SubmitSearchCommand.Execute(null); called.TrySetResult(Environment.CurrentManagedThreadId); }
            catch (Exception error) { called.TrySetException(error); }
        }) { IsBackground = true };
        caller.SetApartmentState(ApartmentState.STA); caller.Start();
        try
        {
            var commandThread = await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await status.Probed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(commandThread, status.Threads);
        }
        finally { Assert.True(caller.Join(TimeSpan.FromSeconds(5))); await model.OnNavigatedFromAsync(); }
    }

    private sealed class ProbedReleaseStatus : IReadOnlyDictionary<int, bool>
    {
        internal readonly ConcurrentBag<int> Threads = new();
        internal readonly TaskCompletionSource Probed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TryGetValue(int key, out bool value)
        { Threads.Add(Environment.CurrentManagedThreadId); Probed.TrySetResult(); value = false; return key == 999; }
        public int Count => 1;
        public IEnumerable<int> Keys => [999];
        public IEnumerable<bool> Values => [false];
        public bool this[int key] => false;
        public bool ContainsKey(int key) => key == 999;
        public IEnumerator<KeyValuePair<int, bool>> GetEnumerator() => new Dictionary<int, bool> { [999] = false }.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
