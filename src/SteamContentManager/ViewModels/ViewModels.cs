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
using Microsoft.Win32;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamContentManager.Models;
using SteamContentManager.Pages;
using SteamContentManager.Services;
using Wpf.Ui.Abstractions.Controls;

namespace SteamContentManager.ViewModels;

public abstract class ViewModelBase : ObservableObject, INavigationAware
{
    protected readonly IAppDataStore Store;
    protected readonly INavigationService Navigation;
    protected readonly ILoggingService Logging;
    protected ViewModelBase(IAppDataStore store, INavigationService navigation, ILoggingService logging) { Store = store; Navigation = navigation; Logging = logging; }
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
    public virtual Task OnNavigatedFromAsync() => Task.CompletedTask;
}

public sealed class DashboardViewModel : ViewModelBase
{
    private readonly IDiskSpaceService _diskSpace;
    private string _diskSpaceText = "Checking…";

    public DashboardViewModel(IAppDataStore s, INavigationService n, ILoggingService l, IArtworkService _, ILibrarySyncService __, IDiskSpaceService diskSpace, ISettingsService ____) : base(s,n,l) { _diskSpace = diskSpace; }
    public IReadOnlyList<Game> RecentGames => Store.Games.Take(12).ToArray();
    public string InstalledSummary => Store.Games.Count == 1 ? "1 game installed" : $"{Store.Games.Count} games installed";
    public Game? FeaturedGame => Store.Games.FirstOrDefault();
    public string LibraryCount => Store.Games.Count.ToString();
    public string ActiveDownloads => Store.Downloads.Count(x => x.IsActive).ToString();
    public string QueueCount => Store.Downloads.Count(x => x.State == DownloadJobState.Queued).ToString();
    public string QueueSummary => $"{QueueCount} queued next";
    public string CurrentSpeed => Store.Downloads.FirstOrDefault(x => x.IsActive)?.Speed ?? "—";
    public string DiskSpace { get => _diskSpaceText; private set => SetProperty(ref _diskSpaceText, value); }
    public string LibraryMessage => Store.Games.Count == 0 ? "No local Steam library found." : "Local library ready.";
    public string LibraryStatus => "LOCAL STEAM LIBRARY";
    public IReadOnlyList<ContentProvider> Providers => Store.Providers.ToArray();
    public string ProviderHealthSummary => $"{Store.Providers.Count(x => x.State == ProviderConnectionState.Healthy)} of {Store.Providers.Count} providers usable";
    public string DepotDownloaderStatus => "Configured in Settings";
    public string DepotDownloaderDetail => "Downloads run through the configured local DepotDownloader.";
    public string FeaturedSummary => FeaturedGame?.Size ?? "No game selected";
    public ICommand NavigateDownloadsCommand => new RelayCommand(() => Navigation.Navigate<DownloadsPage>());
    public ICommand NavigateLibraryCommand => new RelayCommand(() => Navigation.Navigate<LibraryPage>());
    public ICommand NavigateManifestsCommand => new RelayCommand(() => Navigation.Navigate<ManifestPage>());
    public ICommand NavigateSettingsCommand => new RelayCommand(() => Navigation.Navigate<SettingsPage>());
    public ICommand NavigateFixesCommand => new RelayCommand(() => Navigation.Navigate<GameFixesPage>());
    public ICommand NavigateOnlineFixesCommand => new RelayCommand(() => Navigation.Navigate<OnlineFixesPage>());
    public ICommand NavigateDlcUnlockerCommand => new RelayCommand(() => Navigation.Navigate<CreamApiPage>());
    public ICommand NavigateSteamlessCommand => new RelayCommand(() => Navigation.Navigate<SteamlessPage>());
    public ICommand NavigateDenuvoActivationCommand => new RelayCommand(() => Navigation.Navigate<DenuvoActivationPage>());
    public ICommand NavigateGoldbergCommand => new RelayCommand(() => Navigation.Navigate<GoldbergPage>());
    public ICommand RefreshCommand => new RelayCommand(() => OnPropertyChanged(string.Empty));

    public override async Task OnNavigatedToAsync()
    {
        try { DiskSpace = await _diskSpace.GetAvailableAsync(Path.GetTempPath()); }
        catch { DiskSpace = "Unknown"; }
    }
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
                        job.Progress = pct;
                        if (!string.IsNullOrWhiteSpace(p[5])) job.Downloaded = p[5];
                        if (!string.IsNullOrWhiteSpace(p[6])) job.TotalSize = p[6];
                        if (!string.IsNullOrWhiteSpace(p[7])) job.Speed = p[7];
                        if (!string.IsNullOrWhiteSpace(p[8])) job.Eta = p[8];
                        job.Status = $"Downloading depot {p[2]}/{p[3]} — {pct:0.#}%";
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
    private readonly ISteamCatalogService _catalog; private readonly IRyuuCatalogService _ryuu; private readonly IHubcapCatalogService _hubcap; private readonly ILibrarySyncService _librarySync;    private string _search=""; private string _sort="App ID"; private string _typeFilter="All games"; private int _page=1; private int _pageSize=48; private LibraryNsfwScope _nsfwScope=LibraryNsfwScope.Hide;
    public const string RyuuSource="Available (Ryuu)";
    public LibraryViewModel(IAppDataStore s, INavigationService n, ILoggingService l, ISteamCatalogService c, IRyuuCatalogService ryuu, IArtworkService _, ILibrarySyncService sync, IHubcapCatalogService hubcap) : base(s,n,l) { _catalog=c; _ryuu=ryuu; _hubcap=hubcap; _librarySync=sync; }
    public ObservableCollection<Game> Games => Store.Games; public ObservableCollection<SteamCatalogItem> CatalogItems { get; }=new(); public ObservableCollection<SteamCatalogItem> PagedCatalogItems { get; }=new(); public ObservableCollection<Game> FilteredGames { get; }=new(); public ObservableCollection<Game> PagedGames { get; }=new();
    public string[] Modes { get; }={"All games"}; public string[] TypeFilters { get; }={"All games","Game","DLC","Software","Video","Hardware","Music"}; public string[] SortOptions { get; }={"Popular (AAA)","Name A–Z","App ID"}; public int[] PageSizes { get; }={24,48,72};
    public const string HubcapSource="Hubcap";
    public string SelectedMode { get; set; }="All games";    public string SelectedTypeFilter { get=>_typeFilter; set { if(SetProperty(ref _typeFilter,value)) { _page=1; RefreshPage(); } } }
    public LibraryNsfwScope NsfwScope { get=>_nsfwScope; set { if(SetProperty(ref _nsfwScope,value)) { _page=1; RefreshPage(); } } }
    public string SelectedSort { get=>_sort; set { if(SetProperty(ref _sort,value)) RefreshPage(); } } public int PageSize { get=>_pageSize; set { if(SetProperty(ref _pageSize,value)) RefreshPage(); } }
    public string SearchText { get=>_search; set { if(SetProperty(ref _search,value)) RefreshPage(); } }
    public bool IsCatalogLoading { get; private set; } public bool CatalogLoaded { get; private set; } public string CatalogStatus { get; private set; }="Load your Steam app list to begin."; public string LibraryMessage { get; private set; }="";
    public string CatalogCountLabel => $"{CatalogItems.Count:N0} games"; public string VisibleCountLabel=>$"Showing {PagedCatalogItems.Count} of {FilteredCatalogCount:N0}"; public string PageLabel=>$"Page {_page} of {TotalPages}"; public string UpdatedLabel { get; private set; }="Not loaded"; public int FilteredCatalogCount { get; private set; } public int TotalPages=>Math.Max(1,(FilteredCatalogCount+PageSize-1)/PageSize); public bool CanGoPrevious=>_page>1; public bool CanGoNext=>_page<TotalPages; public bool HasCatalogItems=>PagedCatalogItems.Count>0; public bool HasGames=>FilteredGames.Count>0;    public bool HasActiveFilters=>!string.IsNullOrWhiteSpace(SearchText)||SelectedSort!="Popular (AAA)"||PageSize!=24||NsfwScope==LibraryNsfwScope.Show; public string FilterSummary=>HasActiveFilters?"Active filters":"No filters applied"; public string EmptyStateMessage=>"No games match this search.";
    public ICommand LoadCatalogCommand=>new AsyncRelayCommand(()=>LoadAsync(false)); public ICommand RefreshCatalogCommand=>new AsyncRelayCommand(async()=>{ try{_librarySync.Refresh(); RefreshLocalPage();}catch{} await LoadAsync(false); }); public ICommand ScanLibraryCommand=>new RelayCommand(()=>{ try{_librarySync.Refresh();}catch{} RefreshLocalPage(); }); public ICommand FirstPageCommand=>new RelayCommand(()=>SetPage(1)); public ICommand PreviousPageCommand=>new RelayCommand(()=>SetPage(_page-1)); public ICommand NextPageCommand=>new RelayCommand(()=>SetPage(_page+1)); public ICommand LastPageCommand=>new RelayCommand(()=>SetPage(TotalPages)); public ICommand RefreshCommand=>new RelayCommand(RefreshPage); public ICommand OpenDownloadsCommand=>new RelayCommand(()=>Navigation.Navigate<DownloadsPage>()); public ICommand OpenFolderCommand=>new RelayCommand<Game>(_=>{}); public ICommand RefreshLocalCommand=>new RelayCommand(()=>{}); public ICommand OpenStoreCommand=>new RelayCommand(()=>{}); public IAsyncRelayCommand LoadScreenshotsCommand=>new AsyncRelayCommand(()=>Task.CompletedTask);
    // Home dashboard shortcuts + quick stats shown on the Games landing page.
    public ICommand OpenOnlineFixesCommand=>new RelayCommand(()=>Navigation.Navigate<OnlineFixesPage>()); public ICommand OpenGameFixesCommand=>new RelayCommand(()=>Navigation.Navigate<GameFixesPage>()); public ICommand OpenSteamlessCommand=>new RelayCommand(()=>Navigation.Navigate<SteamlessPage>()); public ICommand OpenGoldbergCommand=>new RelayCommand(()=>Navigation.Navigate<GoldbergPage>()); public ICommand OpenCreamInstallerCommand=>new RelayCommand(()=>Navigation.Navigate<CreamInstallerPage>()); public ICommand OpenDenuvoCommand=>new RelayCommand(()=>Navigation.Navigate<DenuvoGenerationPage>()); public ICommand OpenSettingsCommand=>new RelayCommand(()=>Navigation.Navigate<SettingsPage>());
    public int InstalledCount=>Games.Count(g=>g.InstallState==GameInstallState.Installed); public int LocalCount=>Games.Count; public int ActiveDownloadCount=>Store.Downloads.Count(x=>x.IsActive); public int QueuedCount=>Store.Downloads.Count(x=>x.State==DownloadJobState.Queued); public string TotalCatalogCount=>$"{CatalogItems.Count:N0}"; public string LocalLibraryLabel=>Games.Count==1?"1 installed app":$"{Games.Count:N0} installed apps"; public string DownloadsSummary=>ActiveDownloadCount>0?$"{ActiveDownloadCount} active · {QueuedCount} queued":QueuedCount>0?$"{QueuedCount} queued":"Queue empty";
    public override async Task OnNavigatedToAsync(){ try{_librarySync.Refresh();}catch{} RefreshLocalPage(); await LoadAsync(false); OnPropertyChanged(nameof(InstalledCount)); OnPropertyChanged(nameof(LocalCount)); OnPropertyChanged(nameof(ActiveDownloadCount)); OnPropertyChanged(nameof(QueuedCount)); OnPropertyChanged(nameof(TotalCatalogCount)); OnPropertyChanged(nameof(LocalLibraryLabel)); OnPropertyChanged(nameof(DownloadsSummary)); }
    private static async Task<T> WithTimeout<T>(Task<T> task, T fallback, int ms=20000){try{using var cts=new CancellationTokenSource(ms); var delay=Task.Delay(ms,cts.Token); if(await Task.WhenAny(task,delay)==task){cts.Cancel(); return await task;} return fallback;}catch{return fallback;}}
    private async Task LoadAsync(bool force){if(IsCatalogLoading)return; IsCatalogLoading=true; OnPropertyChanged(nameof(IsCatalogLoading)); try { var fail=SteamCatalogSnapshot.Failure("Timed out"); var steamTask=WithTimeout(_catalog.GetCatalogAsync(force),fail); var ryuuTask=WithTimeout(_ryuu.GetGamesAsync(force),fail); var hubcapTask=WithTimeout(_hubcap.GetGamesAsync(force),fail); await Task.WhenAll(steamTask,ryuuTask,hubcapTask); var merged=new Dictionary<int,SteamCatalogItem>(); foreach(var item in steamTask.Result.Items) merged[item.AppId]=item; foreach(var item in ryuuTask.Result.Items) if(!merged.ContainsKey(item.AppId)) merged[item.AppId]=item; foreach(var item in hubcapTask.Result.Items) if(!merged.ContainsKey(item.AppId)) merged[item.AppId]=item; CatalogItems.Clear(); foreach(var item in merged.Values.OrderByDescending(x=>x.AppId))CatalogItems.Add(item); var parts=new List<string>(); if(steamTask.Result.Succeeded) parts.Add($"{steamTask.Result.Items.Count:N0} Steam"); if(ryuuTask.Result.Succeeded) parts.Add($"{ryuuTask.Result.Items.Count:N0} Ryuu"); if(hubcapTask.Result.Succeeded) parts.Add($"{hubcapTask.Result.Items.Count:N0} Hubcap"); CatalogLoaded=steamTask.Result.Succeeded||ryuuTask.Result.Succeeded||hubcapTask.Result.Succeeded; CatalogStatus=$"Merged {merged.Count:N0} games ({string.Join(" + ",parts)})"; var latest=new[]{steamTask.Result.UpdatedAt,ryuuTask.Result.UpdatedAt,hubcapTask.Result.UpdatedAt}.Max(); UpdatedLabel=latest==DateTimeOffset.MinValue?"Not loaded":latest.LocalDateTime.ToString("dd.MM.yyyy HH:mm"); RefreshPage(); } catch(Exception ex){CatalogStatus=$"Games could not be loaded: {ex.GetType().Name}"; if(CatalogItems.Count==0){foreach(var game in Games)CatalogItems.Add(new SteamCatalogItem{AppId=game.AppId,Name=game.Name,CapsuleImageUrl=game.ArtworkUrl,PortraitImageUrl=game.ArtworkUrl}); RefreshPage();}} finally{IsCatalogLoading=false;OnPropertyChanged(string.Empty);} }
    private void RefreshPage(){        var list=SteamCatalogQuery.FilterAndSort(CatalogItems,_search,_typeFilter,_sort,NsfwScope).ToList(); FilteredCatalogCount=list.Count; _page=Math.Clamp(_page,1,TotalPages); PagedCatalogItems.Clear(); foreach(var x in list.Skip((_page-1)*PageSize).Take(PageSize))PagedCatalogItems.Add(x); _ = LoadVisibleArtworkAsync(); RefreshLocalPage(); OnPropertyChanged(string.Empty); } private async Task LoadVisibleArtworkAsync(){var visible=PagedCatalogItems.ToArray(); try { await Task.WhenAll(visible.Select(item=>_catalog.EnsureArtworkAsync(item))); } catch(OperationCanceledException) { } catch { }} private void RefreshLocalPage(){var q=Games.Where(x=>string.IsNullOrWhiteSpace(SearchText)||x.Name.Contains(SearchText,StringComparison.OrdinalIgnoreCase)||x.AppId.ToString().Contains(SearchText)); q=_sort=="Name A–Z"?q.OrderBy(x=>x.Name):_sort=="App ID"?q.OrderByDescending(x=>x.AppId):q; FilteredGames.Clear(); foreach(var x in q)FilteredGames.Add(x); PagedGames.Clear(); foreach(var x in FilteredGames.Skip((_page-1)*PageSize).Take(PageSize))PagedGames.Add(x); OnPropertyChanged(nameof(PagedGames)); OnPropertyChanged(nameof(FilteredGames)); }    private void SetPage(int p){_page=Math.Clamp(p,1,TotalPages);RefreshPage();}
}

public sealed class DepotsViewModel : ViewModelBase { private string _search=""; public DepotsViewModel(IAppDataStore s,INavigationService n,ILoggingService l):base(s,n,l){Depots=s.Depots;RefreshFilter();} public ObservableCollection<Depot> Depots{get;} public ObservableCollection<Depot> FilteredDepots{get;}=new(); public string SearchText{get=>_search;set{if(SetProperty(ref _search,value))RefreshFilter();}} public bool HasActiveFilters=>!string.IsNullOrWhiteSpace(SearchText); public string FilterSummary=>HasActiveFilters?"Search":"No filters applied"; public Depot? SelectedDepot{get;set;} public ICommand RefreshCommand=>new RelayCommand(RefreshFilter); public ICommand SelectAllCommand=>new RelayCommand(()=>{foreach(var x in Depots)x.Selected=true;}); public ICommand ClearSelectionCommand=>new RelayCommand(()=>{foreach(var x in Depots)x.Selected=false;}); private void RefreshFilter(){FilteredDepots.Clear();foreach(var x in Depots.Where(x=>string.IsNullOrWhiteSpace(SearchText)||x.Name.Contains(SearchText,StringComparison.OrdinalIgnoreCase)||x.DepotId.ToString().Contains(SearchText)))FilteredDepots.Add(x);}}

public sealed class ManifestViewModel : ViewModelBase { private string _search=""; private string _status="Nothing imported yet."; public ManifestViewModel(IAppDataStore s,INavigationService n,ILoggingService l,IManifestService _):base(s,n,l){Manifests=s.Manifests;RefreshFilter();} public ObservableCollection<Manifest> Manifests{get;} public ObservableCollection<Manifest> FilteredManifests{get;}=new(); public string SearchText{get=>_search;set{if(SetProperty(ref _search,value))RefreshFilter();}} public bool HasActiveFilters=>!string.IsNullOrWhiteSpace(SearchText); public string FilterSummary=>HasActiveFilters?"Search":"No filters applied"; public Manifest? SelectedManifest{get;set;} public string ImportStatus{get=>_status;private set=>SetProperty(ref _status,value);} public ICommand RefreshCommand=>new RelayCommand(RefreshFilter); public ICommand ValidateCommand=>new RelayCommand<Manifest>(_=>{}); public ICommand ExportCommand=>new RelayCommand<Manifest>(_=>{}); public ICommand DeleteCommand=>new RelayCommand<Manifest>(x=>{if(x!=null)Manifests.Remove(x);}); public Task ImportAsync(string path){return Task.CompletedTask;} private void RefreshFilter(){FilteredManifests.Clear();foreach(var x in Manifests.Where(x=>string.IsNullOrWhiteSpace(SearchText)||x.FileName.Contains(SearchText,StringComparison.OrdinalIgnoreCase)))FilteredManifests.Add(x);} public static (int AppId,int DepotId) ParseManifestFileName(string? name){if(string.IsNullOrWhiteSpace(name))return(0,0);var m=Regex.Match(name,@"^app_(\d+)_depot_(\d+)\.manifest$",RegexOptions.IgnoreCase);return m.Success&&int.TryParse(m.Groups[1].Value,out var a)&&int.TryParse(m.Groups[2].Value,out var d)?(a,d):(0,0);}}

public sealed class BranchesViewModel : ViewModelBase { public BranchesViewModel(IAppDataStore s,INavigationService n,ILoggingService l):base(s,n,l){Branches=s.Branches;} public ObservableCollection<Branch> Branches{get;} public ICommand RefreshCommand=>new RelayCommand(()=>{}); public ICommand RequestCommand=>new RelayCommand<Branch>(_=>{}); public string LastRefresh=>"Just now"; public bool HasActiveFilters=>false; public string FilterSummary=>"No filters applied"; }
public sealed class AchievementsViewModel : ViewModelBase { private string _search=""; public AchievementsViewModel(IAppDataStore s,INavigationService n,ILoggingService l):base(s,n,l){Achievements=s.Achievements;RefreshFilter();} public ObservableCollection<Achievement> Achievements{get;} public ObservableCollection<Achievement> FilteredAchievements{get;}=new(); public string[] Filters{get;}={"All achievements","Unlocked","Locked"}; public string SearchText{get=>_search;set{if(SetProperty(ref _search,value))RefreshFilter();}} public string SelectedFilter{get;set;}="All achievements"; public bool HasActiveFilters=>!string.IsNullOrWhiteSpace(SearchText)||SelectedFilter!="All achievements"; public string FilterSummary=>"No filters applied"; public int UnlockedCount=>Achievements.Count(x=>x.Unlocked); public int TotalCount=>Achievements.Count; public string Completion=>TotalCount==0?"0%":$"{UnlockedCount*100/TotalCount}%"; public string StatusMessage=>"Achievement data is only shown when a source is configured."; public bool HasAchievements=>FilteredAchievements.Count>0; public ICommand RefreshCommand=>new RelayCommand(RefreshFilter); private void RefreshFilter(){FilteredAchievements.Clear();foreach(var x in Achievements.Where(x=>string.IsNullOrWhiteSpace(SearchText)||x.Name.Contains(SearchText,StringComparison.OrdinalIgnoreCase)))FilteredAchievements.Add(x);}}

public sealed class LogsViewModel : ViewModelBase { public LogsViewModel(IAppDataStore s,INavigationService n,ILoggingService l):base(s,n,l){Logs=s.Logs;FilteredLogs=new();RefreshFilter();} public ObservableCollection<LogEntry> Logs{get;} public ObservableCollection<LogEntry> FilteredLogs{get;} public string SearchText{get;set;}=""; public string SelectedLevel{get;set;}="All levels"; public string[] Levels{get;}={"All levels","Info","Warning","Error","Debug"}; public bool HasActiveFilters=>false; public string FilterSummary=>"No filters applied"; public ICommand ClearCommand=>new RelayCommand(()=>{Logs.Clear();RefreshFilter();}); public ICommand RefreshCommand=>new RelayCommand(RefreshFilter); private void RefreshFilter(){FilteredLogs.Clear();foreach(var x in Logs)FilteredLogs.Add(x);}}


public sealed class OnlineFixesViewModel : ViewModelBase
{
    private readonly IOnlineFixSearchService _search;
    public OnlineFixesViewModel() : base(new AppDataStore(), new LocalNavigation(), new LocalLogging()) { _search = new OnlineFixSearchService(); Games = Store.Games; }
    public OnlineFixesViewModel(IAppDataStore s, INavigationService n, ILoggingService l, IOnlineFixSearchService search) : base(s,n,l) { _search=search; Games=s.Games; }
    public ObservableCollection<Game> Games { get; }
    public ObservableCollection<OnlineFix> FilteredFixes { get; } = new();
    public ObservableCollection<OnlineFixSearchResult> GameSearchResults { get; } = new();
    public string SearchText { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public bool IsSearching { get; private set; }
    public bool HasOpenResult { get; private set; }
    public bool LastSearchFoundMatch { get; private set; }
    public string LastSearchSummary { get; private set; } = "Ready";
    public OnlineFixSearchResult? OpenResult { get; private set; }
    public string GamesSummary => $"{Games.Count} games";
    public ICommand OpenSiteCommand => new RelayCommand(()=>OpenUrl("https://online-fix.me"));
    public ICommand OpenLocationCommand => new RelayCommand(()=>{ });
    public ICommand SearchCommand => new AsyncRelayCommand(SearchAsync);
    public ICommand OpenLastResultCommand => new RelayCommand(()=>{ if(OpenResult is not null) OpenUrl(OpenResult.Url); });
    public ICommand ApplyFixCommand => new AsyncRelayCommand<Game>(async game=>{ if(game is not null){ SearchText=game.Name; await SearchAsync(); } });
    public ICommand SearchForGameCommand => new AsyncRelayCommand<OnlineFix>(async fix=>{ if(fix is not null){ SearchText=fix.GameName; await SearchAsync(); } });
    public ICommand ValidateCommand => new RelayCommand<OnlineFix>(_=>{ });
    private async Task SearchAsync(){ if(IsSearching)return; IsSearching=true; try { var game=Games.FirstOrDefault(x=>x.Name.Contains(SearchText,StringComparison.OrdinalIgnoreCase)); var result=await _search.SearchAsync(SearchText,game?.AppId??0); OpenResult=result; HasOpenResult=!string.IsNullOrWhiteSpace(result.Url); LastSearchFoundMatch=HasOpenResult; LastSearchSummary=HasOpenResult?"Match found":"No public match found"; } catch(Exception ex){LastSearchSummary=$"Search failed: {ex.GetType().Name}";} finally{IsSearching=false;OnPropertyChanged(string.Empty);} }
    private static void OpenUrl(string url){ try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch{} }
}
sealed class LocalNavigation : INavigationService { public void Attach(Action<Type> navigate){} public void Detach(){} public void Navigate<TPage>(){} }
sealed class LocalLogging : ILoggingService { public void Add(LogLevel level,string component,string message,int? appId=null,Guid? jobId=null){} }
public sealed class ModFixesViewModel { public ObservableCollection<ModFix> Fixes{get;}=new(); public ModFix? SelectedFix{get;set;} public ICommand RefreshCommand=>new RelayCommand(()=>{}); public ICommand ValidateCommand=>new RelayCommand<ModFix>(_=>{}); public ICommand BackupCommand=>new RelayCommand<ModFix>(_=>{}); public ICommand ApplyCommand=>new RelayCommand<ModFix>(_=>{}); public ICommand ResetCommand=>new RelayCommand<ModFix>(_=>{}); }
public sealed class GameFixesViewModel : ViewModelBase
{
    private readonly IRyuuFixesService _fixesService;
    private readonly IGameFixDownloadService _downloadService;
    private readonly IRyuuSecureDownloadService _ryuuSecure;
    private readonly ISettingsService _settings;
    private readonly ILoggingService _logging;
    private readonly INavigationService _navigation;

    private string _searchText = string.Empty;
    private bool _isLoading;
    private RyuuFixGame? _selectedGame;
    private string _installFolder = string.Empty;
    private string _statusMessage = "Fetch the available fixes to begin.";
    private string _lastFetchSummary = "Not loaded yet.";
    private int _selectedCount;
    private IReadOnlyList<GameFixItem> _items = Array.Empty<GameFixItem>();
    private int _page = 1;
    private int _pageSize = 48;
    private List<GameFixGameCard> _allCards = new();

    public GameFixesViewModel(
        IAppDataStore store,
        INavigationService navigation,
        ILoggingService logging,
        IRyuuFixesService fixesService,
        IGameFixDownloadService downloadService,
        IRyuuSecureDownloadService ryuuSecureDownloadService,
        ISettingsService settings) : base(store, navigation, logging)
    {
        _fixesService = fixesService;
        _downloadService = downloadService;
        _ryuuSecure = ryuuSecureDownloadService;
        _settings = settings;
        _logging = logging;
        _navigation = navigation;

        Games = new ObservableCollection<GameFixGameCard>();
        FilteredItems = new ObservableCollection<GameFixItem>();
    }

    public ObservableCollection<GameFixGameCard> Games { get; }
    public ObservableCollection<GameFixItem> FilteredItems { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _page = 1;
                RefreshPage();
                RefreshFilteredItems();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public RyuuFixGame? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetProperty(ref _selectedGame, value))
            {
                _items = value is null ? Array.Empty<GameFixItem>() : BuildItemLookup(value);
                ItemsSource = value;
                RefreshFilteredItems();
                UpdateSelectedGameSummary();
            }
        }
    }

    public string InstallFolder
    {
        get => _installFolder;
        set
        {
            if (SetProperty(ref _installFolder, value))
                OnPropertyChanged(nameof(HasInstallFolder));
        }
    }

    public bool HasInstallFolder => !string.IsNullOrWhiteSpace(InstallFolder) && Directory.Exists(InstallFolder);
    public bool IsSelectedGameSet => SelectedGame is not null;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string LastFetchSummary
    {
        get => _lastFetchSummary;
        private set => SetProperty(ref _lastFetchSummary, value);
    }

    public int SelectedCount
    {
        get; private set;
    }

    public bool HasSelectedItems => SelectedCount > 0;
    public bool CanFetch => !IsLoading;
    public bool CanDownloadSelected => HasSelectedItems && FilteredItems.Any(i => i.Selected && !i.IsDownloaded) && SelectedGame is not null;
    public bool CanApplySelected => HasSelectedItems && FilteredItems.Any(i => i.Selected && i.IsDownloaded && !i.IsApplied) && HasInstallFolder;
    public bool CanResetSelected => HasSelectedItems;

    public string SelectedGameLabel => SelectedGame is null ? "—" : $"{SelectedGame.Name}  ·  App ID {SelectedGame.AppId}";
    public string ItemsCountLabel => $"{FilteredItems.Count:N0} fixes";
    public string SelectedCountLabel => SelectedCount == 0 ? "No fixes selected" : $"{SelectedCount} selected";
    public string DownloadFolderLabel => _downloadService.DefaultDownloadFolder;
    public bool IsFolderPickerEnabled => SelectedGame is not null;

    public string[] Filters { get; } = { "All", "Not downloaded", "Downloaded", "Applied" };
    public string SelectedFilter { get; set; } = "All";

    public ObservableCollection<GameFixGameCard> PagedGames => Games;
    public int FilteredCount => _allCards.Count(c =>
    {
        var s = _searchText?.Trim();
        return string.IsNullOrEmpty(s) || c.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || c.AppId.Contains(s, StringComparison.OrdinalIgnoreCase);
    });
    public int TotalPages => Math.Max(1, (FilteredCount + _pageSize - 1) / _pageSize);
    public string CatalogCountLabel => $"{FilteredCount:N0} games";
    public string PageLabel => $"{_page} / {TotalPages}";
    public bool HasGames => Games.Count > 0;
    public string EmptyStateMessage => IsLoading ? "Loading…" : "No games found. Try a different search or refresh.";
    public string VisibleCountLabel => $"{Games.Count:N0} shown";
    public string LastFetchLabel => _lastFetchSummary;
    public bool CanGoPrevious => _page > 1;
    public bool CanGoNext => _page < TotalPages;

    public ICommand FetchCommand => new AsyncRelayCommand(FetchAsync, () => CanFetch);
    public ICommand RefreshCommand => new AsyncRelayCommand(FetchAsync, () => CanFetch);
    public ICommand PreviousPageCommand => new RelayCommand(() => { _page--; RefreshPage(); }, () => CanGoPrevious);
    public ICommand NextPageCommand => new RelayCommand(() => { _page++; RefreshPage(); }, () => CanGoNext);
    public ICommand SelectAllShownCommand => new RelayCommand(SelectAllShown, () => FilteredItems.Count > 0);
    public ICommand ClearSelectionCommand => new RelayCommand(ClearSelection, () => FilteredItems.Count > 0);
    public ICommand DownloadSelectedCommand => new AsyncRelayCommand(DownloadSelectedAsync, () => CanDownloadSelected);
    public ICommand ApplySelectedCommand => new AsyncRelayCommand(ApplySelectedAsync, () => CanApplySelected);
    public ICommand ResetSelectedCommand => new AsyncRelayCommand(ResetSelectedAsync, () => CanResetSelected);
    public ICommand BrowseFolderCommand => new RelayCommand(BrowseFolder, () => IsFolderPickerEnabled);
    public ICommand OpenFolderCommand => new RelayCommand(OpenFolder, () => HasInstallFolder);
    public ICommand ClearInstallFolderCommand => new RelayCommand(ClearInstallFolder, () => IsFolderPickerEnabled);
    public ICommand GoToDownloadsCommand => new RelayCommand(() => _navigation.Navigate<DownloadsPage>());

    public override async Task OnNavigatedToAsync()
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        await FetchAsync();
    }

    private async Task FetchAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusMessage = "Fetching available fixes from the Ryuu generator…";
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

            LastFetchSummary = snapshot.Succeeded
                ? $"Loaded {_allCards.Count:N0} games from the Ryuu generator."
                : snapshot.Message;
            StatusMessage = snapshot.Succeeded
                ? $"{_allCards.Count:N0} games available. Pick a game to see its fixes."
                : snapshot.Message;

            _page = 1;
            RefreshPage();
        }
        catch (Exception exception)
        {
            StatusMessage = $"The fixes feed could not be loaded: {exception.GetType().Name}.";
            LastFetchSummary = StatusMessage;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(DownloadFolderLabel));
            OnPropertyChanged(string.Empty);
        }
    }

    private static string MakeKey(string appId, RyuuFixEntry entry)
    {
        var safeName = string.IsNullOrWhiteSpace(entry.Filename) ? entry.Href : entry.Filename;
        return $"{appId}\\{safeName}";
    }

    private IReadOnlyList<GameFixItem> BuildItemLookup(RyuuFixGame game)
    {
        var items = new List<GameFixItem>(game.Fixes.Count);
        foreach (var entry in game.Fixes)
        {
            var id = MakeKey(game.AppId, entry);
            items.Add(new GameFixItem
            {
                Id = id,
                GameName = game.Name,
                AppId = int.TryParse(game.AppId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var aid) ? aid : 0,
                Source = "Ryuu generator",
                Type = PickFixType(entry),
                Name = PickFixName(entry, game.Name),
                Description = BuildFixDescription(entry),
                Version = string.Empty,
                FileName = entry.Filename,
                DownloadUrl = entry.Href,
                Size = entry.Size,
                Status = "Not downloaded"
            });
        }
        return items;
    }

    private void RefreshFilteredItems()
    {
        FilteredItems.Clear();
        if (ItemsSource is null) return;

        IEnumerable<RyuuFixEntry> query = ItemsSource.Fixes;
        var search = _searchText?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(f =>
                f.Filename.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                f.Href.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                f.Size.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var entries = query.ToList();
        var items = new List<GameFixItem>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
            items.Add(_items[i]);

        items = SelectedFilter switch
        {
            "Not downloaded" => items.Where(i => i.Status is "Not downloaded").ToList(),
            "Downloaded" => items.Where(i => i.Status is "Downloaded").ToList(),
            "Applied" => items.Where(i => i.Status is "Applied").ToList(),
            _ => items
        };

        foreach (var item in items)
            FilteredItems.Add(item);

        RecomputeSelection();
        OnPropertyChanged(nameof(ItemsCountLabel));
        OnPropertyChanged(nameof(CanDownloadSelected));
        OnPropertyChanged(nameof(CanApplySelected));
        OnPropertyChanged(nameof(CanResetSelected));
    }

    private RyuuFixGame? ItemsSource { get; set; }

    private void RecomputeSelection()
    {
        SelectedCount = FilteredItems.Count(i => i.Selected);
        OnPropertyChanged(nameof(HasSelectedItems));
        OnPropertyChanged(nameof(SelectedCountLabel));
    }

    private void SelectAllShown()
    {
        foreach (var item in FilteredItems)
            item.Selected = true;
        RecomputeSelection();
    }

    private void ClearSelection()
    {
        foreach (var item in FilteredItems)
            item.Selected = false;
        RecomputeSelection();
    }

    private async Task DownloadSelectedAsync()
    {
        if (SelectedGame is null) return;
        int.TryParse(SelectedGame.AppId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var appIdInt);

        var settings = _settings.Load();
        var authCode = settings.RyuuApiKey;
        if (string.IsNullOrWhiteSpace(authCode))
        {
            StatusMessage = "Set your Ryuu auth code in Settings before downloading.";
            return;
        }

        var toDownload = FilteredItems.Where(i => i.Selected && !i.IsDownloaded).ToList();
        if (toDownload.Count == 0)
        {
            StatusMessage = "No new fixes to download.";
            return;
        }

        StatusMessage = $"Downloading Ryuu archive for {SelectedGame.Name}…";

        var downloadProgress = new Progress<RyuuSecureDownloadProgress>(p =>
        {
            StatusMessage = $"Ryuu download: {p.Percent:0}% · {p.Downloaded} / {p.Total} · {p.Speed} · {p.Eta}";
            OnPropertyChanged(nameof(StatusMessage));
        });

        var ryuuResult = await _ryuuSecure.DownloadAsync(
            appIdInt,
            authCode,
            SelectedGame.Name,
            downloadProgress).ConfigureAwait(false);

        if (!ryuuResult.Succeeded)
        {
            StatusMessage = $"Ryuu download failed: {ryuuResult.Message}";
            _logging.Add(LogLevel.Warning, "GameFixes", $"Ryuu download failed for {SelectedGame.Name} (App ID {SelectedGame.AppId}): {ryuuResult.Message}", appIdInt);
            return;
        }

        foreach (var item in toDownload)
        {
            item.LocalPath = ryuuResult.ArchivePath;
            item.Status = "Downloaded";
        }

        _logging.Add(LogLevel.Info, "GameFixes", $"Ryuu archive downloaded for {SelectedGame.Name} (App ID {SelectedGame.AppId}) to {ryuuResult.ArchivePath}.", appIdInt);
        RefreshFilteredItems();
        StatusMessage = "Ryuu archive ready. Choose the game folder, then Apply to extract the fixes.";
        OnPropertyChanged(nameof(CanApplySelected));
    }

    private async Task ApplySelectedAsync()
    {
        if (!HasInstallFolder) return;
        if (SelectedGame is null) return;
        int.TryParse(SelectedGame.AppId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var appIdInt);

        var toApply = FilteredItems.Where(i => i.Selected && i.IsDownloaded && !i.IsApplied).ToList();
        if (toApply.Count == 0)
        {
            StatusMessage = "No downloaded fixes selected to apply.";
            return;
        }

        var archivePath = toApply.FirstOrDefault()?.LocalPath;
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            StatusMessage = "No Ryuu archive found for this game. Download it first.";
            return;
        }

        StatusMessage = $"Applying Ryuu fixes for {SelectedGame.Name} into {InstallFolder}…";

        var progress = new Progress<string>(m =>
        {
            StatusMessage = m;
            OnPropertyChanged(nameof(StatusMessage));
        });

        var applyResult = await _downloadService.ApplyArchiveAsync(
            archivePath,
            InstallFolder,
            progress).ConfigureAwait(false);

        if (applyResult.Succeeded)
        {
            foreach (var item in toApply)
            {
                item.LocalPath = applyResult.ExtractionRoot;
                item.Status = "Applied";
            }

            _logging.Add(LogLevel.Info, "GameFixes", $"Ryuu fixes applied for {SelectedGame.Name} (App ID {SelectedGame.AppId}) into {InstallFolder}.", appIdInt);
            RefreshFilteredItems();
            StatusMessage = $"Done. Ryuu fixes applied into {InstallFolder}.";
        }
        else
        {
            foreach (var item in toApply)
            {
                item.Status = applyResult.Message.Length <= 60 ? applyResult.Message : $"{applyResult.Message.Substring(0, 57)}…";
            }

            _logging.Add(LogLevel.Warning, "GameFixes", $"Apply failed for {SelectedGame.Name} (App ID {SelectedGame.AppId}): {applyResult.Message}", appIdInt);
            RefreshFilteredItems();
            StatusMessage = $"Apply failed: {applyResult.Message}";
        }

        OnPropertyChanged(nameof(CanApplySelected));
        OnPropertyChanged(nameof(CanResetSelected));
    }

    private async Task ResetSelectedAsync()
    {
        if (!HasInstallFolder) return;

        var toReset = FilteredItems.Where(i => i.Selected && i.IsApplied).ToList();
        if (toReset.Count == 0)
        {
            StatusMessage = "No applied fixes selected to reset.";
            return;
        }

        var anyReset = false;
        foreach (var item in toReset)
        {
            if (await _downloadService.ResetArchiveAsync(InstallFolder, CancellationToken.None).ConfigureAwait(false))
            {
                item.LocalPath = string.Empty;
                item.Status = "Not downloaded";
                anyReset = true;
            }
            else
            {
                item.Status = "Reset failed";
            }
        }

        if (anyReset)
            StatusMessage = $"Done. Applied fixes removed from {InstallFolder}.";
        else
            StatusMessage = "Reset failed. Check the status on each item.";

        RefreshFilteredItems();
        OnPropertyChanged(nameof(CanApplySelected));
        OnPropertyChanged(nameof(CanResetSelected));
    }

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = SelectedGame is null
                ? "Select game folder"
                : $"Select {SelectedGame.Name} folder",
            Multiselect = false,
            ValidateNames = true
        };

        if (dialog.ShowDialog(Application.Current.MainWindow) == true)
            InstallFolder = dialog.FolderName;
    }

    private void OpenFolder()
    {
        if (!HasInstallFolder) return;
        try
        {
            Process.Start(InstallFolder);
        }
        catch { }
    }

    private void ClearInstallFolder()
    {
        InstallFolder = string.Empty;
    }

    private void UpdateSelectedGameSummary()
    {
        OnPropertyChanged(nameof(SelectedGameLabel));
        OnPropertyChanged(nameof(IsFolderPickerEnabled));
        OnPropertyChanged(nameof(IsSelectedGameSet));
        RefreshFilteredItems();
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
            5 => "✧",
            _ => "◆"
        };
    }

    private static string PickFixType(RyuuFixEntry entry)
    {
        var lower = entry.Filename.ToLowerInvariant();
        return lower.Contains("bypass") || lower.Contains("crack") || lower.Contains("unlock") || lower.Contains("denuvo")
            ? "Bypass"
            : lower.Contains("online") || lower.Contains("multiplayer") || lower.Contains("fix") || lower.Contains("voices") || lower.Contains("patch")
                ? "Fix"
                : "Archive";
    }

    private static string PickFixName(RyuuFixEntry entry, string gameName)
    {
        if (!string.IsNullOrWhiteSpace(entry.Filename))
            return Path.GetFileNameWithoutExtension(entry.Filename);
        if (!string.IsNullOrWhiteSpace(entry.Href))
            return Path.GetFileNameWithoutExtension(entry.Href);
        return $"{gameName} fix";
    }

    private static string BuildFixDescription(RyuuFixEntry entry)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Size))
            parts.Add(entry.Size);
        if (entry.Badges.Count > 0)
            parts.Add(string.Join(", ", entry.Badges));
        return parts.Count > 0 ? string.Join(" · ", parts) : "Available from the Ryuu generator.";
    }

    private void RefreshPage()
    {
        var search = _searchText.Trim();
        var filtered = string.IsNullOrEmpty(search)
            ? _allCards
            : _allCards.Where(c =>
                c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.AppId.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        var filteredCount = filtered.Count;
        _page = Math.Clamp(_page, 1, Math.Max(1, (filteredCount + _pageSize - 1) / _pageSize));

        Games.Clear();
        var pageCards = filtered.Skip((_page - 1) * _pageSize).Take(_pageSize).ToList();
        foreach (var card in pageCards)
            Games.Add(card);

        OnPropertyChanged(nameof(Games));
        OnPropertyChanged(nameof(PagedGames));
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamContentManager/1.0");
        return client;
    });
    private static readonly string CacheDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamContentManager", "artwork");

    private System.Windows.Media.Imaging.BitmapImage? _artworkImage;
    private bool _isArtworkLoading;

    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int FixCount { get; init; }
    public string CoverGlyph { get; init; } = "◆";
    public RyuuFixGame? Game { get; init; }

    public string Summary => $"{FixCount} fix(es)";
    public string AppLabel => $"App {AppId}";
    public string ArtworkUrl => $"https://cdn.akamai.steamstatic.com/steam/apps/{AppId}/library_600x900_2x.jpg";

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
