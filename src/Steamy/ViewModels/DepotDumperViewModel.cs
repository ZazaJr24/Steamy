using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

/// <summary>
/// The Depot Dumper: dump the Lua and depot manifests of an app into one folder, then send them to
/// the private dump repository. The page is deliberately narrow in scope — dump, look at the
/// result, share — because that is all a contributor ever needs to do here.
/// </summary>
public sealed class DepotDumperViewModel : ObservableObject
{
    private readonly IManifestShareService _sharing;
    private readonly IManifestSourceService _sources;
    private readonly ISettingsService _settings;
    private readonly IGameLocatorService _gameLocator;
    private readonly ILoggingService _logging;
    private CancellationTokenSource? _runCts;

    private string _appIdText = string.Empty;
    private string _gameSearchText = string.Empty;
    private string _status = "Pick a game or type an App ID, then dump.";
    private string _log = string.Empty;
    private bool _isBusy;
    private bool _hasResult;
    private bool _resultSuccess;
    private string _resultMessage = string.Empty;
    private string _resultDetail = string.Empty;
    private ManifestSourceInfo? _selectedSource;
    private string _targetFolder = string.Empty;
    private InstalledGameEntry? _selectedGame;
    private string? _lastDumpFolder;
    private string _steamUsername = string.Empty;
    private bool _useSteamLogin;

    public DepotDumperViewModel(
        IManifestShareService sharing,
        IManifestSourceService sources,
        ISettingsService settings,
        IGameLocatorService gameLocator,
        ILoggingService logging)
    {
        _sharing = sharing;
        _sources = sources;
        _settings = settings;
        _gameLocator = gameLocator;
        _logging = logging;

        AvailableSources = new ObservableCollection<ManifestSourceInfo>(sources.Sources);
        _selectedSource = AvailableSources.FirstOrDefault();
        _targetFolder = settings.Load().ManifestDumpFolder;
        _steamUsername = settings.Load().DumperSteamUsername;

        DumpCommand = new AsyncRelayCommand(DumpAsync, () => CanRun);
        ShareCommand = new AsyncRelayCommand(ShareAsync, () => CanShare);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        RefreshGamesCommand = new AsyncRelayCommand(LoadGamesAsync);
        RevealFolderCommand = new RelayCommand(RevealFolder);

        _ = LoadGamesAsync();
    }

    /// <summary>
    /// Optional Steam account for the dump. The name is only passed to the tool; the password and
    /// the 2FA/Steam Guard code are typed by the user in the tool's own console window and are never
    /// seen, asked for or stored by Steamy.
    /// </summary>
    public string SteamUsername
    {
        get => _steamUsername;
        set
        {
            if (!SetProperty(ref _steamUsername, value)) return;
            var settings = _settings.Load();
            settings.DumperSteamUsername = value.Trim();
            _ = _settings.SaveAsync(settings);
        }
    }

    /// <summary>When set, the dump runs through your own Steam login (with Steam Guard in the console).</summary>
    public bool UseSteamLogin
    {
        get => _useSteamLogin;
        set => SetProperty(ref _useSteamLogin, value);
    }

    public ObservableCollection<ManifestSourceInfo> AvailableSources { get; }
    public ObservableCollection<InstalledGameEntry> InstalledGames { get; } = new();
    public ObservableCollection<InstalledGameEntry> FilteredGames { get; } = new();

    public string AppIdText
    {
        get => _appIdText;
        set
        {
            if (!SetProperty(ref _appIdText, value)) return;
            RaiseCommands();
            OnPropertyChanged(nameof(DumpFolderPreview));
        }
    }

    public InstalledGameEntry? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!SetProperty(ref _selectedGame, value) || value is null) return;
            AppIdText = value.AppId.ToString();
            Status = $"{value.Name} selected.";
        }
    }

    public string GameSearchText
    {
        get => _gameSearchText;
        set
        {
            if (!SetProperty(ref _gameSearchText, value)) return;
            ApplyGameFilter();
        }
    }

    public ManifestSourceInfo? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value)) return;
            OnPropertyChanged(nameof(SourceHint));
        }
    }

    public string SourceHint => _selectedSource is null
        ? string.Empty
        : _selectedSource.RequiresAuthCode
            ? $"{_selectedSource.Label} needs its key in Settings."
            : $"{_selectedSource.Label} works without a key.";

    public string TargetFolder
    {
        get => _targetFolder;
        set
        {
            if (!SetProperty(ref _targetFolder, value)) return;
            var settings = _settings.Load();
            settings.ManifestDumpFolder = value;
            _ = _settings.SaveAsync(settings);
            OnPropertyChanged(nameof(DumpFolderPreview));
        }
    }

    /// <summary>Mirrors exactly where the service writes, so the preview is never a lie.</summary>
    public string DumpFolderPreview
    {
        get
        {
            var appId = int.TryParse(AppIdText, out var parsed) && parsed > 0 ? parsed.ToString() : "…";
            return string.IsNullOrWhiteSpace(TargetFolder)
                ? Path.Combine(DefaultRoot, "DepotDumps", $"app-{appId}")
                : Path.Combine(TargetFolder, $"app-{appId}");
        }
    }

    private string DefaultRoot
    {
        get
        {
            var settings = _settings.Load();
            return !string.IsNullOrWhiteSpace(settings.DownloadFolder)
                ? settings.DownloadFolder
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy");
        }
    }

    public string ShareTarget => _sharing.TargetDescription;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string Log
    {
        get => _log;
        private set => SetProperty(ref _log, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            RaiseCommands();
        }
    }

    public bool IsIdle => !IsBusy;

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

    public string ResultDetail
    {
        get => _resultDetail;
        private set => SetProperty(ref _resultDetail, value);
    }

    public string GameCountLabel => FilteredGames.Count == 0 ? "No games found" : $"{FilteredGames.Count} games";

    public bool CanRun => !IsBusy && int.TryParse(AppIdText, out var appId) && appId > 0;
    public bool CanShare => !IsBusy && _lastDumpFolder is not null;

    public IAsyncRelayCommand DumpCommand { get; }
    public IAsyncRelayCommand ShareCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand RefreshGamesCommand { get; }
    public ICommand RevealFolderCommand { get; }

    private async Task LoadGamesAsync()
    {
        try
        {
            var games = await Task.Run(() => _gameLocator.ListInstalledGames().OrderBy(game => game.Name).ToList());
            InstalledGames.Clear();
            foreach (var game in games) InstalledGames.Add(game);
            ApplyGameFilter();
        }
        catch (Exception exception)
        {
            _logging.Add(LogLevel.Warning, "DepotDumper", $"Game list failed: {exception.Message}");
        }
    }

    private void ApplyGameFilter()
    {
        FilteredGames.Clear();
        var search = GameSearchText?.Trim() ?? string.Empty;
        foreach (var game in InstalledGames)
        {
            if (string.IsNullOrEmpty(search)
                || game.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || game.AppId.ToString().Contains(search, StringComparison.Ordinal))
            {
                FilteredGames.Add(game);
            }
        }

        OnPropertyChanged(nameof(GameCountLabel));
    }

    private async Task DumpAsync()
    {
        if (!int.TryParse(AppIdText, out var appId) || appId <= 0) return;
        var source = SelectedSource?.Source ?? ManifestSource.Zaza;

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        IsBusy = true;
        HasResult = false;
        Log = string.Empty;
        Status = $"Dumping App {appId} from {source}…";

        var progress = new Progress<string>(line => Append(line));
        try
        {
            var account = UseSteamLogin && !string.IsNullOrWhiteSpace(SteamUsername) ? SteamUsername.Trim() : null;
            if (UseSteamLogin && string.IsNullOrWhiteSpace(SteamUsername))
                Append("Steam login is on but no account name is set — dumping without it.");

            var result = await _sharing.DumpAsync(appId, source, TargetFolder, account, progress, _runCts.Token);
            ShowResult(result.Succeeded, result.Succeeded ? "Dump ready" : "Dump failed",
                result.Message,
                result.FileCount > 0
                    ? $"{result.FileCount} files · {DownloadFormat.Bytes(result.TotalBytes)}\n{result.Folder}"
                    : result.Folder);

            if (result.Succeeded) _lastDumpFolder = result.Folder;

            Status = result.Message;
            OnPropertyChanged(nameof(CanShare));
            ShareCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception exception)
        {
            ShowResult(false, "Dump failed", $"{exception.GetType().Name}: {exception.Message}", string.Empty);
            Status = "Dump failed.";
        }
        finally
        {
            IsBusy = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private async Task ShareAsync()
    {
        if (!int.TryParse(AppIdText, out var appId) || _lastDumpFolder is null) return;

        _runCts = new CancellationTokenSource();
        IsBusy = true;
        Status = $"Sending the dump to {ShareTarget}…";

        var progress = new Progress<string>(line => Append(line));
        try
        {
            var result = await _sharing.ShareAsync(appId, _lastDumpFolder, progress, _runCts.Token);
            ShowResult(result.Succeeded, result.Succeeded ? "Sent" : "Sending failed", result.Message,
                result.RemotePath is null ? string.Empty : $"{ShareTarget}/{result.RemotePath}");
            Status = result.Succeeded ? "Thanks — the dump is on its way." : result.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanShare));
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void Cancel()
    {
        _runCts?.Cancel();
        Status = "Cancelling…";
    }

    private void RevealFolder()
    {
        var folder = _lastDumpFolder;
        if (folder is null || !Directory.Exists(folder)) folder = TargetFolder.Length > 0 ? TargetFolder : DefaultRoot;
        if (!Directory.Exists(folder)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _logging.Add(LogLevel.Warning, "DepotDumper", $"Could not open {folder}: {exception.Message}");
        }
    }

    private void Append(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        Log = Log.Length == 0 ? $"{stamp}  {line}" : $"{Log}{Environment.NewLine}{stamp}  {line}";
    }

    private void ShowResult(bool success, string title, string message, string detail)
    {
        ResultSuccess = success;
        ResultMessage = string.IsNullOrWhiteSpace(message) ? title : $"{title} — {message}";
        ResultDetail = detail;
        HasResult = true;
        OnPropertyChanged(nameof(CanShare));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        DumpCommand.NotifyCanExecuteChanged();
        ShareCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}
