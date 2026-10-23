using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed partial class LibraryViewModel
{
    private readonly SemaphoreSlim _metadataGate = new(4, 4);

    private async Task EnsureCardDetailsAsync(SteamCatalogItem item, CancellationToken token)
    {
        await _metadataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            await WaitForCardWorkAsync(_catalog.EnsureDetailsAsync(item, token), token).ConfigureAwait(false);
        }
        finally { _metadataGate.Release(); }
    }

    private async Task VerifyBaseGameSearchAsync(SteamCatalogItem[] unclassified, Task<GameSearchIndex<SteamCatalogItem>>? index,
        string search, string type, string sort, LibraryNsfwScope scope, int generation, string source, IReadOnlySet<int>? sourceIds,
        CancellationToken token, SteamCatalogItem[] snapshot)
    {
        try
        {
            await Task.Run(() => HydrateCardDetailsAsync(unclassified, token), token);
            if (token.IsCancellationRequested || generation != Volatile.Read(ref _filterGeneration)) return;
            await RefreshPageAsync(index, search, type, sort, scope, generation, source, sourceIds, token, snapshot, verifyUnknown: false);
            if (generation == Volatile.Read(ref _filterGeneration) && !token.IsCancellationRequested && _filteredCatalog.Count == 0)
                SearchHint = unclassified.Any(item => item.AppType == SteamCatalogAppType.Unknown)
                    ? "Game type could not be verified. Refresh to try again. DLCs and add-ons are hidden."
                    : "No base games found. DLCs and add-ons are hidden.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is System.IO.IOException or System.Net.Http.HttpRequestException)
        {
            if (generation == Volatile.Read(ref _filterGeneration) && !token.IsCancellationRequested)
                SearchHint = "Game information is unavailable. Your installed games are still available.";
        }
    }

    private int _additionalVisible;
    public bool CanLoadMore => !_pageLifetime.IsCancellationRequested && (_page - 1) * PageSize + PageSize + _additionalVisible < FilteredCatalogCount;
    public string LoadMoreLabel => $"Show {Math.Min(48, Math.Max(0, FilteredCatalogCount - ((_page - 1) * PageSize + PageSize + _additionalVisible)))} more";
    public ICommand LoadMoreCommand => new RelayCommand(LoadMore, () => CanLoadMore);

    private void LoadMore()
    {
        if (!CanLoadMore) return;
        _additionalVisible += 48;
        _ = RenderCatalogPageAsync();
    }

    private void PublishGallery(IReadOnlyList<SteamCatalogItem> visible)
    {
        // Appending keeps generated containers, focus and the current pixel offset.
        // WPF's item generator requires single-item Add events, rather than range Add.
        var existing = PagedCatalogItems.Count;
        if (existing > 0 && visible.Count >= existing && PagedCatalogItems.SequenceEqual(visible.Take(existing)))
        {
            for (var index = existing; index < visible.Count; index++) PagedCatalogItems.Add(visible[index]);
        }
        else PagedCatalogItems.ReplaceWith(visible);
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(LoadMoreLabel));
    }

    private SteamCatalogItem[] GalleryHydrationCandidates(IReadOnlyList<SteamCatalogItem> visible)
    {
        var prefix = PagedCatalogItems.Count;
        if (prefix > visible.Count || !PagedCatalogItems.SequenceEqual(visible.Take(prefix))) prefix = 0;
        return visible.Skip(prefix).Take(48).ToArray();
    }

    private Task HydrateCardDetailsAsync(IEnumerable<SteamCatalogItem> items, CancellationToken token) =>
        Parallel.ForEachAsync(items.Where(item => item.AppId > 0 && !item.HasDetails),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            async (item, cancellation) => await EnsureCardDetailsAsync(item, cancellation).ConfigureAwait(false));

    private void PruneNonGameCards()
    {
        if (!_filteredCatalog.Any(item => !MatchesType(item, "Game"))) return;
        _filteredCatalog = _filteredCatalog.Where(item => MatchesType(item, "Game")).ToArray();
        for (var index = PagedCatalogItems.Count - 1; index >= 0; index--)
            if (!MatchesType(PagedCatalogItems[index], "Game")) PagedCatalogItems.RemoveAt(index);
        for (var index = SearchSuggestions.Count - 1; index >= 0; index--)
            if (!MatchesType(SearchSuggestions[index], "Game")) SearchSuggestions.RemoveAt(index);
        OnPropertyChanged(nameof(HasSearchSuggestions));
        FilteredCatalogCount = _filteredCatalog.Count;
        OnPropertyChanged(nameof(FilteredCatalogCount)); OnPropertyChanged(nameof(CatalogCountLabel));
        OnPropertyChanged(nameof(CanLoadMore)); OnPropertyChanged(nameof(LoadMoreLabel));
    }

    private static async Task WaitForCardWorkAsync(Task work, CancellationToken token)
    {
        // A provider can ignore cancellation and fail after the view has left.
        // Stop waiting promptly, but keep that late failure observed.
        _ = work.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await work.WaitAsync(token);
    }
}
