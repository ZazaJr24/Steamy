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

public sealed class DownloadsViewModel : ViewModelBase
{
    private readonly IDownloadManager _manager; private readonly IRyuuGameDownloadService _ryuu; private readonly ISettingsService _settings; private string _search = ""; private string _filter = "All downloads";
    public DownloadsViewModel(IAppDataStore s, INavigationService n, ILoggingService l, IDownloadManager m, IRyuuGameDownloadService ryuu, ISettingsService settings) : base(s,n,l) { _manager=m; _ryuu=ryuu; _settings=settings; Settings=settings.Load(); Jobs=s.Downloads; RefreshFilter(); }
    public AppSettings Settings { get; private set; }
    public ObservableCollection<DownloadJob> Jobs { get; }
    public ObservableCollection<DownloadJob> FilteredJobs { get; } = new();
    public string[] Filters { get; } = { "All downloads", "Active", "Queued", "Completed", "Failed" };
    public string SearchText { get=>_search; set { if(SetProperty(ref _search,value)) RefreshFilter(); } }
    public string SelectedFilter { get=>_filter; set { if(SetProperty(ref _filter,value)) RefreshFilter(); } }
    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchText) || SelectedFilter != "All downloads";
    public string FilterSummary => HasActiveFilters ? "Active filters" : "No filters applied";
    public bool HasJobs => FilteredJobs.Count > 0;
    public string EmptyStateMessage => "No downloads yet. Select a game and choose Download.";
    public string TotalProgress => Jobs.Count == 0 ? "0%" : $"{Jobs.Average(x=>x.Progress):0}%";
    public string DownloadToolStatus => string.IsNullOrWhiteSpace(_settings.Load().DepotDownloaderPath) ? "DepotDownloader: configure it in Settings" : "DepotDownloader: ready for authorized downloads";
    public string QueueLimits => $"{_settings.Load().ParallelDownloads} parallel job(s) · {_settings.Load().RetryCount} retries";
    public ICommand PauseCommand => new AsyncRelayCommand<DownloadJob>(x=>x is null?Task.CompletedTask:_manager.PauseAsync(x));
    public ICommand ResumeCommand => new AsyncRelayCommand<DownloadJob>(ResumeAsync);
    public ICommand CancelCommand => new AsyncRelayCommand<DownloadJob>(x=>x is null?Task.CompletedTask:_manager.CancelAsync(x));
    public ICommand RetryCommand => new AsyncRelayCommand<DownloadJob>(x=>x is null?Task.CompletedTask:_manager.RetryAsync(x));
    public ICommand StartCommand => new AsyncRelayCommand<DownloadJob>(StartAsync);
    public ICommand VerifyCommand => new AsyncRelayCommand<DownloadJob>(async x=>{if(x is null)return; await _manager.VerifyAsync(x); RefreshFilter();});
    public ICommand RemoveCommand => new AsyncRelayCommand<DownloadJob>(async x=>{if(x is null)return; await _manager.ForgetAsync(x); Jobs.Remove(x); RefreshFilter();});
    public ICommand OpenFolderCommand => new RelayCommand<DownloadJob>(x=>{if(x is not null && !string.IsNullOrWhiteSpace(x.TargetFolder) && Directory.Exists(x.TargetFolder)) try{Process.Start(new ProcessStartInfo(x.TargetFolder){UseShellExecute=true});}catch{}});
    public ICommand RefreshCommand => new RelayCommand(RefreshFilter);
    public ICommand NavigateLibraryCommand => new RelayCommand(()=>Navigation.Navigate<LibraryPage>());
    private async Task StartAsync(DownloadJob? job) { if(job is null)return; await _manager.StartAsync(job); RefreshFilter(); }
    private async Task ResumeAsync(DownloadJob? job)
    {
        if (job is null) return;
        var mode = job.DownloadMode ?? "";
        if (mode.Contains("Ryuu", StringComparison.OrdinalIgnoreCase) || mode.Contains("Hubcap", StringComparison.OrdinalIgnoreCase))
        {
            job.State = DownloadJobState.Preparing;
            job.Status = "Resuming — loading cached manifests";
            var progress = new Progress<string>(msg => System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                if (msg.StartsWith("PROGRESS|", StringComparison.Ordinal))
                {
                    var p = msg.Split('|');
                    if (p.Length >= 10 && double.TryParse(p[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                    {
                        job.State = DownloadJobState.Downloading;
                        if (int.TryParse(p[2], out var dIdx) && int.TryParse(p[3], out var dTotal) && dTotal > 0)
                            job.Progress = ((dIdx - 1) * 100.0 + pct) / dTotal;
                        else
                            job.Progress = pct;
                        if (!string.IsNullOrWhiteSpace(p[5])) job.Downloaded = p[5];
                        if (!string.IsNullOrWhiteSpace(p[6])) job.TotalSize = p[6];
                        if (!string.IsNullOrWhiteSpace(p[7])) job.Speed = p[7];
                        if (!string.IsNullOrWhiteSpace(p[8])) job.Eta = p[8];
                        job.Status = $"Downloading depot {p[2]}/{p[3]} — {job.Progress:0.#}%";
                    }
                }
                else { job.Status = msg; }
            }));
            var result = await Task.Run(() => _ryuu.ResumeDownloadAsync(job.AppId, job.TargetFolder, progress));
            job.State = result.Succeeded ? DownloadJobState.Completed : DownloadJobState.Failed;
            job.Status = result.Message;
            if (result.Succeeded) { job.Progress = 100; job.Finished = DateTime.Now; }
        }
        else { await _manager.StartAsync(job); }
        RefreshFilter();
    }
    public int ActiveCount => Jobs.Count(x=>x.IsActive);
    public int CompletedCount => Jobs.Count(x=>x.State==DownloadJobState.Completed);
    public int QueuedJobCount => Jobs.Count(x=>x.State==DownloadJobState.Queued);
    public int FailedCount => Jobs.Count(x=>x.State==DownloadJobState.Failed);
    public override Task OnNavigatedToAsync(){ Settings=_settings.Load(); OnPropertyChanged(nameof(Settings)); RefreshFilter(); return Task.CompletedTask; }
    private void RefreshFilter() { Settings=_settings.Load(); OnPropertyChanged(nameof(Settings)); IEnumerable<DownloadJob> q=Jobs; if(!string.IsNullOrWhiteSpace(SearchText)) q=q.Where(x=>x.GameName.Contains(SearchText,StringComparison.OrdinalIgnoreCase)||x.AppId.ToString().Contains(SearchText)); q=SelectedFilter switch { "Active"=>q.Where(x=>x.IsActive),"Queued"=>q.Where(x=>x.State==DownloadJobState.Queued),"Completed"=>q.Where(x=>x.State==DownloadJobState.Completed),"Failed"=>q.Where(x=>x.State==DownloadJobState.Failed),_=>q}; FilteredJobs.Clear(); foreach(var x in q)FilteredJobs.Add(x); OnPropertyChanged(nameof(HasJobs)); OnPropertyChanged(nameof(TotalProgress)); OnPropertyChanged(nameof(ActiveCount)); OnPropertyChanged(nameof(CompletedCount)); OnPropertyChanged(nameof(QueuedJobCount)); OnPropertyChanged(nameof(FailedCount)); }
}

public enum LibraryNsfwScope { Hide, Show }

public sealed class LibraryViewModel : ViewModelBase
{
    private readonly ISteamCatalogService _catalog; private readonly IRyuuCatalogService _ryuu; private readonly IHubcapCatalogService _hubcap; private readonly ILibrarySyncService _librarySync;
    private readonly DispatcherTimer _searchDebounceTimer;
    private CancellationTokenSource? _artworkLoadingCancellation;
    private IReadOnlyList<SteamCatalogItem> _filteredCatalog = Array.Empty<SteamCatalogItem>();
    private SteamCatalogItem[] _catalogSnapshot = Array.Empty<SteamCatalogItem>();
    private int _filterGeneration;
    private string _search=""; private string _sort="Popular (AAA)"; private string _typeFilter="All games"; private int _page=1; private int _pageSize=48; private LibraryNsfwScope _nsfwScope=LibraryNsfwScope.Hide;
    public const string RyuuSource="Available (Ryuu)";
    public LibraryViewModel(IAppDataStore s, INavigationService n, ILoggingService l, ISteamCatalogService c, IRyuuCatalogService ryuu, IArtworkService _, ILibrarySyncService sync, IHubcapCatalogService hubcap) : base(s,n,l)
    {
        _catalog=c; _ryuu=ryuu; _hubcap=hubcap; _librarySync=sync;
        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounceTimer.Tick += OnSearchDebounceElapsed;
    }
    private void OnSearchDebounceElapsed(object? sender, EventArgs e) { _searchDebounceTimer.Stop(); RefreshPage(); }
    public override Task OnNavigatedFromAsync()
    {
        _searchDebounceTimer.Stop();
        Interlocked.Increment(ref _filterGeneration);
        _artworkLoadingCancellation?.Cancel();
        return Task.CompletedTask;
    }
    private ObservableCollection<SteamCatalogItem> _catalogItems = new();
    public ObservableCollection<Game> Games => Store.Games;
    public ObservableCollection<SteamCatalogItem> CatalogItems { get => _catalogItems; private set => SetProperty(ref _catalogItems, value); }
    public ObservableCollection<SteamCatalogItem> PagedCatalogItems { get; }=new(); public ObservableCollection<Game> FilteredGames { get; }=new(); public ObservableCollection<Game> PagedGames { get; }=new();
    public string[] Modes { get; }={"All games"}; public string[] TypeFilters { get; }={"All games","Game","DLC","Software","Video","Hardware","Music"}; public string[] SortOptions { get; }={"Popular (AAA)","Name A–Z","App ID"}; public int[] PageSizes { get; }={24,48,72};
    public const string HubcapSource="Hubcap";
    public string SelectedMode { get; set; }="All games";    public string SelectedTypeFilter { get=>_typeFilter; set { if(SetProperty(ref _typeFilter,value)) { _page=1; RefreshPage(); } } }
    public LibraryNsfwScope NsfwScope { get=>_nsfwScope; set { if(SetProperty(ref _nsfwScope,value)) { _page=1; RefreshPage(); } } }
    public string SelectedSort { get=>_sort; set { if(SetProperty(ref _sort,value)) RefreshPage(); } } public int PageSize { get=>_pageSize; set { if(SetProperty(ref _pageSize,value)) RefreshPage(); } }
    public string SearchText { get=>_search; set { if(SetProperty(ref _search,value)) { _page=1; OnPropertyChanged(nameof(HasActiveFilters)); OnPropertyChanged(nameof(FilterSummary)); _searchDebounceTimer.Stop(); _searchDebounceTimer.Start(); } } }
    public bool IsCatalogLoading { get; private set; } public bool CatalogLoaded { get; private set; } public string CatalogStatus { get; private set; }="Load your Steam app list to begin."; public string LibraryMessage { get; private set; }="";
    public string CatalogCountLabel => $"{CatalogItems.Count:N0} games"; public string VisibleCountLabel=>$"Showing {PagedCatalogItems.Count} of {FilteredCatalogCount:N0}"; public string PageLabel=>$"Page {_page} of {TotalPages}"; public string UpdatedLabel { get; private set; }="Not loaded"; public int FilteredCatalogCount { get; private set; } public int TotalPages=>Math.Max(1,(FilteredCatalogCount+PageSize-1)/PageSize); public bool CanGoPrevious=>_page>1; public bool CanGoNext=>_page<TotalPages; public bool HasCatalogItems=>PagedCatalogItems.Count>0; public bool HasGames=>FilteredGames.Count>0;    public bool HasActiveFilters=>!string.IsNullOrWhiteSpace(SearchText)||SelectedSort!="Popular (AAA)"||PageSize!=24||NsfwScope==LibraryNsfwScope.Show; public string FilterSummary=>HasActiveFilters?"Active filters":"No filters applied"; public string EmptyStateMessage=>"No games match this search.";
    public ICommand LoadCatalogCommand=>new AsyncRelayCommand(()=>LoadAsync(false)); public ICommand RefreshCatalogCommand=>new AsyncRelayCommand(async()=>{ try{_librarySync.Refresh(); RefreshLocalPage();}catch{} await LoadAsync(false); }); public ICommand ScanLibraryCommand=>new RelayCommand(()=>{ try{_librarySync.Refresh();}catch{} RefreshLocalPage(); }); public ICommand FirstPageCommand=>new RelayCommand(()=>SetPage(1)); public ICommand PreviousPageCommand=>new RelayCommand(()=>SetPage(_page-1)); public ICommand NextPageCommand=>new RelayCommand(()=>SetPage(_page+1)); public ICommand LastPageCommand=>new RelayCommand(()=>SetPage(TotalPages)); public ICommand RefreshCommand=>new RelayCommand(RefreshPage); public ICommand OpenDownloadsCommand=>new RelayCommand(()=>Navigation.Navigate<DownloadsPage>()); public ICommand OpenFolderCommand=>new RelayCommand<Game>(_=>{}); public ICommand RefreshLocalCommand=>new RelayCommand(()=>{}); public ICommand OpenStoreCommand=>new RelayCommand(()=>{}); public IAsyncRelayCommand LoadScreenshotsCommand=>new AsyncRelayCommand(()=>Task.CompletedTask);
    // Home dashboard shortcuts + quick stats shown on the Games landing page.
    public ICommand OpenGameFixesCommand=>new RelayCommand(()=>Navigation.Navigate<GameFixesPage>());    public ICommand OpenSteamlessCommand=>new RelayCommand(()=>Navigation.Navigate<SteamlessPage>()); public ICommand OpenCreamInstallerCommand=>new RelayCommand(()=>Navigation.Navigate<CreamInstallerPage>()); public ICommand OpenDenuvoCommand=>new RelayCommand(()=>Navigation.Navigate<DenuvoGenerationPage>()); public ICommand OpenSettingsCommand=>new RelayCommand(()=>Navigation.Navigate<SettingsPage>());
    public int InstalledCount=>Games.Count(g=>g.InstallState==GameInstallState.Installed); public int LocalCount=>Games.Count; public int ActiveDownloadCount=>Store.Downloads.Count(x=>x.IsActive); public int QueuedCount=>Store.Downloads.Count(x=>x.State==DownloadJobState.Queued); public string TotalCatalogCount=>$"{CatalogItems.Count:N0}"; public string LocalLibraryLabel=>Games.Count==1?"1 installed app":$"{Games.Count:N0} installed apps"; public string DownloadsSummary=>ActiveDownloadCount>0?$"{ActiveDownloadCount} active · {QueuedCount} queued":QueuedCount>0?$"{QueuedCount} queued":"Queue empty";
    public override async Task OnNavigatedToAsync(){ try{_librarySync.Refresh();}catch{} RefreshLocalPage(); await LoadAsync(false); OnPropertyChanged(nameof(InstalledCount)); OnPropertyChanged(nameof(LocalCount)); OnPropertyChanged(nameof(ActiveDownloadCount)); OnPropertyChanged(nameof(QueuedCount)); OnPropertyChanged(nameof(TotalCatalogCount)); OnPropertyChanged(nameof(LocalLibraryLabel)); OnPropertyChanged(nameof(DownloadsSummary)); }
    private static async Task<T> WithTimeout<T>(Task<T> task, T fallback, int ms=20000){try{using var cts=new CancellationTokenSource(ms); var delay=Task.Delay(ms,cts.Token); if(await Task.WhenAny(task,delay)==task){cts.Cancel(); return await task;} return fallback;}catch{return fallback;}}
    private async Task LoadAsync(bool force)
    {
        if (IsCatalogLoading) return;
        IsCatalogLoading = true;
        OnPropertyChanged(nameof(IsCatalogLoading));
        try
        {
            var fail = SteamCatalogSnapshot.Failure("Timed out");
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
                var localItems = await Task.Run(() => Games.Select(game => new SteamCatalogItem
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

            ryuuTask = WithTimeout(_ryuu.GetGamesAsync(force), SteamCatalogSnapshot.Failure("Timed out"));
            hubcapTask = WithTimeout(_hubcap.GetGamesAsync(force), SteamCatalogSnapshot.Failure("Timed out"));
            await Task.WhenAll(ryuuTask, hubcapTask);
            var ryuu = await ryuuTask;
            var hubcap = await hubcapTask;
            var merged = await Task.Run(() =>
            {
                var mergedItems = new Dictionary<int, SteamCatalogItem>();
                foreach (var item in steam.Items) mergedItems[item.AppId] = item;
                foreach (var item in ryuu.Items) if (!mergedItems.ContainsKey(item.AppId)) mergedItems[item.AppId] = item;
                foreach (var item in hubcap.Items) if (!mergedItems.ContainsKey(item.AppId)) mergedItems[item.AppId] = item;
                var snapshot = mergedItems.Values.ToArray();
                return (snapshot, new ObservableCollection<SteamCatalogItem>(snapshot));
            });

            if (merged.snapshot.Length > 0 || CatalogItems.Count == 0)
            {
                _catalogSnapshot = merged.snapshot;
                CatalogItems = merged.Item2;
                RefreshPage();
            }

            var parts = new List<string>();
            if (steam.Succeeded) parts.Add($"{steam.Items.Count:N0} Steam");
            if (ryuu.Succeeded) parts.Add($"{ryuu.Items.Count:N0} Ryuu");
            if (hubcap.Succeeded) parts.Add($"{hubcap.Items.Count:N0} Hubcap");
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
        var items = _catalogSnapshot;
        var search = _search;
        var typeFilter = _typeFilter;
        var sort = _sort;
        var nsfwScope = NsfwScope;
        _ = RefreshPageAsync(items, search, typeFilter, sort, nsfwScope, generation);
    }

    private async Task RefreshPageAsync(
        SteamCatalogItem[] items,
        string search,
        string typeFilter,
        string sort,
        LibraryNsfwScope nsfwScope,
        int generation)
    {
        try
        {
            var list = await Task.Run(() => SteamCatalogQuery.FilterAndSort(items, search, typeFilter, sort, nsfwScope));
            if (generation != Volatile.Read(ref _filterGeneration)) return;

            _filteredCatalog = list;
            RenderCatalogPage();
            RefreshLocalPage();
            OnPropertyChanged(string.Empty);
        }
        catch (Exception exception)
        {
            if (generation != Volatile.Read(ref _filterGeneration)) return;
            CatalogStatus = $"Games could not be filtered: {exception.GetType().Name}";
            OnPropertyChanged(nameof(CatalogStatus));
        }
    }

    private void RenderCatalogPage()
    {
        FilteredCatalogCount = _filteredCatalog.Count;
        _page = Math.Clamp(_page, 1, TotalPages);
        PagedCatalogItems.Clear();
        var start = (_page - 1) * PageSize;
        var end = Math.Min(start + PageSize, _filteredCatalog.Count);
        for (var index = start; index < end; index++) PagedCatalogItems.Add(_filteredCatalog[index]);

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
            IEnumerable<Game> query = games;
            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(game => game.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || game.AppId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            query = sort == "Name A–Z" ? query.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
                : sort == "App ID" ? query.OrderByDescending(game => game.AppId)
                : query;
            return query.ToArray();
        });

        if (generation != Volatile.Read(ref _filterGeneration)) return;
        FilteredGames.Clear(); foreach (var game in filtered) FilteredGames.Add(game);
        PagedGames.Clear(); foreach (var game in filtered.Skip((page - 1) * pageSize).Take(pageSize)) PagedGames.Add(game);
        OnPropertyChanged(nameof(PagedGames)); OnPropertyChanged(nameof(FilteredGames));
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
