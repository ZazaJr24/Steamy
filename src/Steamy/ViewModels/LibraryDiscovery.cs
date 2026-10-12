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
    public string SelectedGenre { get => _genre; set { if (SetProperty(ref _genre, value ?? "All genres")) { _page = 1; RefreshPage(); } } }
    public string DiscoveryDescription => SelectedMode == "Your games"
        ? "Your installed Steam, DepotDownloader and locally detected games, together in one library."
        : SelectedMode == "For you" ? _recommendationStatus : "Browse the catalog, search for a title or filter by source.";
    public string LibraryTitle => SelectedMode == "Your games" ? "Your library" : SelectedMode == "For you" ? "Picked for you" : "Games";
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
        var owned = InstalledItems();
        var signature = string.Join(',', owned.Select(game => game.AppId).Order());
        if (signature == _ownedSignature && ReferenceEquals(_recommendationsCatalogReference, _catalogSnapshot)
            && DateTimeOffset.UtcNow - _recommendationsUpdated < TimeSpan.FromMinutes(10)) return;
        if (_recommendationsCancellation is not null) return;
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
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
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(profiles.Select(async item =>
            {
                await gate.WaitAsync(cancellation.Token);
                try { await _catalog.EnsureDetailsAsync(item, cancellation.Token); }
                finally { gate.Release(); }
            }));
            var ranked = GameRecommendationRanker.Rank(owned.Select(Profile).ToArray(), candidates.Where(item => item.AppType == SteamCatalogAppType.Game && !item.IsUpcoming).Select(Profile));
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
        catch (OperationCanceledException) { _recommendationStatus = "Keeping available matches. More public game metadata can load next time you open Games."; }
        catch (Exception error) { Logging.Add(LogLevel.Debug, "Recommendations", $"Metadata unavailable: {error.GetType().Name}"); _recommendationStatus = "Public metadata is unavailable. Your installed library still works."; }
        finally
        {
            if (ReferenceEquals(_recommendationsCancellation, cancellation)) _recommendationsCancellation = null;
            cancellation.Dispose(); OnPropertyChanged(nameof(DiscoveryDescription));
        }
        static RecommendationProfile Profile(SteamCatalogItem item) => new(item.AppId, item.Name, GameRecommendationRanker.Genres(item.GenresDisplay));
    }
}
