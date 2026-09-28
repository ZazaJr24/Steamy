using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed class BstGameEntry : ObservableObject
{
    private bool _isActive;
    public int AppId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string InstallPath { get; init; } = string.Empty;

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

public sealed class BetterSteamToolsViewModel : ObservableObject
{
    private readonly IGameLocatorService _gameLocator;
    private readonly ISettingsService _settings;
    private readonly IGitHubToolDownloadService _downloadService;

    private string _status = "Select games to unlock DLC with BetterSteamTools.";
    private string _searchText = string.Empty;
    private string _toolPath = string.Empty;
    private string _toolVersion = "—";
    private bool _isLoadingGames;
    private bool _isDownloading;
    private bool _isBusy;
    private BstGameEntry? _selectedGame;

    public BetterSteamToolsViewModel(
        IGameLocatorService gameLocator,
        ISettingsService settings,
        IGitHubToolDownloadService downloadService)
    {
        _gameLocator = gameLocator;
        _settings = settings;
        _downloadService = downloadService;

        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsDownloading);
        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => !IsBusy && HasTool);
        LaunchForGameCommand = new AsyncRelayCommand(LaunchForGameAsync, () => !IsBusy && HasTool && SelectedGame is not null);
        RefreshGamesCommand = new AsyncRelayCommand(RefreshGamesAsync);
        RemoveGameCommand = new RelayCommand<BstGameEntry>(RemoveGame);

        var appSettings = settings.Load();
        _toolPath = appSettings.BetterSteamToolsPath ?? string.Empty;

        _ = InitAsync();
    }

    public ObservableCollection<BstGameEntry> Games { get; } = new();
    public ObservableCollection<BstGameEntry> FilteredGames { get; } = new();

    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool IsLoadingGames { get => _isLoadingGames; set => SetProperty(ref _isLoadingGames, value); }
    public bool IsDownloading
    {
        get => _isDownloading;
        set { if (SetProperty(ref _isDownloading, value)) DownloadCommand.NotifyCanExecuteChanged(); }
    }
    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetProperty(ref _isBusy, value)) { LaunchCommand.NotifyCanExecuteChanged(); LaunchForGameCommand.NotifyCanExecuteChanged(); } }
    }
    public string ToolPath { get => _toolPath; set { if (SetProperty(ref _toolPath, value)) { OnPropertyChanged(nameof(HasTool)); OnPropertyChanged(nameof(ToolStatusText)); LaunchCommand.NotifyCanExecuteChanged(); LaunchForGameCommand.NotifyCanExecuteChanged(); } } }
    public string ToolVersion { get => _toolVersion; set => SetProperty(ref _toolVersion, value); }
    public bool HasTool => !string.IsNullOrWhiteSpace(ToolPath) && File.Exists(ToolPath);
    public string ToolStatusText => HasTool ? $"Ready — {Path.GetFileName(ToolPath)}" : "Not downloaded yet";

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilter(); }
    }

    public BstGameEntry? SelectedGame
    {
        get => _selectedGame;
        set { if (SetProperty(ref _selectedGame, value)) LaunchForGameCommand.NotifyCanExecuteChanged(); }
    }

    public string GameCountLabel => FilteredGames.Count == 0
        ? "No games found"
        : $"{FilteredGames.Count} games";

    public IAsyncRelayCommand DownloadCommand { get; }
    public IAsyncRelayCommand LaunchCommand { get; }
    public IAsyncRelayCommand LaunchForGameCommand { get; }
    public IAsyncRelayCommand RefreshGamesCommand { get; }
    public IRelayCommand<BstGameEntry> RemoveGameCommand { get; }

    private async Task InitAsync()
    {
        await RefreshGamesAsync();
        if (!HasTool)
            await TryAutoResolveAsync();
    }

    private Task TryAutoResolveAsync()
    {
        var cached = _downloadService.GetCachedPath(ToolDefinitions.BetterSteamTools);
        if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
        {
            ToolPath = cached;
            SavePath(cached);
            Status = "BetterSteamTools found in cache.";
        }
        return Task.CompletedTask;
    }

    private async Task DownloadAsync()
    {
        IsDownloading = true;
        Status = "Downloading BetterSteamTools from GitHub…";
        try
        {
            var result = await _downloadService.DownloadLatestAsync(ToolDefinitions.BetterSteamTools);
            if (result.Succeeded && !string.IsNullOrEmpty(result.CachedPath) && File.Exists(result.CachedPath))
            {
                ToolPath = result.CachedPath;
                ToolVersion = result.Version ?? "—";
                SavePath(result.CachedPath);
                Status = result.Message;
            }
            else
            {
                Status = $"Download failed: {result.Message}";
            }
        }
        catch (Exception ex)
        {
            Status = $"Download error: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private Task LaunchAsync()
    {
        if (!HasTool) return Task.CompletedTask;
        IsBusy = true;
        Status = "Launching BetterSteamTools…";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ToolPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(ToolPath) ?? string.Empty
            };
            System.Diagnostics.Process.Start(psi);
            Status = "BetterSteamTools launched.";
        }
        catch (Exception ex)
        {
            Status = $"Launch error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        return Task.CompletedTask;
    }

    private Task LaunchForGameAsync()
    {
        if (!HasTool || SelectedGame is null) return Task.CompletedTask;
        IsBusy = true;
        Status = $"Launching BetterSteamTools for {SelectedGame.Name}…";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ToolPath)
            {
                Arguments = $"\"{SelectedGame.InstallPath}\"",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(ToolPath) ?? string.Empty
            };
            System.Diagnostics.Process.Start(psi);
            Status = $"BetterSteamTools launched for {SelectedGame.Name}.";
        }
        catch (Exception ex)
        {
            Status = $"Launch error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        return Task.CompletedTask;
    }

    private async Task RefreshGamesAsync()
    {
        IsLoadingGames = true;
        Games.Clear();
        FilteredGames.Clear();
        Status = "Scanning for installed games…";

        try
        {
            var installed = await Task.Run(() => _gameLocator.ListInstalledGames().OrderBy(g => g.Name).ToList());
            foreach (var g in installed)
                Games.Add(new BstGameEntry { AppId = g.AppId, Name = g.Name, InstallPath = g.InstallPath });

            ApplyFilter();
            Status = Games.Count > 0
                ? $"Found {Games.Count} installed games."
                : "No games found. Add games manually.";
        }
        catch
        {
            Status = "Failed to scan games.";
        }
        finally
        {
            IsLoadingGames = false;
        }
    }

    public void AddGameFolder(string folderPath)
    {
        var name = Path.GetFileName(folderPath);
        if (string.IsNullOrWhiteSpace(name)) name = folderPath;

        if (Games.Any(g => g.InstallPath.Equals(folderPath, StringComparison.OrdinalIgnoreCase)))
        {
            Status = $"{name} is already in the list.";
            return;
        }

        var entry = new BstGameEntry { AppId = 0, Name = name, InstallPath = folderPath };
        Games.Add(entry);
        ApplyFilter();
        Status = $"Added {name}.";
    }

    private void RemoveGame(BstGameEntry? game)
    {
        if (game is null) return;
        Games.Remove(game);
        FilteredGames.Remove(game);
        if (SelectedGame == game) SelectedGame = null;
        OnPropertyChanged(nameof(GameCountLabel));
        Status = $"Removed {game.Name}.";
    }

    private void ApplyFilter()
    {
        FilteredGames.Clear();
        var q = SearchText?.Trim() ?? string.Empty;
        foreach (var g in Games)
        {
            if (string.IsNullOrEmpty(q)
                || g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || g.AppId.ToString().Contains(q))
                FilteredGames.Add(g);
        }
        OnPropertyChanged(nameof(GameCountLabel));
    }

    public void SetToolPath(string path)
    {
        ToolPath = path;
        SavePath(path);
        Status = HasTool ? $"BetterSteamTools set to {Path.GetFileName(path)}." : "Invalid path.";
    }

    private void SavePath(string path)
    {
        var s = _settings.Load();
        s.BetterSteamToolsPath = path;
        _ = _settings.SaveAsync(s);
    }
}
