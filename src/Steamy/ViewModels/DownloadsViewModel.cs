using System.Collections.ObjectModel;
using System.Collections.Concurrent;
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

public sealed class DownloadsViewModel : ViewModelBase
{
    private readonly IDownloadManager _manager; private readonly IRyuuGameDownloadService _ryuu; private readonly ISettingsService _settings; private string _search = ""; private string _filter = "All downloads";
    public DownloadsViewModel(IAppDataStore s, INavigationService n, ILoggingService l, IDownloadManager m, IRyuuGameDownloadService ryuu, ISettingsService settings) : base(s,n,l)
    {
        _manager=m; _ryuu=ryuu; _settings=settings; Settings=settings.Load(); Jobs=s.Downloads;
        foreach (var job in Jobs) Watch(job);
        Jobs.CollectionChanged += OnJobsChanged;
        _liveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _liveTimer.Tick += (_, _) => UpdateLiveStats();
        StartQueuedCommand = new AsyncRelayCommand(StartQueuedAsync);
        StopQueueCommand = new RelayCommand(() => _queueCancellation?.Cancel());
        MoveUpCommand = new AsyncRelayCommand<DownloadJob>(job => MoveQueueJobAsync(job, -1));
        MoveDownCommand = new AsyncRelayCommand<DownloadJob>(job => MoveQueueJobAsync(job, 1));
        CyclePriorityCommand = new AsyncRelayCommand<DownloadJob>(ChangePriorityAsync);
        ApplyTransferSettingsCommand = new AsyncRelayCommand(ApplyTransferSettingsAsync);
        LoadTransferSettings();
        RefreshFilter();
    }

    private const double SparklineWidth = 200;
    private const double SparklineHeight = 40;
    private readonly NetworkThroughputSampler _network = new();
    private readonly DispatcherTimer _liveTimer;
    private int _liveTick;
    private readonly HashSet<DownloadJob> _watchedJobs = new();
    private bool _refreshQueued;
    private DownloadJob? _selectedJob;
    public DownloadJob? SelectedJob { get => _selectedJob; set => SetProperty(ref _selectedJob, value); }
    public bool ShowEmptyResults => !HasJobs;
    public string ResultLabel => $"{FilteredJobs.Count:N0} of {Jobs.Count:N0} downloads";
    public ICommand ClearFiltersCommand => new RelayCommand(() => { SearchText = string.Empty; SelectedFilter = "All downloads"; SelectedSourceFilter = "All sources"; });

    public string InternetSpeedLabel { get; private set; } = "—";
    public string InternetPeakLabel { get; private set; } = string.Empty;
    public System.Windows.Media.PointCollection InternetSparkline { get; private set; } = new();
    public System.Windows.Media.PointCollection InternetSparklineArea { get; private set; } = new();
    public string JobsSpeedLabel { get; private set; } = "—";
    public string OverallEtaLabel { get; private set; } = "—";
    public string OverallEtaHint { get; private set; } = string.Empty;
    public double ActiveProgress { get; private set; }
    public string ActiveProgressLabel { get; private set; } = "—";

    /// <summary>Samples twice per second while the Downloads page is visible.</summary>
    public void StartLiveStats()
    {
        UpdateLiveStats();
        _liveTimer.Start();
    }

    public void StopLiveStats() => _liveTimer.Stop();

    // Frame-rate friendly live stats: labels refresh twice a second, the sparkline geometry is
    // only rebuilt once per second — new PointCollections invalidate the canvas, and doing that
    // every tick doubled the render cost for a curve that crawls anyway.
    private void UpdateLiveStats()
    {
        _network.Sample();
        var active = Jobs.Where(job => job.IsActive).ToList();
        _liveTick++;

        InternetSpeedLabel = _network.IsAvailable ? DownloadFormat.Speed(_network.BytesPerSecond) : "—";
        InternetPeakLabel = !_network.IsAvailable ? "Not measurable on this system"
            : _network.PeakBytesPerSecond > 0 ? $"Peak {DownloadFormat.Speed(_network.PeakBytesPerSecond)}" : "Measuring…";
        if (_liveTick % 2 == 1)
        {
            (InternetSparkline, InternetSparklineArea) = BuildSparkline(_network.History, _network.HistoryLength);
            OnPropertyChanged(nameof(InternetSparkline));
            OnPropertyChanged(nameof(InternetSparklineArea));
        }

        var jobRate = active.Sum(job => job.BytesPerSecond);
        JobsSpeedLabel = active.Count == 0 ? "Idle" : DownloadFormat.Speed(jobRate);

        var longestEta = DownloadPresentation.RemainingSeconds(active.Select(job => job.EtaSeconds));
        OverallEtaLabel = active.Count == 0 ? "—" : longestEta is { } seconds ? DownloadFormat.Duration(seconds) : "Estimating…";
        OverallEtaHint = active.Count == 0 ? "No active downloads"
            : longestEta is not null ? "Active jobs · queued jobs excluded" : "Waiting for estimates from every active job";

        ActiveProgress = active.Count == 0 ? 0 : active.Average(job => job.Progress);
        ActiveProgressLabel = active.Count == 0 ? "—" : $"{ActiveProgress:0.0}%";

        foreach (var name in LiveStatNames) OnPropertyChanged(name);
    }

    private static readonly string[] LiveStatNames =
    {
        nameof(InternetSpeedLabel), nameof(InternetPeakLabel),
        nameof(JobsSpeedLabel), nameof(OverallEtaLabel), nameof(OverallEtaHint), nameof(ActiveProgress), nameof(ActiveProgressLabel)
    };

    private static (System.Windows.Media.PointCollection Line, System.Windows.Media.PointCollection Area) BuildSparkline(IReadOnlyCollection<double> values, int capacity)
    {
        var line = new System.Windows.Media.PointCollection();
        var area = new System.Windows.Media.PointCollection();
        if (values.Count >= 2)
        {
            // A floor keeps an idle connection flat instead of amplifying noise to full height.
            var max = Math.Max(values.Max() * 1.15, 256 * 1024);
            var step = SparklineWidth / Math.Max(capacity - 1, 1);
            var x = SparklineWidth - (values.Count - 1) * step;
            area.Add(new Point(x, SparklineHeight));
            foreach (var value in values)
            {
                var point = new Point(x, SparklineHeight - value / max * (SparklineHeight - 2) - 1);
                line.Add(point);
                area.Add(point);
                x += step;
            }
            area.Add(new Point(SparklineWidth, SparklineHeight));
        }

        line.Freeze();
        area.Freeze();
        return (line, area);
    }
    private void Watch(DownloadJob job)
    {
        if (_watchedJobs.Add(job)) job.PropertyChanged += OnJobPropertyChanged;
    }

    private void OnJobsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            foreach (var job in _watchedJobs) job.PropertyChanged -= OnJobPropertyChanged;
            _watchedJobs.Clear();
            foreach (var job in Jobs) Watch(job);
        }
        else
        {
            if (e.OldItems is not null) foreach (DownloadJob job in e.OldItems)
                if (_watchedJobs.Remove(job)) job.PropertyChanged -= OnJobPropertyChanged;
            if (e.NewItems is not null) foreach (DownloadJob job in e.NewItems) Watch(job);
        }
        QueueRefresh();
    }

    private void OnJobPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadJob.State) or nameof(DownloadJob.DownloadMode)
            or nameof(DownloadJob.Priority) or nameof(DownloadJob.QueuePosition)) QueueRefresh();
    }

    private void QueueRefresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) { RefreshFilter(); return; }
        if (_refreshQueued) return;
        _refreshQueued = true;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshQueued = false;
            RefreshFilter();
        }));
    }

    // The featured card and the virtualized list share the same search and status filter.

    private DownloadJob? _heroJob;
    public DownloadJob? HeroJob
    {
        get => _heroJob;
        private set
        {
            if (!SetProperty(ref _heroJob, value)) return;
            OnPropertyChanged(nameof(HasHeroJob));
            OnPropertyChanged(nameof(ShowIdleHero));
        }
    }
    public bool HasHeroJob => HeroJob is not null;
    public bool ShowIdleHero => HeroJob is null;
    public DownloadJob? NextQueuedJob => FilteredJobs.FirstOrDefault(job => job.State == DownloadJobState.Queued);
    public bool HasNextQueuedJob => NextQueuedJob is not null;
    public string IdleHeadline => HasActiveFilters && !HasJobs ? "No matching downloads"
        : Jobs.Count == 0 ? "Your next game starts here"
        : FilteredJobs.Any(job => job.State == DownloadJobState.Failed) ? "A download needs attention"
        : HasNextQueuedJob ? "Your queue is ready"
        : "Nothing running right now";
    public string IdleDetail => HasActiveFilters && !HasJobs ? EmptyStateMessage
        : Jobs.Count == 0 ? "Browse your library, choose a game and follow its progress here."
        : FilteredJobs.Any(job => job.State == DownloadJobState.Failed) ? "Select a failed download to see its details, or retry it from the list."
        : HasNextQueuedJob ? "Start the next download whenever you're ready."
        : "Review your downloads below, or find your next game in the library.";

    private void UpdateSections()
    {
        HeroJob = FilteredJobs.FirstOrDefault(job => job.IsActive)
            ?? FilteredJobs.FirstOrDefault(job => job.State == DownloadJobState.Paused);
        OnPropertyChanged(nameof(NextQueuedJob));
        OnPropertyChanged(nameof(HasNextQueuedJob));
        OnPropertyChanged(nameof(IdleHeadline));
        OnPropertyChanged(nameof(IdleDetail));
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(HasJobs)); OnPropertyChanged(nameof(ActiveCount)); OnPropertyChanged(nameof(QueuedJobCount)); OnPropertyChanged(nameof(CompletedCount)); OnPropertyChanged(nameof(FailedCount)); OnPropertyChanged(nameof(TotalProgress));
    }
    public AppSettings Settings { get; private set; }
    public ObservableCollection<DownloadJob> Jobs { get; }
    public ObservableCollection<DownloadJob> FilteredJobs { get; } = new();
    public string[] Filters { get; } = { "All downloads", "Active", "Queued", "Paused", "Completed", "Failed", "Cancelled" };
    public string SearchText { get=>_search; set { if(SetProperty(ref _search,value)) RefreshFilter(); } }
    public string SelectedFilter { get=>_filter; set { if(SetProperty(ref _filter,value)) RefreshFilter(); } }
    private string _sourceFilter = "All sources";
    public string[] SourceFilters { get; } = { "All sources", "Sushi", "Zaza", "Ryuu", "Hubcap", "DepotBox", "DepotDownloader", "Custom archive" };
    public string SelectedSourceFilter { get => _sourceFilter; set { if (SetProperty(ref _sourceFilter, value)) RefreshFilter(); } }
    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchText) || SelectedFilter != "All downloads" || SelectedSourceFilter != "All sources";
    public string FilterSummary => HasActiveFilters ? "Active filters" : "No filters applied";
    public bool HasJobs => FilteredJobs.Count > 0;
    public string EmptyStateMessage => HasActiveFilters
        ? "Try a different game name, App ID, status or source."
        : "Choose a game from your library to start your first download.";
    public string TotalProgress => Jobs.Count == 0 ? "0%" : $"{Jobs.Average(x=>x.Progress):0}%";
    public string DownloadToolStatus => string.IsNullOrWhiteSpace(_settings.Load().DepotDownloaderPath) ? "DepotDownloader: configure it in Settings" : "DepotDownloader: ready for authorized downloads";
    public string QueueLimits => $"{_settings.Load().ParallelDownloads} parallel job(s) · {_settings.Load().RetryCount} retries";
    public IAsyncRelayCommand StartQueuedCommand { get; }
    public ICommand StopQueueCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand CyclePriorityCommand { get; }
    public IAsyncRelayCommand ApplyTransferSettingsCommand { get; }
    private CancellationTokenSource? _queueCancellation;
    private readonly SemaphoreSlim _queueEditGate = new(1, 1);
    private int _pendingQueueEdits;
    private bool _isQueueRunning;
    public bool IsQueueRunning { get => _isQueueRunning; private set => SetProperty(ref _isQueueRunning, value); }
    private string _queueStatus = "Priority and order apply when the next slot opens.";
    public string QueueStatus { get => _queueStatus; private set => SetProperty(ref _queueStatus, value); }
    private string _parallelJobsText = "2", _connectionsText = "16", _rateLimitText = "0";
    private string _transferSettingsHint = "Applies to the next start or resume. The bundled Mod speed cap is per game; 0 is unlimited.";
    public string ParallelJobsText { get => _parallelJobsText; set => SetProperty(ref _parallelJobsText, value); }
    public string ConnectionsText { get => _connectionsText; set => SetProperty(ref _connectionsText, value); }
    public string RateLimitText { get => _rateLimitText; set => SetProperty(ref _rateLimitText, value); }
    public string TransferSettingsHint { get => _transferSettingsHint; private set => SetProperty(ref _transferSettingsHint, value); }

    private void LoadTransferSettings()
    {
        var settings = _settings.Load();
        ParallelJobsText = settings.ParallelDownloads.ToString(CultureInfo.InvariantCulture);
        ConnectionsText = settings.DownloadConnections.ToString(CultureInfo.InvariantCulture);
        RateLimitText = settings.DownloadRateLimitMiB.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(QueueLimits));
    }

    private async Task ApplyTransferSettingsAsync()
    {
        if (!int.TryParse(ParallelJobsText, out var parallel) || parallel is < 1 or > 16
            || !int.TryParse(ConnectionsText, out var connections) || connections is < 1 or > 128
            || !int.TryParse(RateLimitText, out var limit) || limit is < 0 or > 1024)
        {
            TransferSettingsHint = "Enter 1–16 parallel jobs, 1–128 connections and 0–1024 MiB/s (0 = unlimited).";
            return;
        }
        var settings = _settings.Load();
        var previous = (settings.ParallelDownloads, settings.DownloadConnections, settings.DownloadRateLimitMiB);
        settings.ParallelDownloads = parallel;
        settings.DownloadConnections = connections;
        settings.DownloadRateLimitMiB = limit;
        try
        {
            await _settings.SaveAsync(settings);
            Settings = settings;
            TransferSettingsHint = limit == 0
                ? "Saved · next start or resume uses these settings. Bundled Mod downloads have no speed cap."
                : $"Saved · next bundled Mod start or resume is capped at {limit} MiB/s per game. Current downloads keep their settings.";
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(QueueLimits));
        }
        catch (Exception exception)
        {
            (settings.ParallelDownloads, settings.DownloadConnections, settings.DownloadRateLimitMiB) = previous;
            TransferSettingsHint = "Settings could not be saved. Try again before starting the queue.";
            Logging.Add(LogLevel.Warning, "Downloads", $"Transfer settings save failed: {exception.GetType().Name}.");
        }
    }

    private IEnumerable<DownloadJob> OrderedWaitingJobs() => Jobs.Where(job => job.State == DownloadJobState.Queued)
        .OrderByDescending(job => job.Priority).ThenBy(job => job.QueuePosition == 0 ? job.Started.Ticks : job.QueuePosition);

    private async Task StartQueuedAsync()
    {
        if (IsQueueRunning) return;
        using var cancellation = new CancellationTokenSource();
        _queueCancellation = cancellation;
        IsQueueRunning = true;
        QueueStatus = "Queue running · existing downloads count towards parallel jobs.";
        var scheduled = new HashSet<DownloadJob>();
        async Task StartScheduledAsync(DownloadJob job)
        {
            scheduled.Add(job);
            try { await StartAsync(job); }
            finally { scheduled.Remove(job); }
        }
        try
        {
            await DownloadQueueScheduler.RunAsync(() => OrderedWaitingJobs().ToArray(),
                job => (int)job.Priority, job => job.QueuePosition == 0 ? job.Started.Ticks : job.QueuePosition,
                StartScheduledAsync, _settings.Load().ParallelDownloads, cancellation.Token,
                () => _pendingQueueEdits > 0 ? int.MaxValue : Jobs.Count(job => !scheduled.Contains(job) && (job.IsActive
                    || _manager is IDownloadOperationStatus status && status.IsOperationRunning(job.Id))));
            QueueStatus = "Queue finished · review completed downloads and any jobs needing attention.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            QueueStatus = "Queue stopped · running downloads continue; waiting jobs stay queued.";
        }
        catch (Exception exception)
        {
            QueueStatus = "Queue stopped after an error. Check the download details before restarting.";
            Logging.Add(LogLevel.Warning, "Downloads", $"Queue stopped: {exception.GetType().Name}.");
        }
        finally
        {
            _queueCancellation = null;
            IsQueueRunning = false;
            RefreshFilter();
        }
    }

    private async Task ChangePriorityAsync(DownloadJob? job)
    {
        if (job?.State != DownloadJobState.Queued) return;
        _pendingQueueEdits++;
        await _queueEditGate.WaitAsync();
        try
        {
            if (job.State != DownloadJobState.Queued) return;
            var priority = job.Priority switch { DownloadPriority.Normal => DownloadPriority.High, DownloadPriority.High => DownloadPriority.Low, _ => DownloadPriority.Normal };
            try
            {
                var queueStore = App.Services?.GetService(typeof(IDownloadQueueStore)) as IDownloadQueueOrderingStore
                    ?? throw new InvalidOperationException("The saved download queue is unavailable.");
                await queueStore.SavePriorityAsync(new DownloadQueuePriority(job.Id, priority));
                job.Priority = priority;
                QueueStatus = "Priority saved · takes effect at the next free slot.";
            }
            catch (Exception exception)
            {
                QueueStatus = "Priority could not be saved. Your previous priority was kept; try again.";
                Logging.Add(LogLevel.Warning, "Downloads", $"Queue priority save failed: {exception.GetType().Name}.", job.AppId, job.Id);
            }
            RefreshFilter();
        }
        finally { _queueEditGate.Release(); _pendingQueueEdits--; }
    }

    private async Task MoveQueueJobAsync(DownloadJob? job, int direction)
    {
        if (job?.State != DownloadJobState.Queued) return;
        _pendingQueueEdits++;
        await _queueEditGate.WaitAsync();
        try
        {
            if (job.State != DownloadJobState.Queued) return;
            var waiting = OrderedWaitingJobs().Where(item => item.Priority == job.Priority).ToList();
            var index = waiting.IndexOf(job);
            var destination = index + direction;
            if (index < 0 || destination < 0 || destination >= waiting.Count) return;
            (waiting[index], waiting[destination]) = (waiting[destination], waiting[index]);
            var previous = waiting.ToDictionary(item => item, item => item.QueuePosition);
            var positions = waiting.Select((item, position) => new DownloadQueuePosition(item.Id, position + 1)).ToArray();
            try
            {
                var queueStore = App.Services?.GetService(typeof(IDownloadQueueStore)) as IDownloadQueueStore
                    ?? throw new InvalidOperationException("The saved download queue is unavailable.");
                if (queueStore is not IDownloadQueueOrderingStore ordering)
                    throw new InvalidOperationException("This saved queue does not support changing order atomically.");
                await ordering.SavePositionsAsync(positions);
                for (var position = 0; position < waiting.Count; position++) waiting[position].QueuePosition = position + 1;
                QueueStatus = "Queue order saved · takes effect at the next free slot.";
            }
            catch (Exception exception)
            {
                foreach (var (item, position) in previous) item.QueuePosition = position;
                QueueStatus = "Queue order could not be saved. Your previous order was kept; try again.";
                Logging.Add(LogLevel.Warning, "Downloads", $"Queue order save failed: {exception.GetType().Name}.", job.AppId, job.Id);
            }
            RefreshFilter();
        }
        finally { _queueEditGate.Release(); _pendingQueueEdits--; }
    }

    private async Task SaveQueueJobAsync(DownloadJob job)
    {
        var queueStore = App.Services?.GetService(typeof(IDownloadQueueStore)) as IDownloadQueueStore;
        if (queueStore is null) throw new InvalidOperationException("The saved download queue is unavailable.");
        await queueStore.SaveAsync(job);
    }
    public ICommand PauseCommand => new AsyncRelayCommand<DownloadJob>(job => RunDownloadActionAsync(job, () => _manager.PauseAsync(job!)));
    public ICommand ResumeCommand => new AsyncRelayCommand<DownloadJob>(job => RunDownloadActionAsync(job, () => ResumeAsync(job)));
    public ICommand CancelCommand => new AsyncRelayCommand<DownloadJob>(job => RunDownloadActionAsync(job, () => _manager.CancelAsync(job!)));
    public ICommand RetryCommand => new AsyncRelayCommand<DownloadJob>(job => RunDownloadActionAsync(job, () => RetryJobAsync(job)));
    public ICommand RepairCommand => new AsyncRelayCommand<DownloadJob>(async job =>
    {
        if (job is null || job.State is not (DownloadJobState.Completed or DownloadJobState.Paused or DownloadJobState.Failed)) return;
        job.AppendLog("Verify & repair requested — the downloader will check existing data against its manifests and replace missing or invalid chunks.");
        await RunDownloadActionAsync(job, () => ResumeAsync(job));
    });
    public ICommand StartCommand => new AsyncRelayCommand<DownloadJob>(job => RunDownloadActionAsync(job, () => StartAsync(job)));
    public ICommand VerifyCommand => new AsyncRelayCommand<DownloadJob>(job => RunDownloadActionAsync(job, async () => { await _manager.VerifyAsync(job!); RefreshFilter(); }));
    public ICommand RemoveCommand => new AsyncRelayCommand<DownloadJob>(async job =>
    {
        if (job is null || job.IsActive || _resuming.ContainsKey(job.Id)) return;
        try
        {
            await _manager.ForgetAsync(job);
            Jobs.Remove(job);
        }
        catch (InvalidOperationException)
        {
            // A paused process can still be shutting down on another page. Keep its queue row.
            QueueStatus = "This download is still finishing an operation. Wait briefly before removing it.";
        }
        catch (Exception exception)
        {
            QueueStatus = "The download could not be removed from the saved queue. It was kept; try again.";
            Logging.Add(LogLevel.Warning, "Downloads", $"Queue removal failed: {exception.GetType().Name}.", job.AppId, job.Id);
        }
    });
    public ICommand OpenFolderCommand => new RelayCommand<DownloadJob>(x=>{if(x is not null && !string.IsNullOrWhiteSpace(x.TargetFolder) && Directory.Exists(x.TargetFolder)) try{Process.Start(new ProcessStartInfo(x.TargetFolder){UseShellExecute=true});}catch{}});
    public ICommand RefreshCommand => new RelayCommand(RefreshFilter);
    public ICommand NavigateLibraryCommand => new RelayCommand(()=>Navigation.Navigate<LibraryPage>());

    private async Task RunDownloadActionAsync(DownloadJob? job, Func<Task> action)
    {
        if (job is null) return;
        try { await action(); }
        catch (OperationCanceledException) { QueueStatus = "Operation cancelled · existing files were kept."; }
        catch (Exception exception)
        {
            QueueStatus = exception is DownloadQueuePersistenceException
                ? "The download state could not be saved. Keep this app open and try again before closing it."
                : "This operation failed. Existing files were kept; check the download details and try again.";
            job.AppendLog(QueueStatus);
            Logging.Add(LogLevel.Warning, "Downloads", $"Download action failed: {exception.GetType().Name}.", job.AppId, job.Id);
        }
        finally { RefreshFilter(); }
    }

    private static bool IsRyuuOrModJob(DownloadJob job) =>
        DownloadJobPolicy.UsesModDownloader(job.DownloadMode, job.DepotId, job.TargetFolder);

    private async Task StartAsync(DownloadJob? job)
    {
        if (job is null || job.IsActive) return;
        if (job.State == DownloadJobState.Queued && _pendingQueueEdits > 0)
        {
            QueueStatus = "Wait until queue changes have been saved, then start this download.";
            return;
        }
        if (IsRyuuOrModJob(job))
            await ResumeAsync(job);
        else
            await _manager.StartAsync(job);
        RefreshFilter();
    }

    private async Task RetryJobAsync(DownloadJob? job)
    {
        if (job is null || job.IsActive) return;
        if (IsRyuuOrModJob(job))
            await ResumeAsync(job);
        else
            await _manager.RetryAsync(job);
        RefreshFilter();
    }

    private async Task ResumeAsync(DownloadJob? job)
    {
        if (job is null || job.IsActive) return;
        if (IsRyuuOrModJob(job))
        {
            // A second Resume click while one is still running must not start a second
            // DepotDownloaderMod for the same job — the two would race on the same files.
            if (!_resuming.TryAdd(job.Id, 0)) return;
            try
            {
                await ResumeModJobAsync(job);
            }
            finally
            {
                _resuming.TryRemove(job.Id, out _);
            }
        }
        else { await _manager.StartAsync(job); }
        RefreshFilter();
    }

    private readonly ConcurrentDictionary<Guid, byte> _resuming = new();

    private async Task ResumeModJobAsync(DownloadJob job)
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        try { _manager.RegisterJob(job.Id, cts); }
        catch (InvalidOperationException)
        {
            // Another page can own this job. Keep its state and cancellation token intact.
            return;
        }

        var acceptingProgress = 1;
        try
        {
            var storage = DownloadStorageGuard.Check(job.TargetFolder);
            if (!storage.CanDownload)
            {
                job.State = DownloadJobState.Failed;
                job.Status = storage.Message;
                return;
            }
            var source = DownloadModeSource(job);
            if (string.IsNullOrWhiteSpace(job.DownloadMode))
                job.DownloadMode = source is null ? "DepotDownloaderMod (Ryuu)" : $"DepotDownloaderMod ({source})";

            var repairing = job.State == DownloadJobState.Completed;
            job.State = DownloadJobState.Preparing;
            job.Status = repairing ? "Verifying existing files with the downloader" : "Resuming from existing files";
            job.Finished = null;
            job.ExitCode = null;
            job.ClearLiveStats();
            job.DepotTotalBytes = 0;
            job.DepotBytesCompleted = 0;
            job.DepotsSeen = 0;
            if (repairing) job.Progress = 0;
            await SaveQueueJobAsync(job);
            using var progress = new Steamy.Controls.BufferedDownloadProgress(msg =>
            {
                if (Volatile.Read(ref acceptingProgress) == 0 || token.IsCancellationRequested || !job.IsActive) return;
                if (!GameDownloadProgressMessage.TryApply(job, msg)) job.Status = msg;
            });

            var result = await Task.Run(() => _ryuu.ResumeDownloadAsync(job.AppId, job.TargetFolder, progress, token));
            token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref acceptingProgress, 0);
            job.State = result.Succeeded ? DownloadJobState.Completed : DownloadJobState.Failed;
            job.Status = result.Message;
            job.ClearLiveStats();
            job.Finished = DateTime.Now;
            if (result.Succeeded) job.Progress = 100;
        }
        catch (OperationCanceledException)
        {
            var wasPaused = _manager.IsPauseRequested(job.Id);
            job.State = wasPaused ? DownloadJobState.Paused : DownloadJobState.Cancelled;
            job.Status = wasPaused ? "Paused — resume will continue from existing files" : "Cancelled — downloaded files were kept";
            job.ClearLiveStats();
            job.Finished = DateTime.Now;
        }
        catch (Exception exception)
        {
            job.State = DownloadJobState.Failed;
            job.Status = DownloadFailurePolicy.Describe(exception);
            job.ClearLiveStats();
            job.Finished = DateTime.Now;
            Logging.Add(LogLevel.Error, "DownloadManager", $"Resume failed: {exception.GetType().Name}", job.AppId, job.Id);
        }
        finally
        {
            Interlocked.Exchange(ref acceptingProgress, 0);
            try
            {
                var queueStore = App.Services?.GetService(typeof(IDownloadQueueStore)) as IDownloadQueueStore;
                if (queueStore is not null) await queueStore.SaveAsync(job);
            }
            finally { _manager.UnregisterJob(job.Id); }
        }
    }

    /// <summary>
    /// The manifest source a download was started with, read from its download mode
    /// ("DepotDownloaderMod (Zaza)" → Zaza). Null when the mode names no known source.
    /// </summary>
    private static ManifestSource? DownloadModeSource(DownloadJob job)
    {
        var mode = job.DownloadMode ?? string.Empty;
        foreach (var name in new[] { "Ryuu", "Zaza", "Hubcap", "DepotBox", "Sushi" })
        {
            if (mode.Contains(name, StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<ManifestSource>(name, out var source))
                return source;
        }

        return null;
    }
    public int ActiveCount => Jobs.Count(x=>x.IsActive);
    public int CompletedCount => Jobs.Count(x=>x.State==DownloadJobState.Completed);
    public int QueuedJobCount => Jobs.Count(x=>x.State==DownloadJobState.Queued);
    public int FailedCount => Jobs.Count(x=>x.State==DownloadJobState.Failed);
    public override Task OnNavigatedToAsync(){ Settings=_settings.Load(); LoadTransferSettings(); OnPropertyChanged(nameof(Settings)); RefreshFilter(); return Task.CompletedTask; }
    private void RefreshFilter()
    {
        var desired = Jobs.Where(job => DownloadPresentation.Matches(job.GameName, job.AppId, job.State, SearchText, SelectedFilter))
            .Where(job => GitHubManifestIndex.Matches(job.DownloadMode, SelectedSourceFilter))
            .OrderBy(job => DownloadPresentation.StateOrder(job.State))
            .ThenByDescending(job => job.State == DownloadJobState.Queued ? (int)job.Priority : 0)
            .ThenBy(job => job.State == DownloadJobState.Queued ? job.QueuePosition == 0 ? job.Started.Ticks : job.QueuePosition : 0)
            .ThenByDescending(job => job.IsTerminal ? job.Finished ?? job.Started : DateTime.MinValue)
            .ToList();
        DownloadPresentation.Synchronize(FilteredJobs, desired);
        if (SelectedJob is not null && !FilteredJobs.Contains(SelectedJob)) SelectedJob = null;
        RaiseCounts();
        OnPropertyChanged(nameof(ShowEmptyResults));
        OnPropertyChanged(nameof(ResultLabel));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(EmptyStateMessage));
        UpdateSections();
    }
}
