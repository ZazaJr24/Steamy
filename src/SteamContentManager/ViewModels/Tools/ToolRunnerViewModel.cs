using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamContentManager.Models;
using SteamContentManager.Services;

namespace SteamContentManager.ViewModels;

public class ToolRunnerViewModel : ObservableObject
{
    private readonly ILocalToolRunner _runner;
    private readonly ISettingsService _settingsService;
    private readonly IGitHubToolDownloadService? _downloadService;
    private readonly GitHubToolDefinition? _toolDefinition;
    private readonly Func<AppSettings, string> _readPath;
    private readonly Action<AppSettings, string> _writePath;
    private readonly string _expectedFileNameHint;
    private readonly bool _showWindow;

    private CancellationTokenSource? _runCts;
    private string _executablePath = string.Empty;
    private string _arguments = string.Empty;
    private string _workingDirectory = string.Empty;
    private string _status = "Not configured";
    private string _version = "—";
    private string _lastMessage;
    private string _output = string.Empty;
    private bool _isBusy;
    private bool _isDownloading;
    private bool _showDone;
    private bool _doneSuccess;
    private string _doneTitle = string.Empty;
    private string _doneMessage = string.Empty;
    private bool _isInfoVisible;

    public ToolRunnerViewModel(
        ILocalToolRunner runner,
        ISettingsService settingsService,
        string pageKey,
        string title,
        string subtitle,
        string expectedFileNameHint,
        string infoText,
        string downloadUrl,
        string suggestedArguments = "",
        bool showWindow = false,
        Func<AppSettings, string>? readPath = null,
        Action<AppSettings, string>? writePath = null,
        IGitHubToolDownloadService? downloadService = null,
        GitHubToolDefinition? toolDefinition = null)
    {
        _runner = runner;
        _settingsService = settingsService;
        _downloadService = downloadService;
        _toolDefinition = toolDefinition;
        _expectedFileNameHint = expectedFileNameHint;
        _showWindow = showWindow;
        PageKey = pageKey;
        Title = title;
        Subtitle = subtitle;
        InfoText = infoText;
        DownloadUrl = downloadUrl;
        Arguments = suggestedArguments;
        _readPath = readPath ?? DefaultRead;
        _writePath = writePath ?? DefaultWrite;

        var settings = settingsService.Load();
        SettingsModel = settings;
        _executablePath = _readPath(settings);

        if (string.IsNullOrWhiteSpace(_executablePath) && _downloadService is not null && _toolDefinition is not null)
        {
            var cached = _downloadService.GetCachedPath(_toolDefinition);
            if (!string.IsNullOrWhiteSpace(cached))
                _executablePath = cached;
        }

        _lastMessage = string.IsNullOrWhiteSpace(_executablePath)
            ? "Downloading tool automatically…"
            : "Tool ready. Press Run when you are ready.";
        _status = string.IsNullOrWhiteSpace(_executablePath) ? "Downloading…" : "Ready";

        BrowseCommand = new RelayCommand<Action<string>>(_ => { });
        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync);
        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
        CancelRunCommand = new RelayCommand(CancelRun, () => IsBusy);
        CloseDoneCommand = new RelayCommand(() => ShowDone = false);
        DismissDoneCommand = new RelayCommand(() => ShowDone = false);
        ToggleInfoCommand = new RelayCommand(() => IsInfoVisible = !IsInfoVisible);
        DownloadCommand = new AsyncRelayCommand(AutoDownloadAsync);

        if (string.IsNullOrWhiteSpace(_executablePath) && _downloadService is not null && _toolDefinition is not null)
            _ = AutoDownloadAsync();
    }

    public string PageKey { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string InfoText { get; }
    public string DownloadUrl { get; }
    public string ExpectedFileNameHint => _expectedFileNameHint;
    public string ExecutablePlaceholder => $"Paste the path to {_expectedFileNameHint} or use Browse…";

    public bool HasAutoDownload => _downloadService is not null && _toolDefinition is not null;

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(DownloadButtonText));
            }
        }
    }

    public string DownloadButtonText => IsDownloading ? "Downloading…" : "Re-download from GitHub";

    public async Task PathEnteredAsync()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath)) return;
        await RefreshStatusAsync().ConfigureAwait(false);
        await PersistAsync().ConfigureAwait(false);
    }

    public AppSettings SettingsModel { get; private set; }

    public string ExecutablePath
    {
        get => _executablePath;
        set
        {
            if (SetProperty(ref _executablePath, value))
            {
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(CanRun));
            }
        }
    }

    public string DisplayPath
    {
        get => string.IsNullOrWhiteSpace(ExecutablePath)
            ? $"No executable selected ({_expectedFileNameHint})"
            : ExecutablePath;
        set { }
    }

    public string Arguments
    {
        get => _arguments;
        set => SetProperty(ref _arguments, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set
        {
            if (SetProperty(ref _workingDirectory, value)) OnPropertyChanged(nameof(DisplayWorkingDirectory));
        }
    }

    public string DisplayWorkingDirectory
    {
        get => string.IsNullOrWhiteSpace(WorkingDirectory)
            ? "Uses the folder of the selected executable"
            : WorkingDirectory;
        set { }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ToolVersion
    {
        get => _version;
        private set => SetProperty(ref _version, value);
    }

    public string LastMessage
    {
        get => _lastMessage;
        private set => SetProperty(ref _lastMessage, value);
    }

    public string Output
    {
        get => _output;
        set
        {
            if (SetProperty(ref _output, value)) OnPropertyChanged(nameof(HasOutput));
        }
    }

    public bool HasOutput => !string.IsNullOrWhiteSpace(Output);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(CanRun));
        }
    }

    public bool ShowDone
    {
        get => _showDone;
        private set => SetProperty(ref _showDone, value);
    }

    public bool DoneSuccess
    {
        get => _doneSuccess;
        private set => SetProperty(ref _doneSuccess, value);
    }

    public string DoneTitle
    {
        get => _doneTitle;
        private set => SetProperty(ref _doneTitle, value);
    }

    public string DoneMessage
    {
        get => _doneMessage;
        private set => SetProperty(ref _doneMessage, value);
    }

    public bool IsInfoVisible
    {
        get => _isInfoVisible;
        private set => SetProperty(ref _isInfoVisible, value);
    }

    public bool CanRun => !IsBusy && !IsDownloading && !string.IsNullOrWhiteSpace(ExecutablePath);

    public ICommand BrowseCommand { get; }
    public IAsyncRelayCommand RefreshStatusCommand { get; }
    public IAsyncRelayCommand RunCommand { get; }
    public ICommand CancelRunCommand { get; }
    public ICommand CloseDoneCommand { get; }
    public ICommand DismissDoneCommand { get; }
    public ICommand ToggleInfoCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }

    public async Task AutoDownloadAsync()
    {
        if (_downloadService is null || _toolDefinition is null) return;
        if (IsDownloading) return;

        if (!string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath))
        {
            Status = "Ready";
            LastMessage = "Tool ready. Press Run when you are ready.";
            return;
        }

        IsDownloading = true;
        Status = "Downloading…";
        LastMessage = $"Downloading {Title} from GitHub…";

        try
        {
            var result = await _downloadService.DownloadLatestAsync(_toolDefinition).ConfigureAwait(false);
            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.CachedPath))
            {
                ExecutablePath = result.CachedPath;
                ToolVersion = result.Version ?? "—";
                Status = "Ready";
                LastMessage = $"{Title} downloaded and ready.";
                await PersistAsync().ConfigureAwait(false);
            }
            else
            {
                Status = "Download failed";
                LastMessage = result.Message;
            }
        }
        catch (Exception ex)
        {
            Status = "Download failed";
            LastMessage = $"Auto-download failed: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public async Task SetExecutableAsync(string path)
    {
        ExecutablePath = path;
        await RefreshStatusAsync().ConfigureAwait(false);
        await PersistAsync().ConfigureAwait(false);
    }

    public async Task RefreshStatusAsync()
    {
        try
        {
            var status = await _runner.CheckAsync(ExecutablePath, _expectedFileNameHint).ConfigureAwait(false);
            Status = status.IsReady ? "Ready to run" : status.Message;
            ToolVersion = string.IsNullOrWhiteSpace(status.Version) ? "—" : status.Version;
            if (!status.IsReady) LastMessage = status.Message;
        }
        catch (Exception exception)
        {
            Status = "Error";
            LastMessage = $"The tool could not be checked: {exception.Message}";
        }
    }

    public async Task PersistAsync()
    {
        _writePath(SettingsModel, ExecutablePath);
        await _settingsService.SaveAsync(SettingsModel).ConfigureAwait(false);
    }

    public async Task RunAsync()
    {
        if (!CanRun) return;

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        IsBusy = true;
        ShowDone = false;
        Output = string.Empty;
        LastMessage = $"Running {Path.GetFileName(ExecutablePath)}…";

        try
        {
            var request = new LocalToolRunRequest(ExecutablePath, Arguments, WorkingDirectory, ShowWindow: _showWindow);
            var result = await _runner.RunAsync(request, _runCts.Token).ConfigureAwait(false);

            DoneSuccess = result.Succeeded;
            DoneTitle = result.Succeeded ? $"{Title} finished" : $"{Title} finished with an issue";
            DoneMessage = BuildMessage(result);
            Output = result.Output;
            ShowDone = true;
            LastMessage = result.Message;
            Status = result.Succeeded ? "Completed" : result.WasCancelled ? "Cancelled" : "Completed with issues";
        }
        catch (OperationCanceledException)
        {
            DoneSuccess = false;
            DoneTitle = $"{Title} cancelled";
            DoneMessage = "The run was cancelled before it finished.";
            ShowDone = true;
            LastMessage = "Cancelled.";
            Status = "Cancelled";
        }
        catch (Exception exception)
        {
            DoneSuccess = false;
            DoneTitle = $"{Title} error";
            DoneMessage = $"The run failed: {exception.Message}";
            ShowDone = true;
            LastMessage = $"Error: {exception.Message}";
            Status = "Error";
        }
        finally
        {
            IsBusy = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void CancelRun()
    {
        _runCts?.Cancel();
        LastMessage = "Cancelling…";
    }

    private static string BuildMessage(LocalToolRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(result.Message);
        if (result.ExitCode is not null) builder.AppendLine($"Exit code: {result.ExitCode}");
        if (result.WasCancelled) builder.AppendLine("The run was cancelled.");
        return builder.ToString().TrimEnd();
    }

    private static string DefaultRead(AppSettings settings) => string.Empty;
    private static void DefaultWrite(AppSettings settings, string value) { }
}
