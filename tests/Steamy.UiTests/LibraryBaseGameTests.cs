using System.Collections.Concurrent;
using System.Reflection;
using Steamy.Models;

namespace Steamy.UiTests;

public sealed partial class LibrarySearchLifetimeTests
{
    [Fact]
    public async Task LegacyMultiplayerAndOsxCompanionsDoNotCountAsAdditionalBaseGames()
    {
        var model = CreateModel(); Seed(model);
        var items = (SteamCatalogItem[])typeof(Steamy.ViewModels.LibraryViewModel).GetField("_catalogSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        items[0] = new() { AppId = 100001, Name = "Call of Duty: Black Ops - Multiplayer", AppType = SteamCatalogAppType.Game };
        items[1] = new() { AppId = 100002, Name = "Call of Duty®: Advanced Warfare - Multiplayer", AppType = SteamCatalogAppType.Game };
        items[2] = new() { AppId = 100003, Name = "Call of Duty: Black Ops - OSX", AppType = SteamCatalogAppType.Game };
        items[3] = new() { AppId = 100004, Name = "Call of Duty: Black Ops", AppType = SteamCatalogAppType.Game };
        try
        {
            await Search(model, "Call of Duty");
            Assert.Equal(1, model.FilteredCatalogCount);
            Assert.Equal(100004, Assert.Single(model.PagedCatalogItems).AppId);
            Assert.DoesNotContain(model.PagedCatalogItems, item => Steamy.Services.GameClassification.IsAuxiliaryCatalogEntry(item.Name));
        }
        finally { await model.OnNavigatedFromAsync(); }
    }
    [Fact]
    public async Task LibraryExcludesDlcSoftwareAndUnverifiedEntriesFromCardsAndCount()
    {
        var model = CreateModel(); Seed(model);
        var items = (SteamCatalogItem[])typeof(Steamy.ViewModels.LibraryViewModel).GetField("_catalogSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        items[1].AppType = SteamCatalogAppType.Dlc;
        items[2].AppType = SteamCatalogAppType.Software;
        items[3].AppType = SteamCatalogAppType.Unknown;
        try
        {
            await Search(model, "Fixture");
            Assert.DoesNotContain(model.PagedCatalogItems, item => item.AppType != SteamCatalogAppType.Game);
            Assert.Equal(997, model.FilteredCatalogCount);
            Assert.Equal("997 base games", model.CatalogCountLabel);
            Assert.DoesNotContain("DLC", model.TypeFilters);
            model.SelectedTypeFilter = "DLC";
            Assert.Equal("Game", model.SelectedTypeFilter);
        }
        finally { await model.OnNavigatedFromAsync(); }
    }

    [Fact]
    public async Task UnknownSearchMatchesAreVerifiedInABoundedBatchBeforeTheyCanAppear()
    {
        var catalog = new TypeCheckingCatalog(); var model = CreateModel(catalog); Seed(model);
        var items = (SteamCatalogItem[])typeof(Steamy.ViewModels.LibraryViewModel).GetField("_catalogSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        foreach (var item in items) { item.AppType = SteamCatalogAppType.Unknown; item.HasDetails = false; }
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PagedCatalogItems.CollectionChanged += (_, _) =>
        { if (model.PagedCatalogItems.Count > 0) published.TrySetResult(); };
        try
        {
            await Search(model, "Fixture");
            await catalog.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(model.PagedCatalogItems);
            Assert.InRange(catalog.Requests.Count, 1, 4);
            catalog.Proceed.TrySetResult();
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await catalog.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(24, catalog.Requests.Count);
            Assert.InRange(catalog.Peak, 1, 4);
            Assert.All(model.PagedCatalogItems, item => Assert.Equal(SteamCatalogAppType.Game, item.AppType));
            Assert.DoesNotContain(model.PagedCatalogItems, item => item.AppId % 2 == 0);
        }
        finally { catalog.Proceed.TrySetResult(); await model.OnNavigatedFromAsync(); }
    }

    [Fact]
    public async Task AProviderCorrectionToDlcRemovesTheCardAndSearchSuggestion()
    {
        var catalog = new TypeCheckingCatalog(); var model = CreateModel(catalog); Seed(model);
        var items = (SteamCatalogItem[])typeof(Steamy.ViewModels.LibraryViewModel).GetField("_catalogSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        items[1].HasDetails = false;
        var pruned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, change) =>
        { if (change.PropertyName == nameof(model.CatalogCountLabel) && items[1].AppType == SteamCatalogAppType.Dlc) pruned.TrySetResult(); };
        try
        {
            await Search(model, "2");
            await catalog.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(model.PagedCatalogItems, item => item.AppId == 2);
            catalog.Proceed.TrySetResult();
            await pruned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(model.PagedCatalogItems, item => item.AppType != SteamCatalogAppType.Game);
            Assert.DoesNotContain(model.SearchSuggestions, item => item.AppType != SteamCatalogAppType.Game);
        }
        finally { catalog.Proceed.TrySetResult(); await model.OnNavigatedFromAsync(); }
    }

    private sealed class TypeCheckingCatalog : Catalog
    {
        internal readonly ConcurrentBag<int> Requests = [];
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active, _finished;
        internal int Peak;
        public override async Task<bool> EnsureDetailsAsync(SteamCatalogItem item, CancellationToken cancellationToken = default)
        {
            Requests.Add(item.AppId);
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensionsMax(ref Peak, active);
            Started.TrySetResult();
            try
            {
                await Proceed.Task.WaitAsync(cancellationToken);
                item.AppType = item.AppId % 2 == 0 ? SteamCatalogAppType.Dlc : SteamCatalogAppType.Game;
                item.HasDetails = true;
                return true;
            }
            finally { Interlocked.Decrement(ref _active); if (Interlocked.Increment(ref _finished) == 24) Completed.TrySetResult(); }
        }
        private static void InterlockedExtensionsMax(ref int field, int value)
        {
            int old;
            do { old = Volatile.Read(ref field); if (old >= value) return; } while (Interlocked.CompareExchange(ref field, value, old) != old);
        }
    }
}
