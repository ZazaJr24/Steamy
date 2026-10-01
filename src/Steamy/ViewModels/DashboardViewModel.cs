using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed record DashboardStatusItem(string Title, string Detail, bool IsOk, string ActionLabel, ICommand Action);

public sealed record DashboardLibraryEntry(Game Game, bool IsFavorite, DateTimeOffset? OpenedAt)
{
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string FavoriteActionLabel => IsFavorite ? "Remove from favorites" : "Add to favorites";
    public string OpenedLabel => OpenedAt is { } opened ? $"Opened in Steamy · {opened.LocalDateTime:g}" : Game.Size;
}

public sealed record DashboardSearchMatch(SteamCatalogItem Item, Game Game)
{
    public string Detail => Item.IsInstalled ? $"Installed · {Item.AppLabel}" : Item.AppLabel;
}

public sealed class DashboardFeature(SpotlightGame metadata) : UiObservableObject
{
    private ImageSource? _heroArtwork;
    public SpotlightGame Metadata { get; } = metadata;
    public Game Game { get; } = new()
    {
        AppId = metadata.AppId, Name = metadata.Name, CoverColor = "#283E53", InstallState = GameInstallState.NotInstalled
    };
    public string Description => Metadata.Description;
    public string Genres => Metadata.Genres;
    public string ReleaseLabel => Metadata.ReleaseLabel;
    public string ReleaseStatus => Metadata.ComingSoon ? "COMING SOON" : "NEW RELEASE";
    public string Publisher => Metadata.Publisher;
    public ImageSource? HeroArtwork { get => _heroArtwork; set => SetProperty(ref _heroArtwork, value); }
}

public sealed class DashboardViewModel : ViewModelBase
{
    private const int RecentGameCount = 12;
    private const int JobPreviewCount = 2;
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromSeconds(30);

    private readonly ISteamCatalogService _catalog;
    private readonly SearchHistoryStore _searchHistory;
    private Task<SteamCatalogSnapshot>? _searchCatalogTask;
    private SteamCatalogSnapshot? _searchIndexedSnapshot;
    private Task<GameSearchIndex<SteamCatalogItem>>? _searchIndexTask;
    private readonly Dictionary<int, DashboardSearchMatch> _searchMatches = new();
    private readonly Dictionary<int, Task> _searchArtworkLoads = new();
    private readonly SemaphoreSlim _searchArtworkGate = new(2, 2);
    private CancellationTokenSource? _searchCancellation;
    private string _searchText = string.Empty;
    private string _searchStatus = string.Empty;
    private bool _isSearchBusy;
    public RangeObservableCollection<SteamCatalogItem> SearchResults { get; } = new();
    public RangeObservableCollection<DashboardSearchMatch> SearchMatches { get; } = new();
    public RangeObservableCollection<string> RecentSearches { get; } = new();
    private bool _isSearchFocused;
    public bool IsSearchFocused
    {
        get => _isSearchFocused;
        set { if (SetProperty(ref _isSearchFocused, value)) OnPropertyChanged(nameof(ShowSearchPanel)); }
    }
    public bool ShowSearchPanel => HasSearchQuery || (IsSearchFocused && RecentSearches.Count > 0);
    public bool ShowRecentSearches => !HasSearchQuery && RecentSearches.Count > 0;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            OnPropertyChanged(nameof(HasSearchQuery));
            OnPropertyChanged(nameof(ShowSearchPanel));
            OnPropertyChanged(nameof(ShowRecentSearches));
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = new CancellationTokenSource();
            _ = SearchAsync(value.Trim(), _searchCancellation.Token);
        }
    }
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchText);
    public string SearchStatus { get => _searchStatus; private set => SetProperty(ref _searchStatus, value); }
    public bool IsSearchBusy { get => _isSearchBusy; private set => SetProperty(ref _isSearchBusy, value); }
    public ICommand OpenSearchResultCommand { get; }
    public ICommand OpenSearchCommand { get; }
    public ICommand UseRecentSearchCommand { get; }
    public ICommand ClearSearchHistoryCommand { get; }
    public void StopSearch() { _searchCancellation?.Cancel(); IsSearchBusy = false; }

    public async Task LoadSearchHistoryAsync()
    {
        try { SetRecentSearches(await _searchHistory.GetAsync()); }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Search", $"History unavailable: {exception.GetType().Name}."); }
    }

    private void SetRecentSearches(IReadOnlyList<string> queries)
    {
        RecentSearches.ReplaceWith(queries);
        OnPropertyChanged(nameof(ShowSearchPanel));
        OnPropertyChanged(nameof(ShowRecentSearches));
    }

    private async Task ClearSearchHistoryAsync()
    {
        try { SetRecentSearches(await _searchHistory.ClearAsync()); }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Search", $"History could not be cleared: {exception.GetType().Name}."); }
    }

    private async Task RememberSearchAsync(string query)
    {
        try { SetRecentSearches(await _searchHistory.RecordAsync(query)); }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Search", $"History could not be saved: {exception.GetType().Name}."); }
    }

    private async Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (query.Length < 2)
        {
            SearchResults.ReplaceWith(Array.Empty<SteamCatalogItem>());
            SearchMatches.ReplaceWith(Array.Empty<DashboardSearchMatch>());
            IsSearchBusy = false;
            SearchStatus = query.Length == 0 ? string.Empty : "Type at least two characters.";
            return;
        }
        IsSearchBusy = true;
        SearchStatus = "Searching…";
        try
        {
            await Task.Delay(180, cancellationToken);
            // One shared cached catalog request; typing never reloads optional provider indexes.
            _searchCatalogTask ??= Task.Run(() => _catalog.GetCatalogAsync());
            var snapshot = await _searchCatalogTask.WaitAsync(cancellationToken);
            if (!ReferenceEquals(_searchIndexedSnapshot, snapshot))
            {
                _searchIndexedSnapshot = snapshot;
                _searchIndexTask = Task.Run(() => new GameSearchIndex<SteamCatalogItem>(snapshot.Items, item => item.AppId, item => item.Name));
            }
            var index = await _searchIndexTask!.WaitAsync(cancellationToken);
            var local = Store.Games.Select(game => new SteamCatalogItem
            {
                AppId = game.AppId, Name = game.Name,
                IsInstalled = game.InstallState == GameInstallState.Installed
            }).ToArray();
            var matches = await Task.Run(() =>
            {
                var catalogMatches = index.Find(query, cancellationToken, item => !item.Nsfw);
                var localMatches = new GameSearchIndex<SteamCatalogItem>(local, item => item.AppId, item => item.Name).Find(query, cancellationToken);
                var useExact = (localMatches.Items.Count > 0 && !localMatches.IsTypoMatch)
                    || (catalogMatches.Items.Count > 0 && !catalogMatches.IsTypoMatch);
                var candidates = (useExact && localMatches.IsTypoMatch ? Array.Empty<SteamCatalogItem>() : localMatches.Items)
                    .Concat(useExact && catalogMatches.IsTypoMatch ? Array.Empty<SteamCatalogItem>() : catalogMatches.Items);
                var items = candidates.DistinctBy(item => item.AppId)
                    .OrderByDescending(item => item.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture) == query)
                    .ThenByDescending(item => item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(item => item.IsInstalled)
                    .ThenByDescending(item => PopularityLookup.GetScore(item.AppId))
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Take(6).ToArray();
                return new GameSearchResult<SteamCatalogItem>(items, !useExact && items.Length > 0);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SearchResults.ReplaceWith(matches.Items);
            var visible = matches.Items.Select(CreateSearchMatch).ToArray();
            SearchMatches.ReplaceWith(visible);
            _ = LoadSearchArtworkAsync(visible, cancellationToken);
            SearchStatus = matches.Items.Count == 0 ? "No matches. Press Enter to search the full library."
                : matches.IsTypoMatch ? "Closest matches — select a game to open it." : "Select a game, or press Enter for all results.";
            if (!snapshot.Succeeded) _searchCatalogTask = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested) return;
            _searchCatalogTask = null;
            SearchStatus = "Catalog unavailable. Press Enter to search Games.";
            Logging.Add(LogLevel.Debug, "Search", exception.GetType().Name);
        }
        finally { if (!cancellationToken.IsCancellationRequested) IsSearchBusy = false; }
    }

    private DashboardSearchMatch CreateSearchMatch(SteamCatalogItem item)
    {
        if (!_searchMatches.TryGetValue(item.AppId, out var match) || match.Item.Name != item.Name || match.Item.IsInstalled != item.IsInstalled)
        {
            // Bound decoded artwork retained by the suggestion cache, even across many searches.
            if (_searchMatches.Count >= 32)
            {
                var key = _searchMatches.Keys.First();
                _searchMatches.Remove(key);
                _searchArtworkLoads.Remove(key);
            }
            var installed = Store.Games.FirstOrDefault(game => game.AppId == item.AppId);
            match = new DashboardSearchMatch(item, installed ?? new Game
            {
                AppId = item.AppId, Name = item.Name, InstallState = GameInstallState.NotInstalled
            });
            _searchMatches[item.AppId] = match;
        }
        return match;
    }

    private async Task LoadSearchArtworkAsync(IReadOnlyList<DashboardSearchMatch> matches, CancellationToken cancellationToken)
    {
        foreach (var match in matches)
        {
            if (_searchArtworkLoads.TryGetValue(match.Item.AppId, out var existing) && existing.IsCanceled) _searchArtworkLoads.Remove(match.Item.AppId);
            if (match.Game.HeaderImage is not null || _searchArtworkLoads.ContainsKey(match.Item.AppId)) continue;
            _searchArtworkLoads[match.Item.AppId] = LoadSearchHeaderAsync(match.Game, cancellationToken);
        }
        try { await Task.WhenAll(matches.Where(match => _searchArtworkLoads.ContainsKey(match.Item.AppId)).Select(match => _searchArtworkLoads[match.Item.AppId])); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Search", $"Optional covers unavailable: {exception.GetType().Name}."); }
    }

    private async Task LoadSearchHeaderAsync(Game game, CancellationToken cancellationToken)
    {
        await _searchArtworkGate.WaitAsync(cancellationToken);
        try { await Task.Run(() => _artwork.LoadHeadersAsync(new[] { game }, cancellationToken), cancellationToken); }
        finally { _searchArtworkGate.Release(); }
    }

    private void OpenSearch(string query, string? historyQuery = null)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        _ = RememberSearchAsync(historyQuery ?? query);
        var library = App.Services.GetRequiredService<LibraryViewModel>();
        library.SelectedSourceFilter = "All sources";
        library.SelectedTypeFilter = "All games";
        library.SearchText = query;
        SearchText = string.Empty;
        IsSearchFocused = false;
        Navigation.Navigate<LibraryPage>();
    }

    private readonly IArtworkService _artwork;
    private readonly ISpotlightService _spotlight;
    private Task? _discoveryArtworkTask;
    private readonly Dictionary<SpotlightGame, Task<BitmapImage?>> _heroLoads = new();
    private readonly Dictionary<SpotlightGame, Task<BitmapImage?>> _headerLoads = new();
    private int _featuredIndex;
    public IReadOnlyList<DashboardFeature> DiscoverGames { get; private set; } = Array.Empty<DashboardFeature>();
    public IReadOnlyList<DashboardFeature> NewGames => DiscoverGames.Take(3).ToArray();
    public bool HasSpotlight => DiscoverGames.Count > 0;
    public DashboardFeature? FeaturedGame => HasSpotlight ? DiscoverGames[_featuredIndex] : null;
    public string FeaturedPosition => HasSpotlight ? $"{_featuredIndex + 1:00} / {DiscoverGames.Count:00}" : string.Empty;
    public string FeaturedDownloadLabel => FeaturedGame?.Metadata.ComingSoon == true ? "Check sources" : "Download";
    public ICommand NextFeaturedCommand { get; }
    public ICommand PreviousFeaturedCommand { get; }
    public ICommand ViewFeaturedCommand { get; }
    public ICommand DownloadFeaturedCommand { get; }
    public ICommand OpenSpotlightCommand { get; }

    public Task EnsureDiscoveryArtworkAsync(bool force = false) => _discoveryArtworkTask is { IsCompleted: false }
        ? _discoveryArtworkTask : _discoveryArtworkTask = LoadDiscoveryArtworkAsync(force);

    private async Task LoadDiscoveryArtworkAsync(bool force)
    {
        // Retry optional failures on an explicit refresh, without refetching successful images.
        if (force)
        {
            foreach (var key in _heroLoads.Where(pair => pair.Value.IsCompleted &&
                (!pair.Value.IsCompletedSuccessfully || pair.Value.Result is null)).Select(pair => pair.Key).ToArray()) _heroLoads.Remove(key);
            foreach (var key in _headerLoads.Where(pair => pair.Value.IsCompleted &&
                (!pair.Value.IsCompletedSuccessfully || pair.Value.Result is null)).Select(pair => pair.Key).ToArray()) _headerLoads.Remove(key);
        }
        // Show cached artwork immediately; checking the feed must not delay the first paint.
        var savedArtwork = LoadVisibleArtworkAsync();
        try { ApplySpotlight(await _spotlight.GetAsync(force)); }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Spotlight", $"Using saved discoveries: {exception.GetType().Name}."); }
        await LoadVisibleArtworkAsync();
        await savedArtwork;
    }

    private async Task LoadVisibleArtworkAsync()
    {
        var cards = LoadDiscoveryHeadersAsync();
        await LoadFeaturedArtworkAsync();
        try { await cards; }
        catch (Exception exception)
        {
            Logging.Add(LogLevel.Debug, "Dashboard", $"Optional card artwork unavailable: {exception.GetType().Name}.");
        }
    }

    private void ApplySpotlight(SpotlightSnapshot snapshot)
    {
        if (DiscoverGames.Select(feature => feature.Metadata).SequenceEqual(snapshot.Games)) return;
        var previousId = FeaturedGame?.Game.AppId;
        DiscoverGames = snapshot.Games.Select(game => new DashboardFeature(game)).ToArray();
        _featuredIndex = Math.Max(0, Array.FindIndex(DiscoverGames.ToArray(), feature => feature.Game.AppId == previousId));
        _heroLoads.Clear();
        _headerLoads.Clear();
        OnPropertyChanged(nameof(DiscoverGames));
        OnPropertyChanged(nameof(NewGames));
        OnPropertyChanged(nameof(HasSpotlight));
        OnPropertyChanged(nameof(FeaturedGame));
        OnPropertyChanged(nameof(FeaturedPosition));
        OnPropertyChanged(nameof(FeaturedDownloadLabel));
        (NextFeaturedCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (PreviousFeaturedCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ViewFeaturedCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (DownloadFeaturedCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private async Task LoadDiscoveryHeadersAsync()
    {
        // Only the three visible recommendations need headers; the rest load as selected.
        foreach (var feature in NewGames)
        {
            try
            {
                if (!_headerLoads.TryGetValue(feature.Metadata, out var task))
                    _headerLoads[feature.Metadata] = task = Task.Run(() => _artwork.LoadSpotlightHeaderAsync(feature.Metadata));
                feature.Game.HeaderImage = await task;
            }
            catch (Exception exception) { Logging.Add(LogLevel.Debug, "Spotlight", $"Optional card artwork unavailable: {exception.GetType().Name}."); }
        }
    }

    private async Task LoadFeaturedArtworkAsync()
    {
        var feature = FeaturedGame;
        if (feature is null) return;
        try
        {
            if (!_heroLoads.TryGetValue(feature.Metadata, out var task))
                _heroLoads[feature.Metadata] = task = Task.Run(() => _artwork.LoadSpotlightHeroAsync(feature.Metadata));
            feature.HeroArtwork = await task;
            _ = WarmNextSpotlightArtworkAsync();
        }
        catch (Exception exception)
        {
            Logging.Add(LogLevel.Debug, "Dashboard", $"Optional artwork unavailable: {exception.GetType().Name}.");
        }
    }

    private async Task WarmNextSpotlightArtworkAsync()
    {
        if (DiscoverGames.Count < 2) return;
        var next = DiscoverGames[(_featuredIndex + 1) % DiscoverGames.Count];
        try
        {
            if (!_heroLoads.TryGetValue(next.Metadata, out var hero))
                _heroLoads[next.Metadata] = hero = Task.Run(() => _artwork.LoadSpotlightHeroAsync(next.Metadata));
            if (!_headerLoads.TryGetValue(next.Metadata, out var header))
                _headerLoads[next.Metadata] = header = Task.Run(() => _artwork.LoadSpotlightHeaderAsync(next.Metadata));
            next.HeroArtwork = await hero;
            next.Game.HeaderImage = await header;
        }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Spotlight", $"Optional next image unavailable: {exception.GetType().Name}."); }
    }

    private void MoveFeatured(int direction)
    {
        if (!HasSpotlight) return;
        _featuredIndex = (_featuredIndex + direction + DiscoverGames.Count) % DiscoverGames.Count;
        OnPropertyChanged(nameof(FeaturedGame));
        OnPropertyChanged(nameof(FeaturedPosition));
        OnPropertyChanged(nameof(FeaturedDownloadLabel));
        _ = LoadFeaturedArtworkAsync();
    }

    private void ViewFeatured()
    {
        if (FeaturedGame is null) return;
        var library = App.Services.GetRequiredService<LibraryViewModel>();
        library.SelectedSourceFilter = "All sources";
        library.SelectedTypeFilter = "All games";
        library.SearchText = FeaturedGame.Game.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Navigation.Navigate<LibraryPage>();
    }

    private void DownloadFeatured() => OpenSpotlight(FeaturedGame);

    private void OpenSpotlight(DashboardFeature? featured)
    {
        featured ??= FeaturedGame;
        if (featured is null) return;
        var library = App.Services.GetRequiredService<LibraryViewModel>();
        library.SelectedSourceFilter = "All sources";
        library.RequestedDownload = new SteamCatalogItem
        {
            AppId = featured.Game.AppId, Name = featured.Game.Name,
            AppType = SteamCatalogAppType.Game, HeaderImage = featured.Game.HeaderImage,
            ArtworkImage = featured.Game.ArtworkImage
        };
        Navigation.Navigate<LibraryPage>();
    }


    private readonly ILibrarySyncService _librarySync;
    private readonly ISettingsService _settings;
    private readonly IGameActivityService _activity;
    private GameActivitySnapshot _activitySnapshot = GameActivitySnapshot.Empty;
    private readonly Dictionary<int, Game> _catalogFavorites = new();
    private readonly HashSet<int> _resolvedFavoriteAppIds = new();
    private readonly HashSet<Game> _personalArtworkRequested = new();
    private Task? _favoriteDetailsTask;
    private CancellationTokenSource? _personalArtworkCancellation;
    private readonly IDepotDownloaderCheckService _depotCheck;
    private readonly ShareViewModel _share;
    private readonly HashSet<DownloadJob> _watchedJobs = new();

    private SteamLibraryScanResult? _scan;
    private DepotDownloaderToolStatus? _depotStatus;
    private string? _depotStatusPath;
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _isRefreshing;
    private bool _libraryRefreshQueued;
    private bool _downloadRefreshQueued;
    private readonly DispatcherTimer _statsTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _statsDirty;

    public void StartLiveStats()
    {
        _personalArtworkCancellation?.Cancel();
        _personalArtworkCancellation?.Dispose();
        _personalArtworkCancellation = new CancellationTokenSource();
        _personalArtworkRequested.Clear();
        LoadPersonalArtwork();
        RaiseDownloadStats();
        _statsTimer.Start();
    }

    public void StopLiveStats()
    {
        _statsTimer.Stop();
        _personalArtworkCancellation?.Cancel();
        _personalArtworkCancellation?.Dispose();
        _personalArtworkCancellation = null;
        _personalArtworkRequested.Clear();
    }
    private DownloadJob? _heroDownload;
    private Game? _heroGame;
    private long _storageFree = -1;
    private long _storageTotal;
    private string _storageDrive = string.Empty;
    private IReadOnlyList<DashboardStatusItem> _statusItems = Array.Empty<DashboardStatusItem>();

    public DashboardViewModel(
        IAppDataStore store,
        INavigationService navigation,
        ILoggingService logging,
        ILibrarySyncService librarySync,
        ISettingsService settings,
        IDepotDownloaderCheckService depotCheck,
        DownloadsViewModel downloadActions,
        ShareViewModel share,
        IArtworkService artwork,
        ISteamCatalogService catalog,
        ISpotlightService spotlight,
        IGameActivityService activity,
        SearchHistoryStore? searchHistory = null) : base(store, navigation, logging)
    {
        _catalog = catalog;
        _searchHistory = searchHistory ?? SearchHistoryStore.Default;
        OpenSearchResultCommand = new RelayCommand<SteamCatalogItem>(item => { if (item is not null) OpenSearch(item.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture), item.Name); });
        OpenSearchCommand = new RelayCommand(() => OpenSearch(SearchText.Trim()));
        UseRecentSearchCommand = new RelayCommand<string>(query => { if (query is not null) SearchText = query; });
        ClearSearchHistoryCommand = new AsyncRelayCommand(ClearSearchHistoryAsync);
        _artwork = artwork;
        _spotlight = spotlight;
        NextFeaturedCommand = new RelayCommand(() => MoveFeatured(1), () => DiscoverGames.Count > 1);
        PreviousFeaturedCommand = new RelayCommand(() => MoveFeatured(-1), () => DiscoverGames.Count > 1);
        ViewFeaturedCommand = new RelayCommand(ViewFeatured, () => HasSpotlight);
        DownloadFeaturedCommand = new RelayCommand(DownloadFeatured, () => HasSpotlight);
        OpenSpotlightCommand = new RelayCommand<DashboardFeature>(OpenSpotlight);
        ApplySpotlight(spotlight.Cached);
        _librarySync = librarySync;
        _settings = settings;
        _activity = activity;
        _activity.Changed += OnActivityChanged;
        ToggleFavoriteCommand = new AsyncRelayCommand<Game>(ToggleFavoriteAsync);
        ContinueGameCommand = new AsyncRelayCommand(() => PlayGameAsync(ContinueGame?.Game), () => CanPlayGame(ContinueGame?.Game));
        PlayGameCommand = new AsyncRelayCommand<Game>(PlayGameAsync, CanPlayGame);
        _depotCheck = depotCheck;
        _share = share;
        DownloadActions = downloadActions;

        _share.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShareViewModel.TotalCount) or nameof(ShareViewModel.NewCount)
                or nameof(ShareViewModel.IsScanning) or nameof(ShareViewModel.LastShareLabel))
                RaiseShareStats();
        };

        Store.Games.CollectionChanged += (_, _) => QueueLibraryRefresh();
        Store.Downloads.CollectionChanged += OnDownloadsChanged;
        foreach (var job in Store.Downloads) Watch(job);

        _statsTimer.Tick += (_, _) =>
        {
            if (!_statsDirty) return;
            _statsDirty = false;
            RaiseDownloadStats();
        };
        RefreshLibrary();
        RefreshDownloads();
        RefreshStatus();
    }

    /// <summary>Pause/resume/start go through the Downloads page's commands so both pages behave the same.</summary>
    public DownloadsViewModel DownloadActions { get; }

    public ObservableCollection<Game> RecentGames { get; } = new();
    public RangeObservableCollection<DashboardLibraryEntry> PersonalLibrary { get; } = new();
    public RangeObservableCollection<DashboardLibraryEntry> FavoriteGames { get; } = new();
    public RangeObservableCollection<DashboardLibraryEntry> RecentlyOpenedGames { get; } = new();
    public DashboardLibraryEntry? ContinueGame => RecentlyOpenedGames.FirstOrDefault(entry => entry.Game.InstallState == GameInstallState.Installed);
    public bool HasContinueGame => ContinueGame is not null;
    public bool HasFavoriteGames => FavoriteGames.Count > 0;
    public bool HasRecentlyOpenedGames => RecentlyOpenedGames.Count > 0;
    public bool IsFavorite(int appId) => _activitySnapshot.FavoriteAppIds.Contains(appId);
    public int FavoriteCount => _activitySnapshot.FavoriteAppIds.Count;
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand ContinueGameCommand { get; }
    private string _activityNotice = string.Empty;
    public string ActivityNotice { get => _activityNotice; private set => SetProperty(ref _activityNotice, value); }
    public bool HasActivityNotice => !string.IsNullOrWhiteSpace(ActivityNotice);
    public ObservableCollection<DownloadJob> ActiveJobs { get; } = new();

    // ---- header ---------------------------------------------------------------------------------

    public string Greeting => DateTime.Now.Hour switch
    {
        >= 5 and < 12 => "Good morning",
        >= 12 and < 18 => "Good afternoon",
        _ => "Good evening"
    };

    public string DateLabel => DateTime.Now.ToString("dddd, d MMMM", System.Globalization.CultureInfo.CurrentCulture).ToUpperInvariant();

    public string HeaderSummary
    {
        get
        {
            var parts = new List<string>
            {
                Store.Games.Count == 1 ? "1 game installed" : $"{Store.Games.Count:N0} games installed"
            };
            var active = Store.Downloads.Count(job => job.IsActive);
            if (active > 0) parts.Add(active == 1 ? "1 download running" : $"{active} downloads running");
            else if (Store.Downloads.Any(job => !job.IsTerminal)) parts.Add("downloads waiting");
            return string.Join("  ·  ", parts);
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!SetProperty(ref _isRefreshing, value)) return;
            OnPropertyChanged(nameof(ShowLibraryNotice));
            OnPropertyChanged(nameof(LibraryNotice));
        }
    }

    public bool ShowLibraryNotice => IsRefreshing || _scan is { Succeeded: false };
    public string LibraryNotice => IsRefreshing ? "Scanning your library. You can keep browsing while it updates…" : _scan?.Message ?? string.Empty;

    // ---- hero -----------------------------------------------------------------------------------

    public DownloadJob? HeroDownload
    {
        get => _heroDownload;
        private set
        {
            if (!SetProperty(ref _heroDownload, value)) return;
            OnPropertyChanged(nameof(ShowDownloadHero));
            OnPropertyChanged(nameof(ShowGameHero));
            OnPropertyChanged(nameof(ShowWelcomeHero));
            OnPropertyChanged(nameof(HasHeroArt));
            OnPropertyChanged(nameof(HeroDownloadDetail));
        }
    }

    public Game? HeroGame
    {
        get => _heroGame;
        private set
        {
            if (!SetProperty(ref _heroGame, value)) return;
            OnPropertyChanged(nameof(ShowGameHero));
            OnPropertyChanged(nameof(ShowWelcomeHero));
            OnPropertyChanged(nameof(HasHeroArt));
            OnPropertyChanged(nameof(HeroGameDetail));
        }
    }

    public bool ShowDownloadHero => HeroDownload is not null;
    public bool ShowGameHero => HeroDownload is null && HeroGame is not null;
    public bool ShowWelcomeHero => HeroDownload is null && HeroGame is null;
    public bool HasHeroArt => !ShowWelcomeHero;

    public string HeroDownloadDetail
    {
        get
        {
            var job = HeroDownload;
            if (job is null) return string.Empty;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(job.SizeSummary)) parts.Add(job.SizeSummary);
            if (job.IsActive)
            {
                if (HasValue(job.Speed)) parts.Add(job.Speed);
                if (HasValue(job.Eta)) parts.Add($"{job.Eta} left");
            }
            else if (!string.IsNullOrWhiteSpace(job.Status))
            {
                parts.Add(job.Status);
            }
            return string.Join("  ·  ", parts);
        }
    }

    public string HeroGameDetail => HeroGame is null
        ? string.Empty
        : string.Join("  ·  ", new[] { HeroGame.Size, HeroGame.LastPlayed }.Where(part => !string.IsNullOrWhiteSpace(part)));

    // ---- stat tiles -----------------------------------------------------------------------------

    public string InstalledCount => Store.Games.Count.ToString("N0");

    public string InstalledSummary => Store.Games.Count == 0
        ? "No Steam library found"
        : $"{ByteSize.Format(Store.Games.Sum(game => Math.Max(0, game.SizeOnDiskBytes)))} on disk";

    public string ActiveDownloadCount => Store.Downloads.Count(job => job.IsActive).ToString();

    public string DownloadSummary
    {
        get
        {
            var queued = Store.Downloads.Count(job => job.State == DownloadJobState.Queued);
            var paused = Store.Downloads.Count(job => job.State == DownloadJobState.Paused);
            if (queued == 0 && paused == 0)
                return Store.Downloads.Any(job => job.IsActive) ? "Nothing waiting" : "Nothing running";
            var parts = new List<string>();
            if (queued > 0) parts.Add($"{queued} queued");
            if (paused > 0) parts.Add($"{paused} paused");
            return string.Join(" · ", parts);
        }
    }

    public string CurrentSpeed
    {
        get
        {
            var speed = Store.Downloads.FirstOrDefault(job => job.IsActive && !string.IsNullOrWhiteSpace(job.Speed))?.Speed;
            return string.IsNullOrWhiteSpace(speed) ? "—" : speed;
        }
    }

    public string SpeedSummary
    {
        get
        {
            var active = Store.Downloads.FirstOrDefault(job => job.IsActive);
            if (active is null) return "Idle";
            return HasValue(active.Eta) ? $"{active.Eta} left" : active.GameName;
        }
    }

    public string StorageFree => _storageFree < 0 ? "—" : ByteSize.Format(_storageFree);

    public string StorageSummary => _storageFree < 0
        ? "Drive not available"
        : $"free of {ByteSize.Format(_storageTotal)} on {_storageDrive}";

    public double StorageUsedPercent => _storageTotal <= 0 ? 0 : 100.0 * (_storageTotal - _storageFree) / _storageTotal;

    public bool IsStorageLow => _storageTotal > 0 && _storageFree < _storageTotal * 0.1;

    // ---- SteamMidra-style system info ---------------------------------------------------------------

    public string SteamPath => _scan is { Succeeded: true, SteamRoot.Length: > 0 } scan ? scan.SteamRoot : "Not configured";
    public string LibraryFolderCountLabel => (_scan?.LibraryFolders.Count ?? 0).ToString();
    public string DetectedAppCountLabel => (_scan?.Apps.Count ?? 0).ToString();
    public string TotalGameSize => ByteSize.Format(Store.Games.Sum(g => Math.Max(0, g.SizeOnDiskBytes)));
    public string DepotToolVersionLabel => _depotStatus is { IsReady: true } status
        ? (string.IsNullOrWhiteSpace(status.Version) || status.Version == "unknown" ? "Ready" : $"v{status.Version}")
        : "Not configured";
    public bool IsDepotReady => _depotStatus?.IsReady == true;
    public string LastScanLabel => _lastRefresh == DateTime.MinValue
        ? "Never"
        : _lastRefresh.ToLocalTime().ToString("HH:mm");

    // ---- sharing --------------------------------------------------------------------------------

    public string ShareNewCount => _share.NewCount.ToString("N0");
    public string ShareTotalLabel => _share.TotalCount == 1 ? "1 game ready to share" : $"{_share.TotalCount:N0} games ready to share";
    public bool HasShareNew => _share.NewCount > 0;
    public bool IsShareScanning => _share.IsScanning;
    public string ShareLastLabel => _share.LastShareLabel;

    public string ShareHeadline => _share.NewCount switch
    {
        _ when _share.IsScanning && _share.TotalCount == 0 => "Looking for your manifests…",
        0 when _share.TotalCount == 0 => "Nothing to share yet",
        0 => "Everything is shared",
        1 => "1 game has new manifests",
        var count => $"{count:N0} games have new manifests"
    };

    // ---- lists ----------------------------------------------------------------------------------

    public bool HasRecentGames => RecentGames.Count > 0;
    public bool ShowNoGames => RecentGames.Count == 0;
    public string NoGamesDetail => _scan is { Succeeded: false } scan && !string.IsNullOrWhiteSpace(scan.Message)
        ? scan.Message
        : "Set your Steam folder in Settings, or let the app detect it automatically.";

    public bool HasActiveJobs => ActiveJobs.Count > 0;
    public bool ShowNoJobs => ActiveJobs.Count == 0;
    public string JobCountLabel => Store.Downloads.Count(job => !job.IsTerminal).ToString();

    public IReadOnlyList<DashboardStatusItem> StatusItems
    {
        get => _statusItems;
        private set => SetProperty(ref _statusItems, value);
    }

    public string StatusSummary => _statusItems.All(item => item.IsOk)
        ? "Everything is set up"
        : $"{_statusItems.Count(item => !item.IsOk)} of {_statusItems.Count} need attention";

    // ---- commands -------------------------------------------------------------------------------

    public ICommand RefreshCommand => new AsyncRelayCommand(() => RefreshAsync(force: true));
    public ICommand PlayGameCommand { get; }
    public ICommand OpenGameFolderCommand => new RelayCommand<Game>(OpenGameFolder);
    public ICommand NavigateDownloadsCommand => new RelayCommand(() => Navigation.Navigate<DownloadsPage>());
    public ICommand NavigateLibraryCommand => new RelayCommand(() => Navigation.Navigate<LibraryPage>());
    public ICommand NavigateFavoritesCommand => new RelayCommand(() =>
    {
        var library = App.Services.GetRequiredService<LibraryViewModel>();
        library.SearchText = string.Empty;
        library.SelectedTypeFilter = "All games";
        library.SelectedSourceFilter = "Favorites";
        Navigation.Navigate<LibraryPage>();
    });
    public ICommand ExploreSourceCommand => new RelayCommand<string>(source =>
    {
        if (source is null) return;
        var library = App.Services.GetRequiredService<LibraryViewModel>();
        library.SearchText = string.Empty;
        library.SelectedTypeFilter = "All games";
        library.SelectedSourceFilter = source;
        Navigation.Navigate<LibraryPage>();
    });
    public ICommand NavigateSettingsCommand => new RelayCommand(() => Navigation.Navigate<SettingsPage>());
    public ICommand NavigateFixesCommand => new RelayCommand(() => Navigation.Navigate<GameFixesPage>());
    public ICommand NavigateDlcUnlockerCommand => new RelayCommand(() => Navigation.Navigate<CreamApiPage>());
    public ICommand NavigateSteamlessCommand => new RelayCommand(() => Navigation.Navigate<SteamlessPage>());
    public ICommand NavigateDenuvoActivationCommand => new RelayCommand(() => Navigation.Navigate<DenuvoActivationPage>());
    public ICommand NavigateDepotDownloaderCommand => new RelayCommand(() => Navigation.Navigate<DepotDownloaderPage>());
    public ICommand NavigateFamilyShareCommand => new RelayCommand(() => Navigation.Navigate<FamilySharePage>());
    public ICommand OpenManifestFolderCommand => new RelayCommand(OpenManifestFolder);
    public ICommand NavigateShareCommand => new RelayCommand(() => Navigation.Navigate<SharePage>());
    public ICommand ShareAllNewCommand => new RelayCommand(_share.PrepareShareAllNew);
    public ICommand NavigateGoldbergCommand => new RelayCommand(() => Navigation.Navigate<GoldbergPage>());

    public override Task OnNavigatedToAsync()
    {
        _ = EnsureDiscoveryArtworkAsync();
        _ = RefreshActivityAsync();
        _ = LoadSearchHistoryAsync();
        return RefreshAsync(force: false);
    }

    public async Task RefreshAsync(bool force)
    {
        if (IsRefreshing || (!force && DateTime.UtcNow - _lastRefresh < RefreshDebounce)) return;
        IsRefreshing = true;
        _lastRefresh = DateTime.UtcNow;
        _ = EnsureDiscoveryArtworkAsync(force);
        try
        {
            try { _scan = await _librarySync.RefreshAsync(); }
            catch (Exception exception) { _scan = SteamLibraryScanResult.Failure($"The Steam library could not be read ({exception.GetType().Name})."); }

            RefreshLibrary();
            _ = RefreshStorageAsync();
            RefreshDownloads();
            RefreshStatus();

            _ = RefreshShareAsync();
            await RefreshDepotStatusAsync(force);
            RefreshStatus();
        }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(Greeting));
            OnPropertyChanged(nameof(DateLabel));
            OnPropertyChanged(nameof(DepotToolVersionLabel));
            OnPropertyChanged(nameof(IsDepotReady));
            OnPropertyChanged(nameof(LastScanLabel));
        }
    }

    // ---- refresh helpers ------------------------------------------------------------------------

    private void RefreshLibrary()
    {
        var favoriteIds = _activitySnapshot.FavoriteAppIds.ToHashSet();
        var opened = _activitySnapshot.Launches.ToDictionary(item => item.AppId, item => (DateTimeOffset?)item.OpenedAt);
        var recent = Store.Games
            .Where(game => game.InstallState != GameInstallState.NotInstalled)
            .OrderByDescending(game => favoriteIds.Contains(game.AppId))
            .ThenByDescending(game => opened.GetValueOrDefault(game.AppId))
            .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .Take(RecentGameCount)
            .ToList();

        if (!recent.SequenceEqual(RecentGames))
        {
            DownloadPresentation.Synchronize(RecentGames, recent);
        }

        PersonalLibrary.ReplaceWith(recent.Select(game => new DashboardLibraryEntry(game, favoriteIds.Contains(game.AppId), opened.GetValueOrDefault(game.AppId))));
        var favorites = favoriteIds.Select(appId => Store.Games.FirstOrDefault(game => game.AppId == appId)
            ?? _catalogFavorites.GetValueOrDefault(appId)
            ?? new Game { AppId = appId, Name = $"App {appId}", InstallState = GameInstallState.NotInstalled });
        if (FavoriteGames.ReplaceWith(favorites
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).Take(RecentGameCount)
            .Select(game => new DashboardLibraryEntry(game, true, opened.GetValueOrDefault(game.AppId)))))
            OnPropertyChanged(nameof(FavoriteGames));
        RecentlyOpenedGames.ReplaceWith(Store.Games.Where(game => game.InstallState != GameInstallState.NotInstalled && opened.ContainsKey(game.AppId))
            .OrderByDescending(game => opened[game.AppId]).Take(RecentGameCount)
            .Select(game => new DashboardLibraryEntry(game, favoriteIds.Contains(game.AppId), opened[game.AppId])));

        HeroGame = ContinueGame?.Game ?? recent.FirstOrDefault();
        OnPropertyChanged(nameof(ContinueGame));
        OnPropertyChanged(nameof(HasContinueGame));
        OnPropertyChanged(nameof(HasFavoriteGames));
        OnPropertyChanged(nameof(FavoriteCount));
        OnPropertyChanged(nameof(HasRecentlyOpenedGames));
        (ContinueGameCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (PlayGameCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasRecentGames));
        OnPropertyChanged(nameof(ShowNoGames));
        OnPropertyChanged(nameof(NoGamesDetail));
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(InstalledSummary));
        OnPropertyChanged(nameof(HeaderSummary));
        OnPropertyChanged(nameof(SteamPath));
        OnPropertyChanged(nameof(LibraryFolderCountLabel));
        OnPropertyChanged(nameof(DetectedAppCountLabel));
        OnPropertyChanged(nameof(TotalGameSize));
        LoadPersonalArtwork();
    }

    public async Task RefreshActivityAsync()
    {
        try
        {
            _activitySnapshot = await _activity.GetAsync();
            RefreshLibrary();
            await EnsureFavoriteDetailsAsync();
        }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Library", $"Personal library unavailable: {exception.GetType().Name}."); }
    }

    private Task EnsureFavoriteDetailsAsync() => _favoriteDetailsTask is { IsCompleted: false }
        ? _favoriteDetailsTask : _favoriteDetailsTask = ResolveFavoriteDetailsAsync();

    private async Task ResolveFavoriteDetailsAsync()
    {
        var savedIds = _activitySnapshot.FavoriteAppIds.ToHashSet();
        foreach (var appId in _catalogFavorites.Keys.Where(appId => !savedIds.Contains(appId)).ToArray())
            _catalogFavorites.Remove(appId);
        _resolvedFavoriteAppIds.RemoveWhere(appId => !savedIds.Contains(appId));
        if (!savedIds.Any(appId => !_resolvedFavoriteAppIds.Contains(appId)
            && !Store.Games.Any(game => game.AppId == appId))) return;

        try
        {
            // Favorites and live search share one cached catalog request. Saving a catalog game
            // never adds it to the installed library or invents an installation/launch target.
            _searchCatalogTask ??= Task.Run(() => _catalog.GetCatalogAsync());
            var snapshot = await _searchCatalogTask;
            savedIds = _activitySnapshot.FavoriteAppIds.ToHashSet();
            foreach (var item in snapshot.Items.Where(item => savedIds.Contains(item.AppId)))
            {
                _catalogFavorites.TryGetValue(item.AppId, out var previous);
                _catalogFavorites[item.AppId] = new Game
                {
                    AppId = item.AppId, Name = string.IsNullOrWhiteSpace(item.Name) ? $"App {item.AppId}" : item.Name,
                    InstallState = GameInstallState.NotInstalled,
                    Size = "Not installed", HeaderImage = item.HeaderImage ?? previous?.HeaderImage,
                    ArtworkImage = item.ArtworkImage ?? previous?.ArtworkImage
                };
            }
            if (snapshot.Succeeded) _resolvedFavoriteAppIds.UnionWith(savedIds);
            else _searchCatalogTask = null;
            RefreshLibrary();
        }
        catch (Exception exception)
        {
            _searchCatalogTask = null;
            Logging.Add(LogLevel.Debug, "Library", $"Favorite titles are temporarily unavailable: {exception.GetType().Name}.");
        }
    }

    private void LoadPersonalArtwork()
    {
        if (_personalArtworkCancellation is not { IsCancellationRequested: false } cancellation) return;
        var visible = PersonalLibrary.Concat(FavoriteGames).Select(entry => entry.Game).DistinctBy(game => game.AppId).ToArray();
        _personalArtworkRequested.RemoveWhere(game => !visible.Contains(game));
        foreach (var game in visible)
            if (game.HeaderImage is null && _personalArtworkRequested.Add(game))
                _ = LoadPersonalHeaderAsync(game, cancellation.Token);
    }

    private async Task LoadPersonalHeaderAsync(Game game, CancellationToken cancellationToken)
    {
        try { await LoadSearchHeaderAsync(game, cancellationToken); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Library", $"Optional cover unavailable: {exception.GetType().Name}."); }
    }

    private void OnActivityChanged(object? sender, EventArgs args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is { HasShutdownStarted: false } && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = RefreshActivityAsync()));
        else if (dispatcher?.HasShutdownStarted != true) _ = RefreshActivityAsync();
    }

    private async Task ToggleFavoriteAsync(Game? game)
    {
        if (game is null || game.AppId <= 0) return;
        try
        {
            var favorite = await _activity.ToggleFavoriteAsync(game.AppId);
            if (favorite && !_catalogFavorites.ContainsKey(game.AppId))
                _catalogFavorites[game.AppId] = new Game
                {
                    AppId = game.AppId, Name = string.IsNullOrWhiteSpace(game.Name) ? $"App {game.AppId}" : game.Name,
                    InstallState = GameInstallState.NotInstalled,
                    Size = "Not installed", HeaderImage = game.HeaderImage, ArtworkImage = game.ArtworkImage,
                    CoverColor = game.CoverColor, CoverGlyph = game.CoverGlyph
                };
            await RefreshActivityAsync();
            ActivityNotice = string.Empty;
        }
        catch (Exception exception)
        {
            ActivityNotice = "Your favorite could not be saved. Check access to the Steamy data folder.";
            Logging.Add(LogLevel.Warning, "Library", $"Favorite could not be saved: {exception.Message}");
        }
        OnPropertyChanged(nameof(HasActivityNotice));
    }

    private void RefreshDownloads()
    {
        var open = Store.Downloads
            .Where(job => !job.IsTerminal)
            .OrderBy(job => job.IsActive ? 0 : job.State == DownloadJobState.Paused ? 1 : 2)
            .ThenBy(job => job.Started)
            .ToList();

        var preview = open.Take(JobPreviewCount).ToList();
        if (!preview.SequenceEqual(ActiveJobs))
        {
            DownloadPresentation.Synchronize(ActiveJobs, preview);
        }

        HeroDownload = open.FirstOrDefault();
        OnPropertyChanged(nameof(HasActiveJobs));
        OnPropertyChanged(nameof(ShowNoJobs));
        OnPropertyChanged(nameof(JobCountLabel));
        RaiseDownloadStats();
    }

    private void RaiseDownloadStats()
    {
        OnPropertyChanged(nameof(ActiveDownloadCount));
        OnPropertyChanged(nameof(DownloadSummary));
        OnPropertyChanged(nameof(CurrentSpeed));
        OnPropertyChanged(nameof(SpeedSummary));
        OnPropertyChanged(nameof(HeaderSummary));
        OnPropertyChanged(nameof(HeroDownloadDetail));
    }

    private async Task RefreshStorageAsync()
    {
        var probe = _scan is { Succeeded: true, SteamRoot.Length: > 0 } scan ? scan.SteamRoot : Path.GetTempPath();
        var storage = await Task.Run(() =>
        {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(probe));
            var drive = string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root);
            if (drive is { IsReady: true })
            {
                return (Free: drive.AvailableFreeSpace, Total: drive.TotalSize, Drive: drive.Name.TrimEnd('\\', '/'));
            }
            else
            {
                return (Free: -1L, Total: 0L, Drive: string.Empty);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return (Free: -1L, Total: 0L, Drive: string.Empty);
        }
        });
        _storageFree = storage.Free;
        _storageTotal = storage.Total;
        _storageDrive = storage.Drive;

        OnPropertyChanged(nameof(StorageFree));
        OnPropertyChanged(nameof(StorageSummary));
        OnPropertyChanged(nameof(StorageUsedPercent));
        OnPropertyChanged(nameof(IsStorageLow));
    }

    private async Task RefreshDepotStatusAsync(bool force)
    {
        var path = _settings.Load().DepotDownloaderPath ?? string.Empty;
        if (!force && _depotStatus is not null && path == _depotStatusPath) return;
        _depotStatusPath = path;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _depotStatus = await _depotCheck.CheckAsync(path, timeout.Token);
        }
        catch (Exception exception)
        {
            _depotStatus = new DepotDownloaderToolStatus(false, path, string.Empty, $"Check failed ({exception.GetType().Name}).", DateTime.Now);
        }
    }

    private async Task RefreshShareAsync()
    {
        try
        {
            await _share.EnsureScannedAsync();
        }
        catch (Exception exception)
        {
            Logging.Add(LogLevel.Debug, "Dashboard", $"Share scan skipped: {exception.GetType().Name}.");
        }

        RaiseShareStats();
    }

    private void RaiseShareStats()
    {
        OnPropertyChanged(nameof(ShareNewCount));
        OnPropertyChanged(nameof(ShareTotalLabel));
        OnPropertyChanged(nameof(HasShareNew));
        OnPropertyChanged(nameof(IsShareScanning));
        OnPropertyChanged(nameof(ShareLastLabel));
        OnPropertyChanged(nameof(ShareHeadline));
    }

    private void RefreshStatus()
    {
        var settings = _settings.Load();

        var library = _scan switch
        {
            null => new DashboardStatusItem("Steam library", "Not scanned yet.", false, "Settings", NavigateSettingsCommand),
            { Succeeded: true } scan => new DashboardStatusItem("Steam library",
                $"{Plural(scan.LibraryFolders.Count, "library folder")} · {Plural(scan.Apps.Count, "game")}", true, "Settings", NavigateSettingsCommand),
            var scan => new DashboardStatusItem("Steam library", scan.Message, false, "Set folder", NavigateSettingsCommand)
        };

        var depot = _depotStatus switch
        {
            null => new DashboardStatusItem("DepotDownloader", "Checking…", true, "Settings", NavigateSettingsCommand),
            { IsReady: true } status => new DashboardStatusItem("DepotDownloader",
                string.IsNullOrWhiteSpace(status.Version) || status.Version == "unknown" ? "Ready" : $"Ready · v{status.Version}", true, "Settings", NavigateSettingsCommand),
            var status => new DashboardStatusItem("DepotDownloader", status.Message, false, "Set up", NavigateSettingsCommand)
        };

        var fixSource = FixSource.Resolve(settings.FixMirrorUrl);
        var fixes = fixSource is null
            ? new DashboardStatusItem("Fixes source", "The URL in Settings is not valid.", false, "Set up", NavigateSettingsCommand)
            : new DashboardStatusItem("Fixes source",
                DescribeSource(string.IsNullOrWhiteSpace(settings.FixMirrorUrl) ? FixSource.DefaultUrl : settings.FixMirrorUrl), true, "Settings", NavigateSettingsCommand);

        StatusItems = new[] { library, depot, fixes };
        OnPropertyChanged(nameof(StatusSummary));
    }

    // ---- live updates ---------------------------------------------------------------------------

    private void OnDownloadsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var job in _watchedJobs) job.PropertyChanged -= OnJobChanged;
            _watchedJobs.Clear();
            foreach (var job in Store.Downloads) Watch(job);
        }
        else
        {
            foreach (DownloadJob job in e.OldItems ?? Array.Empty<DownloadJob>())
                if (_watchedJobs.Remove(job)) job.PropertyChanged -= OnJobChanged;
            foreach (DownloadJob job in e.NewItems ?? Array.Empty<DownloadJob>())
                Watch(job);
        }

        QueueDownloadRefresh();
    }

    private void Watch(DownloadJob job)
    {
        if (_watchedJobs.Add(job)) job.PropertyChanged += OnJobChanged;
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadJob.State)) QueueDownloadRefresh();
        else if (e.PropertyName is nameof(DownloadJob.BytesPerSecond) or nameof(DownloadJob.Progress)
                 or nameof(DownloadJob.Speed) or nameof(DownloadJob.SizeSummary)) _statsDirty = true;
    }

    // Progress events arrive many times a second; batch them into one refresh per dispatcher pass.
    private void QueueDownloadRefresh() => Queue(ref _downloadRefreshQueued, () => { _downloadRefreshQueued = false; RefreshDownloads(); });

    private void QueueLibraryRefresh() => Queue(ref _libraryRefreshQueued, () => { _libraryRefreshQueued = false; RefreshLibrary(); });

    private static void Queue(ref bool queued, Action refresh)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            refresh();
            return;
        }

        if (queued) return;
        queued = true;
        dispatcher.BeginInvoke(DispatcherPriority.Background, refresh);
    }

    // ---- actions --------------------------------------------------------------------------------

    private bool CanPlayGame(Game? game) => game is { AppId: > 0, InstallState: GameInstallState.Installed }
        && Store.Games.Any(installed => installed.AppId == game.AppId && installed.InstallState == GameInstallState.Installed);

    private async Task PlayGameAsync(Game? game)
    {
        if (game is null || !CanPlayGame(game)) return;
        if (!StartShell($"steam://rungameid/{game.AppId}")) return;
        try
        {
            await _activity.RecordLaunchAsync(game.AppId);
            await RefreshActivityAsync();
        }
        catch (Exception exception) { Logging.Add(LogLevel.Warning, "Library", $"The launch was requested, but its history could not be saved: {exception.Message}"); }
    }

    private void OpenGameFolder(Game? game)
    {
        if (game is null || string.IsNullOrWhiteSpace(game.InstallFolder) || !Directory.Exists(game.InstallFolder)) return;
        StartShell(game.InstallFolder);
    }

    private void OpenManifestFolder()
    {
        var settings = _settings.Load();
        var folder = !string.IsNullOrWhiteSpace(settings.DownloadFolder) ? settings.DownloadFolder
            : !string.IsNullOrWhiteSpace(settings.WorkingDirectory) ? settings.WorkingDirectory
            : null;
        if (folder is not null && Directory.Exists(folder))
            StartShell(folder);
    }

    private bool StartShell(string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            // Shell activation can hand the URI to an already running Steam process and return
            // no process object. Record the accepted launch request, without claiming playtime.
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Logging.Add(LogLevel.Warning, "Dashboard", $"Could not open {target}: {exception.Message}");
            return false;
        }
    }

    private static bool HasValue(string? text) => !string.IsNullOrWhiteSpace(text) && text != "—";

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    private static string DescribeSource(string configured)
    {
        var text = configured.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            ? (uri.Host + uri.AbsolutePath).TrimEnd('/')
            : configured;
    }
}
