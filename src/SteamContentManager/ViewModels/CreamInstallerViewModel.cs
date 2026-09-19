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

public sealed class CreamInstallerViewModel : ObservableObject
{
    private readonly ILocalToolRunner _runner;
    private readonly ICreamInstallerDownloadService _downloader;
    private readonly ISettingsService _settingsService;
    private CancellationTokenSource? _runCts;

    private string _executablePath = string.Empty;
    private string _targetExePath = string.Empty;
    private string _steamApiPath = string.Empty;
    private string _status = "Not configured";
    private string _lastMessage = "Select CreamInstaller.exe and the game .exe to begin.";
    private string _output = string.Empty;
    private bool _isBusy;
    private bool _showDone;
    private bool _doneSuccess;
    private string _doneTitle = string.Empty;
    private string _doneMessage = string.Empty;
    private bool _isInfoVisible;
    private bool _hasSteamApiPath;
    private bool _isDownloading;

    public CreamInstallerViewModel(ILocalToolRunner runner, ICreamInstallerDownloadService downloader, ISettingsService settings)
    {
        _runner = runner;
        _downloader = downloader;
        _settingsService = settings;
        SettingsModel = settings.Load();

        _executablePath = SettingsModel.CreamInstallerPath;
        _targetExePath = SettingsModel.CreamInstallerTargetExePath;
        _steamApiPath = SettingsModel.CreamInstallerSteamApiPath;
        _hasSteamApiPath = !string.IsNullOrWhiteSpace(_steamApiPath);

        if (string.IsNullOrWhiteSpace(_executablePath) && HasCachedExecutable)
        {
            _executablePath = _downloader.CachedPath ?? string.Empty;
        }

        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
        CancelRunCommand = new RelayCommand(CancelRun, () => IsBusy);
        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync);
        CloseDoneCommand = new RelayCommand(() => ShowDone = false);
        DismissDoneCommand = new RelayCommand(() => ShowDone = false);
        ToggleInfoCommand = new RelayCommand(() => IsInfoVisible = !IsInfoVisible);
        ClearSteamApiCommand = new RelayCommand(() =>
        {
            SteamApiPath = string.Empty;
            _ = PersistAsync();
        });
        DownloadCommand = new AsyncRelayCommand(DownloadAsync);

        if (string.IsNullOrWhiteSpace(_executablePath))
        {
            _ = EnsureDownloadedAsync();
        }
    }

    public AppSettings SettingsModel { get; private set; }

    public string PageKey => "cream-installer";
    public string Title => "CreamInstaller — CreamAPI / ScreamAPI";
    public string Subtitle => "Runs your own CreamInstaller build locally. All of the workflow stays inside this app.";
    public string InfoText =>
        "This page only launches the CreamInstaller executable you select and the game you point it at. " +
        "It does not download, bundle, patch or repackage CreamInstaller, and it never touches your Steam or Epic account credentials. " +
        "The optional SteamAPI folder is used as the working directory and SteamAPI path for the tool run. " +
        "There is no separate DLC-selection popup: the tool is started directly from this page.";
    public string DownloadUrl => "https://github.com/acidicoala/CreamInstaller";

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
            ? "No executable selected"
            : ExecutablePath;
        set { }
    }

    public string TargetExePath
    {
        get => _targetExePath;
        set
        {
            if (SetProperty(ref _targetExePath, value))
            {
                OnPropertyChanged(nameof(TargetHint));
                OnPropertyChanged(nameof(CanRun));
            }
        }
    }

    public string DisplayTargetPath
    {
        get => string.IsNullOrWhiteSpace(TargetExePath) ? "No game .exe selected" : TargetExePath;
        set { }
    }

    public string SteamApiPath
    {
        get => _steamApiPath;
        set
        {
            if (SetProperty(ref _steamApiPath, value))
            {
                OnPropertyChanged(nameof(DisplaySteamApiPath));
                OnPropertyChanged(nameof(HasSteamApiPath));
                OnPropertyChanged(nameof(TargetHint));
                OnPropertyChanged(nameof(CanRun));
            }
        }
    }

    public string DisplaySteamApiPath
    {
        get => string.IsNullOrWhiteSpace(SteamApiPath) ? "No SteamAPI folder selected" : SteamApiPath;
        set { }
    }

    public bool HasSteamApiPath
    {
        get => _hasSteamApiPath;
        set => SetProperty(ref _hasSteamApiPath, value);
    }

    public string TargetHint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TargetExePath)) return "Pick the .exe that CreamInstaller should run against.";

            var working = ResolveWorkingDirectory();

            if (!string.IsNullOrWhiteSpace(working))
                return $"Working directory for the run: {working}.";

            return "Select the game .exe and, optionally, the SteamAPI folder if the app should not look in the game folder.";
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
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

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value)) OnPropertyChanged(nameof(DownloadButtonText));
        }
    }

    public string DownloadButtonText => IsDownloading
        ? "Downloading…"
        : (_downloader.HasCachedExecutable ? "Update CreamInstaller" : "Download CreamInstaller");

    public bool HasCachedExecutable => _downloader.HasCachedExecutable;

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

    public string RunningLabel => IsBusy ? "The tool is running…" : string.Empty;

    public bool CanRun => !IsBusy
        && !string.IsNullOrWhiteSpace(ExecutablePath)
        && !string.IsNullOrWhiteSpace(TargetExePath);

    public IAsyncRelayCommand RunCommand { get; }
    public ICommand CancelRunCommand { get; }
    public IAsyncRelayCommand RefreshStatusCommand { get; }
    public ICommand CloseDoneCommand { get; }
    public ICommand DismissDoneCommand { get; }
    public ICommand ToggleInfoCommand { get; }
    public ICommand ClearSteamApiCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }

    public string EffectiveWorkingDirectory => ResolveWorkingDirectory();

    private string ResolveWorkingDirectory()
    {
        var targetFolder = string.Empty;
        if (!string.IsNullOrWhiteSpace(TargetExePath) && File.Exists(TargetExePath))
        {
            targetFolder = Path.GetDirectoryName(TargetExePath)!;
        }

        if (!string.IsNullOrWhiteSpace(SteamApiPath))
        {
            if (Directory.Exists(SteamApiPath)) return SteamApiPath;
            if (File.Exists(SteamApiPath)) return Path.GetDirectoryName(SteamApiPath)!;
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(targetFolder))
        {
            var dllCandidate = Path.Combine(targetFolder, "steam_api64.dll");
            if (File.Exists(dllCandidate)) return targetFolder;
        }

        return string.Empty;
    }

    public async Task RefreshStatusAsync()
    {
        try
        {
            var status = await _runner.CheckAsync(ExecutablePath, "CreamInstaller.exe").ConfigureAwait(false);
            Status = status.IsReady ? "Ready to run" : status.Message;
            if (!status.IsReady) LastMessage = status.Message;
        }
        catch (Exception exception)
        {
            Status = "Error";
            LastMessage = $"The tool could not be checked: {exception.Message}";
        }
    }

    private async Task EnsureDownloadedAsync()
    {
        if (!string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath))
            return;

        if (!string.IsNullOrWhiteSpace(_downloader.CachedPath) && File.Exists(_downloader.CachedPath))
        {
            ExecutablePath = _downloader.CachedPath;
            return;
        }

        IsDownloading = true;
        LastMessage = "Downloading CreamInstaller from GitHub…";

        try
        {
            var result = await _downloader.DownloadLatestAsync().ConfigureAwait(false);

            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.CachedPath) && File.Exists(result.CachedPath))
            {
                ExecutablePath = result.CachedPath;
                LastMessage = "CreamInstaller downloaded and ready to use.";
            }
            else
            {
                LastMessage = result.Message;
                Status = "Not configured";
            }
        }
        catch (Exception exception)
        {
            LastMessage = $"The automatic download failed: {exception.Message}.";
            Status = "Not configured";
        }
        finally
        {
            IsDownloading = false;
            await PersistAsync().ConfigureAwait(false);
        }
    }

    private async Task DownloadAsync()
    {
        if (IsDownloading) return;

        IsDownloading = true;
        LastMessage = "Downloading CreamInstaller from GitHub…";

        try
        {
            var result = await _downloader.DownloadLatestAsync().ConfigureAwait(false);

            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.CachedPath) && File.Exists(result.CachedPath))
            {
                ExecutablePath = result.CachedPath;
                Status = "Ready to run";
                LastMessage = "CreamInstaller downloaded and ready to use.";
            }
            else
            {
                LastMessage = result.Message;
            }
        }
        catch (Exception exception)
        {
            LastMessage = $"The download failed: {exception.Message}.";
        }
        finally
        {
            IsDownloading = false;
            await PersistAsync().ConfigureAwait(false);
        }
    }

    public async Task PersistAsync()
    {
        SettingsModel.CreamInstallerPath = ExecutablePath;
        SettingsModel.CreamInstallerTargetExePath = TargetExePath;
        SettingsModel.CreamInstallerSteamApiPath = SteamApiPath;
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
        LastMessage = $"Running {Path.GetFileName(ExecutablePath)} against {Path.GetFileName(TargetExePath)}…";

        try
        {
            var request = new LocalToolRunRequest(
                ExecutablePath,
                string.Empty,
                EffectiveWorkingDirectory,
                ShowWindow: true);

            var result = await _runner.RunAsync(request, _runCts.Token).ConfigureAwait(false);

            DoneSuccess = result.Succeeded;
            DoneTitle = result.Succeeded
                ? "CreamInstaller finished"
                : result.WasCancelled
                    ? "CreamInstaller cancelled"
                    : "CreamInstaller finished with an issue";
            DoneMessage = BuildMessage(result);
            Output = result.Output;
            ShowDone = true;
            LastMessage = result.Message;
            Status = result.Succeeded ? "Completed" : result.WasCancelled ? "Cancelled" : "Completed with issues";
        }
        catch (OperationCanceledException)
        {
            DoneSuccess = false;
            DoneTitle = "CreamInstaller cancelled";
            DoneMessage = "The run was cancelled before it finished.";
            ShowDone = true;
            LastMessage = "Cancelled.";
            Status = "Cancelled";
        }
        catch (Exception exception)
        {
            DoneSuccess = false;
            DoneTitle = "CreamInstaller error";
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
            await PersistAsync().ConfigureAwait(false);
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
}
