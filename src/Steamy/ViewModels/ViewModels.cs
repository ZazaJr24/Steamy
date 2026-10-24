using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Wpf.Ui.Abstractions.Controls;

namespace Steamy.ViewModels;

public abstract class ViewModelBase : ObservableObject, INavigationAware
{
    protected readonly IAppDataStore Store;
    protected readonly INavigationService Navigation;
    protected readonly ILoggingService Logging;
    protected ViewModelBase(IAppDataStore store, INavigationService navigation, ILoggingService logging) { Store = store; Navigation = navigation; Logging = logging; }
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
    public virtual Task OnNavigatedFromAsync() => Task.CompletedTask;
}

public enum LibraryNsfwScope { Hide, Show }

public sealed partial class LibraryViewModel : ViewModelBase
{
    public SteamCatalogItem? RequestedDownload { get; set; }
    private readonly ISteamCatalogService _catalog; private readonly IRyuuCatalogService _ryuu; private readonly IHubcapCatalogService _hubcap; private readonly ILibrarySyncService _librarySync;
    private readonly DispatcherTimer _searchDebounceTimer;
    private CancellationTokenSource? _artworkLoadingCancellation;
    private readonly HashSet<SteamCatalogItem> _loadedCatalogArtwork = new();
    private IReadOnlyList<SteamCatalogItem> _filteredCatalog = Array.Empty<SteamCatalogItem>();
    private SteamCatalogItem[] _catalogSnapshot = Array.Empty<SteamCatalogItem>();
    private SteamCatalogItem[]? _indexedCatalog;
    private Task<GameSearchIndex<SteamCatalogItem>>? _searchIndexTask;
    private CancellationTokenSource? _searchIndexCancellation;
    private CancellationTokenSource? _localFilterCancellation;
    private readonly ISpotlightService? _spotlight;
    private readonly IGameActivityService? _activity;
    private IReadOnlySet<int> _favoriteApps = new HashSet<int>();
    private bool _isSearchBusy;
    private bool _isTypoMatch;
    private string _searchHint = string.Empty;
    private int _filterGeneration;
    private CancellationTokenSource? _filterCancellation;
    private readonly IFreeManifestCatalogService _freeSources;
    private IReadOnlyDictionary<string, IReadOnlySet<int>> _sourceApps = new Dictionary<string, IReadOnlySet<int>>();
    private DateTimeOffset _lastCatalogLoad;
    private bool _navigating;
    private CancellationTokenSource _pageLifetime = new();
    private DateTimeOffset _lastLibraryScan;
    private string _sourceFilter = "All sources";
    private IReadOnlyDictionary<string, string> _sourceMessages = new Dictionary<string, string>();
    public string SourceFilterHint => SelectedSourceFilter == "All sources" ? "Sushi and Zaza are free, without an API key."
        : SelectedSourceFilter == "Installed" ? "Steam, DepotDownloader and locally detected installations."
        : SelectedSourceFilter == "Favorites" ? $"{_favoriteApps.Count:N0} favorites saved on this device."
        : _sourceMessages.TryGetValue(SelectedSourceFilter, out var message) ? message : "Source index is loading; you can still select a source in the download picker.";
    public string[] SourceFilters { get; } = { "All sources", "Sushi", "Zaza", "Ryuu", "Hubcap", "Installed" };
    public string SelectedSourceFilter
    {
        get => _sourceFilter;
        set
        {
            if (!SetProperty(ref _sourceFilter, value)) return;
            _page = 1;
            OnPropertyChanged(nameof(HasActiveFilters));
            OnPropertyChanged(nameof(FilterSummary));
            OnPropertyChanged(nameof(SourceFilterHint));
            RefreshPage();
        }
    }
    private string _search=""; private string _sort="Popular (AAA)"; private string _typeFilter="Game"; private int _page=1; private int _pageSize=48; private LibraryNsfwScope _nsfwScope=LibraryNsfwScope.Hide;
    public const string RyuuSource="Available (Ryuu)";
    public LibraryViewModel(IAppDataStore s, INavigationService n, ILoggingService l, ISteamCatalogService c, IRyuuCatalogService ryuu, IArtworkService artwork, ILibrarySyncService sync, IHubcapCatalogService hubcap, IFreeManifestCatalogService freeSources, IGameActivityService? activity = null, ISpotlightService? spotlight = null) : base(s,n,l)
    {
        _catalog=c; _ryuu=ryuu; _hubcap=hubcap; _librarySync=sync; _freeSources=freeSources;
        _activity = activity;
        _spotlight = spotlight;
        UpdateReleaseFilter(spotlight?.Cached ?? SpotlightSnapshot.Empty);
        if (_activity is not null) _activity.Changed += OnFavoriteActivityChanged;
        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _searchDebounceTimer.Tick += OnSearchDebounceElapsed;
        SubmitSearchCommand = new RelayCommand(RefreshPage);
        SelectSearchSuggestionCommand = new RelayCommand<SteamCatalogItem>(item =>
        {
            if (item is null) return;
            SearchText = item.AppId.ToString(CultureInfo.InvariantCulture);
            RefreshPage();
        });
        _ = RefreshFavoriteFilterAsync();
    }
    private void OnFavoriteActivityChanged(object? sender, EventArgs args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is { HasShutdownStarted: false } && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = RefreshFavoriteFilterAsync()));
        else if (dispatcher?.HasShutdownStarted != true) _ = RefreshFavoriteFilterAsync();
    }
    private async Task RefreshFavoriteFilterAsync()
    {
        if (_activity is null || _pageLifetime.IsCancellationRequested) return;
        var token = _pageLifetime.Token;
        try
        {
            var favorites = (await _activity.GetAsync(token).WaitAsync(token)).FavoriteAppIds.ToHashSet();
            token.ThrowIfCancellationRequested();
            if (favorites.SetEquals(_favoriteApps)) return;
            _favoriteApps = favorites;
            if (SelectedSourceFilter == "Favorites") RefreshPage();
            OnPropertyChanged(nameof(SourceFilterHint));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Library", $"Favorites unavailable: {exception.GetType().Name}."); }
    }
    private void OnSearchDebounceElapsed(object? sender, EventArgs e) { _searchDebounceTimer.Stop(); RefreshPage(); }
    public override Task OnNavigatedFromAsync()
    {
        _pageLifetime.Cancel();
        _navigating = false;
        IsCatalogLoading = false;
        OnPropertyChanged(nameof(IsCatalogLoading));
        _searchDebounceTimer.Stop();
        Interlocked.Increment(ref _filterGeneration);
        _filterCancellation?.Cancel();
        ReleaseSearchIndex();
        _localFilterCancellation?.Cancel();
        _localFilterCancellation?.Dispose();
        _localFilterCancellation = null;
        _artworkLoadingCancellation?.Cancel();
        _recommendationsCancellation?.Cancel();
        _recommendationsCancellation = null; // Each request disposes its own source when it drains.
        foreach (var item in _loadedCatalogArtwork) { item.ArtworkImage = null; item.HeaderImage = null; }
        _loadedCatalogArtwork.Clear();
        IsSearchBusy = false;
        return Task.CompletedTask;
    }
    private ObservableCollection<SteamCatalogItem> _catalogItems = new();
    public ObservableCollection<Game> Games => Store.Games;
    public ObservableCollection<SteamCatalogItem> CatalogItems { get => _catalogItems; private set => SetProperty(ref _catalogItems, value); }
    public RangeObservableCollection<SteamCatalogItem> PagedCatalogItems { get; }=new(); public RangeObservableCollection<Game> FilteredGames { get; }=new(); public RangeObservableCollection<Game> PagedGames { get; }=new();
    public string[] Modes { get; }={"Browse all","Your games","For you"}; public string[] TypeFilters { get; }={"Game"}; public string[] SortOptions { get; }={"Popular (AAA)","Name A–Z","App ID"}; public int[] PageSizes { get; }={24,48,96};
    public const string HubcapSource="Hubcap";
    public string SelectedMode { get => _mode; set { if (SetProperty(ref _mode, value)) { _page = 1; OnPropertyChanged(nameof(DiscoveryDescription)); OnPropertyChanged(nameof(LibraryTitle)); OnPropertyChanged(nameof(CatalogCountLabel)); RefreshPage(); if (value != "Your games") _ = LoadSelectedCatalogAsync(); } } }    public string SelectedTypeFilter { get=>_typeFilter; set { if(SetProperty(ref _typeFilter,"Game")) { _page=1; RefreshPage(); } } }
    public LibraryNsfwScope NsfwScope { get=>_nsfwScope; set { if(SetProperty(ref _nsfwScope,value)) { _page=1; RefreshPage(); } } }
    public string SelectedSort { get=>_sort; set { if(SetProperty(ref _sort,value)) RefreshPage(); } } public int PageSize { get=>_pageSize; set { if(SetProperty(ref _pageSize,value)) RefreshPage(); } }
    public string SearchText { get=>_search; set { if(SetProperty(ref _search,value ?? string.Empty)) { _page=1; OnPropertyChanged(nameof(HasActiveFilters)); OnPropertyChanged(nameof(FilterSummary)); OnPropertyChanged(nameof(HasSearchQuery)); OnPropertyChanged(nameof(HasSearchSuggestions)); Interlocked.Increment(ref _filterGeneration); _filterCancellation?.Cancel(); _searchDebounceTimer.Stop(); _searchDebounceTimer.Start(); } } }
    public RangeObservableCollection<SteamCatalogItem> SearchSuggestions { get; } = new();
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchText);
    public bool HasSearchSuggestions => HasSearchQuery && SearchSuggestions.Count > 0;
    public bool IsSearchBusy { get => _isSearchBusy; private set => SetProperty(ref _isSearchBusy, value); }
    public bool IsTypoMatch { get => _isTypoMatch; private set => SetProperty(ref _isTypoMatch, value); }
    public string SearchHint { get => _searchHint; private set => SetProperty(ref _searchHint, value); }
    public ICommand SubmitSearchCommand { get; }
    public ICommand SelectSearchSuggestionCommand { get; }
    public bool IsCatalogLoading { get; private set; } public bool CatalogLoaded { get; private set; } public string CatalogStatus { get; private set; }="Load your Steam app list to begin."; public string LibraryMessage { get; private set; }="";
    public string CatalogCountLabel => SelectedMode == "Your games" ? $"{Games.Count(game => game.InstallState == GameInstallState.Installed && !GameClassification.IsRuntime(game.AppId, game.Name)):N0} installed" : $"{FilteredCatalogCount:N0} base games"; public string VisibleCountLabel=>$"Showing {PagedCatalogItems.Count} of {FilteredCatalogCount:N0}"; public string PageLabel=>$"Page {_page} of {TotalPages}"; public string UpdatedLabel { get; private set; }="Not loaded"; public int FilteredCatalogCount { get; private set; } public int TotalPages=>Math.Max(1,(FilteredCatalogCount+PageSize-1)/PageSize); public bool CanGoPrevious=>_page>1; public bool CanGoNext=>_page<TotalPages; public bool HasCatalogItems=>PagedCatalogItems.Count>0; public bool HasGames=>FilteredGames.Count>0;    public bool HasActiveFilters=>SelectedSourceFilter!="All sources"||!string.IsNullOrWhiteSpace(SearchText)||SelectedSort!="Popular (AAA)"||PageSize!=48||NsfwScope==LibraryNsfwScope.Show; public string FilterSummary=>HasActiveFilters?"Active filters":"No filters applied"; public string EmptyStateMessage => SelectedMode == "Your games" ? "No installed games match. Run setup or Scan PC from Home to find Steam, DepotDownloader and standalone installations." : "No base games found. Try another title or App ID.";
    public ICommand LoadCatalogCommand=>new AsyncRelayCommand(()=>LoadAsync(false)); public ICommand RefreshCatalogCommand=>new AsyncRelayCommand(async()=>{ try{await _librarySync.RefreshAsync(); RefreshLocalPage();}catch{} await LoadAsync(true); await PrepareRecommendationsAsync(); }); public ICommand ScanLibraryCommand=>new AsyncRelayCommand(async()=>{ try{await _librarySync.RefreshAsync();}catch{} RefreshLocalPage(); }); public ICommand FirstPageCommand=>new RelayCommand(()=>SetPage(1)); public ICommand PreviousPageCommand=>new RelayCommand(()=>SetPage(_page-1)); public ICommand NextPageCommand=>new RelayCommand(()=>SetPage(_page+1)); public ICommand LastPageCommand=>new RelayCommand(()=>SetPage(TotalPages)); public ICommand RefreshCommand=>new RelayCommand(RefreshPage); public ICommand OpenDownloadsCommand=>new RelayCommand(()=>Navigation.Navigate<DownloadsPage>()); public ICommand OpenFolderCommand=>new RelayCommand<Game>(_=>{}); public ICommand RefreshLocalCommand=>new RelayCommand(()=>{}); public ICommand OpenStoreCommand=>new RelayCommand(()=>{}); public IAsyncRelayCommand LoadScreenshotsCommand=>new AsyncRelayCommand(()=>Task.CompletedTask);
    // Home dashboard shortcuts + quick stats shown on the Games landing page.
    public ICommand OpenGameFixesCommand=>new RelayCommand(()=>Navigation.Navigate<GameFixesPage>());    public ICommand OpenSteamlessCommand=>new RelayCommand(()=>Navigation.Navigate<SteamlessPage>()); public ICommand OpenCreamInstallerCommand=>new RelayCommand(()=>Navigation.Navigate<CreamInstallerPage>()); public ICommand OpenDenuvoCommand=>new RelayCommand(()=>Navigation.Navigate<DenuvoGenerationPage>()); public ICommand OpenSettingsCommand=>new RelayCommand(()=>Navigation.Navigate<SettingsPage>());
    public int InstalledCount=>Games.Count(g=>g.InstallState==GameInstallState.Installed); public int LocalCount=>Games.Count; public int ActiveDownloadCount=>Store.Downloads.Count(x=>x.IsActive); public int QueuedCount=>Store.Downloads.Count(x=>x.State==DownloadJobState.Queued); public string TotalCatalogCount=>$"{CatalogItems.Count:N0}"; public string LocalLibraryLabel=>Games.Count==1?"1 installed app":$"{Games.Count:N0} installed apps"; public string DownloadsSummary=>ActiveDownloadCount>0?$"{ActiveDownloadCount} active · {QueuedCount} queued":QueuedCount>0?$"{QueuedCount} queued":"Queue empty";
    public override async Task OnNavigatedToAsync()
    {
        if (_navigating) return;
        if (_pageLifetime.IsCancellationRequested) { _pageLifetime.Dispose(); _pageLifetime = new(); }
        var lifetime = _pageLifetime;
        var token = lifetime.Token;
        _navigating = true;
        try
        {
            RefreshPage();
            // The installed library is complete without materializing the entire public catalog.
            var catalogTask = SelectedMode == "Your games" ? Task.CompletedTask : LoadAsync(false);
            if (DateTimeOffset.UtcNow - _lastLibraryScan >= TimeSpan.FromSeconds(30))
            {
                try
                {
                    await _librarySync.RefreshAsync(token).WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    _lastLibraryScan = DateTimeOffset.UtcNow;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { }
            }
            token.ThrowIfCancellationRequested();
            RefreshLocalPage();
            await RefreshFavoriteFilterAsync();
            await catalogTask.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            RefreshPage();
            if (SelectedMode == "For you") _ = PrepareRecommendationsAsync();
            OnPropertyChanged(nameof(InstalledCount));
            OnPropertyChanged(nameof(LocalCount));
            OnPropertyChanged(nameof(ActiveDownloadCount));
            OnPropertyChanged(nameof(QueuedCount));
            OnPropertyChanged(nameof(TotalCatalogCount));
            OnPropertyChanged(nameof(LocalLibraryLabel));
            OnPropertyChanged(nameof(DownloadsSummary));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_pageLifetime, lifetime)) _navigating = false; }
    }
    private static async Task<T> WithTimeout<T>(Task<T> task, T fallback, int ms = 20000, CancellationToken cancellationToken = default)
    {
        // Providers may complete or fail after a timed-out/cancelled page has left.
        _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await task.WaitAsync(TimeSpan.FromMilliseconds(ms), cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return fallback; }
    }
    private async Task LoadSelectedCatalogAsync()
    {
        var token = _pageLifetime.Token;
        await LoadAsync(false);
        if (!token.IsCancellationRequested && SelectedMode == "For you") await PrepareRecommendationsAsync();
    }
    private async Task LoadAsync(bool force)
    {
        var lifetime = _pageLifetime;
        var token = lifetime.Token;
        if (IsCatalogLoading || token.IsCancellationRequested) return;
        if (!force && CatalogLoaded && DateTimeOffset.UtcNow - _lastCatalogLoad < TimeSpan.FromMinutes(10))
        {
            RefreshPage();
            return;
        }
        IsCatalogLoading = true;
        OnPropertyChanged(nameof(IsCatalogLoading));
        try
        {
            _ = RefreshReleaseFilterAsync(force);
            var fail = SteamCatalogSnapshot.Failure("Timed out");
            var unavailable = new FreeManifestIndex(false, new HashSet<int>(), "Source index unavailable.");
            var sushiTask = WithTimeout(Task.Run(() => _freeSources.GetAsync("Sushi", force, token), token), unavailable, cancellationToken: token);
            var zazaTask = WithTimeout(Task.Run(() => _freeSources.GetAsync("Zaza", force, token), token), unavailable, cancellationToken: token);
            var steamTask = WithTimeout(Task.Run(() => _catalog.GetCatalogAsync(force, token), token), fail, cancellationToken: token);
            // Optional sources load while the first cached Steam cards are being prepared.
            var ryuuTask = WithTimeout(Task.Run(() => _ryuu.GetGamesAsync(force, token), token), fail, cancellationToken: token);
            var hubcapTask = WithTimeout(Task.Run(() => _hubcap.GetGamesAsync(force, token), token), fail, cancellationToken: token);
            var steam = await steamTask;
            token.ThrowIfCancellationRequested();

            // Let the cached Steam catalog paint immediately instead of waiting for optional providers.
            if (CatalogItems.Count == 0 && steam.Succeeded && steam.Items.Count > 0)
            {
                var initial = await Task.Run(() =>
                {
                    var snapshot = steam.Items.ToArray();
                    return (snapshot, new ObservableCollection<SteamCatalogItem>(snapshot));
                }, token);
                token.ThrowIfCancellationRequested();
                _catalogSnapshot = initial.snapshot;
                CatalogItems = initial.Item2;
                CatalogLoaded = true;
                CatalogStatus = $"Loaded {initial.snapshot.Length:N0} Steam games; checking other sources…";
                RefreshPage();
                OnPropertyChanged(string.Empty);
            }
            else if (CatalogItems.Count == 0 && Games.Count > 0)
            {
                var localGames = Games.ToArray();
                var localItems = await Task.Run(() => localGames.Select(game =>
                {
                    token.ThrowIfCancellationRequested();
                    return new SteamCatalogItem
                    {
                        AppId = game.AppId,
                        Name = game.Name,
                        CapsuleImageUrl = game.ArtworkUrl,
                        PortraitImageUrl = game.ArtworkUrl
                    };
                }).ToArray(), token);
                var localCollection = await Task.Run(() => new ObservableCollection<SteamCatalogItem>(localItems), token);
                token.ThrowIfCancellationRequested();
                _catalogSnapshot = localItems;
                CatalogItems = localCollection;
                RefreshPage();
            }

            await Task.WhenAll(ryuuTask, hubcapTask);
            var ryuu = await ryuuTask;
            var hubcap = await hubcapTask;
            var sushi = await sushiTask;
            var zaza = await zazaTask;
            token.ThrowIfCancellationRequested();
            _sourceMessages = new Dictionary<string, string>
            {
                ["Sushi"] = sushi.Message, ["Zaza"] = zaza.Message,
                ["Ryuu"] = ryuu.Message, ["Hubcap"] = hubcap.Message
            };
            var merged = await Task.Run(() =>
            {
                var mergedItems = new Dictionary<int, SteamCatalogItem>();
                foreach (var item in steam.Items) { token.ThrowIfCancellationRequested(); mergedItems[item.AppId] = item; }
                foreach (var item in ryuu.Items) { token.ThrowIfCancellationRequested(); if (!mergedItems.ContainsKey(item.AppId)) mergedItems[item.AppId] = item; }
                foreach (var item in hubcap.Items) { token.ThrowIfCancellationRequested(); if (!mergedItems.ContainsKey(item.AppId)) mergedItems[item.AppId] = item; }
                foreach (var id in sushi.AppIds.Concat(zaza.AppIds))
                {
                    token.ThrowIfCancellationRequested();
                    if (!mergedItems.ContainsKey(id)) mergedItems[id] = new SteamCatalogItem { AppId = id, Name = $"App {id}" };
                }
                var snapshot = mergedItems.Values.ToArray();
                var sources = new Dictionary<string, IReadOnlySet<int>>
                {
                    ["Sushi"] = sushi.AppIds, ["Zaza"] = zaza.AppIds,
                    ["Ryuu"] = ryuu.Items.Select(item => item.AppId).ToHashSet(),
                    ["Hubcap"] = hubcap.Items.Select(item => item.AppId).ToHashSet()
                };
                return (snapshot, collection: new ObservableCollection<SteamCatalogItem>(snapshot), sources);
            }, token);
            token.ThrowIfCancellationRequested();
            _sourceApps = merged.sources;
            OnPropertyChanged(nameof(SourceFilterHint));

            if (merged.snapshot.Length > 0 || CatalogItems.Count == 0)
            {
                _catalogSnapshot = merged.snapshot;
                CatalogItems = merged.collection;
                RefreshPage();
            }

            var parts = new List<string>();
            if (steam.Succeeded) parts.Add($"{steam.Items.Count:N0} Steam");
            if (ryuu.Succeeded) parts.Add($"{ryuu.Items.Count:N0} Ryuu");
            if (hubcap.Succeeded) parts.Add($"{hubcap.Items.Count:N0} Hubcap");
            if (sushi.Succeeded) parts.Add($"{sushi.AppIds.Count:N0} Sushi (free)");
            if (zaza.Succeeded) parts.Add($"{zaza.AppIds.Count:N0} Zaza (free)");
            _lastCatalogLoad = DateTimeOffset.UtcNow;
            CatalogLoaded = steam.Succeeded || ryuu.Succeeded || hubcap.Succeeded || CatalogItems.Count > 0;
            CatalogStatus = CatalogItems.Count > 0 && parts.Count == 0
                ? $"Public sources unavailable; keeping {CatalogItems.Count:N0} cached games."
                : $"Merged {CatalogItems.Count:N0} games ({string.Join(" + ", parts)})";
            var latest = new[] { steam.UpdatedAt, ryuu.UpdatedAt, hubcap.UpdatedAt }.Max();
            UpdatedLabel = latest == DateTimeOffset.MinValue ? "Not loaded" : latest.LocalDateTime.ToString("dd.MM.yyyy HH:mm");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested) return;
            CatalogStatus = $"Games could not be loaded: {exception.GetType().Name}";
            if (CatalogItems.Count == 0 && Games.Count > 0)
            {
                _catalogSnapshot = Games.Select(game => new SteamCatalogItem
                {
                    AppId = game.AppId,
                    Name = game.Name,
                    CapsuleImageUrl = game.ArtworkUrl,
                    PortraitImageUrl = game.ArtworkUrl
                }).ToArray();
                CatalogItems = new ObservableCollection<SteamCatalogItem>(_catalogSnapshot);
                RefreshPage();
            }
        }
        finally
        {
            if (ReferenceEquals(_pageLifetime, lifetime) && !token.IsCancellationRequested)
            { IsCatalogLoading = false; OnPropertyChanged(string.Empty); }
        }
    }
    private void RefreshPage()
    {
        if (_pageLifetime.IsCancellationRequested)
        {
            _pageLifetime.Dispose();
            _pageLifetime = new CancellationTokenSource();
        }
        _searchDebounceTimer.Stop();
        _additionalVisible = 0;
        var generation = Interlocked.Increment(ref _filterGeneration);
        ++_renderGeneration;
        _artworkLoadingCancellation?.Cancel();
        _localFilterCancellation?.Cancel();
        _filterCancellation?.Cancel();
        _filterCancellation?.Dispose();
        _filterCancellation = new CancellationTokenSource();
        var cancellationToken = _filterCancellation.Token;
        var items = SelectedMode == "Your games" ? InstalledItems() : _catalogSnapshot;
        if (!ReferenceEquals(_indexedCatalog, items)) ReleaseSearchIndex();
        if (SelectedMode != "Your games" && !string.IsNullOrWhiteSpace(_search)
            && (!ReferenceEquals(_indexedCatalog, items) || _searchIndexTask is null))
        {
            _indexedCatalog = items;
            _searchIndexCancellation = new CancellationTokenSource();
            var indexToken = _searchIndexCancellation.Token;
            _searchIndexTask = Task.Run(() => new GameSearchIndex<SteamCatalogItem>(items, item => item.AppId, item => item.Name, indexToken), indexToken);
        }
        var search = _search;
        var typeFilter = _typeFilter;
        var sort = _sort;
        var nsfwScope = NsfwScope;
        var source = SelectedSourceFilter;
        IReadOnlySet<int>? ids = source == "Favorites" ? _favoriteApps
            : source == "Installed" ? Games.Where(game => game.InstallState != GameInstallState.NotInstalled).Select(game => game.AppId).ToHashSet()
            : _sourceApps.TryGetValue(source, out var available) ? available : null;
        IsSearchBusy = !string.IsNullOrWhiteSpace(search);
        _ = RefreshPageAsync(_searchIndexTask!, search, typeFilter, sort, nsfwScope, generation, source, ids, cancellationToken, items);
    }

    private void ReleaseSearchIndex()
    {
        _searchIndexCancellation?.Cancel(); _searchIndexCancellation?.Dispose(); _searchIndexCancellation = null;
        _searchIndexTask = null; _indexedCatalog = null;
    }

    private async Task RefreshPageAsync(
        Task<GameSearchIndex<SteamCatalogItem>>? searchIndexTask,
        string search,
        string typeFilter,
        string sort,
        LibraryNsfwScope nsfwScope,
        int generation, string source, IReadOnlySet<int>? sourceIds, CancellationToken cancellationToken, SteamCatalogItem[] snapshot, bool verifyUnknown = true)
    {
        try
        {
            bool typoMatch = false;
            var upcomingIds = _upcomingIds;
            var mode = SelectedMode;
            var genre = SelectedGenre;
            var recommendations = _recommendations;
            var releaseStatus = _discoveryReleaseStatus;
            SteamCatalogItem[] unclassified = [];
            Func<Task<IReadOnlyList<SteamCatalogItem>>> filterFunc = async () =>
            {
                IEnumerable<SteamCatalogItem> candidates = snapshot;
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var index = mode == "Your games"
                        ? new GameSearchIndex<SteamCatalogItem>(snapshot, item => item.AppId, item => item.Name, cancellationToken)
                        : await searchIndexTask!.WaitAsync(cancellationToken);
                    var matches = index.Find(search, cancellationToken);
                    candidates = matches.Items;
                    typoMatch = matches.IsTypoMatch;
                }
                candidates = candidates.Where(item => !GameClassification.IsAuxiliaryCatalogEntry(item.Name)
                    && (mode == "Browse all" || !upcomingIds.Contains(item.AppId)) && (source == "All sources" || sourceIds?.Contains(item.AppId) == true));
                if (mode == "For you") candidates = candidates.Where(item => recommendations.ContainsKey(item.AppId));
                if (genre != "All genres") candidates = candidates.Where(item => GameRecommendationRanker.Genres(item.GenresDisplay).Contains(genre, StringComparer.OrdinalIgnoreCase));
                // The public App ID list has no type information. Never label these
                // unknown entries as base games; verify only a bounded search batch.
                if (verifyUnknown && !string.IsNullOrWhiteSpace(search))
                    unclassified = candidates.Where(item => item.AppType == SteamCatalogAppType.Unknown).Take(24).ToArray();
                // Read the captured release feed during filtering instead of first
                // walking/mutating every model. Obsolete workers cannot overwrite it.
                var filtered = SteamCatalogQuery.FilterAndSort(candidates, null, "Game", sort, nsfwScope, cancellationToken, includeUpcoming: mode == "Browse all", releaseStatus: releaseStatus);
                return mode == "For you" ? filtered.OrderByDescending(item => recommendations[item.AppId].Score).ToArray() : filtered;
            };
            // Even a few thousand cards can stall input when sorting/genre checks run
            // on the dispatcher. Capture inputs above and do the CPU work off-thread.
            var list = await Task.Run(filterFunc, cancellationToken);
            if (generation != Volatile.Read(ref _filterGeneration)) return;

            IsTypoMatch = typoMatch;
            SearchHint = string.IsNullOrWhiteSpace(search) ? string.Empty : typoMatch
                ? "Similar titles shown — no exact matches."
                : list.Count == 0 ? "No matches. Try a shorter title or an App ID." : string.Empty;
            SearchSuggestions.ReplaceWith(string.IsNullOrWhiteSpace(search) ? Array.Empty<SteamCatalogItem>() : list.Where(item => item.IsReleaseVerified && !item.IsUpcoming).Take(6).ToArray());
            OnPropertyChanged(nameof(HasSearchSuggestions));
            _filteredCatalog = list;
            // Card publication begins immediately; optional HTTP/artwork work owns
            // its own cancellation/revision and must not hold search/local results busy.
            _ = RenderCatalogPageAsync();
            if (generation != Volatile.Read(ref _filterGeneration) || cancellationToken.IsCancellationRequested) return;
            RefreshLocalPage();
            if (unclassified.Length > 0) _ = VerifyBaseGameSearchAsync(unclassified, searchIndexTask, search, typeFilter, sort, nsfwScope, generation, source, sourceIds, cancellationToken, snapshot);
            foreach (var property in new[] { nameof(VisibleCountLabel), nameof(PageLabel), nameof(TotalPages), nameof(CanGoPrevious), nameof(CanGoNext), nameof(HasCatalogItems), nameof(FilteredCatalogCount), nameof(CatalogCountLabel) })
                OnPropertyChanged(property);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (generation != Volatile.Read(ref _filterGeneration)) return;
            Logging.Add(LogLevel.Debug, "Library", $"Filtering failed: {exception.GetType().Name}");
            CatalogStatus = "The library could not update. Please try again.";
            OnPropertyChanged(nameof(CatalogStatus));
        }
        finally { if (generation == Volatile.Read(ref _filterGeneration)) IsSearchBusy = false; }
    }

    private static bool MatchesType(SteamCatalogItem item, string type) => type switch
    {
        "Game" or "Games" => (item.AppType == SteamCatalogAppType.Game || item.IsUpcoming) && !GameClassification.IsAuxiliaryCatalogEntry(item.Name),
        "DLC" or "Dlc" => item.AppType == SteamCatalogAppType.Dlc,
        "Software" => item.AppType == SteamCatalogAppType.Software,
        "Video" => item.AppType == SteamCatalogAppType.Video,
        "Hardware" => item.AppType == SteamCatalogAppType.Hardware,
        "Music" => item.AppType == SteamCatalogAppType.Music,
        _ => true
    };

    private IReadOnlySet<int> _upcomingIds = new HashSet<int>();
    private IReadOnlyDictionary<int, bool> _discoveryReleaseStatus = new Dictionary<int, bool>();
    private void UpdateReleaseFilter(SpotlightSnapshot snapshot)
    {
        _upcomingIds = snapshot.Games.Where(game => game.ComingSoon).Select(game => game.AppId).ToHashSet();
        _discoveryReleaseStatus = snapshot.Games.ToDictionary(game => game.AppId, game => game.ComingSoon);
    }
    private async Task RefreshReleaseFilterAsync(bool force)
    {
        if (_spotlight is null || _pageLifetime.IsCancellationRequested) return;
        var token = _pageLifetime.Token;
        try
        {
            var snapshot = await Task.Run(() => _spotlight.GetAsync(force, token), token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            UpdateReleaseFilter(snapshot); RefreshPage();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Library", $"Keeping release metadata: {exception.GetType().Name}."); }
    }

    private int _renderGeneration;
    private async Task RenderCatalogPageAsync()
    {
        var renderGeneration = ++_renderGeneration;
        _artworkLoadingCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetime.Token);
        _artworkLoadingCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            FilteredCatalogCount = _filteredCatalog.Count;
            _page = Math.Clamp(_page, 1, TotalPages);
            var start = (_page - 1) * PageSize;
            var visible = _filteredCatalog.Skip(start).Take(PageSize + _additionalVisible).ToArray();
            foreach (var item in visible)
                if (_discoveryReleaseStatus.TryGetValue(item.AppId, out var comingSoon))
                {
                    item.IsUpcoming = comingSoon;
                    item.IsReleaseVerified = true;
                }
            // Browsing the full catalog never waits for release metadata to show cards.
            // Artwork and details hydrate in the background, preserving stable card positions.
            if (SelectedMode == "Browse all")
            {
                var hydrate = GalleryHydrationCandidates(visible);
                PublishGallery(visible);
                SearchSuggestions.ReplaceWith(string.IsNullOrWhiteSpace(_search) ? Array.Empty<SteamCatalogItem>() : visible.Take(6).ToArray());
                OnPropertyChanged(string.Empty);
                await WaitForCardWorkAsync(Task.WhenAll(
                    Task.Run(() => _catalog.PrepareReleaseStatusAsync(hydrate.Where(item => !item.IsInstalled).ToArray(), token), token),
                    LoadVisibleArtworkAsync(visible.Where(item => item.AppId > 0).ToArray(), cancellation)), token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (renderGeneration != _renderGeneration) return;
                await HydrateCardDetailsAsync(hydrate, token);
                if (renderGeneration == _renderGeneration && !token.IsCancellationRequested) PruneNonGameCards();
                return;
            }
            PublishGallery(visible.Where(item => item.IsReleaseVerified && !item.IsUpcoming).ToArray());
            var unconfirmed = false;
            for (var attempt = 0; attempt < 3 && visible.Length > 0; attempt++)
            {
                await WaitForCardWorkAsync(Task.Run(() => _catalog.PrepareReleaseStatusAsync(visible.Where(item => !item.IsInstalled).ToArray(), token), token), token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (renderGeneration != _renderGeneration) return;
                unconfirmed |= visible.Any(item => !item.IsReleaseVerified);
                if (unconfirmed) break;
                var excluded = visible.Where(item => item.IsUpcoming || !item.IsReleaseVerified).Select(item => item.AppId).ToHashSet();
                if (excluded.Count == 0) break;
                _filteredCatalog = _filteredCatalog.Where(item => !excluded.Contains(item.AppId)).ToArray();
                visible = _filteredCatalog.Skip(start).Take(PageSize + _additionalVisible).ToArray();
            }
            var released = visible.Where(item => item.IsReleaseVerified && !item.IsUpcoming && !_upcomingIds.Contains(item.AppId)).ToArray();
            PublishGallery(released);
            SearchSuggestions.ReplaceWith(string.IsNullOrWhiteSpace(_search) ? Array.Empty<SteamCatalogItem>() : released.Take(6).ToArray());
            FilteredCatalogCount = _filteredCatalog.Count;
            OnPropertyChanged(string.Empty);
            if (unconfirmed)
                CatalogStatus = "Release status unavailable. Refresh to check Steam again.";
            await LoadVisibleArtworkAsync(released.Where(item => item.AppId > 0).ToArray(), cancellation);
            await HydrateCardDetailsAsync(released, token);
            if (renderGeneration == _renderGeneration && !token.IsCancellationRequested) PruneNonGameCards();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Library", $"Release metadata unavailable: {exception.GetType().Name}."); }
        finally
        {
            if (ReferenceEquals(_artworkLoadingCancellation, cancellation)) _artworkLoadingCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task LoadVisibleArtworkAsync(SteamCatalogItem[] visible, CancellationTokenSource cancellation)
    {
        // Realized tile leases hydrate the rest as the viewport moves. Never fan out
        // an entire 96/1,000-item data page into background downloads.
        visible = visible.Take(24).ToArray();
        var keep = visible.ToHashSet();
        foreach (var old in _loadedCatalogArtwork.Where(item => !keep.Contains(item)).ToArray())
        { old.ArtworkImage = null; old.HeaderImage = null; _loadedCatalogArtwork.Remove(old); }
        foreach (var item in visible) _loadedCatalogArtwork.Add(item);
        try
        {
            // Cached covers must not wait for a metadata round trip. Hydrate both concurrently.
            var token = cancellation.Token;
            await WaitForCardWorkAsync(Task.Run(() => _catalog.PrepareArtworkAsync(visible, token), token), token);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void RefreshLocalPage()
    {
        if (_pageLifetime.IsCancellationRequested) return;
        var search = _search;
        var sort = _sort;
        var page = _page;
        var pageSize = PageSize;
        var games = Games.ToArray();
        var generation = Volatile.Read(ref _filterGeneration);
        _localFilterCancellation?.Cancel();
        var cancellation = _localFilterCancellation = new CancellationTokenSource();
        _ = RefreshLocalPageAsync(games, search, sort, page, pageSize, generation, cancellation);
    }

    private async Task RefreshLocalPageAsync(Game[] games, string search, string sort, int page, int pageSize, int generation, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            var filtered = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                // Browsing/sorting an unfiltered library needs no normalized vocabulary
                // or fuzzy-word dictionaries. Preserve the same source order and items.
                IEnumerable<Game> query = string.IsNullOrWhiteSpace(search) ? games
                    : new GameSearchIndex<Game>(games, game => game.AppId, game => game.Name, token).Find(search, token).Items;

                query = sort == "Name A–Z" ? query.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
                    : sort == "App ID" ? query.OrderByDescending(game => game.AppId)
                    : query;
                var result = query.ToArray();
                token.ThrowIfCancellationRequested();
                return result;
            }, token);

            if (generation != Volatile.Read(ref _filterGeneration) || token.IsCancellationRequested
                || !ReferenceEquals(_localFilterCancellation, cancellation)) return;
            FilteredGames.ReplaceWith(filtered);
            PagedGames.ReplaceWith(filtered.Skip((page - 1) * pageSize).Take(pageSize));
            OnPropertyChanged(nameof(HasGames));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Library", $"Local filter unavailable: {exception.GetType().Name}."); }
        finally
        {
            if (ReferenceEquals(_localFilterCancellation, cancellation)) _localFilterCancellation = null;
            cancellation.Dispose();
        }
    }

    private void SetPage(int p)
    {
        _additionalVisible = 0;
        if (_searchDebounceTimer.IsEnabled)
        {
            _searchDebounceTimer.Stop();
            _page = Math.Max(1, p);
            RefreshPage();
            return;
        }

        _page = Math.Clamp(p, 1, TotalPages);
        _ = RenderCatalogPageAsync();
        RefreshLocalPage();
        OnPropertyChanged(string.Empty);
    }
}

public sealed class DepotsViewModel : ViewModelBase { private string _search=""; public DepotsViewModel(IAppDataStore s,INavigationService n,ILoggingService l):base(s,n,l){Depots=s.Depots;RefreshFilter();} public ObservableCollection<Depot> Depots{get;} public ObservableCollection<Depot> FilteredDepots{get;}=new(); public string SearchText{get=>_search;set{if(SetProperty(ref _search,value))RefreshFilter();}} public bool HasActiveFilters=>!string.IsNullOrWhiteSpace(SearchText); public string FilterSummary=>HasActiveFilters?"Search":"No filters applied"; public Depot? SelectedDepot{get;set;} public ICommand RefreshCommand=>new RelayCommand(RefreshFilter); public ICommand SelectAllCommand=>new RelayCommand(()=>{foreach(var x in Depots)x.Selected=true;}); public ICommand ClearSelectionCommand=>new RelayCommand(()=>{foreach(var x in Depots)x.Selected=false;}); private void RefreshFilter(){FilteredDepots.Clear();foreach(var x in Depots.Where(x=>string.IsNullOrWhiteSpace(SearchText)||x.Name.Contains(SearchText,StringComparison.OrdinalIgnoreCase)||x.DepotId.ToString().Contains(SearchText)))FilteredDepots.Add(x);}}

public sealed class ManifestViewModel : ViewModelBase { private string _search=""; private string _status="Nothing imported yet."; public ManifestViewModel(IAppDataStore s,INavigationService n,ILoggingService l,IManifestService _):base(s,n,l){Manifests=s.Manifests;RefreshFilter();} public ObservableCollection<Manifest> Manifests{get;} public ObservableCollection<Manifest> FilteredManifests{get;}=new(); public string SearchText{get=>_search;set{if(SetProperty(ref _search,value))RefreshFilter();}} public bool HasActiveFilters=>!string.IsNullOrWhiteSpace(SearchText); public string FilterSummary=>HasActiveFilters?"Search":"No filters applied"; public Manifest? SelectedManifest{get;set;} public string ImportStatus{get=>_status;private set=>SetProperty(ref _status,value);} public ICommand RefreshCommand=>new RelayCommand(RefreshFilter); public ICommand ValidateCommand=>new RelayCommand<Manifest>(_=>{}); public ICommand ExportCommand=>new RelayCommand<Manifest>(_=>{}); public ICommand DeleteCommand=>new RelayCommand<Manifest>(x=>{if(x!=null)Manifests.Remove(x);}); public Task ImportAsync(string path){return Task.CompletedTask;} private void RefreshFilter(){FilteredManifests.Clear();foreach(var x in Manifests.Where(x=>string.IsNullOrWhiteSpace(SearchText)||x.FileName.Contains(SearchText,StringComparison.OrdinalIgnoreCase)))FilteredManifests.Add(x);} public static (int AppId,int DepotId) ParseManifestFileName(string? name){if(string.IsNullOrWhiteSpace(name))return(0,0);var m=Regex.Match(name,@"^app_(\d+)_depot_(\d+)\.manifest$",RegexOptions.IgnoreCase);return m.Success&&int.TryParse(m.Groups[1].Value,out var a)&&int.TryParse(m.Groups[2].Value,out var d)?(a,d):(0,0);}}

public sealed class BranchesViewModel : ViewModelBase { public BranchesViewModel(IAppDataStore s,INavigationService n,ILoggingService l):base(s,n,l){Branches=s.Branches;} public ObservableCollection<Branch> Branches{get;} public ICommand RefreshCommand=>new RelayCommand(()=>{}); public ICommand RequestCommand=>new RelayCommand<Branch>(_=>{}); public string LastRefresh=>"Just now"; public bool HasActiveFilters=>false; public string FilterSummary=>"No filters applied"; }
public sealed class AchievementsViewModel : ViewModelBase { private string _search=""; public AchievementsViewModel(IAppDataStore s,INavigationService n,ILoggingService l):base(s,n,l){Achievements=s.Achievements;RefreshFilter();} public ObservableCollection<Achievement> Achievements{get;} public ObservableCollection<Achievement> FilteredAchievements{get;}=new(); public string[] Filters{get;}={"All achievements","Unlocked","Locked"}; public string SearchText{get=>_search;set{if(SetProperty(ref _search,value))RefreshFilter();}} public string SelectedFilter{get;set;}="All achievements"; public bool HasActiveFilters=>!string.IsNullOrWhiteSpace(SearchText)||SelectedFilter!="All achievements"; public string FilterSummary=>"No filters applied"; public int UnlockedCount=>Achievements.Count(x=>x.Unlocked); public int TotalCount=>Achievements.Count; public string Completion=>TotalCount==0?"0%":$"{UnlockedCount*100/TotalCount}%"; public string StatusMessage=>"Achievement data is only shown when a source is configured."; public bool HasAchievements=>FilteredAchievements.Count>0; public ICommand RefreshCommand=>new RelayCommand(RefreshFilter); private void RefreshFilter(){FilteredAchievements.Clear();foreach(var x in Achievements.Where(x=>string.IsNullOrWhiteSpace(SearchText)||x.Name.Contains(SearchText,StringComparison.OrdinalIgnoreCase)))FilteredAchievements.Add(x);}}

public sealed class LogsViewModel : ViewModelBase { public LogsViewModel(IAppDataStore s,INavigationService n,ILoggingService l):base(s,n,l){Logs=s.Logs;FilteredLogs=new();RefreshFilter();} public ObservableCollection<LogEntry> Logs{get;} public ObservableCollection<LogEntry> FilteredLogs{get;} public string SearchText{get;set;}=""; public string SelectedLevel{get;set;}="All levels"; public string[] Levels{get;}={"All levels","Info","Warning","Error","Debug"}; public bool HasActiveFilters=>false; public string FilterSummary=>"No filters applied"; public ICommand ClearCommand=>new RelayCommand(()=>{Logs.Clear();RefreshFilter();}); public ICommand RefreshCommand=>new RelayCommand(RefreshFilter); private void RefreshFilter(){FilteredLogs.Clear();foreach(var x in Logs)FilteredLogs.Add(x);}}


public sealed class ModFixesViewModel { public ObservableCollection<ModFix> Fixes{get;}=new(); public ModFix? SelectedFix{get;set;} public ICommand RefreshCommand=>new RelayCommand(()=>{}); public ICommand ValidateCommand=>new RelayCommand<ModFix>(_=>{}); public ICommand BackupCommand=>new RelayCommand<ModFix>(_=>{}); public ICommand ApplyCommand=>new RelayCommand<ModFix>(_=>{}); public ICommand ResetCommand=>new RelayCommand<ModFix>(_=>{}); }
public sealed class GameFixesViewModel : ViewModelBase
{
    private const int PageSize = 48;

    private readonly IFixCatalogService _fixesService;
    private readonly IArtworkService _artwork;
    private readonly LocalGameCatalog? _localCatalog;
    private readonly List<GameFixGameCard> _allCards = new();
    private string _searchText = string.Empty;
    private string _lastFetchSummary = "Not loaded yet.";
    private bool _isLoading;
    private int _page = 1;

    public GameFixesViewModel(
        IAppDataStore store,
        INavigationService navigation,
        ILoggingService logging,
        IFixCatalogService fixesService, IArtworkService artwork, LocalGameCatalog? localCatalog = null) : base(store, navigation, logging)
    {
        _fixesService = fixesService;
        _artwork = artwork;
        _localCatalog = localCatalog;
    }

    public ObservableCollection<GameFixGameCard> PagedGames { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _page = 1;
                RefreshPage();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public int FilteredCount => FilteredCards().Count();
    public int TotalPages => Math.Max(1, (FilteredCount + PageSize - 1) / PageSize);
    public string CatalogCountLabel => $"{FilteredCount:N0} games";
    public string PageLabel => $"{_page} / {TotalPages}";
    public bool HasGames => PagedGames.Count > 0;
    public string EmptyStateMessage => IsLoading
        ? "Loading…"
        : _allCards.Count == 0 ? _lastFetchSummary : "No games found. Try a different search.";
    public string VisibleCountLabel => $"{PagedGames.Count:N0} shown";
    public string LastFetchLabel => _lastFetchSummary;
    public bool CanGoPrevious => _page > 1;
    public bool CanGoNext => _page < TotalPages;

    public ICommand FetchCommand => new AsyncRelayCommand(FetchAsync, () => !IsLoading);
    public ICommand PreviousPageCommand => new RelayCommand(() => { _page--; RefreshPage(); }, () => CanGoPrevious);
    public ICommand NextPageCommand => new RelayCommand(() => { _page++; RefreshPage(); }, () => CanGoNext);

    public override Task OnNavigatedToAsync() => FetchAsync();

    private async Task FetchAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            IReadOnlyList<FixGame> local = Array.Empty<FixGame>();
            var localWarning = "";
            try { if (_localCatalog is not null) local = await _localCatalog.ReadFixesAsync(); }
            catch (Exception error) { localWarning = " · Local catalog unavailable: " + error.Message; }
            // Make local entries visible before waiting for the remote source.
            SetCards(local);
            RefreshPage();
            FixFeedSnapshot snapshot;
            try { snapshot = await _fixesService.GetFixesAsync(forceRefresh: true, cancellationToken: CancellationToken.None); }
            catch (Exception error) { snapshot = FixFeedSnapshot.Failure("Remote source unavailable: " + error.GetType().Name); }
            var games = snapshot.Games.Concat(local).GroupBy(game => game.AppId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new FixGame { AppId = group.Key, Name = group.Last().Name, Fixes = group.SelectMany(game => game.Fixes).ToArray() });
            SetCards(games);
            _lastFetchSummary = snapshot.Message + (local.Count > 0 ? $" · {local.Count} local games" : "") + localWarning;
            _page = 1;
            RefreshPage();
        }
        catch (Exception exception)
        {
            _lastFetchSummary = $"The fixes catalog could not be loaded: {exception.GetType().Name}.";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(string.Empty);
        }
    }

    private void SetCards(IEnumerable<FixGame> games)
    {
            _allCards.Clear();
            foreach (var game in games)
            {
                _allCards.Add(new GameFixGameCard
                {
                    AppId = game.AppId,
                    Name = game.Name,
                    FixCount = game.Fixes.Count,
                    CoverGlyph = PickGlyph(game.AppId),
                    Game = game
                });
            }

    }

    private IEnumerable<GameFixGameCard> FilteredCards()
    {
        var search = _searchText.Trim();
        return string.IsNullOrEmpty(search)
            ? _allCards
            : _allCards.Where(c =>
                c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.AppId.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static string PickGlyph(string appId)
    {
        if (!int.TryParse(appId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
            return "◆";
        return (id % 6) switch
        {
            0 => "◈",
            1 => "✦",
            2 => "★",
            3 => "☽",
            4 => "△",
            _ => "✧"
        };
    }

    private void RefreshPage()
    {
        var filtered = FilteredCards().ToList();
        _page = Math.Clamp(_page, 1, Math.Max(1, (filtered.Count + PageSize - 1) / PageSize));

        PagedGames.Clear();
        var pageCards = filtered.Skip((_page - 1) * PageSize).Take(PageSize).ToList();
        foreach (var card in pageCards)
            PagedGames.Add(card);

        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(EmptyStateMessage));
        OnPropertyChanged(nameof(CatalogCountLabel));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(VisibleCountLabel));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(LastFetchLabel));

        _ = LoadVisibleArtworkAsync(pageCards);
    }

    private async Task LoadVisibleArtworkAsync(List<GameFixGameCard> cards)
    {
        var semaphore = new SemaphoreSlim(4);
        var tasks = cards.Where(c => c.ArtworkImage is null).Select(async card =>
        {
            await semaphore.WaitAsync();
            try { await card.LoadArtworkAsync(_artwork); }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);
    }
}

public sealed class GameFixGameCard : UiObservableObject
{
    private System.Windows.Media.Imaging.BitmapImage? _artworkImage;
    private bool _isArtworkLoading;

    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int FixCount { get; init; }
    public string CoverGlyph { get; init; } = "◆";
    public FixGame? Game { get; init; }

    public string Summary => $"{FixCount} fix(es)";
    public string AppLabel => $"App {AppId}";
    public string ArtworkUrl => $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{AppId}/library_600x900_2x.jpg";

    public System.Windows.Media.Imaging.BitmapImage? ArtworkImage
    {
        get => _artworkImage;
        set { if (SetProperty(ref _artworkImage, value)) OnPropertyChanged(nameof(IsArtworkFallback)); }
    }

    public bool IsArtworkLoading
    {
        get => _isArtworkLoading;
        set => SetProperty(ref _isArtworkLoading, value);
    }

    public bool IsArtworkFallback => ArtworkImage is null;

    public async Task LoadArtworkAsync(IArtworkService artwork)
    {
        if (ArtworkImage is not null || IsArtworkLoading || !int.TryParse(AppId, out var appId)) return;
        IsArtworkLoading = true;
        try
        {
            var game = new Game { AppId = appId, Name = Name };
            await artwork.LoadAsync(game);
            ArtworkImage = (game.ArtworkImage ?? game.HeaderImage) as System.Windows.Media.Imaging.BitmapImage;
        }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Fix cover: {exception.Message}"); }
        finally { IsArtworkLoading = false; }
    }

}


public sealed class HubViewModel : ViewModelBase { public HubViewModel(IAppDataStore s,INavigationService n,ILoggingService l,ISettingsService _,IDownloadManager __,ISteamCatalogService ___,IDepotDownloaderCheckService ____):base(s,n,l){} public string StatusMessage=>"Repository status is available in the Games and Downloads pages."; public bool IsRefreshing=>false; public ObservableCollection<Game> ZazaHubGames{get;}=new(); public ICommand RefreshCommand=>new RelayCommand(()=>{}); public ICommand DownloadGameCommand=>new RelayCommand<Game>(_=>{}); }
