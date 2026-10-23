using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Steamy.Pages;
using Steamy.Services;
using System.Windows;
using System.Windows.Threading;

namespace Steamy.ViewModels;

public sealed record SteamToolsSourceChoice(string Label, ManifestSource? Source);
public sealed record SteamToolsAddedGame(int AppId) { public string Label => "App " + AppId; }

public sealed class BetterSteamToolsViewModel : ObservableObject
{
    private readonly IBetterSteamToolsService _service;
    private readonly ISettingsService _settings;
    private readonly INavigationService _navigation;
    private CancellationTokenSource? _operation;
    private readonly Dispatcher? _dispatcher;
    private CancellationTokenSource? _detectionCancellation;
    private Task _detection = Task.CompletedTask;
    private string? _requestedDetectionRoot;
    private long _detectionVersion;
    private bool _applyingDetection;
    private bool _detecting;
    private bool _busy;
    private string _steamRoot = "";
    private string _gameInput = "";
    private string _status = "Detecting Steam…";
    private string _sourceDetails = "";
    public string SourceDetails
    {
        get => _sourceDetails;
        private set { if (SetProperty(ref _sourceDetails, value)) OnPropertyChanged(nameof(HasSourceDetails)); }
    }
    public bool HasSourceDetails => !string.IsNullOrWhiteSpace(SourceDetails);
    private string _filesLabel = "";
    private bool _detected;
    private bool _backend;
    private SteamToolsSourceChoice _source;

    public BetterSteamToolsViewModel(IBetterSteamToolsService service, ISettingsService settings, INavigationService navigation)
        : this(service, settings, navigation, Application.Current?.Dispatcher) { }

    internal BetterSteamToolsViewModel(IBetterSteamToolsService service, ISettingsService settings, INavigationService navigation, Dispatcher? dispatcher)
    {
        _dispatcher = dispatcher;
        _service = service; _settings = settings; _navigation = navigation;
        _source = Sources[0];
        InstallBackendCommand = new AsyncRelayCommand(() => RunAsync(token => _service.InstallBackendAsync(SteamRoot, OperationProgress(), token)), () => !IsBusy && SteamDetected);
        AddGameCommand = new AsyncRelayCommand(() => RunAsync(async token =>
        {
            var setup = await EnsureBackendAsync(token);
            return setup ?? await _service.AddFromSourceAsync(SteamRoot, ParseAppId(GameInput)!.Value, SelectedSource.Source, OperationProgress(), token);
        }), () => !IsBusy && SteamDetected && ParseAppId(GameInput) is not null);
        BrowseFilesCommand = new AsyncRelayCommand(BrowseFilesAsync, () => !IsBusy && SteamDetected);
        CancelCommand = new RelayCommand(() => { _operation?.Cancel(); Status = "Cancelling…"; }, () => IsBusy);
        DetectCommand = new RelayCommand(() => _ = RefreshDetectionAsync(), () => !IsBusy);
        BrowseSteamCommand = new AsyncRelayCommand(BrowseSteamAsync, () => !IsBusy);
        OpenSteamCommand = new RelayCommand(OpenSteam, () => !IsBusy && SteamDetected);
        OpenSettingsCommand = new RelayCommand(() => _navigation.Navigate<SettingsPage>(), () => !IsBusy);
        OpenGamesCommand = new RelayCommand(() => _navigation.Navigate<LibraryPage>(), () => !IsBusy);
        OpenProjectCommand = new RelayCommand(() => OpenUrl("https://github.com/madoiscool/BetterSteamTools"));
        _ = RefreshDetectionAsync();
    }

    public IReadOnlyList<SteamToolsSourceChoice> Sources { get; } = [new("Automatic · detect available sources", null), new("Sushi · free", ManifestSource.Sushi), new("Zaza · free", ManifestSource.Zaza), new("Hubcap", ManifestSource.Hubcap), new("Ryuu", ManifestSource.Ryuu), new("DepotBox", ManifestSource.DepotBox)];
    public ObservableCollection<SteamToolsAddedGame> AddedGames { get; } = new();
    public string AddedCount => $"{AddedGames.Count} game configuration(s) detected";
    public string SteamRoot { get => _steamRoot; set { if (SetProperty(ref _steamRoot, value)) { InvalidateDetection(); SteamDetected = false; BackendInstalled = false; CommandsChanged(); } } }
    public string GameInput { get => _gameInput; set { if (SetProperty(ref _gameInput, value)) CommandsChanged(); } }
    public SteamToolsSourceChoice SelectedSource { get => _source; set => SetProperty(ref _source, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string FilesLabel { get => _filesLabel; private set { if (SetProperty(ref _filesLabel, value)) OnPropertyChanged(nameof(HasSelectedFiles)); } }
    public bool HasSelectedFiles => !string.IsNullOrEmpty(FilesLabel);
    public bool SteamDetected { get => _detected; private set { if (SetProperty(ref _detected, value)) OnPropertyChanged(nameof(ConnectionLabel)); CommandsChanged(); } }
    public string ConnectionLabel => IsDetecting ? "Checking Steam…" : !SteamDetected ? "Steam not detected" : BackendInstalled ? "BetterSteamTools installed" : "Steam detected · setup required";
    public bool BackendInstalled { get => _backend; private set { if (SetProperty(ref _backend, value)) { OnPropertyChanged(nameof(BackendLabel)); OnPropertyChanged(nameof(ConnectionLabel)); OnPropertyChanged(nameof(BackendSetupRequired)); CommandsChanged(); } } }
    public bool BackendSetupRequired => !IsDetecting && !BackendInstalled;
    public string BackendLabel => IsDetecting ? "Checking BetterSteamTools files…" : BackendInstalled ? "BetterSteamTools installed · files verified" : "BetterSteamTools not installed or needs repair";
    public bool IsDetecting { get => _detecting; private set { if (SetProperty(ref _detecting, value)) { OnPropertyChanged(nameof(ConnectionLabel)); OnPropertyChanged(nameof(BackendLabel)); OnPropertyChanged(nameof(BackendSetupRequired)); } } }
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(IsIdle)); CommandsChanged(); } } }
    public bool IsIdle => !IsBusy;
    public IAsyncRelayCommand InstallBackendCommand { get; }
    public IAsyncRelayCommand AddGameCommand { get; }
    public IAsyncRelayCommand BrowseFilesCommand { get; }
    public IAsyncRelayCommand BrowseSteamCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand DetectCommand { get; }
    public IRelayCommand OpenSteamCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IRelayCommand OpenGamesCommand { get; }
    public ICommand OpenProjectCommand { get; }

    public static int? ParseAppId(string text)
    {
        if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0) return number;
        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("app", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0) return number;
        }
        return null;
    }

    public void RefreshDetection()
    {
        if (IsBusy) return;
        // Explicit synchronous compatibility API; normal page/dialog paths use
        // RefreshDetectionAsync so file verification never delays their opening.
        InvalidateDetection();
        var state = _service.Detect(SteamRoot) ?? new BetterSteamToolsState("", false, false, [], "Steam is not available.");
        ApplyDetection(state);
    }
    public Task RefreshDetectionAsync()
    {
        if (IsBusy) return Task.CompletedTask;
        var root = SteamRoot;
        if (!_detection.IsCompleted && _requestedDetectionRoot == root) return _detection;
        InvalidateDetection();
        var request = _detectionCancellation = new CancellationTokenSource();
        var version = _detectionVersion;
        _requestedDetectionRoot = root;
        IsDetecting = true;
        return _detection = ReadDetectionAsync(root, request.Token, version);
    }
    private void InvalidateDetection()
    {
        Interlocked.Increment(ref _detectionVersion);
        _detectionCancellation?.Cancel(); _detectionCancellation?.Dispose(); _detectionCancellation = null;
        IsDetecting = false;
    }
    private async Task ReadDetectionAsync(string root, CancellationToken token, long version)
    {
        try
        {
            var state = await Task.Run(() => _service.Detect(root), token).ConfigureAwait(false)
                ?? new BetterSteamToolsState("", false, false, [], "Steam is not available.");
            await OnUiAsync(() =>
            {
                if (token.IsCancellationRequested || version != Volatile.Read(ref _detectionVersion) || IsBusy || SteamRoot != root) return;
                _requestedDetectionRoot = state.SteamRoot;
                ApplyDetection(state);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await OnUiAsync(() => { if (version == Volatile.Read(ref _detectionVersion) && !IsBusy) Status = "Steam detection failed: " + error.Message; }).ConfigureAwait(false);
        }
        finally { await OnUiAsync(() => { if (version == Volatile.Read(ref _detectionVersion)) IsDetecting = false; }).ConfigureAwait(false); }
    }
    private async Task OnUiAsync(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess()) { action(); return; }
        if (_dispatcher.HasShutdownStarted) return;
        try { await _dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
    private void ApplyDetection(BetterSteamToolsState state)
    {
        _applyingDetection = true;
        try
        {
            SetProperty(ref _steamRoot, state.SteamRoot, nameof(SteamRoot));
            SteamDetected = state.SteamDetected; BackendInstalled = state.BackendInstalled;
            var ids = state.AddedAppIds.Where(id => id > 0).Distinct().ToArray();
            if (!AddedGames.Select(item => item.AppId).SequenceEqual(ids))
            {
                var present = ids.ToHashSet();
                for (var i = AddedGames.Count - 1; i >= 0; i--) if (!present.Contains(AddedGames[i].AppId)) AddedGames.RemoveAt(i);
                for (var i = 0; i < ids.Length; i++)
                {
                    var id = ids[i];
                    if (i < AddedGames.Count && AddedGames[i].AppId == id) continue;
                    var old = AddedGames.FirstOrDefault(item => item.AppId == id);
                    if (old is not null) AddedGames.Move(AddedGames.IndexOf(old), i); else AddedGames.Insert(i, new(id));
                }
                OnPropertyChanged(nameof(AddedCount));
            }
            Status = state.Message;
        }
        finally { _applyingDetection = false; CommandsChanged(); }
    }

    public async Task ImportFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return;
        if (!SteamDetected) { Status = "Choose the Steam folder before importing game metadata."; return; }
        FilesLabel = string.Join(", ", paths.Select(Path.GetFileName));
        if (!string.IsNullOrWhiteSpace(GameInput) && ParseAppId(GameInput) is null) { Status = "Enter a valid Steam App ID or clear the game field for automatic detection."; return; }
        await RunAsync(async token =>
        {
            var setup = await EnsureBackendAsync(token);
            return setup ?? await _service.ImportAsync(SteamRoot, paths, ParseAppId(GameInput), token);
        });
    }

    private async Task<BetterSteamToolsResult?> EnsureBackendAsync(CancellationToken token)
    {
        if ((await Task.Run(() => _service.Detect(SteamRoot), token)).BackendInstalled) return null;
        await OnUiAsync(() => Status = "Installing BetterSteamTools before adding metadata… Close Steam before continuing.");
        var installed = await _service.InstallBackendAsync(SteamRoot, OperationProgress(), token);
        if (!installed.Succeeded) return installed;
        token.ThrowIfCancellationRequested();
        if (!(await Task.Run(() => _service.Detect(SteamRoot), token)).BackendInstalled)
            return new(false, "BetterSteamTools could not be verified after installation. Metadata was not added.", []);
        return null;
    }

    private IProgress<string> OperationProgress()
    {
        var operation = _operation;
        return new Progress<string>(text => _ = OnUiAsync(() => { if (IsBusy && ReferenceEquals(_operation, operation)) Status = text; }));
    }

    private async Task RunAsync(Func<CancellationToken, Task<BetterSteamToolsResult>> operation)
    {
        if (IsBusy) return;
        _operation = new CancellationTokenSource();
        InvalidateDetection();
        IsBusy = true;
        SourceDetails = "";
        Status = "Preparing…";
        string message;
        try
        {
            var token = _operation.Token;
            var result = await Task.Run(() => operation(token), token);
            message = result.Message;
            if (!result.Succeeded && message.StartsWith("No source supplied usable metadata.", StringComparison.Ordinal))
            {
                SourceDetails = message.Replace(" · ", "\n");
                message = "No source returned usable metadata. Open Source details, or import a ZIP archive.";
            }
        }
        catch (OperationCanceledException) { message = "Operation cancelled."; }
        catch (Exception exception) { message = "Operation failed: " + exception.Message; }
        finally
        {
            IsBusy = false;
            _operation.Dispose(); _operation = null;
        }
        await RefreshDetectionAsync();
        Status = message;
    }

    private async Task BrowseFilesAsync()
    {
        var dialog = new OpenFileDialog { Title = "Add game metadata", Filter = "Metadata (*.zip;*.7z;*.rar;*.lua;*.manifest)|*.zip;*.7z;*.rar;*.lua;*.manifest", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog() == true) await ImportFilesAsync(dialog.FileNames);
    }

    private async Task BrowseSteamAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Select the Steam folder containing steam.exe" };
        if (dialog.ShowDialog() != true) return;
        if (!File.Exists(Path.Combine(dialog.FolderName, "steam.exe"))) { Status = "Select the Steam installation folder containing steam.exe."; return; }
        SteamRoot = dialog.FolderName;
        var model = _settings.Load(); model.SteamLibraryPath = SteamRoot;
        try { await _settings.SaveAsync(model); await RefreshDetectionAsync(); }
        catch (Exception exception) { Status = "Steam folder could not be saved: " + exception.Message; }
    }

    private void OpenSteam()
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(SteamRoot, "steam.exe")) { UseShellExecute = true }); }
        catch (Exception exception) { Status = "Steam could not be started: " + exception.Message; }
    }
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void CommandsChanged()
    {
        if (_applyingDetection) return;
        (InstallBackendCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (AddGameCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (BrowseFilesCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (BrowseSteamCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (CancelCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (DetectCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (OpenSteamCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (OpenSettingsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (OpenGamesCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }
}
