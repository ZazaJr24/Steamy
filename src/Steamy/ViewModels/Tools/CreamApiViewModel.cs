using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed class CreamApiDlcItem : ObservableObject
{
    private bool _isSelected = true;
    public int AppId { get; init; }
    public string Name { get; init; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class CreamApiViewModel : ObservableObject
{
    private readonly ICreamApiService _creamApi;
    private readonly ISettingsService _settings;
    private readonly IGameLocatorService _gameLocator;
    private readonly ILoggingService _logging;
    private CancellationTokenSource? _fetchCts;

    private string _gameFolder = string.Empty;
    private string _appIdText = string.Empty;
    private string _gameSearchText = string.Empty;
    private string _status = "Select a game or browse manually.";
    private string _proxyAddress = string.Empty;
    private string _selectedLanguage = "english";
    private bool _unlockAll = true;
    private bool _extraProtection;
    private bool _forceOffline;
    private bool _isFetching;
    private bool _isApplying;
    private bool _hasDlls;
    private string _resultMessage = string.Empty;
    private bool _showResult;
    private bool _resultSuccess;
    private InstalledGameEntry? _selectedGame;
    private bool _isLoadingGames;
    private DlcUnlockerMode _selectedMode = DlcUnlockerMode.CreamAPI;

    public CreamApiViewModel(ICreamApiService creamApi, ISettingsService settings, IGameLocatorService gameLocator, ILoggingService logging)
    {
        _creamApi = creamApi;
        _settings = settings;
        _gameLocator = gameLocator;
        _logging = logging;

        var appSettings = settings.Load();
        _proxyAddress = appSettings.CreamApiProxy ?? string.Empty;
        _hasDlls = creamApi.HasCachedDlls(_selectedMode);

        if (!string.IsNullOrWhiteSpace(_proxyAddress))
            creamApi.ProxyAddress = _proxyAddress;

        FetchDlcCommand = new AsyncRelayCommand(FetchDlcAsync, () => CanFetch);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanApply);
        RestoreCommand = new RelayCommand(Restore, () => !string.IsNullOrWhiteSpace(GameFolder));
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        DeselectAllCommand = new RelayCommand(() => SetAll(false));
        DismissResultCommand = new RelayCommand(() => ShowResult = false);
        RefreshGamesCommand = new AsyncRelayCommand(RefreshGamesAsync);

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var gamesTask = RefreshGamesAsync();
        var dllsTask = EnsureDllsAsync();
        await Task.WhenAll(gamesTask, dllsTask);
    }

    private async Task EnsureDllsAsync()
    {
        if (_creamApi.HasCachedDlls(_selectedMode))
        {
            HasDlls = true;
            return;
        }

        Status = $"Preparing {_selectedMode} DLLs…";
        try
        {
            await _creamApi.EnsureDllsAvailableAsync(_selectedMode);
            HasDlls = _creamApi.HasCachedDlls(_selectedMode);
            if (HasDlls)
                Status = "Ready.";
        }
        catch
        {
            Status = $"Failed to load {_selectedMode} DLLs.";
        }
    }

    public ObservableCollection<CreamApiDlcItem> DlcList { get; } = new();
    public ObservableCollection<InstalledGameEntry> InstalledGames { get; } = new();
    public ObservableCollection<InstalledGameEntry> FilteredGames { get; } = new();

    public string[] Languages { get; } =
    {
        "english", "german", "french", "italian", "spanish",
        "portuguese", "brazilian", "russian", "japanese", "korean",
        "schinese", "tchinese", "polish", "czech", "hungarian",
        "turkish", "dutch", "finnish", "swedish", "norwegian",
        "danish", "romanian", "thai", "arabic"
    };

    public DlcUnlockerMode[] AvailableModes { get; } =
    {
        DlcUnlockerMode.CreamAPI,
        DlcUnlockerMode.SmokeAPI,
        DlcUnlockerMode.Koalageddon,
        DlcUnlockerMode.UplayR2Unlocker
    };

    public DlcUnlockerMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!SetProperty(ref _selectedMode, value)) return;
            OnPropertyChanged(nameof(IsCreamApiMode));
            OnPropertyChanged(nameof(IsSmokeApiMode));
            HasDlls = _creamApi.HasCachedDlls(value);
            _ = EnsureDllsAsync();
        }
    }

    public bool IsCreamApiMode => _selectedMode == DlcUnlockerMode.CreamAPI;
    public bool IsSmokeApiMode => _selectedMode == DlcUnlockerMode.SmokeAPI;

    public InstalledGameEntry? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!SetProperty(ref _selectedGame, value) || value is null) return;
            GameFolder = value.InstallPath;
            AppIdText = value.AppId.ToString();
            Status = $"{value.Name} selected — loading DLCs…";
            _ = FetchDlcAsync();
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

    public string GameFolder
    {
        get => _gameFolder;
        set
        {
            if (!SetProperty(ref _gameFolder, value)) return;
            OnPropertyChanged(nameof(GameFolderDisplay));
            OnPropertyChanged(nameof(HasGameFolder));
            OnPropertyChanged(nameof(DetectedDlls));
            OnPropertyChanged(nameof(CanApply));
            FetchDlcCommand.NotifyCanExecuteChanged();
            RestoreCommand.NotifyCanExecuteChanged();
        }
    }

    public string GameFolderDisplay => string.IsNullOrWhiteSpace(GameFolder) ? "No folder selected" : GameFolder;
    public bool HasGameFolder => !string.IsNullOrWhiteSpace(GameFolder) && Directory.Exists(GameFolder);

    public string DetectedDlls
    {
        get
        {
            if (!HasGameFolder) return string.Empty;
            try
            {
                var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.EnumerateFiles(GameFolder, "steam_api.dll", SearchOption.AllDirectories))
                    locations.Add(Path.GetDirectoryName(f)!);
                foreach (var f in Directory.EnumerateFiles(GameFolder, "steam_api64.dll", SearchOption.AllDirectories))
                    locations.Add(Path.GetDirectoryName(f)!);

                if (locations.Count == 0) return "No steam_api DLL found";

                var parts = locations.Select(dir =>
                {
                    var rel = Path.GetRelativePath(GameFolder, dir);
                    var has32 = File.Exists(Path.Combine(dir, "steam_api.dll"));
                    var has64 = File.Exists(Path.Combine(dir, "steam_api64.dll"));
                    var dlls = has32 && has64 ? "32+64" : has64 ? "64" : "32";
                    return $"{(rel == "." ? "root" : rel)} ({dlls})";
                });
                return string.Join(", ", parts);
            }
            catch { return "Scan error"; }
        }
    }

    public string AppIdText
    {
        get => _appIdText;
        set
        {
            if (!SetProperty(ref _appIdText, value)) return;
            FetchDlcCommand.NotifyCanExecuteChanged();
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    public string ProxyAddress
    {
        get => _proxyAddress;
        set
        {
            if (!SetProperty(ref _proxyAddress, value)) return;
            _creamApi.ProxyAddress = value;
            var s = _settings.Load();
            s.CreamApiProxy = value;
            _ = _settings.SaveAsync(s);
        }
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set => SetProperty(ref _selectedLanguage, value);
    }

    public bool UnlockAll
    {
        get => _unlockAll;
        set => SetProperty(ref _unlockAll, value);
    }

    public bool ExtraProtection
    {
        get => _extraProtection;
        set => SetProperty(ref _extraProtection, value);
    }

    public bool ForceOffline
    {
        get => _forceOffline;
        set => SetProperty(ref _forceOffline, value);
    }

    public bool IsFetching
    {
        get => _isFetching;
        set
        {
            if (!SetProperty(ref _isFetching, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            FetchDlcCommand.NotifyCanExecuteChanged();
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsApplying
    {
        get => _isApplying;
        set
        {
            if (!SetProperty(ref _isApplying, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsIdle => !IsFetching && !IsApplying;

    public bool HasDlls
    {
        get => _hasDlls;
        set => SetProperty(ref _hasDlls, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string ResultMessage
    {
        get => _resultMessage;
        set => SetProperty(ref _resultMessage, value);
    }

    public bool ShowResult
    {
        get => _showResult;
        set => SetProperty(ref _showResult, value);
    }

    public bool ResultSuccess
    {
        get => _resultSuccess;
        set => SetProperty(ref _resultSuccess, value);
    }

    public bool IsLoadingGames
    {
        get => _isLoadingGames;
        set => SetProperty(ref _isLoadingGames, value);
    }

    public string GameCountLabel => FilteredGames.Count == 0
        ? "No games found"
        : $"{FilteredGames.Count} games";

    public string DlcCountLabel => DlcList.Count == 0
        ? "No DLCs loaded"
        : $"{DlcList.Count(d => d.IsSelected)} / {DlcList.Count} DLCs selected";

    public bool CanFetch => !IsFetching && int.TryParse(AppIdText, out var id) && id > 0;
    public bool CanApply => !IsFetching && !IsApplying && HasGameFolder && DlcList.Count > 0 && int.TryParse(AppIdText, out _);

    public IAsyncRelayCommand FetchDlcCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public IRelayCommand RestoreCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand DismissResultCommand { get; }
    public IAsyncRelayCommand RefreshGamesCommand { get; }

    private async Task RefreshGamesAsync()
    {
        IsLoadingGames = true;
        InstalledGames.Clear();
        FilteredGames.Clear();
        OnPropertyChanged(nameof(GameCountLabel));
        Status = "Scanning for games…";

        try
        {
            var games = await Task.Run(() => _gameLocator.ListInstalledGames().OrderBy(g => g.Name).ToList());

            foreach (var game in games)
            {
                InstalledGames.Add(game);
                FilteredGames.Add(game);
            }

            OnPropertyChanged(nameof(GameCountLabel));
            Status = InstalledGames.Count > 0
                ? $"Found {InstalledGames.Count} installed games."
                : "No games found. Try browsing manually.";
        }
        catch
        {
            Status = "Failed to load games.";
        }
        finally
        {
            IsLoadingGames = false;
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
                || game.AppId.ToString().Contains(search))
            {
                FilteredGames.Add(game);
            }
        }

        OnPropertyChanged(nameof(GameCountLabel));
    }

    public async Task LoadCreamApiDllsAsync(string archivePath)
    {
        Status = "Extracting DLLs…";
        var ok = await _creamApi.ExtractDllsFromArchiveAsync(archivePath);
        HasDlls = _creamApi.HasCachedDlls(_selectedMode);
        Status = ok ? "DLLs ready." : "Extraction failed.";
    }

    private async Task FetchDlcAsync()
    {
        if (!int.TryParse(AppIdText, out var appId) || appId <= 0) return;

        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();
        IsFetching = true;
        DlcList.Clear();
        OnPropertyChanged(nameof(DlcCountLabel));
        Status = $"Fetching DLCs for App {appId}…";

        try
        {
            var dlcs = await _creamApi.FetchDlcListAsync(appId, _fetchCts.Token);

            foreach (var dlc in dlcs)
                DlcList.Add(new CreamApiDlcItem { AppId = dlc.AppId, Name = dlc.Name });

            Status = DlcList.Count > 0
                ? $"Found {DlcList.Count} DLCs."
                : $"No DLCs found for App {appId}.";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Fetch error: {ex.Message}";
        }
        finally
        {
            IsFetching = false;
            OnPropertyChanged(nameof(DlcCountLabel));
            ApplyCommand.NotifyCanExecuteChanged();
            _fetchCts?.Dispose();
            _fetchCts = null;
        }
    }

    private async Task ApplyAsync()
    {
        if (!int.TryParse(AppIdText, out var appId)) return;

        IsApplying = true;
        Status = $"Applying {_selectedMode}…";

        try
        {
            var selectedDlcs = DlcList
                .Select(d => new DlcEntry(d.AppId, d.Name, d.IsSelected))
                .ToList();

            await Task.Run(() =>
            {
                var result = _creamApi.ApplyToGameFolder(
                    GameFolder, appId, selectedDlcs, _selectedMode,
                    SelectedLanguage, UnlockAll, ExtraProtection, ForceOffline);

                ResultSuccess = result.Succeeded;
                ResultMessage = result.Message;
                ShowResult = true;
                Status = result.Succeeded
                    ? $"{_selectedMode} applied successfully!"
                    : $"Failed: {result.Message}";
            });
        }
        catch (Exception ex)
        {
            ResultSuccess = false;
            ResultMessage = ex.Message;
            ShowResult = true;
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsApplying = false;
        }
    }

    private void Restore()
    {
        if (string.IsNullOrWhiteSpace(GameFolder)) return;

        var result = _creamApi.RestoreOriginalDlls(GameFolder);
        ResultSuccess = result.Succeeded;
        ResultMessage = result.Message;
        ShowResult = true;
        Status = result.Succeeded ? "Original DLLs restored." : result.Message;
    }

    private void SetAll(bool selected)
    {
        foreach (var item in DlcList)
            item.IsSelected = selected;
        OnPropertyChanged(nameof(DlcCountLabel));
    }

    public void NotifyDlcCountChanged() => OnPropertyChanged(nameof(DlcCountLabel));
}
