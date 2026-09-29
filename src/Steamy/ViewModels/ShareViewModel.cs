using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;

namespace Steamy.ViewModels;

public enum ShareItemState
{
    Idle,
    Sending,
    Sent,
    Failed
}

public enum ShareFilter
{
    All,
    New,
    Installed,
    Lua,
    Manifests
}

/// <summary>One row of the Share page: one app with its manifests (and Lua, when there is one).</summary>
public sealed class ShareItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected;
    private ShareHistoryEntry? _shared;
    private ShareItemState _state;

    public ShareItemViewModel(ShareCandidate candidate, string name, ShareHistoryEntry? shared, Action selectionChanged)
    {
        Candidate = candidate;
        Name = name;
        _shared = shared;
        _selectionChanged = selectionChanged;
    }

    public ShareCandidate Candidate { get; }
    public int AppId => Candidate.AppId;
    public string Name { get; }
    public ShareSourceKind SourceKind => Candidate.Source;
    public string SourceLabel => Candidate.Source switch
    {
        ShareSourceKind.SteamLibrary => "Installed",
        ShareSourceKind.Lua => "Lua",
        _ => "Manifests"
    };
    public string AppIdLabel => $"App {AppId}";
    public string SizeLabel => Candidate.NeedsManifestFetch ? "—" : DownloadFormat.Bytes(Candidate.TotalBytes);
    public string CapsuleUrl => $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{AppId}/capsule_184x69.jpg";

    /// <summary>True for an installed game whose manifests get fetched from a source when shared.</summary>
    public bool NeedsFetch => Candidate.NeedsManifestFetch;

    public string ContentSummary
    {
        get
        {
            if (Candidate.NeedsManifestFetch)
            {
                var depots = Candidate.Depots.Count;
                return Candidate.Depots.Count == 0
                    ? "manifests are fetched on share"
                    : depots == 1 ? "1 depot · manifests are fetched on share"
                    : $"{depots} depots · manifests are fetched on share";
            }

            var manifests = Candidate.ManifestCount;
            var parts = new List<string> { manifests == 1 ? "1 manifest" : $"{manifests} manifests" };
            if (Candidate.HasLua) parts.Add("Lua");
            var others = Candidate.Files.Count - manifests - Candidate.Files.Count(file => file.EntryName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            if (others > 0) parts.Add(others == 1 ? "1 more file" : $"{others} more files");
            return string.Join(" · ", parts);
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) _selectionChanged();
        }
    }

    public ShareHistoryEntry? Shared
    {
        get => _shared;
        set
        {
            if (!SetProperty(ref _shared, value)) return;
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(StatusLabel));
        }
    }

    public ShareItemState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(StatusLabel));
        }
    }

    public bool IsNew => Shared is null;

    public string StatusLabel => State switch
    {
        ShareItemState.Sending => "Sending…",
        ShareItemState.Sent => "Sent",
        ShareItemState.Failed => "Failed",
        _ => Shared is null ? "New" : $"Shared {Shared.SharedUtc.ToLocalTime():dd MMM}"
    };
}

/// <summary>
/// The Share page: lists everything the user can share — installed games, Lua scripts and every
/// cached depot manifest, installed or not — and sends any selection to the dump repository in
/// one commit, or saves it as one ZIP to pass on anywhere.
/// </summary>
public sealed class ShareViewModel : ViewModelBase
{
    private static readonly TimeSpan RescanAfter = TimeSpan.FromMinutes(2);

    private readonly IManifestShareService _sharing;
    private readonly ISettingsService _settings;
    private readonly IOwnedGamesService _ownedGames;
    private readonly ObservableCollection<ShareItemViewModel> _items = new();
    private CancellationTokenSource? _runCts;
    private Task? _scanTask;
    private DateTime _lastScan = DateTime.MinValue;

    private string _searchText = string.Empty;
    private ShareFilter _filter = ShareFilter.All;
    private bool _isScanning;
    private bool _isBusy;
    private bool _hasToken;
    private double _progress;
    private string _progressText = string.Empty;
    private bool _hasResult;
    private bool _resultSuccess;
    private string _resultMessage = string.Empty;
    private string? _resultUrl;
    private string _scanSummary = "Not scanned yet.";
    private bool _selectNewAfterScan;
    private IReadOnlyList<OwnedGame> _owned = Array.Empty<OwnedGame>();
    private bool _ownedLoaded;
    private bool _ownedFailed;

    public ShareViewModel(
        IAppDataStore store,
        INavigationService navigation,
        ILoggingService logging,
        IManifestShareService sharing,
        ISettingsService settings,
        IOwnedGamesService ownedGames) : base(store, navigation, logging)
    {
        _sharing = sharing;
        _settings = settings;
        _ownedGames = ownedGames;

        // A new dump or share makes the cached scan stale; the next visit scans again.
        _sharing.ShareablesChanged += (_, _) => _lastScan = DateTime.MinValue;

        Items = CollectionViewSource.GetDefaultView(_items);
        Items.Filter = Matches;

        RescanCommand = new AsyncRelayCommand(() => ScanAsync(force: true), () => !IsBusy);
        OwnedGamesCommand = new AsyncRelayCommand(LoadOwnedGamesAsync, () => !IsLoadingOwned && !IsBusy);
        ShareSelectedCommand = new AsyncRelayCommand(ShareSelectedAsync, () => CanShare);
        CancelCommand = new RelayCommand(() => _runCts?.Cancel(), () => IsBusy);
        SelectAllCommand = new RelayCommand(() => SetSelection(_ => true, visibleOnly: true));
        SelectNewCommand = new RelayCommand(() => SetSelection(item => item.IsNew, visibleOnly: false));
        ClearSelectionCommand = new RelayCommand(() => SetSelection(_ => false, visibleOnly: false));
        OpenSettingsCommand = new RelayCommand(() => Navigation.Navigate<SettingsPage>());
        OpenLibraryCommand = new RelayCommand(() => Navigation.Navigate<LibraryPage>());
        OpenResultCommand = new RelayCommand(OpenResult, () => _resultUrl is not null);
    }

    /// <summary>Filtered, searchable view of all shareable apps.</summary>
    public ICollectionView Items { get; }

    public IAsyncRelayCommand RescanCommand { get; }
    public IAsyncRelayCommand OwnedGamesCommand { get; }
    public IAsyncRelayCommand ShareSelectedCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNewCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenLibraryCommand { get; }
    public IRelayCommand OpenResultCommand { get; }

    // ---- filter ---------------------------------------------------------------------------------

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) RefreshView();
        }
    }

    public bool ShowAll { get => _filter == ShareFilter.All; set { if (value) SetFilter(ShareFilter.All); } }
    public bool ShowNew { get => _filter == ShareFilter.New; set { if (value) SetFilter(ShareFilter.New); } }
    public bool ShowInstalled { get => _filter == ShareFilter.Installed; set { if (value) SetFilter(ShareFilter.Installed); } }
    public bool ShowLua { get => _filter == ShareFilter.Lua; set { if (value) SetFilter(ShareFilter.Lua); } }
    public bool ShowManifests { get => _filter == ShareFilter.Manifests; set { if (value) SetFilter(ShareFilter.Manifests); } }

    // ---- numbers --------------------------------------------------------------------------------

    public int TotalCount => _items.Count;
    public int NewCount => _items.Count(item => item.IsNew);
    public int InstalledCount => _items.Count(item => item.SourceKind == ShareSourceKind.SteamLibrary);
    public int LuaCount => _items.Count(item => item.Candidate.HasLua);
    public int FetchCount => _items.Count(item => item.NeedsFetch);
    public int SelectedCount => _items.Count(item => item.IsSelected);
    public bool HasItems => _items.Count > 0;
    public bool ShowEmpty => _items.Count == 0 && !IsScanning;
    public bool HasNoMatches => _items.Count > 0 && Items.IsEmpty;
    public string FetchCountLabel => FetchCount.ToString("N0");

    public string TotalCountLabel => TotalCount.ToString("N0");
    public string NewCountLabel => NewCount.ToString("N0");
    public string SourcesLabel => $"{InstalledCount:N0} installed · {LuaCount:N0} with Lua · {TotalCount - InstalledCount:N0} not installed";
    public string TotalSizeLabel => DownloadFormat.Bytes(_items.Where(item => !item.NeedsFetch).Sum(item => item.Candidate.TotalBytes));
    public string TotalFilesLabel
    {
        get
        {
            var files = _items.Sum(item => item.Candidate.Files.Count);
            return files == 1 ? "1 file" : $"{files:N0} files";
        }
    }

    public string LastShareLabel => _sharing.LastSharedUtc is { } last
        ? $"Last share {last.ToLocalTime():dd MMM, HH:mm}"
        : "Nothing shared yet";

    public string SelectionSummary
    {
        get
        {
            var selected = _items.Where(item => item.IsSelected).ToList();
            if (selected.Count == 0) return "Nothing selected";
            var files = selected.Sum(item => item.Candidate.Files.Count);
            return $"{(selected.Count == 1 ? "1 game" : $"{selected.Count:N0} games")} · {files:N0} files · {DownloadFormat.Bytes(selected.Sum(item => item.Candidate.TotalBytes))}";
        }
    }

    public string ShareButtonLabel => SelectedCount switch
    {
        0 => "Share selected",
        1 => "Share 1 game",
        var count => $"Share {count:N0} games"
    };

    public string TargetLabel => _sharing.TargetDescription;
    public string BranchLabel => $"Branch {_sharing.Target.Branch}";

    public bool HasToken
    {
        get => _hasToken;
        private set
        {
            if (!SetProperty(ref _hasToken, value)) return;
            OnPropertyChanged(nameof(TokenStatus));
            OnPropertyChanged(nameof(CanShare));
            ShareSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    public string TokenStatus => HasToken ? "Token stored · ready to send" : "No token — add one in Settings";

    public string ScanSummary
    {
        get => _scanSummary;
        private set => SetProperty(ref _scanSummary, value);
    }

    // ---- state ----------------------------------------------------------------------------------

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetProperty(ref _isScanning, value)) return;
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(CanShare));
            OnPropertyChanged(nameof(CanExport));
            ShareSelectedCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            RescanCommand.NotifyCanExecuteChanged();
            OwnedGamesCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsIdle => !IsBusy;
    public bool CanShare => !IsBusy && HasToken && SelectedCount > 0;
    public bool CanExport => !IsBusy && SelectedCount > 0;

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public bool HasResult
    {
        get => _hasResult;
        private set => SetProperty(ref _hasResult, value);
    }

    public bool ResultSuccess
    {
        get => _resultSuccess;
        private set => SetProperty(ref _resultSuccess, value);
    }

    public string ResultMessage
    {
        get => _resultMessage;
        private set => SetProperty(ref _resultMessage, value);
    }

    public bool HasResultLink => _resultUrl is not null;

    public ICommand DismissResultCommand => new RelayCommand(() => HasResult = false);

    // ---- lifecycle ------------------------------------------------------------------------------

    public override async Task OnNavigatedToAsync()
    {
        await RefreshTokenAsync();
        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(BranchLabel));
        OnPropertyChanged(nameof(LastShareLabel));
        await ScanAsync(force: false);
    }

    /// <summary>Scans once (or again when the last scan is old) — used by the Dashboard for its counts.</summary>
    public Task EnsureScannedAsync() => ScanAsync(force: false);

    /// <summary>Opens the page with every new item selected, ready for one click on Share.</summary>
    public void PrepareShareAllNew()
    {
        SetFilter(ShareFilter.All);
        SearchText = string.Empty;
        if (IsScanning || _lastScan == DateTime.MinValue) _selectNewAfterScan = true;
        else SetSelection(item => item.IsNew, visibleOnly: false);
        Navigation.Navigate<SharePage>();
    }

    public async Task ScanAsync(bool force)
    {
        if (_scanTask is { IsCompleted: false })
        {
            await _scanTask;
            return;
        }

        if (!force && DateTime.UtcNow - _lastScan < RescanAfter) return;
        _scanTask = RunScanAsync();
        await _scanTask;
    }

    private async Task RunScanAsync()
    {
        IsScanning = true;
        try
        {
            var result = await _sharing.ScanAsync();

            // When the account library was loaded once, every scan folds it back in, so the list
            // keeps covering the whole account even after a rescan.
            var scanned = _ownedLoaded ? OwnedGamesMerge.MergeOwned(result.Items, _owned) : result.Items;
            var selected = _items.Where(item => item.IsSelected).Select(item => item.Candidate.Fingerprint).ToHashSet();
            var names = Store.Games.GroupBy(game => game.AppId).ToDictionary(group => group.Key, group => group.First().Name);

            _items.Clear();
            foreach (var candidate in scanned)
            {
                var name = !ManifestLibraryScanner.HasRealName(candidate.Name) && names.TryGetValue(candidate.AppId, out var known)
                    ? known
                    : candidate.Name;
                var item = new ShareItemViewModel(candidate, name, _sharing.FindShared(candidate), OnSelectionChanged);
                if (selected.Contains(candidate.Fingerprint)) item.IsSelected = true;
                _items.Add(item);
            }

            _lastScan = DateTime.UtcNow;
            ScanSummary = DescribeScan(result);

            if (_selectNewAfterScan)
            {
                _selectNewAfterScan = false;
                SetSelection(item => item.IsNew, visibleOnly: false);
            }
        }
        catch (Exception exception)
        {
            Logging.Add(LogLevel.Warning, "Sharing", $"Scan failed: {exception.Message}");
            ScanSummary = $"The scan failed: {exception.Message}";
        }
        finally
        {
            IsScanning = false;
            RaiseCounts();
        }
    }

    // ---- account library ----------------------------------------------------------------------

    public bool IsLoadingOwned { get => _ownedLoading; private set { if (SetProperty(ref _ownedLoading, value)) { OnPropertyChanged(nameof(ShowEmpty)); OwnedGamesCommand.NotifyCanExecuteChanged(); } } }
    private bool _ownedLoading;

    /// <summary>True once the account library was loaded (or definitively failed).</summary>
    public bool OwnedLoaded => _ownedLoaded;
    public bool OwnedFailed => _ownedFailed;

    public string OwnedStatusLabel => _ownedLoaded
        ? $"{_owned.Count:N0} account games in the list"
        : _ownedFailed ? _ownedFailedMessage
        : "Account games not loaded yet — only installed games and cached manifests are listed.";

    private string _ownedFailedMessage = string.Empty;

    /// <summary>
    /// Loads the user's whole account library and folds it into the list: every owned game without
    /// local manifests becomes an auto-fetch entry, so the page covers all games, not only the
    /// installed ones.
    /// </summary>
    public async Task LoadOwnedGamesAsync()
    {
        if (IsLoadingOwned) return;
        IsLoadingOwned = true;
        try
        {
            var result = await _ownedGames.LoadAsync();
            if (result.Succeeded)
            {
                _owned = result.Games;
                _ownedLoaded = true;
                _ownedFailed = false;
                await ScanAsync(force: true);
                if (SelectedCount == 0)
                    SetSelection(item => item.Candidate.Files.Count == 0, visibleOnly: false);
            }
            else
            {
                _ownedFailed = true;
                _ownedFailedMessage = result.Message;
                ShowResult(false, result.Message, null);
            }
        }
        catch (Exception exception)
        {
            _ownedFailed = true;
            _ownedFailedMessage = $"The account library could not be loaded: {exception.Message}";
            Logging.Add(LogLevel.Warning, "Sharing", _ownedFailedMessage);
            ShowResult(false, _ownedFailedMessage, null);
        }
        finally
        {
            IsLoadingOwned = false;
            OnPropertyChanged(nameof(OwnedLoaded));
            OnPropertyChanged(nameof(OwnedFailed));
            OnPropertyChanged(nameof(OwnedStatusLabel));
        }
    }

    // ---- actions --------------------------------------------------------------------------------

    private async Task ShareSelectedAsync()
    {
        var selected = _items.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0) return;

        _runCts = new CancellationTokenSource();
        IsBusy = true;
        HasResult = false;
        Progress = 0;
        ProgressText = "Preparing…";
        foreach (var item in selected) item.State = ShareItemState.Sending;

        var progress = new Progress<ShareBatchProgress>(update =>
        {
            Progress = Math.Round(update.Fraction * 100, 1);
            ProgressText = update.Message;
        });

        try
        {
            var result = await _sharing.ShareManyAsync(selected.Select(item => item.Candidate).ToList(), progress, _runCts.Token);
            foreach (var item in selected)
            {
                var shared = _sharing.FindShared(item.Candidate);
                item.Shared = shared;
                item.State = shared is not null ? ShareItemState.Sent : ShareItemState.Failed;
                if (shared is not null) item.IsSelected = false;
            }

            ShowResult(result.Succeeded, result.Message, result.CommitUrl);
        }
        catch (Exception exception)
        {
            foreach (var item in selected) item.State = ShareItemState.Failed;
            ShowResult(false, $"Sharing failed: {exception.Message}", null);
        }
        finally
        {
            IsBusy = false;
            _runCts.Dispose();
            _runCts = null;
            OnPropertyChanged(nameof(LastShareLabel));
            RaiseCounts();
        }
    }

    /// <summary>Called by the page after the user picked where to save the ZIP.</summary>
    public async Task ExportSelectedAsync(string zipPath)
    {
        var selected = _items.Where(item => item.IsSelected).Select(item => item.Candidate).ToList();
        if (selected.Count == 0) return;

        IsBusy = true;
        HasResult = false;
        Progress = 0;
        ProgressText = "Packing the ZIP…";
        try
        {
            var result = await _sharing.ExportAsync(selected, zipPath);
            ShowResult(result.Succeeded, result.Message, result.Succeeded ? Path.GetDirectoryName(zipPath) : null);
        }
        catch (Exception exception)
        {
            // Packing itself is guarded inside the service; this catches everything around it
            // (dialog, disk full, unexpected IO) so the page never dies on an export.
            Logging.Add(LogLevel.Warning, "Sharing", $"Export failed: {exception.Message}");
            ShowResult(false, $"The ZIP could not be written: {exception.Message}", null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public string SuggestedExportName => $"steamy-share-{DateTime.Now:yyyyMMdd-HHmm}.zip";

    private void OpenResult()
    {
        if (_resultUrl is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_resultUrl) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Logging.Add(LogLevel.Warning, "Sharing", $"Could not open {_resultUrl}: {exception.Message}");
        }
    }

    private void ShowResult(bool success, string message, string? url)
    {
        ResultSuccess = success;
        ResultMessage = message;
        _resultUrl = url;
        OnPropertyChanged(nameof(HasResultLink));
        OpenResultCommand.NotifyCanExecuteChanged();
        HasResult = true;
    }

    private async Task RefreshTokenAsync()
    {
        try { HasToken = await _sharing.HasTokenAsync(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { HasToken = false; }
    }

    // ---- helpers --------------------------------------------------------------------------------

    private void SetFilter(ShareFilter filter)
    {
        if (_filter == filter) return;
        _filter = filter;
        OnPropertyChanged(nameof(ShowAll));
        OnPropertyChanged(nameof(ShowNew));
        OnPropertyChanged(nameof(ShowInstalled));
        OnPropertyChanged(nameof(ShowLua));
        OnPropertyChanged(nameof(ShowManifests));
        RefreshView();
    }

    private bool Matches(object value)
    {
        if (value is not ShareItemViewModel item) return false;
        var passesFilter = _filter switch
        {
            ShareFilter.New => item.IsNew,
            ShareFilter.Installed => item.SourceKind == ShareSourceKind.SteamLibrary,
            ShareFilter.Lua => item.Candidate.HasLua,
            ShareFilter.Manifests => item.SourceKind == ShareSourceKind.Manifests,
            _ => true
        };
        if (!passesFilter) return false;

        var search = _searchText.Trim();
        return search.Length == 0
            || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.AppId.ToString().Contains(search, StringComparison.Ordinal)
            || (item.Name.Length > 0 && item.AppIdLabel.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshView()
    {
        Items.Refresh();
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private void SetSelection(Func<ShareItemViewModel, bool> select, bool visibleOnly)
    {
        var scope = visibleOnly ? Items.Cast<ShareItemViewModel>().ToList() : _items.ToList();
        foreach (var item in scope) item.IsSelected = select(item);
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(ShareButtonLabel));
        OnPropertyChanged(nameof(CanShare));
        OnPropertyChanged(nameof(CanExport));
        ShareSelectedCommand.NotifyCanExecuteChanged();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(NewCount));
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(LuaCount));
        OnPropertyChanged(nameof(FetchCount));
        OnPropertyChanged(nameof(FetchCountLabel));
        OnPropertyChanged(nameof(TotalCountLabel));
        OnPropertyChanged(nameof(NewCountLabel));
        OnPropertyChanged(nameof(SourcesLabel));
        OnPropertyChanged(nameof(TotalSizeLabel));
        OnPropertyChanged(nameof(TotalFilesLabel));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
        OnSelectionChanged();
        Items.Refresh();
    }

    private static string DescribeScan(ShareScanResult result)
    {
        var parts = new List<string>();
        parts.Add(result.SteamRoot.Length > 0 ? $"Steam library at {result.SteamRoot}" : "no Steam installation found");
        parts.Add(result.LuaFolders.Count switch
        {
            0 => "no Lua folder",
            1 => $"Luas in {result.LuaFolders[0]}",
            var count => $"{count} Lua folders"
        });
        return "Looked at " + string.Join(" and ", parts) + ".";
    }
}
