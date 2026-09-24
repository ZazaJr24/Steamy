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
using CommunityToolkit.Mvvm.Input;
using SteamContentManager.Models;
using SteamContentManager.Pages;
using SteamContentManager.Services;

namespace SteamContentManager.ViewModels;

public sealed record DashboardStatusItem(string Title, string Detail, bool IsOk, string ActionLabel, ICommand Action);

public sealed class DashboardViewModel : ViewModelBase
{
    private const int RecentGameCount = 8;
    private const int JobPreviewCount = 4;
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromSeconds(3);

    private readonly ILibrarySyncService _librarySync;
    private readonly ISettingsService _settings;
    private readonly IDepotDownloaderCheckService _depotCheck;
    private readonly HashSet<DownloadJob> _watchedJobs = new();

    private SteamLibraryScanResult? _scan;
    private DepotDownloaderToolStatus? _depotStatus;
    private string? _depotStatusPath;
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _isRefreshing;
    private bool _libraryRefreshQueued;
    private bool _downloadRefreshQueued;
    private DownloadJob? _heroDownload;
    private Game? _heroGame;
    private int _heroImageAppId;
    private ImageSource? _heroImage;
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
        DownloadsViewModel downloadActions) : base(store, navigation, logging)
    {
        _librarySync = librarySync;
        _settings = settings;
        _depotCheck = depotCheck;
        DownloadActions = downloadActions;

        Store.Games.CollectionChanged += (_, _) => QueueLibraryRefresh();
        Store.Downloads.CollectionChanged += OnDownloadsChanged;
        foreach (var job in Store.Downloads) Watch(job);

        RefreshLibrary();
        RefreshDownloads();
        RefreshStatus();
    }

    /// <summary>Pause/resume/start go through the Downloads page's commands so both pages behave the same.</summary>
    public DownloadsViewModel DownloadActions { get; }

    public ObservableCollection<Game> RecentGames { get; } = new();
    public ObservableCollection<DownloadJob> ActiveJobs { get; } = new();

    // ---- header ---------------------------------------------------------------------------------

    public string Greeting => DateTime.Now.Hour switch
    {
        >= 5 and < 12 => "Good morning",
        >= 12 and < 18 => "Good afternoon",
        _ => "Good evening"
    };

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
        private set => SetProperty(ref _isRefreshing, value);
    }

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
            UpdateHeroImage();
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
            UpdateHeroImage();
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

    public ImageSource? HeroImage
    {
        get => _heroImage;
        private set => SetProperty(ref _heroImage, value);
    }

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
    public ICommand PlayGameCommand => new RelayCommand<Game>(PlayGame);
    public ICommand OpenGameFolderCommand => new RelayCommand<Game>(OpenGameFolder);
    public ICommand NavigateDownloadsCommand => new RelayCommand(() => Navigation.Navigate<DownloadsPage>());
    public ICommand NavigateLibraryCommand => new RelayCommand(() => Navigation.Navigate<LibraryPage>());
    public ICommand NavigateSettingsCommand => new RelayCommand(() => Navigation.Navigate<SettingsPage>());
    public ICommand NavigateFixesCommand => new RelayCommand(() => Navigation.Navigate<GameFixesPage>());
    public ICommand NavigateDlcUnlockerCommand => new RelayCommand(() => Navigation.Navigate<CreamApiPage>());
    public ICommand NavigateSteamlessCommand => new RelayCommand(() => Navigation.Navigate<SteamlessPage>());
    public ICommand NavigateDenuvoActivationCommand => new RelayCommand(() => Navigation.Navigate<DenuvoActivationPage>());
    public ICommand NavigateDepotDownloaderCommand => new RelayCommand(() => Navigation.Navigate<DepotDownloaderPage>());

    public override Task OnNavigatedToAsync() => RefreshAsync(force: false);

    public async Task RefreshAsync(bool force)
    {
        if (IsRefreshing || (!force && DateTime.UtcNow - _lastRefresh < RefreshDebounce)) return;
        IsRefreshing = true;
        _lastRefresh = DateTime.UtcNow;
        try
        {
            try { _scan = _librarySync.Refresh(); }
            catch (Exception exception) { _scan = SteamLibraryScanResult.Failure($"The Steam library could not be read ({exception.GetType().Name})."); }

            RefreshLibrary();
            RefreshStorage();
            RefreshDownloads();
            RefreshStatus();

            await RefreshDepotStatusAsync(force);
            RefreshStatus();
        }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(Greeting));
        }
    }

    // ---- refresh helpers ------------------------------------------------------------------------

    private void RefreshLibrary()
    {
        var recent = Store.Games
            .OrderByDescending(game => game.LastUpdated ?? DateTime.MinValue)
            .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .Take(RecentGameCount)
            .ToList();

        if (!recent.SequenceEqual(RecentGames))
        {
            RecentGames.Clear();
            foreach (var game in recent) RecentGames.Add(game);
        }

        HeroGame = recent.FirstOrDefault();
        OnPropertyChanged(nameof(HasRecentGames));
        OnPropertyChanged(nameof(ShowNoGames));
        OnPropertyChanged(nameof(NoGamesDetail));
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(InstalledSummary));
        OnPropertyChanged(nameof(HeaderSummary));
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
            ActiveJobs.Clear();
            foreach (var job in preview) ActiveJobs.Add(job);
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

    private void RefreshStorage()
    {
        var probe = _scan is { Succeeded: true, SteamRoot.Length: > 0 } scan ? scan.SteamRoot : Path.GetTempPath();
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(probe));
            var drive = string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root);
            if (drive is { IsReady: true })
            {
                _storageFree = drive.AvailableFreeSpace;
                _storageTotal = drive.TotalSize;
                _storageDrive = drive.Name.TrimEnd('\\', '/');
            }
            else
            {
                _storageFree = -1;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _storageFree = -1;
        }

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

    private void UpdateHeroImage()
    {
        var appId = HeroDownload?.AppId ?? HeroGame?.AppId ?? 0;
        if (appId == _heroImageAppId) return;
        _heroImageAppId = appId;
        HeroImage = appId <= 0 ? null : LoadSteamImage(appId, "library_hero.jpg", fallback: "header.jpg");
    }

    private BitmapImage? LoadSteamImage(int appId, string file, string? fallback)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/{file}");
            image.DecodePixelWidth = 1600;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            if (fallback is not null)
            {
                image.DownloadFailed += (_, _) =>
                {
                    if (_heroImageAppId == appId) HeroImage = LoadSteamImage(appId, fallback, fallback: null);
                };
            }
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
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

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e) => QueueDownloadRefresh();

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

    private void PlayGame(Game? game)
    {
        if (game is null || game.AppId <= 0) return;
        StartShell($"steam://rungameid/{game.AppId}");
    }

    private void OpenGameFolder(Game? game)
    {
        if (game is null || string.IsNullOrWhiteSpace(game.InstallFolder) || !Directory.Exists(game.InstallFolder)) return;
        StartShell(game.InstallFolder);
    }

    private void StartShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Logging.Add(LogLevel.Warning, "Dashboard", $"Could not open {target}: {exception.Message}");
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
