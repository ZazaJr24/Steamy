using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed class DepotDownloaderViewModel : ViewModelBase
{
    private readonly IDepotDownloaderService _depotDownloader;
    private readonly IDownloadManager _downloadManager;
    private readonly ISettingsService _settingsService;
    private readonly IDownloadQueueStore _queueStore;
    private readonly HashSet<DownloadJob> _watchedJobs = new();
    private int _busyOperations;
    private string _targetFolder = string.Empty;
    private Game? _selectedGame;
    private Depot? _selectedDepot;
    private Branch? _selectedBranch;
    private Manifest? _selectedManifest;
    private bool _authorizationConfirmed;
    private bool _isBusy;
    private string _toolStatus = "Not checked";
    private string _toolVersion = "—";
    private string _lastMessage = "Select a game, configure the tool in Settings and add an authorized job.";

    public ObservableCollection<Game> Games { get; }
    public ObservableCollection<Depot> Depots { get; } = new();
    public ObservableCollection<Branch> Branches { get; }
    public ObservableCollection<Manifest> Manifests { get; } = new();
    public ObservableCollection<DownloadJob> Jobs { get; }
    public ICollectionView QueueJobs { get; }
    public bool HasJobs => Jobs.Any(IsManualJob);
    public bool HasNoJobs => !HasJobs;
    private static bool IsManualJob(DownloadJob job) => job.DownloadMode is "DepotDownloader" or "Not configured";

    public Game? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetProperty(ref _selectedGame, value)) RefreshSelections();
        }
    }

    public Depot? SelectedDepot
    {
        get => _selectedDepot;
        set { if (SetProperty(ref _selectedDepot, value)) RefreshManifests(); }
    }
    public Branch? SelectedBranch { get => _selectedBranch; set => SetProperty(ref _selectedBranch, value); }
    public Manifest? SelectedManifest { get => _selectedManifest; set => SetProperty(ref _selectedManifest, value); }
    public string TargetFolder { get => _targetFolder; set => SetProperty(ref _targetFolder, value); }
    public bool AuthorizationConfirmed { get => _authorizationConfirmed; set => SetProperty(ref _authorizationConfirmed, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string ToolStatus { get => _toolStatus; private set => SetProperty(ref _toolStatus, value); }
    public string ToolVersion { get => _toolVersion; private set => SetProperty(ref _toolVersion, value); }
    public string LastMessage { get => _lastMessage; private set => SetProperty(ref _lastMessage, value); }
    public string ToolPath => _settingsService.Load().DepotDownloaderPath ?? string.Empty;
    public string QueueSummary => $"{Jobs.Count(job => IsManualJob(job) && job.IsActive)} active · "
        + $"{Jobs.Count(job => IsManualJob(job) && job.State == DownloadJobState.Queued)} waiting · "
        + $"{Jobs.Count(job => IsManualJob(job) && job.State == DownloadJobState.Paused)} paused";

    public ICommand CheckToolCommand { get; }
    public ICommand AddToQueueCommand { get; }
    public IAsyncRelayCommand StartQueuedCommand { get; }
    public IAsyncRelayCommand<DownloadJob> StartCommand { get; }
    public IAsyncRelayCommand<DownloadJob> PauseCommand { get; }
    public IAsyncRelayCommand<DownloadJob> ResumeCommand { get; }
    public IAsyncRelayCommand<DownloadJob> CancelCommand { get; }
    public IAsyncRelayCommand<DownloadJob> RetryCommand { get; }
    public IAsyncRelayCommand<DownloadJob> VerifyCommand { get; }
    public IAsyncRelayCommand<DownloadJob> RepairCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand CopyFolderCommand { get; }

    public DepotDownloaderViewModel(
        IAppDataStore store,
        INavigationService navigation,
        ILoggingService logging,
        IDepotDownloaderService depotDownloader,
        IDownloadManager downloadManager,
        ISettingsService settingsService,
        IDownloadQueueStore queueStore) : base(store, navigation, logging)
    {
        _depotDownloader = depotDownloader;
        _downloadManager = downloadManager;
        _settingsService = settingsService;
        _queueStore = queueStore;
        Games = store.Games;
        Branches = store.Branches;
        Jobs = store.Downloads;
        QueueJobs = new ListCollectionView(Jobs) { Filter = item => item is DownloadJob job && IsManualJob(job) };
        Jobs.CollectionChanged += OnJobsChanged;
        foreach (var job in Jobs) Watch(job);
        SelectedGame = Games.FirstOrDefault();
        var settings = settingsService.Load();
        TargetFolder = settings.DownloadFolder;

        CheckToolCommand = new AsyncRelayCommand(CheckToolAsync);
        AddToQueueCommand = new AsyncRelayCommand(AddToQueueAsync);
        StartQueuedCommand = new AsyncRelayCommand(StartQueuedAsync);
        StartCommand = new AsyncRelayCommand<DownloadJob>(job => job is null ? Task.CompletedTask : StartAsync(job));
        PauseCommand = new AsyncRelayCommand<DownloadJob>(job => RunJobAsync(job, () => _downloadManager.PauseAsync(job!)));
        ResumeCommand = new AsyncRelayCommand<DownloadJob>(job => job is null ? Task.CompletedTask : StartAsync(job));
        CancelCommand = new AsyncRelayCommand<DownloadJob>(job => RunJobAsync(job, () => _downloadManager.CancelAsync(job!)));
        RetryCommand = new AsyncRelayCommand<DownloadJob>(job => RunJobAsync(job, () => _downloadManager.RetryAsync(job!)));
        VerifyCommand = new AsyncRelayCommand<DownloadJob>(job => job is null ? Task.CompletedTask : VerifyAsync(job));
        RepairCommand = new AsyncRelayCommand<DownloadJob>(job =>
        {
            if (job is null || job.State is not (DownloadJobState.Completed or DownloadJobState.Paused or DownloadJobState.Failed)) return Task.CompletedTask;
            job.AppendLog("Verify & repair — checking existing files against the downloader manifests; missing or invalid chunks may be downloaded.");
            return StartAsync(job);
        });
        RemoveCommand = new AsyncRelayCommand<DownloadJob>(RemoveJobAsync);
        CopyFolderCommand = new RelayCommand<DownloadJob>(CopyFolder);
        RefreshSelections();
    }

    private async Task CheckToolAsync()
    {
        IsBusy = ++_busyOperations > 0;
        try
        {
            var status = await _depotDownloader.CheckAsync(ToolPath);
            ToolStatus = status.Message;
            ToolVersion = string.IsNullOrWhiteSpace(status.Version) ? "—" : status.Version;
            LastMessage = status.Message;
            Logging.Add(status.IsReady ? LogLevel.Info : LogLevel.Warning, "DepotDownloader", status.Message);
            OnPropertyChanged(nameof(ToolPath));
        }
        catch (Exception exception)
        {
            ToolStatus = "Tool check failed";
            ToolVersion = "—";
            ReportError("Could not check the download tool", exception);
        }
        finally
        {
            IsBusy = --_busyOperations > 0;
        }
    }

    private void Watch(DownloadJob job)
    {
        if (_watchedJobs.Add(job)) PropertyChangedEventManager.AddHandler(job, OnJobChanged, string.Empty);
    }

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var job in _watchedJobs) PropertyChangedEventManager.RemoveHandler(job, OnJobChanged, string.Empty);
            _watchedJobs.Clear();
            foreach (var job in Jobs) Watch(job);
        }
        else
        {
            if (e.OldItems is not null)
                foreach (DownloadJob job in e.OldItems)
                    if (_watchedJobs.Remove(job)) PropertyChangedEventManager.RemoveHandler(job, OnJobChanged, string.Empty);
            if (e.NewItems is not null)
                foreach (DownloadJob job in e.NewItems) Watch(job);
        }
        RefreshQueueSummary();
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadJob.State) or nameof(DownloadJob.DownloadMode)) RefreshQueueSummary();
    }

    private void RefreshQueueSummary()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(RefreshQueueSummary));
            return;
        }
        QueueJobs.Refresh();
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(HasNoJobs));
    }

    private void RefreshSelections()
    {
        Depots.Clear();
        foreach (var depot in Store.Depots.Where(item => SelectedGame is not null && item.AppId == SelectedGame.AppId)) Depots.Add(depot);
        SelectedDepot = Depots.FirstOrDefault(item => item.Selected);
        RefreshManifests();
        SelectedBranch = Branches.FirstOrDefault(item => item.Name.Equals("public", StringComparison.OrdinalIgnoreCase)) ?? Branches.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(TargetFolder)) TargetFolder = SelectedGame?.InstallFolder ?? _settingsService.Load().DownloadFolder;
    }

    private void RefreshManifests()
    {
        Manifests.Clear();
        foreach (var manifest in Store.Manifests.Where(item => SelectedGame is not null
            && item.AppId == SelectedGame.AppId && SelectedDepot is not null && item.DepotId == SelectedDepot.DepotId))
            Manifests.Add(manifest);
        SelectedManifest = Manifests.FirstOrDefault();
    }

    private async Task AddToQueueAsync()
    {
        if (SelectedGame is null)
        {
            LastMessage = "Select a game first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(TargetFolder))
        {
            LastMessage = "Select a target folder first.";
            return;
        }
        if (!AuthorizationConfirmed)
        {
            LastMessage = "Confirm that you are authorized to access this content.";
            Logging.Add(LogLevel.Warning, "DepotDownloader", "Queue request rejected because authorization was not confirmed.", SelectedGame.AppId);
            return;
        }
        string targetFolder;
        try
        {
            if (!Path.IsPathFullyQualified(TargetFolder.Trim()))
            {
                LastMessage = "Choose an absolute target folder, for example C:\\Games\\My Game.";
                return;
            }
            targetFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(TargetFolder.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            LastMessage = "The target folder is invalid. Choose another folder.";
            return;
        }
        if (Jobs.Any(job => job.AppId == SelectedGame.AppId
            && SameFolder(job.TargetFolder, targetFolder)
            && (job.IsActive || job.State is DownloadJobState.Queued or DownloadJobState.Paused)
            && (!IsManualJob(job) || job.DepotId is null || SelectedDepot is null || job.DepotId == SelectedDepot.DepotId)))
        {
            LastMessage = "This folder already has an active, queued or paused download for this game. Continue that job in Downloads, or choose a different folder.";
            return;
        }

        var job = new DownloadJob
        {
            AppId = SelectedGame.AppId,
            GameName = SelectedGame.Name,
            CoverColor = SelectedGame.CoverColor,
            CoverGlyph = SelectedGame.CoverGlyph,
            TargetFolder = targetFolder,
            TotalSize = SelectedDepot?.Size ?? SelectedGame.Size,
            Downloaded = "0 B",
            Speed = "—",
            Eta = "Queued",
            CurrentFile = "—",
            Branch = SelectedBranch?.Name ?? "public",
            DepotId = SelectedDepot?.DepotId,
            ManifestId = SelectedManifest?.ManifestId ?? string.Empty,
            AuthorizationConfirmed = true,
            State = DownloadJobState.Queued,
            Status = "Waiting for slot",
            Started = DateTime.Now,
            Priority = DownloadPriority.Normal,
            DownloadMode = string.IsNullOrWhiteSpace(ToolPath) ? "Not configured" : "DepotDownloader"
        };
        try { await _queueStore.SaveAsync(job); }
        catch (Exception exception)
        {
            ReportError("Could not save the queued download", exception);
            return;
        }
        Jobs.Insert(0, job);
        LastMessage = $"{SelectedGame.Name} added to the queue.";
        Logging.Add(LogLevel.Info, "DepotDownloader", "Authorized job added to queue.", job.AppId, job.Id);
        OnPropertyChanged(nameof(QueueSummary));
    }

    private async Task StartQueuedAsync()
    {
        var settings = _settingsService.Load();
        using var gate = new SemaphoreSlim(Math.Clamp(settings.ParallelDownloads, 1, 16));
        var queued = Jobs.Where(job => IsManualJob(job) && job.State == DownloadJobState.Queued).ToArray();
        await Task.WhenAll(queued.Select(async job =>
        {
            await gate.WaitAsync();
            try
            {
                // A waiting job may have been cancelled/removed while another job used the slot.
                if (Jobs.Contains(job) && job.State == DownloadJobState.Queued) await StartAsync(job);
            }
            finally { gate.Release(); }
        }));
        OnPropertyChanged(nameof(QueueSummary));
    }

    private Task StartAsync(DownloadJob job) => RunJobAsync(job, async () =>
    {
        if (job.IsActive) return;
        var started = await _downloadManager.StartAsync(job);
        LastMessage = started ? $"{job.GameName}: {job.Status}" : $"{job.GameName}: {job.Status}. If an operation is finishing, wait briefly and try again.";
    });

    private Task VerifyAsync(DownloadJob job) => RunJobAsync(job, async () =>
    {
        await _downloadManager.VerifyAsync(job);
        LastMessage = $"{job.GameName}: {job.Status}";
    });

    private async Task RunJobAsync(DownloadJob? job, Func<Task> action)
    {
        if (job is null || !IsManualJob(job)) return;
        IsBusy = ++_busyOperations > 0;
        try { await action(); }
        catch (OperationCanceledException) { LastMessage = $"{job.GameName}: operation cancelled; existing files were kept."; }
        catch (Exception exception) { ReportError($"{job.GameName}: operation failed", exception); }
        finally
        {
            IsBusy = --_busyOperations > 0;
            RefreshQueueSummary();
        }
    }

    private async Task RemoveJobAsync(DownloadJob? job)
    {
        if (job is null || job.IsActive || !IsManualJob(job)) return;
        await RunJobAsync(job, async () =>
        {
            // A paused process can still be saving its state. Keep its row if removal is refused.
            await _downloadManager.ForgetAsync(job);
            Jobs.Remove(job);
            LastMessage = "Download removed from the queue; local files were kept.";
            Logging.Add(LogLevel.Info, "DepotDownloader", LastMessage, job.AppId, job.Id);
        });
    }

    private void ReportError(string context, Exception exception)
    {
        LastMessage = $"{context}: {exception.Message}";
        Logging.Add(LogLevel.Warning, "DepotDownloader", LastMessage);
    }

    private static bool SameFolder(string left, string right)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left.Trim())), right, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public override async Task OnNavigatedToAsync()
    {
        RefreshSelections();
        await base.OnNavigatedToAsync();
        OnPropertyChanged(nameof(ToolPath));
        if (ToolStatus == "Not checked" && !string.IsNullOrWhiteSpace(ToolPath)) await CheckToolAsync();
    }

    /// <summary>
    /// Puts the target folder on the clipboard instead of launching Explorer: the application
    /// never opens a second window, not even a file manager, and the path is shown in the job row.
    /// </summary>
    private void CopyFolder(DownloadJob? job)
    {
        if (job is null || string.IsNullOrWhiteSpace(job.TargetFolder))
        {
            LastMessage = "This job has no target folder yet.";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(job.TargetFolder);
            LastMessage = $"Folder path copied: {job.TargetFolder}";
        }
        catch (Exception exception)
        {
            // The clipboard can be locked by another process; that is not worth a crash.
            LastMessage = $"Could not copy the folder path: {exception.Message}";
        }
    }
}
