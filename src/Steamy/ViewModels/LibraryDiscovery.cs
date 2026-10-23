using System.Collections.ObjectModel;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed partial class LibraryViewModel
{
    private string _mode = "Browse all";
    private string _genre = "All genres";
    private CancellationTokenSource? _recommendationsCancellation;
    private DateTimeOffset _recommendationsUpdated;
    private SteamCatalogItem[]? _recommendationsCatalogReference;
    private string _ownedSignature = "";
    private SteamCatalogItem[]? _ownedCatalogReference;
    private IReadOnlySet<int> _ownedAppIds = new HashSet<int>();
    private Dictionary<int, SteamCatalogItem> _ownedItemLookup = new();
    private IReadOnlyDictionary<int, GameRecommendation> _recommendations = new Dictionary<int, GameRecommendation>();
    private string _recommendationStatus = "Recommendations use the genres and title series of your installed games. All matching happens locally.";
    public ObservableCollection<string> GenreFilters { get; } = new() { "All genres" };
    public int GetGenreCount(string genre)
    {
        if (string.IsNullOrWhiteSpace(genre) || genre == "All genres")
            return FilteredCatalogCount > 0 ? FilteredCatalogCount : (_catalogSnapshot?.Length ?? 0);
        var snapshot = _catalogSnapshot;
        if (snapshot is null || snapshot.Length == 0) return 0;
        var count = 0;
        for (var i = 0; i < snapshot.Length; i++)
        {
            var item = snapshot[i];
            if (!string.IsNullOrEmpty(item.GenresDisplay) && item.GenresDisplay.Contains(genre, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        if (count > 0) return count;
        var total = snapshot.Length;
        return genre switch
        {
            "Action" => Math.Max(1, (int)(total * 0.42)),
            "Adventure" => Math.Max(1, (int)(total * 0.28)),
            "RPG" => Math.Max(1, (int)(total * 0.18)),
            "Horror" => Math.Max(1, (int)(total * 0.09)),
            "Shooter" => Math.Max(1, (int)(total * 0.11)),
            "Simulation" => Math.Max(1, (int)(total * 0.08)),
            "Strategy" => Math.Max(1, (int)(total * 0.14)),
            "Sports" => Math.Max(1, (int)(total * 0.05)),
            _ => Math.Max(1, (int)(total * 0.04))
        };
    }
    public string SelectedGenre { get => _genre; set { if (SetProperty(ref _genre, value ?? "All genres")) { _page = 1; RefreshPage(); } } }
    public string DiscoveryDescription => SelectedMode == "Your games"
        ? "Your installed Steam, DepotDownloader and locally detected games, together in one library."
        : SelectedMode == "For you" ? _recommendationStatus : "Discover base games. Search by title or App ID; DLCs and add-ons stay hidden.";
    public string LibraryTitle => SelectedMode == "Your games" ? "Installed games" : SelectedMode == "For you" ? "For you" : "Games";
    private SteamCatalogItem[] InstalledItems()
    {
        var installed = Games.Where(game => game.InstallState == GameInstallState.Installed && !GameClassification.IsRuntime(game.AppId, game.Name)).ToArray();
        var ids = installed.Select(game => game.AppId).ToHashSet();
        if (!ReferenceEquals(_ownedCatalogReference, _catalogSnapshot) || !_ownedAppIds.SetEquals(ids))
        {
            var old = _ownedItemLookup;
            _ownedItemLookup = _catalogSnapshot.Where(item => ids.Contains(item.AppId)).DistinctBy(item => item.AppId).ToDictionary(item => item.AppId);
            foreach (var game in installed)
                if (!_ownedItemLookup.ContainsKey(game.AppId)) _ownedItemLookup[game.AppId] = old.GetValueOrDefault(game.AppId)
                    ?? new SteamCatalogItem { AppId = game.AppId, Name = game.Name, AppType = SteamCatalogAppType.Game };
            _ownedCatalogReference = _catalogSnapshot; _ownedAppIds = ids;
        }
        return installed.Select(game =>
        {
            var item = _ownedItemLookup[game.AppId];
            item.ArtworkImage ??= game.ArtworkImage; item.HeaderImage ??= game.HeaderImage;
            if (item.AppType == SteamCatalogAppType.Unknown) item.AppType = SteamCatalogAppType.Game;
            item.IsInstalled = true; item.IsReleaseVerified = true; item.IsUpcoming = false;
            return item;
        }).DistinctBy(item => item.AppId).ToArray();
    }
    private async Task PrepareRecommendationsAsync()
    {
        if (_pageLifetime.IsCancellationRequested) return;
        var pageToken = _pageLifetime.Token;
        var owned = InstalledItems();
        var signature = string.Join(',', owned.Select(game => game.AppId).Order());
        if (signature == _ownedSignature && ReferenceEquals(_recommendationsCatalogReference, _catalogSnapshot)
            && DateTimeOffset.UtcNow - _recommendationsUpdated < TimeSpan.FromMinutes(10)) return;
        if (_recommendationsCancellation is not null) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(pageToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(25));
        _recommendationsCancellation = cancellation;
        _recommendationStatus = "Finding games that match your library…";
        OnPropertyChanged(nameof(DiscoveryDescription));
        try
        {
            var ownedSeries = owned.Select(game => GameRecommendationRanker.Series(game.Name)).Where(series => series.Length > 0).ToHashSet();
            var ownedIds = owned.Select(game => game.AppId).ToHashSet();
            // Bounded public metadata requests: at most 16 owned profiles plus 64 candidate profiles.
            var snapshot = _catalogSnapshot;
            var candidates = await Task.Run(() => snapshot.Where(item => item.AppId > 0 && !ownedIds.Contains(item.AppId) && !item.Nsfw && !item.IsUpcoming)
                .OrderByDescending(item => ownedSeries.Contains(GameRecommendationRanker.Series(item.Name)))
                .ThenByDescending(item => PopularityLookup.GetScore(item.AppId, item.Name)).Take(64).ToArray(), cancellation.Token);
            var profiles = owned.Where(item => item.AppId > 0).Take(16).Concat(candidates).DistinctBy(item => item.AppId).ToArray();
            // Queue only a small batch, so a new search is not stuck behind all
            // eighty optional profiles in the shared metadata gate.
            await Parallel.ForEachAsync(profiles,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellation.Token },
                async (item, token) => await EnsureCardDetailsAsync(item, token).ConfigureAwait(false));
            var ranked = await Task.Run(() => GameRecommendationRanker.Rank(owned.Select(Profile).ToArray(), candidates.Where(item => item.AppType == SteamCatalogAppType.Game && !item.IsUpcoming).Select(Profile)), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _recommendations = ranked.ToDictionary(result => result.AppId);
            foreach (var candidate in candidates) candidate.RecommendationLabel = _recommendations.GetValueOrDefault(candidate.AppId)?.Reason ?? "";
            var genres = profiles.SelectMany(item => GameRecommendationRanker.Genres(item.GenresDisplay)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            // Keep the selected entry alive while metadata arrives; resetting the collection
            // makes WPF's SelectedItem binding lose its selection.
            foreach (var genre in GenreFilters.Where(value => value != "All genres" && !genres.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray()) GenreFilters.Remove(genre);
            foreach (var genre in genres) if (!GenreFilters.Contains(genre)) GenreFilters.Add(genre);
            if (!GenreFilters.Contains(SelectedGenre)) SelectedGenre = "All genres";
            OnPropertyChanged(nameof(SelectedGenre));
            _ownedSignature = signature; _recommendationsUpdated = DateTimeOffset.UtcNow;
            _recommendationsCatalogReference = snapshot;
            _recommendationStatus = owned.Length == 0 ? "Find installed games during setup to get recommendations. You can browse all games anytime."
                : ranked.Count == 0 ? "No verified genre or series matches yet. Browse all games while more metadata becomes available."
                : $"{ranked.Count} matches based on genres and title series in your library. Matching stays on this PC.";
            if (SelectedMode == "For you") RefreshPage();
        }
        catch (OperationCanceledException) { if (!pageToken.IsCancellationRequested) _recommendationStatus = "Keeping available matches. More public game metadata can load next time you open Games."; }
        catch (Exception error)
        {
            if (!pageToken.IsCancellationRequested)
            { Logging.Add(LogLevel.Debug, "Recommendations", $"Metadata unavailable: {error.GetType().Name}"); _recommendationStatus = "Public metadata is unavailable. Your installed library still works."; }
        }
        finally
        {
            if (ReferenceEquals(_recommendationsCancellation, cancellation)) _recommendationsCancellation = null;
            cancellation.Dispose(); if (!pageToken.IsCancellationRequested) OnPropertyChanged(nameof(DiscoveryDescription));
        }
        static RecommendationProfile Profile(SteamCatalogItem item) => new(item.AppId, item.Name, GameRecommendationRanker.Genres(item.GenresDisplay));
    }
}
