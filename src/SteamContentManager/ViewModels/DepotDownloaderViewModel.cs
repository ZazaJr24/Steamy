using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using SteamContentManager.Models;
using SteamContentManager.Pages;
using SteamContentManager.Services;

namespace SteamContentManager.ViewModels;

public sealed class DepotDownloaderViewModel : ViewModelBase
{
    private readonly IDepotDownloaderService _depotDownloader;
    private readonly IDownloadManager _downloadManager;
    private readonly ISettingsService _settingsService;
    private string _targetFolder = string.Empty;
    private Game? _selectedGame;
    private Depot? _selectedDepot;
    private Branch? _selectedBranch;
    private Manifest? _selectedManifest;
    private bool _authorizationConfirmed;
    private bool _verifyAfterDownload = true;
    private bool _isBusy;
    private string _toolStatus = "Not checked";
    private string _toolVersion = "—";
    private string _lastMessage = "Select a game, configure the tool in Settings and add an authorized job.";

    public ObservableCollection<Game> Games { get; }
    public ObservableCollection<Depot> Depots { get; } = new();
    public ObservableCollection<Branch> Branches { get; }
    public ObservableCollection<Manifest> Manifests { get; } = new();
    public ObservableCollection<DownloadJob> Jobs { get; }

    public Game? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetProperty(ref _selectedGame, value)) RefreshSelections();
        }
    }

    public Depot? SelectedDepot { get => _selectedDepot; set => SetProperty(ref _selectedDepot, value); }
    public Branch? SelectedBranch { get => _selectedBranch; set => SetProperty(ref _selectedBranch, value); }
    public Manifest? SelectedManifest { get => _selectedManifest; set => SetProperty(ref _selectedManifest, value); }
    public string TargetFolder { get => _targetFolder; set => SetProperty(ref _targetFolder, value); }
    public bool AuthorizationConfirmed { get => _authorizationConfirmed; set => SetProperty(ref _authorizationConfirmed, value); }
    public bool VerifyAfterDownload { get => _verifyAfterDownload; set => SetProperty(ref _verifyAfterDownload, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string ToolStatus { get => _toolStatus; private set => SetProperty(ref _toolStatus, value); }
    public string ToolVersion { get => _toolVersion; private set => SetProperty(ref _toolVersion, value); }
    public string LastMessage { get => _lastMessage; private set => SetProperty(ref _lastMessage, value); }
    public string ToolPath
{
    get
    {
        var settings = _settingsService?.Load();
        return settings?.DepotDownloaderPath ?? string.Empty;
    }
}

    public string QueueSummary => $"{Jobs.Count(job => job.State == DownloadJobState.Queued)} queued · {Jobs.Count(job => job.IsActive)} active";

    public ICommand CheckToolCommand { get; }
    public ICommand AddToQueueCommand { get; }
    public IAsyncRelayCommand StartQueuedCommand { get; }
    public IAsyncRelayCommand<DownloadJob> StartCommand { get; }
    public IAsyncRelayCommand<DownloadJob> PauseCommand { get; }
    public IAsyncRelayCommand<DownloadJob> ResumeCommand { get; }
    public IAsyncRelayCommand<DownloadJob> CancelCommand { get; }
    public IAsyncRelayCommand<DownloadJob> RetryCommand { get; }
    public IAsyncRelayCommand<DownloadJob> VerifyCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand CopyFolderCommand { get; }

    public DepotDownloaderViewModel(
        IAppDataStore store,
        INavigationService navigation,
        ILoggingService logging,
        IDepotDownloaderService depotDownloader,
        IDownloadManager downloadManager,
        ISettingsService settingsService) : base(store, navigation, logging)
    {
        _depotDownloader = depotDownloader;
        _downloadManager = downloadManager;
        _settingsService = settingsService;
        Games = store.Games;
        Branches = store.Branches;
        Jobs = store.Downloads;
        SelectedGame = Games.FirstOrDefault();
        var settings = settingsService.Load();
        TargetFolder = settings.DownloadFolder;
        VerifyAfterDownload = settings.VerifyAfterDownload;

        CheckToolCommand = new AsyncRelayCommand(CheckToolAsync);
        AddToQueueCommand = new RelayCommand(AddToQueue);
        StartQueuedCommand = new AsyncRelayCommand(StartQueuedAsync);
        StartCommand = new AsyncRelayCommand<DownloadJob>(job => job is null ? Task.CompletedTask : StartAsync(job));
        PauseCommand = new AsyncRelayCommand<DownloadJob>(job => job is null ? Task.CompletedTask : _downloadManager.PauseAsync(job));
        ResumeCommand = new AsyncRelayCommand<DownloadJob>(job => job is null ? Task.CompletedTask : StartAsync(job));
        CancelCommand = new AsyncRelayCommand<DownloadJob>(job => job is null ? Task.CompletedTask : _downloadManager.CancelAsync(job));
        RetryCommand = new AsyncRelayCommand<DownloadJob>(job => job is null ? Task.CompletedTask : _downloadManager.RetryAsync(job));
        VerifyCommand = new AsyncRelayCommand<DownloadJob>(job => job is null ? Task.CompletedTask : VerifyAsync(job));
        RemoveCommand = new AsyncRelayCommand<DownloadJob>(RemoveJobAsync);
        CopyFolderCommand = new RelayCommand<DownloadJob>(CopyFolder);
        RefreshSelections();
    }

    private async Task CheckToolAsync()
    {
        IsBusy = true;
        try
        {
            var status = await _depotDownloader.CheckAsync(ToolPath);
            ToolStatus = status.Message;
            ToolVersion = string.IsNullOrWhiteSpace(status.Version) ? "—" : status.Version;
            LastMessage = status.Message;
            Logging.Add(status.IsReady ? LogLevel.Info : LogLevel.Warning, "DepotDownloader", status.Message);
            OnPropertyChanged(nameof(ToolPath));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshSelections()
    {
        Depots.Clear();
        foreach (var depot in Store.Depots.Where(item => SelectedGame is null || item.AppId == SelectedGame.AppId)) Depots.Add(depot);
        Manifests.Clear();
        foreach (var manifest in Store.Manifests.Where(item => SelectedGame is null || item.AppId == SelectedGame.AppId)) Manifests.Add(manifest);
        SelectedDepot = Depots.FirstOrDefault(item => item.Selected);
        SelectedBranch = Branches.FirstOrDefault(item => item.Name.Equals("public", StringComparison.OrdinalIgnoreCase)) ?? Branches.FirstOrDefault();
        SelectedManifest = Manifests.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(TargetFolder)) TargetFolder = SelectedGame?.InstallFolder ?? _settingsService.Load().DownloadFolder;
    }

    private void AddToQueue()
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
        if (Jobs.Any(job => job.AppId == SelectedGame.AppId && job.DepotId == SelectedDepot?.DepotId && job.State is DownloadJobState.Queued or DownloadJobState.Downloading or DownloadJobState.Paused))
        {
            LastMessage = "This game/depot is already in the active queue.";
            return;
        }

        var job = new DownloadJob
        {
            AppId = SelectedGame.AppId,
            GameName = SelectedGame.Name,
            CoverColor = SelectedGame.CoverColor,
            CoverGlyph = SelectedGame.CoverGlyph,
            TargetFolder = TargetFolder,
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
        Jobs.Insert(0, job);
        LastMessage = $"{SelectedGame.Name} added to the queue.";
        Logging.Add(LogLevel.Info, "DepotDownloader", "Authorized job added to queue.", job.AppId, job.Id);
        OnPropertyChanged(nameof(QueueSummary));
    }

    private async Task StartQueuedAsync()
    {
        var settings = _settingsService.Load();
        using var gate = new SemaphoreSlim(Math.Clamp(settings.ParallelDownloads, 1, 16));
        var queued = Jobs.Where(job => job.State == DownloadJobState.Queued).ToArray();
        await Task.WhenAll(queued.Select(async job =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { await StartAsync(job).ConfigureAwait(false); }
            finally { gate.Release(); }
        }));
        OnPropertyChanged(nameof(QueueSummary));
    }

    private async Task StartAsync(DownloadJob job)
    {
        try
        {
            IsBusy = true;
            await _downloadManager.StartAsync(job);
            LastMessage = $"{job.GameName}: {job.Status}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(QueueSummary));
        }
    }

    private async Task VerifyAsync(DownloadJob job)
    {
        var result = await _downloadManager.VerifyAsync(job);
        LastMessage = result ? $"{job.GameName}: verification passed." : $"{job.GameName}: verification unavailable or failed.";
        OnPropertyChanged(nameof(QueueSummary));
    }

    private async Task RemoveJobAsync(DownloadJob? job)
    {
        if (job is null || job.IsActive) return;
        Jobs.Remove(job);
        await _downloadManager.ForgetAsync(job).ConfigureAwait(true);
        Logging.Add(LogLevel.Info, "DepotDownloader", "Inactive job removed from the queue; local files were left untouched.", job.AppId, job.Id);
        OnPropertyChanged(nameof(QueueSummary));
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
