using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

/// <summary>
/// Drives the Steamless page: pick the Steamless command line build and the game executable it
/// should unpack, run it, and then swap the produced <c>.unpacked.exe</c> in for the original.
/// The swap is reversible, so an Undo is offered whenever a backup exists.
/// </summary>
public sealed class SteamlessViewModel : ObservableObject
{
    private readonly ISteamlessService _steamless;
    private readonly ISettingsService _settingsService;

    private CancellationTokenSource? _runCts;
    private string _steamlessPath = string.Empty;
    private string _targetExePath = string.Empty;
    private string _extraArguments = string.Empty;
    private string _status = "Not configured";
    private string _toolVersion = "—";
    private string _lastMessage = "Choose your Steamless executable and the game .exe you want to unpack.";
    private string _output = string.Empty;
    private bool _isBusy;
    private bool _showDone;
    private bool _doneSuccess;
    private string _doneTitle = string.Empty;
    private string _doneMessage = string.Empty;
    private bool _isInfoVisible;
    private bool _showLog;
    private bool _showAdvanced;

    public SteamlessViewModel(ISteamlessService steamless, ISettingsService settings)
    {
        _steamless = steamless;
        _settingsService = settings;
        SettingsModel = settings.Load();
        _steamlessPath = SettingsModel.SteamlessExePath;
        _targetExePath = SettingsModel.SteamlessTargetExePath;
        _extraArguments = SettingsModel.SteamlessExtraArguments;

        // The bundled build is ready before any check runs, so the page must never open claiming
        // "Not configured" while a usable Steamless sits next to the application.
        _status = string.IsNullOrWhiteSpace(_steamlessPath)
            ? (SteamlessBundle.IsAvailable ? "Bundled build ready" : "Not configured")
            : "Not checked";

        _lastMessage = string.IsNullOrWhiteSpace(_steamlessPath)
            ? "Pick the game .exe, then press Run."
            : "Ready. Pick the game .exe, then press Run.";

        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync);
        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => CanUndo);
        CancelRunCommand = new RelayCommand(CancelRun, () => IsBusy);
        CloseDoneCommand = new RelayCommand(() => ShowDone = false);
        DismissDoneCommand = new RelayCommand(() => ShowDone = false);
        ToggleInfoCommand = new RelayCommand(() => IsInfoVisible = !IsInfoVisible);
        ClearTargetCommand = new RelayCommand(() => TargetExePath = string.Empty);
        UseBundledBuildCommand = new RelayCommand(UseBundledBuild, () => SteamlessBundle.IsAvailable);
        ToggleLogCommand = new RelayCommand(ToggleLog);
        ToggleAdvancedCommand = new RelayCommand(() => ShowAdvanced = !ShowAdvanced);
    }

    public AppSettings SettingsModel { get; private set; }

    /// <summary>
    /// The build that will actually run: the bundled one unless the user picked their own. An empty
    /// selection therefore still works out of the box.
    /// </summary>
    public string EffectiveSteamlessPath => string.IsNullOrWhiteSpace(_steamlessPath)
        ? (SteamlessBundle.IsAvailable ? SteamlessBundle.CliPath : string.Empty)
        : _steamlessPath;

    /// <summary>True while the workflow uses the build that ships inside the application.</summary>
    public bool UsingBundledBuild => string.IsNullOrWhiteSpace(_steamlessPath) && SteamlessBundle.IsAvailable;

    public string BundleSummary => SteamlessBundle.IsAvailable
        ? $"Bundled with the app: Steamless {BundleVersion} — nothing to download."
        : "The bundled Steamless build is missing from this installation. Pick your own Steamless.CLI.exe below.";

    public string BundleVersion
    {
        get
        {
            try
            {
                return System.Diagnostics.FileVersionInfo.GetVersionInfo(SteamlessBundle.CliPath).FileVersion ?? "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }

    /// <summary>
    /// Re-checks paths that were typed or pasted instead of picked through the file dialog and
    /// remembers them. Called when either path box loses focus.
    /// </summary>
    public async Task PathsEnteredAsync()
    {
        if (string.IsNullOrWhiteSpace(SteamlessPath) && string.IsNullOrWhiteSpace(TargetExePath)) return;
        if (!string.IsNullOrWhiteSpace(SteamlessPath)) await RefreshStatusAsync().ConfigureAwait(false);
        await PersistAsync().ConfigureAwait(false);
    }

    public string SteamlessPath
    {
        get => _steamlessPath;
        set
        {
            if (!SetProperty(ref _steamlessPath, value)) return;
            OnPropertyChanged(nameof(DisplaySteamlessPath));
            OnPropertyChanged(nameof(EffectiveSteamlessPath));
            OnPropertyChanged(nameof(UsingBundledBuild));
            OnPropertyChanged(nameof(BundleSummary));
            OnPropertyChanged(nameof(ShowStatusStrip));
            OnPropertyChanged(nameof(CanRun));
        }
    }

    public string TargetExePath
    {
        get => _targetExePath;
        set
        {
            if (!SetProperty(ref _targetExePath, value)) return;
            OnPropertyChanged(nameof(DisplayTargetExePath));
            OnPropertyChanged(nameof(TargetHint));
            OnPropertyChanged(nameof(CanRun));
            _ = RefreshUndoStateAsync();
        }
    }

    /// <summary>Display-only projections. They stay writable so a two-way binding cannot throw.</summary>
    public string DisplaySteamlessPath
    {
        get => string.IsNullOrWhiteSpace(SteamlessPath)
            ? (SteamlessBundle.IsAvailable ? $"{SteamlessBundle.CliPath} (bundled)" : "No Steamless build selected")
            : SteamlessPath;
        set { }
    }

    public string DisplayTargetExePath
    {
        get => string.IsNullOrWhiteSpace(TargetExePath) ? "No game .exe selected" : TargetExePath;
        set { }
    }

    public string ExtraArguments
    {
        get => _extraArguments;
        set => SetProperty(ref _extraArguments, value);
    }

    /// <summary>Tells the user what the rename step will do for the currently selected file.</summary>
    public string TargetHint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TargetExePath)) return "Pick the .exe that Steamless should unpack.";

            var backup = SteamlessFileSwap.BackupPathFor(TargetExePath);
            var unpacked = SteamlessFileSwap.UnpackedPathFor(TargetExePath);

            if (File.Exists(backup))
                return $"A backup already exists ({Path.GetFileName(backup)}). Running again replaces it, and Undo restores it at any time.";

            return $"After a successful run: {Path.GetFileName(TargetExePath)} becomes {Path.GetFileName(backup)}, and {Path.GetFileName(unpacked)} takes the original name.";
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(ShowStatusStrip));
        }
    }

    /// <summary>
    /// The bundled build is the normal case, so the status bar stays hidden for it. It appears as
    /// soon as the user selects their own build (then the path and version matter) or when
    /// something needs attention.
    /// </summary>
    public bool ShowStatusStrip => !UsingBundledBuild
        || Status is "Needs attention" or "Error";

    public string ToolVersion
    {
        get => _toolVersion;
        private set => SetProperty(ref _toolVersion, value);
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
            if (!SetProperty(ref _output, value)) return;
            OnPropertyChanged(nameof(HasOutput));
            OnPropertyChanged(nameof(LogSummary));
        }
    }

    public bool HasOutput => !string.IsNullOrWhiteSpace(Output);

    /// <summary>The log panel is collapsed by default and opened with the Log button.</summary>
    public bool ShowLog
    {
        get => _showLog;
        private set
        {
            if (SetProperty(ref _showLog, value)) OnPropertyChanged(nameof(LogButtonLabel));
        }
    }

    public string LogButtonLabel => ShowLog ? "Hide log" : "Log";

    /// <summary>How many lines the tool wrote — a click on Log with nothing there should say so.</summary>
    public string LogSummary
    {
        get
        {
            if (!HasOutput) return "No output yet. Press Run and the tool's report appears here.";
            var lines = Output.Split('\n').Length;
            return $"Steamless report — {lines} lines";
        }
    }

    /// <summary>Own build and extra arguments are tucked away to keep the page compact.</summary>
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        private set
        {
            if (SetProperty(ref _showAdvanced, value)) OnPropertyChanged(nameof(AdvancedButtonLabel));
        }
    }

    public string AdvancedButtonLabel => ShowAdvanced ? "Hide options" : "Options";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanRun));
            OnPropertyChanged(nameof(CanUndo));
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

    public string InfoText =>
        "Steamless removes Steam's wrapper (SteamStub) from a game executable. Steamless ships inside this app, so there is nothing to install: " +
        "just pick the game .exe. Optionally you can still point the page at your own build. " +
        "then swaps the result in: Steamless writes <name>.unpacked.exe next to the input, so afterwards the original is kept as <name>.bak.exe " +
        "and the unpacked build takes the original name. Nothing is downloaded, no credentials are stored, and Undo reverses the swap. " +
        "The tool runs without a window of its own — its output appears right here on this page. " +
        "Use the command line build (Steamless.CLI.exe); the GUI build ignores command line arguments.";

    public bool CanRun => !IsBusy
        && !string.IsNullOrWhiteSpace(EffectiveSteamlessPath)
        && !string.IsNullOrWhiteSpace(TargetExePath);

    public bool CanUndo => !IsBusy
        && !string.IsNullOrWhiteSpace(TargetExePath)
        && File.Exists(SteamlessFileSwap.BackupPathFor(TargetExePath));

    public IAsyncRelayCommand RefreshStatusCommand { get; }
    public IAsyncRelayCommand RunCommand { get; }
    public IAsyncRelayCommand UndoCommand { get; }
    public ICommand CancelRunCommand { get; }
    public ICommand CloseDoneCommand { get; }
    public ICommand DismissDoneCommand { get; }
    public ICommand ToggleInfoCommand { get; }
    public ICommand ClearTargetCommand { get; }
    public ICommand UseBundledBuildCommand { get; }
    public ICommand ToggleLogCommand { get; }
    public ICommand ToggleAdvancedCommand { get; }

    /// <summary>
    /// Re-checks the build that would run. An empty selection means the bundled build, which the
    /// service reports as ready — this must never throw, otherwise the page would die on load.
    /// </summary>
    public async Task RefreshStatusAsync()
    {
        try
        {
            var status = await _steamless.CheckAsync(SteamlessPath).ConfigureAwait(false);
            Status = status.IsReady
                ? (string.IsNullOrWhiteSpace(SteamlessPath) ? "Bundled build ready" : "Ready to run")
                : "Needs attention";
            ToolVersion = string.IsNullOrWhiteSpace(status.Version) ? "—" : status.Version;
            if (!status.IsReady) LastMessage = status.Message;
        }
        catch (Exception exception)
        {
            Status = "Error";
            LastMessage = $"The Steamless build could not be checked: {exception.Message}";
        }
    }

    /// <summary>
    /// Shows or hides the tool log. Clicking Log with an empty log opens it too and states that
    /// there is nothing yet, which is friendlier than a click that appears to do nothing.
    /// </summary>
    private void ToggleLog()
    {
        ShowLog = !ShowLog;
        if (ShowLog && !HasOutput) LastMessage = "No tool output yet — press Run first.";
    }

    /// <summary>Returns to the build that ships with the app, dropping any own selection.</summary>
    private void UseBundledBuild()
    {
        SteamlessPath = string.Empty;
        _ = PersistAsync();
        _ = RefreshStatusAsync();
    }

    public async Task RefreshUndoStateAsync()
    {
        await Task.CompletedTask;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(TargetHint));
    }

    public async Task PersistAsync()
    {
        SettingsModel.SteamlessExePath = SteamlessPath;
        SettingsModel.SteamlessTargetExePath = TargetExePath;
        SettingsModel.SteamlessExtraArguments = ExtraArguments;
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
        var build = EffectiveSteamlessPath;
        LastMessage = $"Running {Path.GetFileName(build)} on {Path.GetFileName(TargetExePath)}…";

        try
        {
            var request = new SteamlessRequest(SteamlessPath, TargetExePath, ExtraArguments);

            var result = await _steamless.RunAsync(request, _runCts.Token).ConfigureAwait(false);
            ApplyResult(result, "Steamless");
        }
        catch (OperationCanceledException)
        {
            ShowFailure("Steamless cancelled", "The run was cancelled before it finished.");
        }
        catch (Exception exception)
        {
            ShowFailure("Steamless error", $"The run failed: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
            _runCts?.Dispose();
            _runCts = null;
            await PersistAsync().ConfigureAwait(false);
        }
    }

    public async Task UndoAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(TargetExePath)) return;

        IsBusy = true;
        ShowDone = false;
        LastMessage = "Restoring the backup…";

        try
        {
            var result = await _steamless.UndoAsync(TargetExePath).ConfigureAwait(false);
            ApplyResult(result, "Undo");
        }
        catch (Exception exception)
        {
            ShowFailure("Undo failed", $"The undo failed: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(TargetHint));
        }
    }

    private void ApplyResult(SteamlessRunResult result, string scope)
    {
        DoneSuccess = result.Succeeded;
        DoneTitle = result.Succeeded ? $"{scope} done" : $"{scope} finished with an issue";
        DoneMessage = result.Message;

        if (result.BackupPath is not null)
            DoneMessage = $"{DoneMessage}{Environment.NewLine}Backup: {result.BackupPath}";
        if (result.ExitCode is not null)
            DoneMessage = $"{DoneMessage}{Environment.NewLine}Steamless exit code: {result.ExitCode}";

        ShowDone = true;
        LastMessage = result.Message;
        Status = result.Succeeded ? "Completed" : "Completed with issues";

        // Steamless writes its own report; show it inside the page instead of a console window.
        // It opens automatically after a run, and the Log button collapses it again.
        var log = result.Output;
        Output = string.IsNullOrWhiteSpace(log) ? result.Message : $"{result.Message}{Environment.NewLine}{Environment.NewLine}{log}";
        OnPropertyChanged(nameof(LogSummary));
        ShowLog = true;

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(TargetHint));
    }

    private void ShowFailure(string title, string message)
    {
        DoneSuccess = false;
        DoneTitle = title;
        DoneMessage = message;
        ShowDone = true;
        LastMessage = message;
        Status = "Error";
    }

    private void CancelRun()
    {
        _runCts?.Cancel();
        LastMessage = "Cancelling…";
    }
}
