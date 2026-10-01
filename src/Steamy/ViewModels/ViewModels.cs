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

public sealed class LibraryViewModel : ViewModelBase
{
    private readonly ISteamCatalogService _catalog; private readonly IRyuuCatalogService _ryuu; private readonly IHubcapCatalogService _hubcap; private readonly ILibrarySyncService _librarySync;
    private readonly DispatcherTimer _searchDebounceTimer;
    private CancellationTokenSource? _artworkLoadingCancellation;
    private IReadOnlyList<SteamCatalogItem> _filteredCatalog = Array.Empty<SteamCatalogItem>();
    private SteamCatalogItem[] _catalogSnapshot = Array.Empty<SteamCatalogItem>();
    private SteamCatalogItem[]? _indexedCatalog;
    private Task<GameSearchIndex<SteamCatalogItem>>? _searchIndexTask;
    private readonly SearchHistoryStore _searchHistory;
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
    private DateTimeOffset _lastLibraryScan;
    private string _sourceFilter = "All sources";
    private IReadOnlyDictionary<string, string> _sourceMessages = new Dictionary<string, string>();
    public string SourceFilterHint => SelectedSourceFilter == "All sources" ? "Sushi and Zaza are free, without an API key."
        : SelectedSourceFilter == "Installed" ? "Apps found in your local Steam libraries."
        : SelectedSourceFilter == "Favorites" ? $"{_favoriteApps.Count:N0} favorites saved on this device."
        : _sourceMessages.TryGetValue(SelectedSourceFilter, out var message) ? message : "Source index is loading; you can still select a source in the download picker.";
    public string[] SourceFilters { get; } = { "All sources", "Favorites", "Sushi", "Zaza", "Ryuu", "Hubcap", "Installed" };
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
    private string _search=""; private string _sort="Popular (AAA)"; private string _typeFilter="All games"; private int _page=1; private int _pageSize=48; private LibraryNsfwScope _nsfwScope=LibraryNsfwScope.Hide;
    public const string RyuuSource="Available (Ryuu)";
    public LibraryViewModel(IAppDataStore s, INavigationService n, ILoggingService l, ISteamCatalogService c, IRyuuCatalogService ryuu, IArtworkService artwork, ILibrarySyncService sync, IHubcapCatalogService hubcap, IFreeManifestCatalogService freeSources, SearchHistoryStore? searchHistory = null, IGameActivityService? activity = null) : base(s,n,l)
    {
        _catalog=c; _ryuu=ryuu; _hubcap=hubcap; _librarySync=sync; _freeSources=freeSources;
        _searchHistory = searchHistory ?? SearchHistoryStore.Default;
        _activity = activity;
        if (_activity is not null) _activity.Changed += OnFavoriteActivityChanged;
        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        _searchDebounceTimer.Tick += OnSearchDebounceElapsed;
        UseRecentSearchCommand = new RelayCommand<string>(query => { if (query is not null) SearchText = query; });
        ClearSearchHistoryCommand = new AsyncRelayCommand(ClearSearchHistoryAsync);
        SubmitSearchCommand = new RelayCommand(() => { RefreshPage(); _ = RememberSearchAsync(); });
        SelectSearchSuggestionCommand = new RelayCommand<SteamCatalogItem>(item =>
        {
            if (item is null) return;
            _ = RememberSearchAsync();
            SearchText = item.AppId.ToString(CultureInfo.InvariantCulture);
            RefreshPage();
        });
        _ = LoadRecentSearchesAsync();
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
        if (_activity is null) return;
        try
        {
            var favorites = (await _activity.GetAsync()).FavoriteAppIds.ToHashSet();
            if (favorites.SetEquals(_favoriteApps)) return;
            _favoriteApps = favorites;
            if (SelectedSourceFilter == "Favorites") RefreshPage();
            OnPropertyChanged(nameof(SourceFilterHint));
        }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Library", $"Favorites unavailable: {exception.GetType().Name}."); }
    }
    private void OnSearchDebounceElapsed(object? sender, EventArgs e) { _searchDebounceTimer.Stop(); RefreshPage(); }
    public override Task OnNavigatedFromAsync()
    {
        _searchDebounceTimer.Stop();
        Interlocked.Increment(ref _filterGeneration);
        _filterCancellation?.Cancel();
        _artworkLoadingCancellation?.Cancel();
        IsSearchBusy = false;
        return Task.CompletedTask;
    }
    private ObservableCollection<SteamCatalogItem> _catalogItems = new();
    public ObservableCollection<Game> Games => Store.Games;
    public ObservableCollection<SteamCatalogItem> CatalogItems { get => _catalogItems; private set => SetProperty(ref _catalogItems, value); }
    public RangeObservableCollection<SteamCatalogItem> PagedCatalogItems { get; }=new(); public RangeObservableCollection<Game> FilteredGames { get; }=new(); public RangeObservableCollection<Game> PagedGames { get; }=new();
    public string[] Modes { get; }={"All games"}; public string[] TypeFilters { get; }={"All games","Game","DLC","Software","Video","Hardware","Music"}; public string[] SortOptions { get; }={"Popular (AAA)","Name A–Z","App ID"}; public int[] PageSizes { get; }={24,48,72};
    public const string HubcapSource="Hubcap";
    public string SelectedMode { get; set; }="All games";    public string SelectedTypeFilter { get=>_typeFilter; set { if(SetProperty(ref _typeFilter,value)) { _page=1; RefreshPage(); } } }
    public LibraryNsfwScope NsfwScope { get=>_nsfwScope; set { if(SetProperty(ref _nsfwScope,value)) { _page=1; RefreshPage(); } } }
    public string SelectedSort { get=>_sort; set { if(SetProperty(ref _sort,value)) RefreshPage(); } } public int PageSize { get=>_pageSize; set { if(SetProperty(ref _pageSize,value)) RefreshPage(); } }
    public string SearchText { get=>_search; set { if(SetProperty(ref _search,value ?? string.Empty)) { _page=1; OnPropertyChanged(nameof(HasActiveFilters)); OnPropertyChanged(nameof(FilterSummary)); OnPropertyChanged(nameof(HasSearchQuery)); OnPropertyChanged(nameof(ShowRecentSearches)); OnPropertyChanged(nameof(HasSearchSuggestions)); Interlocked.Increment(ref _filterGeneration); _filterCancellation?.Cancel(); _searchDebounceTimer.Stop(); _searchDebounceTimer.Start(); } } }
    public RangeObservableCollection<string> RecentSearches { get; } = new();
    public RangeObservableCollection<SteamCatalogItem> SearchSuggestions { get; } = new();
    public bool HasRecentSearches => RecentSearches.Count > 0;
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchText);
    public bool ShowRecentSearches => !HasSearchQuery && HasRecentSearches;
    public bool HasSearchSuggestions => HasSearchQuery && SearchSuggestions.Count > 0;
    public bool IsSearchBusy { get => _isSearchBusy; private set => SetProperty(ref _isSearchBusy, value); }
    public bool IsTypoMatch { get => _isTypoMatch; private set => SetProperty(ref _isTypoMatch, value); }
    public string SearchHint { get => _searchHint; private set => SetProperty(ref _searchHint, value); }
    public ICommand UseRecentSearchCommand { get; }
    public ICommand ClearSearchHistoryCommand { get; }
    public ICommand SubmitSearchCommand { get; }
    public ICommand SelectSearchSuggestionCommand { get; }
    public async Task RememberSearchAsync()
    {
        var query = SearchText.Trim();
        if (query.Length < 2) return;
        try { ApplyRecentSearches(await _searchHistory.RecordAsync(query)); }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Search", $"History could not be saved: {exception.GetType().Name}."); }
    }
    private async Task LoadRecentSearchesAsync()
    {
        try { ApplyRecentSearches(await _searchHistory.GetAsync()); }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Search", $"History unavailable: {exception.GetType().Name}."); }
    }
    private async Task ClearSearchHistoryAsync()
    {
        try { ApplyRecentSearches(await _searchHistory.ClearAsync()); }
        catch (Exception exception) { Logging.Add(LogLevel.Debug, "Search", $"History could not be cleared: {exception.GetType().Name}."); }
    }
    private void ApplyRecentSearches(IReadOnlyList<string> queries)
    {
        RecentSearches.ReplaceWith(queries);
        OnPropertyChanged(nameof(HasRecentSearches));
        OnPropertyChanged(nameof(ShowRecentSearches));
    }
    public bool IsCatalogLoading { get; private set; } public bool CatalogLoaded { get; private set; } public string CatalogStatus { get; private set; }="Load your Steam app list to begin."; public string LibraryMessage { get; private set; }="";
    public string CatalogCountLabel => $"{CatalogItems.Count:N0} games"; public string VisibleCountLabel=>$"Showing {PagedCatalogItems.Count} of {FilteredCatalogCount:N0}"; public string PageLabel=>$"Page {_page} of {TotalPages}"; public string UpdatedLabel { get; private set; }="Not loaded"; public int FilteredCatalogCount { get; private set; } public int TotalPages=>Math.Max(1,(FilteredCatalogCount+PageSize-1)/PageSize); public bool CanGoPrevious=>_page>1; public bool CanGoNext=>_page<TotalPages; public bool HasCatalogItems=>PagedCatalogItems.Count>0; public bool HasGames=>FilteredGames.Count>0;    public bool HasActiveFilters=>SelectedSourceFilter!="All sources"||!string.IsNullOrWhiteSpace(SearchText)||SelectedSort!="Popular (AAA)"||PageSize!=48||NsfwScope==LibraryNsfwScope.Show; public string FilterSummary=>HasActiveFilters?"Active filters":"No filters applied"; public string EmptyStateMessage=>"No games match this search.";
    public ICommand LoadCatalogCommand=>new AsyncRelayCommand(()=>LoadAsync(false)); public ICommand RefreshCatalogCommand=>new AsyncRelayCommand(async()=>{ try{await _librarySync.RefreshAsync(); RefreshLocalPage();}catch{} await LoadAsync(true); }); public ICommand ScanLibraryCommand=>new AsyncRelayCommand(async()=>{ try{await _librarySync.RefreshAsync();}catch{} RefreshLocalPage(); }); public ICommand FirstPageCommand=>new RelayCommand(()=>SetPage(1)); public ICommand PreviousPageCommand=>new RelayCommand(()=>SetPage(_page-1)); public ICommand NextPageCommand=>new RelayCommand(()=>SetPage(_page+1)); public ICommand LastPageCommand=>new RelayCommand(()=>SetPage(TotalPages)); public ICommand RefreshCommand=>new RelayCommand(RefreshPage); public ICommand OpenDownloadsCommand=>new RelayCommand(()=>Navigation.Navigate<DownloadsPage>()); public ICommand OpenFolderCommand=>new RelayCommand<Game>(_=>{}); public ICommand RefreshLocalCommand=>new RelayCommand(()=>{}); public ICommand OpenStoreCommand=>new RelayCommand(()=>{}); public IAsyncRelayCommand LoadScreenshotsCommand=>new AsyncRelayCommand(()=>Task.CompletedTask);
    // Home dashboard shortcuts + quick stats shown on the Games landing page.
    public ICommand OpenGameFixesCommand=>new RelayCommand(()=>Navigation.Navigate<GameFixesPage>());    public ICommand OpenSteamlessCommand=>new RelayCommand(()=>Navigation.Navigate<SteamlessPage>()); public ICommand OpenCreamInstallerCommand=>new RelayCommand(()=>Navigation.Navigate<CreamInstallerPage>()); public ICommand OpenDenuvoCommand=>new RelayCommand(()=>Navigation.Navigate<DenuvoGenerationPage>()); public ICommand OpenSettingsCommand=>new RelayCommand(()=>Navigation.Navigate<SettingsPage>());
    public int InstalledCount=>Games.Count(g=>g.InstallState==GameInstallState.Installed); public int LocalCount=>Games.Count; public int ActiveDownloadCount=>Store.Downloads.Count(x=>x.IsActive); public int QueuedCount=>Store.Downloads.Count(x=>x.State==DownloadJobState.Queued); public string TotalCatalogCount=>$"{CatalogItems.Count:N0}"; public string LocalLibraryLabel=>Games.Count==1?"1 installed app":$"{Games.Count:N0} installed apps"; public string DownloadsSummary=>ActiveDownloadCount>0?$"{ActiveDownloadCount} active · {QueuedCount} queued":QueuedCount>0?$"{QueuedCount} queued":"Queue empty";
    public override async Task OnNavigatedToAsync()
    {
        if (_navigating) return;
        _navigating = true;
        try
        {
            if (DateTimeOffset.UtcNow - _lastLibraryScan >= TimeSpan.FromSeconds(30))
            {
                _lastLibraryScan = DateTimeOffset.UtcNow;
                try { await _librarySync.RefreshAsync(); } catch { }
            }
            RefreshLocalPage();
            await RefreshFavoriteFilterAsync();
            await LoadRecentSearchesAsync();
            await LoadAsync(false);
            OnPropertyChanged(nameof(InstalledCount));
            OnPropertyChanged(nameof(LocalCount));
            OnPropertyChanged(nameof(ActiveDownloadCount));
            OnPropertyChanged(nameof(QueuedCount));
            OnPropertyChanged(nameof(TotalCatalogCount));
            OnPropertyChanged(nameof(LocalLibraryLabel));
            OnPropertyChanged(nameof(DownloadsSummary));
        }
        finally { _navigating = false; }
    }
    private static async Task<T> WithTimeout<T>(Task<T> task, T fallback, int ms=20000){try{using var cts=new CancellationTokenSource(ms); var delay=Task.Delay(ms,cts.Token); if(await Task.WhenAny(task,delay)==task){cts.Cancel(); return await task;} return fallback;}catch{return fallback;}}
    private async Task LoadAsync(bool force)
    {
        if (IsCatalogLoading) return;
        if (!force && CatalogLoaded && DateTimeOffset.UtcNow - _lastCatalogLoad < TimeSpan.FromMinutes(10))
        {
            RefreshPage();
            return;
        }
        IsCatalogLoading = true;
        OnPropertyChanged(nameof(IsCatalogLoading));
        try
        {
            var fail = SteamCatalogSnapshot.Failure("Timed out");
            var sushiTask = _freeSources.GetAsync("Sushi", force);
            var zazaTask = _freeSources.GetAsync("Zaza", force);
            var steamTask = WithTimeout(_catalog.GetCatalogAsync(force), fail);
            var steam = await steamTask;
            Task<SteamCatalogSnapshot> ryuuTask = Task.FromResult(SteamCatalogSnapshot.Failure("Timed out"));
            Task<SteamCatalogSnapshot> hubcapTask = Task.FromResult(SteamCatalogSnapshot.Failure("Timed out"));

            // Let the cached Steam catalog paint immediately instead of waiting for optional providers.
            if (CatalogItems.Count == 0 && steam.Succeeded && steam.Items.Count > 0)
            {
                var initial = await Task.Run(() =>
                {
                    var snapshot = steam.Items.ToArray();
                    return (snapshot, new ObservableCollection<SteamCatalogItem>(snapshot));
                });
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
                var localItems = await Task.Run(() => localGames.Select(game => new SteamCatalogItem
                {
                    AppId = game.AppId,
                    Name = game.Name,
                    CapsuleImageUrl = game.ArtworkUrl,
                    PortraitImageUrl = game.ArtworkUrl
                }).ToArray());
                var localCollection = await Task.Run(() => new ObservableCollection<SteamCatalogItem>(localItems));
                _catalogSnapshot = localItems;
                CatalogItems = localCollection;
                RefreshPage();
            }

            ryuuTask = WithTimeout(Task.Run(() => _ryuu.GetGamesAsync(force)), SteamCatalogSnapshot.Failure("Timed out"));
            hubcapTask = WithTimeout(Task.Run(() => _hubcap.GetGamesAsync(force)), SteamCatalogSnapshot.Failure("Timed out"));
            await Task.WhenAll(ryuuTask, hubcapTask);
            var ryuu = await ryuuTask;
            var hubcap = await hubcapTask;
            var unavailable = new FreeManifestIndex(false, new HashSet<int>(), "Source index unavailable.");
            var sushi = await WithTimeout(sushiTask, unavailable);
            var zaza = await WithTimeout(zazaTask, unavailable);
            _sourceMessages = new Dictionary<string, string>
            {
                ["Sushi"] = sushi.Message, ["Zaza"] = zaza.Message,
                ["Ryuu"] = ryuu.Message, ["Hubcap"] = hubcap.Message
            };
            var merged = await Task.Run(() =>
            {
                var mergedItems = new Dictionary<int, SteamCatalogItem>();
                foreach (var item in steam.Items) mergedItems[item.AppId] = item;
                foreach (var item in ryuu.Items) if (!mergedItems.ContainsKey(item.AppId)) mergedItems[item.AppId] = item;
                foreach (var item in hubcap.Items) if (!mergedItems.ContainsKey(item.AppId)) mergedItems[item.AppId] = item;
                foreach (var id in sushi.AppIds.Concat(zaza.AppIds))
                    if (!mergedItems.ContainsKey(id)) mergedItems[id] = new SteamCatalogItem { AppId = id, Name = $"App {id}" };
                var snapshot = mergedItems.Values.ToArray();
                var sources = new Dictionary<string, IReadOnlySet<int>>
                {
                    ["Sushi"] = sushi.AppIds, ["Zaza"] = zaza.AppIds,
                    ["Ryuu"] = ryuu.Items.Select(item => item.AppId).ToHashSet(),
                    ["Hubcap"] = hubcap.Items.Select(item => item.AppId).ToHashSet()
                };
                return (snapshot, collection: new ObservableCollection<SteamCatalogItem>(snapshot), sources);
            });
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
        catch (Exception exception)
        {
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
            IsCatalogLoading = false;
            OnPropertyChanged(string.Empty);
        }
    }
    private void RefreshPage()
    {
        _searchDebounceTimer.Stop();
        var generation = Interlocked.Increment(ref _filterGeneration);
        _filterCancellation?.Cancel();
        _filterCancellation?.Dispose();
        _filterCancellation = new CancellationTokenSource();
        var cancellationToken = _filterCancellation.Token;
        var items = _catalogSnapshot;
        if (!ReferenceEquals(_indexedCatalog, items))
        {
            _indexedCatalog = items;
            _searchIndexTask = Task.Run(() => new GameSearchIndex<SteamCatalogItem>(items, item => item.AppId, item => item.Name));
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
        _ = RefreshPageAsync(_searchIndexTask!, search, typeFilter, sort, nsfwScope, generation, source, ids, cancellationToken);
    }

    private async Task RefreshPageAsync(
        Task<GameSearchIndex<SteamCatalogItem>> searchIndexTask,
        string search,
        string typeFilter,
        string sort,
        LibraryNsfwScope nsfwScope,
        int generation, string source, IReadOnlySet<int>? sourceIds, CancellationToken cancellationToken)
    {
        try
        {
            var index = await searchIndexTask.WaitAsync(cancellationToken);
            var matches = await Task.Run(() => index.Find(search, cancellationToken, item =>
                (source == "All sources" || sourceIds?.Contains(item.AppId) == true)
                && (nsfwScope != LibraryNsfwScope.Hide || !item.Nsfw)
                && MatchesType(item, typeFilter)), cancellationToken);
            var list = await Task.Run(() => SteamCatalogQuery.FilterAndSort(matches.Items,
                null, typeFilter, sort, nsfwScope, cancellationToken), cancellationToken);
            if (generation != Volatile.Read(ref _filterGeneration)) return;

            IsTypoMatch = matches.IsTypoMatch;
            SearchHint = string.IsNullOrWhiteSpace(search) ? string.Empty : matches.IsTypoMatch
                ? "Similar titles shown — no exact matches."
                : list.Count == 0 ? "No matches. Try a shorter title or an App ID." : string.Empty;
            SearchSuggestions.ReplaceWith(string.IsNullOrWhiteSpace(search) ? Array.Empty<SteamCatalogItem>() : list.Take(6).ToArray());
            OnPropertyChanged(nameof(HasSearchSuggestions));
            _filteredCatalog = list;
            RenderCatalogPage();
            RefreshLocalPage();
            foreach (var property in new[] { nameof(VisibleCountLabel), nameof(PageLabel), nameof(TotalPages), nameof(CanGoPrevious), nameof(CanGoNext), nameof(HasCatalogItems), nameof(FilteredCatalogCount) })
                OnPropertyChanged(property);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (generation != Volatile.Read(ref _filterGeneration)) return;
            CatalogStatus = $"Games could not be filtered: {exception.GetType().Name}";
            OnPropertyChanged(nameof(CatalogStatus));
        }
        finally { if (generation == Volatile.Read(ref _filterGeneration)) IsSearchBusy = false; }
    }

    private static bool MatchesType(SteamCatalogItem item, string type) => type switch
    {
        "Game" or "Games" => item.AppType == SteamCatalogAppType.Game,
        "DLC" or "Dlc" => item.AppType == SteamCatalogAppType.Dlc,
        "Software" => item.AppType == SteamCatalogAppType.Software,
        "Video" => item.AppType == SteamCatalogAppType.Video,
        "Hardware" => item.AppType == SteamCatalogAppType.Hardware,
        "Music" => item.AppType == SteamCatalogAppType.Music,
        _ => true
    };

    private void RenderCatalogPage()
    {
        FilteredCatalogCount = _filteredCatalog.Count;
        _page = Math.Clamp(_page, 1, TotalPages);
        var start = (_page - 1) * PageSize;
        var end = Math.Min(start + PageSize, _filteredCatalog.Count);
        var visible = Enumerable.Range(start, end - start).Select(index => _filteredCatalog[index]).ToArray();
        if (!PagedCatalogItems.ReplaceWith(visible)) return;

        _artworkLoadingCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _artworkLoadingCancellation = cancellation;
        _ = LoadVisibleArtworkAsync(PagedCatalogItems.ToArray(), cancellation);
    }

    private async Task LoadVisibleArtworkAsync(SteamCatalogItem[] visible, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.WhenAll(visible.Select(item => _catalog.EnsureArtworkAsync(item, cancellationToken: cancellation.Token)));
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (ReferenceEquals(_artworkLoadingCancellation, cancellation)) _artworkLoadingCancellation = null;
            cancellation.Dispose();
        }
    }

    private void RefreshLocalPage()
    {
        var search = _search;
        var sort = _sort;
        var page = _page;
        var pageSize = PageSize;
        var games = Games.ToArray();
        var generation = Volatile.Read(ref _filterGeneration);
        _ = RefreshLocalPageAsync(games, search, sort, page, pageSize, generation);
    }

    private async Task RefreshLocalPageAsync(Game[] games, string search, string sort, int page, int pageSize, int generation)
    {
        var filtered = await Task.Run(() =>
        {
            IEnumerable<Game> query = new GameSearchIndex<Game>(games, game => game.AppId, game => game.Name).Find(search).Items;

            query = sort == "Name A–Z" ? query.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
                : sort == "App ID" ? query.OrderByDescending(game => game.AppId)
                : query;
            return query.ToArray();
        });

        if (generation != Volatile.Read(ref _filterGeneration)) return;
        FilteredGames.ReplaceWith(filtered);
        PagedGames.ReplaceWith(filtered.Skip((page - 1) * pageSize).Take(pageSize));
        OnPropertyChanged(nameof(HasGames));
    }

    private void SetPage(int p)
    {
        if (_searchDebounceTimer.IsEnabled)
        {
            _searchDebounceTimer.Stop();
            _page = Math.Max(1, p);
            RefreshPage();
            return;
        }

        _page = Math.Clamp(p, 1, TotalPages);
        RenderCatalogPage();
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
    private readonly List<GameFixGameCard> _allCards = new();
    private string _searchText = string.Empty;
    private string _lastFetchSummary = "Not loaded yet.";
    private bool _isLoading;
    private int _page = 1;

    public GameFixesViewModel(
        IAppDataStore store,
        INavigationService navigation,
        ILoggingService logging,
        IFixCatalogService fixesService) : base(store, navigation, logging)
    {
        _fixesService = fixesService;
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
            var snapshot = await _fixesService.GetFixesAsync(forceRefresh: true, cancellationToken: CancellationToken.None);
            _allCards.Clear();
            foreach (var game in snapshot.Games)
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

            _lastFetchSummary = snapshot.Message;
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

    private static async Task LoadVisibleArtworkAsync(List<GameFixGameCard> cards)
    {
        var semaphore = new SemaphoreSlim(4);
        var tasks = cards.Where(c => c.ArtworkImage is null).Select(async card =>
        {
            await semaphore.WaitAsync();
            try { await card.LoadArtworkAsync(); }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);
    }
}

public sealed class GameFixGameCard : UiObservableObject
{
    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");
        return client;
    });
    private static readonly string CacheDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Steamy", "artwork");

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

    public async Task LoadArtworkAsync()
    {
        if (ArtworkImage is not null || string.IsNullOrWhiteSpace(AppId)) return;
        IsArtworkLoading = true;
        try
        {
            var cachePath = System.IO.Path.Combine(CacheDir, $"{AppId}_library.jpg");
            byte[]? bytes = null;

            if (System.IO.File.Exists(cachePath))
            {
                bytes = await System.IO.File.ReadAllBytesAsync(cachePath);
            }
            else
            {
                using var response = await SharedClient.Value.GetAsync(ArtworkUrl, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                {
                    bytes = await response.Content.ReadAsByteArrayAsync();
                    try
                    {
                        System.IO.Directory.CreateDirectory(CacheDir);
                        await System.IO.File.WriteAllBytesAsync(cachePath, bytes);
                    }
                    catch { }
                }
            }

            if (bytes is { Length: > 0 })
            {
                using var stream = new System.IO.MemoryStream(bytes, writable: false);
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.StreamSource = stream;
                img.EndInit();
                img.Freeze();
                ArtworkImage = img;
            }
        }
        catch { }
        finally
        {
            IsArtworkLoading = false;
        }
    }
}


public sealed class HubViewModel : ViewModelBase { public HubViewModel(IAppDataStore s,INavigationService n,ILoggingService l,ISettingsService _,IDownloadManager __,ISteamCatalogService ___,IDepotDownloaderCheckService ____):base(s,n,l){} public string StatusMessage=>"Repository status is available in the Games and Downloads pages."; public bool IsRefreshing=>false; public ObservableCollection<Game> ZazaHubGames{get;}=new(); public ICommand RefreshCommand=>new RelayCommand(()=>{}); public ICommand DownloadGameCommand=>new RelayCommand<Game>(_=>{}); }
