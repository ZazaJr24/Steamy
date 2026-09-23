using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SharpCompress.Archives;
using SharpCompress.Common;
using SteamContentManager.Models;
using SteamContentManager.Services;

namespace SteamContentManager.ViewModels;

public sealed class AppListEntry : ObservableObject
{
    private string _name = string.Empty;
    private string _fileLabel = string.Empty;

    public int AppId { get; init; }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string FileLabel { get => _fileLabel; set => SetProperty(ref _fileLabel, value); }
}

public sealed class FamilyShareViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly ISecureCredentialService _credentials;
    private readonly IDownloadQueueStore _downloadQueue;
    private readonly IRyuuGameDownloadService _ryuuService;
    private readonly IRyuuSecureDownloadService _ryuuDownload;
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _searchCts;

    private string _steamPath = string.Empty;
    private string _glPath = string.Empty;
    private string _searchText = string.Empty;
    private string _statusMessage = string.Empty;
    private string _actionStatus = string.Empty;
    private bool _isSearching;
    private bool _isBusy;
    private bool _stealthMode = true;
    private bool _greenLumaReady;
    private bool _isInstalling;
    private int _appListCount;

    private static readonly string SaveFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ResonanceTools", "familyshare_games.json");

    private static readonly string GlPathFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ResonanceTools", "greenluma_path.txt");

    private static readonly string DefaultGlDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ResonanceTools", "GreenLuma");

    public FamilyShareViewModel(
        IAppDataStore store,
        INavigationService navigation,
        ILoggingService logging,
        ISettingsService settings,
        ISecureCredentialService credentials,
        IDownloadQueueStore downloadQueue,
        IRyuuGameDownloadService ryuuService,
        IRyuuSecureDownloadService ryuuDownload)
        : base(store, navigation, logging)
    {
        _settings = settings;
        _credentials = credentials;
        _downloadQueue = downloadQueue;
        _ryuuService = ryuuService;
        _ryuuDownload = ryuuDownload;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ResonanceTools/1.0");

        GenerateCommand = new RelayCommand(GenerateAppList, () => Games.Count > 0);
        CleanCommand = new RelayCommand(CleanAppList);
        CleanTracesCommand = new RelayCommand(CleanAllTraces, () => HasSteamPath);
        RemoveGameCommand = new RelayCommand<int>(RemoveGame);
        ClearAllCommand = new RelayCommand(ClearAll);
        PasteCommand = new RelayCommand(PasteAppIds);
        LaunchCommand = new RelayCommand(LaunchGreenLuma, () => GreenLumaReady);
        DetectSteamCommand = new RelayCommand(DetectSteam);
        InstallGreenLumaCommand = new AsyncRelayCommand(InstallGreenLumaAsync);
        BrowseGlPathCommand = new RelayCommand(BrowseGlPath);
        RestartSteamCommand = new RelayCommand(RestartSteam, () => HasSteamPath);
        DownloadGameCommand = new AsyncRelayCommand<int>(DownloadGameAsync);
        PrepareManifestsCommand = new AsyncRelayCommand(PrepareManifestsAsync, () => Games.Count > 0 && HasSteamPath);

        DetectSteam();
        LoadGlPath();
        DetectGreenLuma();
        if (!GreenLumaReady) _ = TryAutoInstallAsync();
        LoadSavedGames();
        ImportExistingAppList();
    }

    public ObservableCollection<SteamSearchEntry> Suggestions { get; } = new();
    public ObservableCollection<AppListEntry> Games { get; } = new();

    public string SteamPath
    {
        get => _steamPath;
        set
        {
            if (!SetProperty(ref _steamPath, value)) return;
            OnPropertyChanged(nameof(HasSteamPath));
            CheckExistingAppList();
            UpdateStatus();
        }
    }

    public string GlPath
    {
        get => _glPath;
        set
        {
            if (!SetProperty(ref _glPath, value)) return;
            OnPropertyChanged(nameof(HasGlPath));
            DetectGreenLuma();
            SaveGlPath();
        }
    }

    public bool HasSteamPath => !string.IsNullOrWhiteSpace(SteamPath) && Directory.Exists(SteamPath);
    public bool HasGlPath => !string.IsNullOrWhiteSpace(GlPath) && Directory.Exists(GlPath);

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            var trimmed = value?.Trim() ?? string.Empty;
            if (int.TryParse(trimmed, out _))
                Suggestions.Clear();
            else
                _ = SearchAsync(trimmed);
        }
    }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ActionStatus { get => _actionStatus; private set => SetProperty(ref _actionStatus, value); }
    public bool IsSearching { get => _isSearching; private set => SetProperty(ref _isSearching, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) GenerateCommand.NotifyCanExecuteChanged(); } }
    public bool GreenLumaReady { get => _greenLumaReady; private set { if (SetProperty(ref _greenLumaReady, value)) { OnPropertyChanged(nameof(GreenLumaMissing)); LaunchCommand.NotifyCanExecuteChanged(); } } }
    public bool GreenLumaMissing => !GreenLumaReady && HasSteamPath;
    public bool IsInstalling { get => _isInstalling; private set => SetProperty(ref _isInstalling, value); }
    public bool StealthMode { get => _stealthMode; set => SetProperty(ref _stealthMode, value); }
    public int AppListCount { get => _appListCount; private set => SetProperty(ref _appListCount, value); }
    public string GameCountLabel => Games.Count == 0 ? "empty" : $"{Games.Count}";
    public bool HasGames => Games.Count > 0;

    public RelayCommand GenerateCommand { get; }
    public RelayCommand CleanCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public ICommand CleanTracesCommand { get; }
    public ICommand RemoveGameCommand { get; }
    public ICommand ClearAllCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand DetectSteamCommand { get; }
    public IAsyncRelayCommand InstallGreenLumaCommand { get; }
    public ICommand BrowseGlPathCommand { get; }
    public RelayCommand RestartSteamCommand { get; }
    public IAsyncRelayCommand DownloadGameCommand { get; }
    public IAsyncRelayCommand PrepareManifestsCommand { get; }

    // ── GreenLuma Install ───────────────────────────────────────────

    private async Task TryAutoInstallAsync()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(downloads)) return;

        try
        {
            // Scan Downloads up to 3 levels deep for DLLInjector.exe
            // e.g. Downloads\GL\NormalMode\DLLInjector.exe
            foreach (var f in Directory.EnumerateFiles(downloads, "DLLInjector.exe", SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(f)!;
                if (!HasInjector(dir)) continue;
                GlPath = dir;
                DetectGreenLuma();
                if (GreenLumaReady)
                {
                    ActionStatus = $"GreenLuma found → {dir}";
                    Logging.Add(LogLevel.Info, "GreenLuma", $"Auto-detected at {dir}");
                    return;
                }
            }

            // Fallback: extract from archive in Downloads
            var zip = Directory.EnumerateFiles(downloads, "GreenLuma*.zip")
                .Concat(Directory.EnumerateFiles(downloads, "GreenLuma*.7z"))
                .Concat(Directory.EnumerateFiles(downloads, "GreenLuma*.rar"))
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();

            if (zip == null) return;
            ActionStatus = $"Auto-installing from {Path.GetFileName(zip)}…";
            await ExtractGreenLumaAsync(zip);
        }
        catch { }
    }

    private async Task InstallGreenLumaAsync()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "GreenLuma Archiv auswählen",
            Filter = "Archives (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|All files|*.*",
            InitialDirectory = Directory.Exists(downloads) ? downloads : null
        };
        if (dlg.ShowDialog() != true) return;

        await ExtractGreenLumaAsync(dlg.FileName);
    }

    private async Task ExtractGreenLumaAsync(string archivePath)
    {
        IsInstalling = true;
        ActionStatus = $"Extracting {Path.GetFileName(archivePath)}…";

        try
        {
            var targetDir = DefaultGlDir;
            Directory.CreateDirectory(targetDir);

            await Task.Run(() =>
            {
                using var stream = File.OpenRead(archivePath);
                using var archive = ArchiveFactory.Open(stream);

                string? rootPrefix = null;
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();

                var keys = entries.Select(e => e.Key ?? string.Empty).Where(k => k.Length > 0).ToList();
                if (keys.Count > 0)
                {
                    var first = keys[0];
                    var sep = first.IndexOfAny(new[] { '/', '\\' });
                    if (sep > 0)
                    {
                        var candidate = first[..(sep + 1)];
                        if (keys.All(k => k.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
                            rootPrefix = candidate;
                    }
                }

                foreach (var entry in entries)
                {
                    var key = entry.Key ?? string.Empty;
                    if (rootPrefix != null && key.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        key = key[rootPrefix.Length..];

                    if (string.IsNullOrWhiteSpace(key)) continue;

                    var destPath = Path.Combine(targetDir, key.Replace('/', '\\'));
                    var destDir = Path.GetDirectoryName(destPath);
                    if (destDir != null) Directory.CreateDirectory(destDir);

                    using var entryStream = entry.OpenEntryStream();
                    using var fileStream = File.Create(destPath);
                    entryStream.CopyTo(fileStream);
                }
            });

            GlPath = targetDir;
            DetectGreenLuma();

            ActionStatus = GreenLumaReady
                ? $"GreenLuma installed → {Path.GetFileName(archivePath)}"
                : "Extracted but DLLInjector.exe not found — check archive.";
            Logging.Add(LogLevel.Info, "GreenLuma", $"Installed from {Path.GetFileName(archivePath)}");
        }
        catch (Exception ex)
        {
            ActionStatus = $"Install failed: {ex.Message}";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private void BrowseGlPath()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "GreenLuma Ordner auswählen",
            Multiselect = false
        };
        if (dlg.ShowDialog() == true)
        {
            GlPath = dlg.FolderName;
        }
    }

    // ── Launch GreenLuma ────────────────────────────────────────────

    private void LaunchGreenLuma()
    {
        if (!GreenLumaReady || !HasSteamPath) return;

        var injector = FindInjector();
        if (injector == null) { ActionStatus = "GreenLuma launcher not found."; return; }

        try
        {
            foreach (var proc in Process.GetProcessesByName("steam"))
                try { proc.Kill(); } catch { }

            var injDir = Path.GetDirectoryName(injector)!;
            var iniPath = Path.Combine(injDir, "DLLInjector.ini");
            var steamExe = Path.Combine(SteamPath, "steam.exe");

            var dllFile = Directory.EnumerateFiles(injDir, "GreenLuma*x64*.dll").FirstOrDefault()
                       ?? Directory.EnumerateFiles(injDir, "GreenLuma*.dll").FirstOrDefault();
            var dllFullPath = dllFile ?? Path.Combine(injDir, "GreenLuma_2026_x64.dll");

            var useFullPaths = !injDir.Equals(SteamPath, StringComparison.OrdinalIgnoreCase);
            var exeVal = useFullPaths ? steamExe : "Steam.exe";
            var dllVal = useFullPaths ? dllFullPath : Path.GetFileName(dllFullPath);

            File.WriteAllLines(iniPath, new[]
            {
                "[DllInjector]",
                "AllowMultipleInstancesOfDLLInjector = 0",
                $"UseFullPathsFromIni = {(useFullPaths ? "1" : "0")}",
                "",
                $"Exe = {exeVal}",
                "CommandLine = -inhibitbootstrap",
                "",
                $"Dll = {dllVal}",
                "",
                "Export = Init",
                "CheckReturnValue = 0",
                "TerminateOnError = 1",
                "WaitForProcessTermination = 1",
                "",
                "EnableFakeParentProcess = 0",
                "FakeParentProcess = explorer.exe",
                "",
                "CreateFiles = 0",
                "FileToCreate_1 =",
                "FileToCreate_2 ="
            }, new UTF8Encoding(false));

            Process.Start(new ProcessStartInfo(injector)
            {
                WorkingDirectory = Path.GetDirectoryName(injector)!,
                UseShellExecute = true
            });

            ActionStatus = "GreenLuma launched — Steam is restarting.";
            if (StealthMode) CleanGreenLumaLogs();

            // Trigger steam://install for each game after Steam starts
            if (Games.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(12));
                    foreach (var game in Games.ToList())
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo($"steam://install/{game.AppId}") { UseShellExecute = true });
                            await Task.Delay(1500);
                        }
                        catch { }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            ActionStatus = $"Launch failed: {ex.Message}";
        }
    }

    private string? FindInjector()
    {
        foreach (var dir in new[] { GlPath, SteamPath })
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

            if (File.Exists(Path.Combine(dir, "DLLInjector.exe")))
                return Path.Combine(dir, "DLLInjector.exe");
            if (File.Exists(Path.Combine(dir, "x64launcher.exe")) &&
                Directory.EnumerateFiles(dir, "GreenLuma*.dll").Any())
                return Path.Combine(dir, "x64launcher.exe");

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (File.Exists(Path.Combine(sub, "DLLInjector.exe")))
                        return Path.Combine(sub, "DLLInjector.exe");
                    if (File.Exists(Path.Combine(sub, "x64launcher.exe")) &&
                        Directory.EnumerateFiles(sub, "GreenLuma*.dll").Any())
                        return Path.Combine(sub, "x64launcher.exe");
                }
            }
            catch { }
        }
        return null;
    }

    // ── Search (Hubcap + Steam Store) ───────────────────────────────

    private async Task SearchAsync(string query)
    {
        _searchCts?.Cancel();
        Suggestions.Clear();
        if (string.IsNullOrWhiteSpace(query) || query.Length < 3) return;

        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        try
        {
            await Task.Delay(250, ct);
            IsSearching = true;
            if (!await SearchHubcapAsync(query, ct))
                await SearchSteamStoreAsync(query, ct);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { IsSearching = false; }
    }

    private async Task<bool> SearchHubcapAsync(string query, CancellationToken ct)
    {
        var key = await _credentials.ReadAsync("hubcap-api-key");
        if (string.IsNullOrWhiteSpace(key)) return false;

        var appSettings = _settings.Load();
        var baseUrl = appSettings.HubcapBaseUrl?.TrimEnd('/') ?? "https://hubcapmanifest.com";
        var url = $"{baseUrl}/api/v1/search?q={Uri.EscapeDataString(query)}&limit=10";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        using var resp = await _httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return false;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        JsonElement results;
        if (!doc.RootElement.TryGetProperty("results", out results) &&
            !doc.RootElement.TryGetProperty("games", out results))
            return false;

        foreach (var item in results.EnumerateArray())
        {
            if (ct.IsCancellationRequested) break;
            var id = item.TryGetProperty("game_id", out var gid) && int.TryParse(gid.ToString(), out var p1) ? p1
                   : item.TryGetProperty("app_id", out var aid) && int.TryParse(aid.ToString(), out var p2) ? p2 : 0;
            var name = item.TryGetProperty("game_name", out var gn) ? gn.GetString() ?? $"App {id}"
                     : item.TryGetProperty("name", out var n) ? n.GetString() ?? $"App {id}" : $"App {id}";
            if (id > 0) Suggestions.Add(new SteamSearchEntry(id, name));
            if (Suggestions.Count >= 10) break;
        }
        return Suggestions.Count > 0;
    }

    private async Task SearchSteamStoreAsync(string query, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&l=english&cc=US";
        using var resp = await _httpClient.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items)) return;

        foreach (var item in items.EnumerateArray())
        {
            if (ct.IsCancellationRequested) break;
            var id = item.GetProperty("id").GetInt32();
            var name = item.GetProperty("name").GetString() ?? $"App {id}";
            Suggestions.Add(new SteamSearchEntry(id, name));
            if (Suggestions.Count >= 10) break;
        }
    }

    public void SelectSuggestion(SteamSearchEntry entry)
    {
        Suggestions.Clear();
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        AddGame(entry.AppId, entry.Name);
    }

    // ── Add / Remove games ──────────────────────────────────────────

    public void AddFromInput(string input)
    {
        var trimmed = input.Trim();
        if (int.TryParse(trimmed, out var singleId) && singleId > 0)
        {
            AddGame(singleId, $"App {singleId}");
            _ = ResolveNameAsync(singleId);
            return;
        }

        var ids = Regex.Split(trimmed, @"[\s,;]+")
            .Select(s => s.Trim())
            .Where(s => Regex.IsMatch(s, @"^\d{1,10}$"))
            .Select(int.Parse)
            .Distinct()
            .ToList();

        if (ids.Count == 0) { ActionStatus = "Enter a valid App ID or search by name."; return; }
        foreach (var id in ids) { AddGame(id, $"App {id}"); _ = ResolveNameAsync(id); }
    }

    private void AddGame(int appId, string name)
    {
        if (Games.Any(g => g.AppId == appId)) { ActionStatus = $"{appId} already in list."; return; }
        Games.Add(new AppListEntry { AppId = appId, Name = name, FileLabel = $"#{Games.Count + 1}" });
        RenumberGames();
        NotifyListChanged();
        SaveGameList();
        ActionStatus = $"+ {name} ({appId})";
    }

    private void RemoveGame(int appId)
    {
        var entry = Games.FirstOrDefault(g => g.AppId == appId);
        if (entry is null) return;
        Games.Remove(entry);
        RenumberGames();
        NotifyListChanged();
        SaveGameList();
        ActionStatus = $"- {entry.Name}";
    }

    private void ClearAll() { Games.Clear(); NotifyListChanged(); SaveGameList(); ActionStatus = "List cleared."; }

    private void PasteAppIds()
    {
        try { var text = System.Windows.Clipboard.GetText(); if (!string.IsNullOrWhiteSpace(text)) AddFromInput(text); } catch { }
    }

    private void RenumberGames() { for (var i = 0; i < Games.Count; i++) Games[i].FileLabel = $"#{i + 1}"; }

    private void NotifyListChanged()
    {
        OnPropertyChanged(nameof(GameCountLabel));
        OnPropertyChanged(nameof(HasGames));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    // ── Name resolution ─────────────────────────────────────────────

    private async Task ResolveNameAsync(int appId)
    {
        var entry = Games.FirstOrDefault(g => g.AppId == appId);
        if (entry is null || !entry.Name.StartsWith("App ")) return;
        try
        {
            using var resp = await _httpClient.GetAsync($"https://store.steampowered.com/api/appdetails/?appids={appId}&filters=basic&cc=US");
            if (!resp.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty(appId.ToString(), out var el) &&
                el.TryGetProperty("data", out var data) &&
                data.TryGetProperty("name", out var nameEl))
            {
                var name = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(name)) { entry.Name = name; SaveGameList(); }
            }
        }
        catch { }
    }

    // ── Generate / Clean AppList ────────────────────────────────────

    private void GenerateAppList()
    {
        if (Games.Count == 0) return;
        try
        {
            IsBusy = true;

            var injector = FindInjector();
            var baseDir = injector != null ? Path.GetDirectoryName(injector)!
                        : HasGlPath ? GlPath
                        : HasSteamPath ? SteamPath : null;
            if (baseDir == null) { ActionStatus = "No GreenLuma or Steam path."; return; }

            var appListDir = Path.Combine(baseDir, "AppList");

            if (Directory.Exists(appListDir))
            {
                foreach (var f in Directory.EnumerateFiles(appListDir, "*.txt"))
                    try { File.Delete(f); } catch { }
                var oldIni = Path.Combine(appListDir, "AppList.ini");
                if (File.Exists(oldIni)) try { File.Delete(oldIni); } catch { }
            }
            else
                Directory.CreateDirectory(appListDir);

            // GL 2026 AppList.ini format
            var lines = new List<string> { "[AppList]" };
            foreach (var game in Games)
                lines.Add($"{game.AppId} = ");
            File.WriteAllLines(Path.Combine(appListDir, "AppList.ini"), lines, new UTF8Encoding(false));

            // Legacy numbered .txt format (compatibility with older GL / guides)
            for (var i = 0; i < Games.Count; i++)
                File.WriteAllText(Path.Combine(appListDir, $"{i}.txt"), Games[i].AppId.ToString(), new UTF8Encoding(false));

            // Create fake ACF files so Steam recognizes the games
            if (HasSteamPath)
            {
                var steamapps = Path.Combine(SteamPath, "steamapps");
                if (Directory.Exists(steamapps))
                {
                    foreach (var game in Games)
                    {
                        var acfPath = Path.Combine(steamapps, $"appmanifest_{game.AppId}.acf");
                        if (!File.Exists(acfPath))
                        {
                            var gameName = game.Name.StartsWith("App ") ? $"App_{game.AppId}" : SanitizeFolderName(game.Name);
                            File.WriteAllText(acfPath, string.Join("\r\n",
                                "\"AppState\"",
                                "{",
                                $"  \"AppID\"  \"{game.AppId}\"",
                                "  \"Universe\" \"1\"",
                                $"  \"installdir\" \"{gameName}\"",
                                "  \"StateFlags\" \"1026\"",
                                "}"), new UTF8Encoding(false));
                        }
                    }
                }
            }

            AppListCount = Games.Count;
            ActionStatus = $"AppList: {Games.Count} game(s) → {appListDir}";
            if (StealthMode) CleanGreenLumaLogs();
        }
        catch (Exception ex) { ActionStatus = $"Failed: {ex.Message}"; }
        finally { IsBusy = false; UpdateStatus(); }
    }

    private async Task PrepareManifestsAsync()
    {
        if (!HasSteamPath || Games.Count == 0) return;

        var appSettings = _settings.Load();
        var authCode = appSettings.RyuuApiKey;
        if (string.IsNullOrWhiteSpace(authCode))
            authCode = await _credentials.ReadAsync("ryuu-auth-key");
        if (string.IsNullOrWhiteSpace(authCode))
        {
            ActionStatus = "No Ryuu auth code — set it in Settings.";
            return;
        }

        var depotcache = Path.Combine(SteamPath, "depotcache");
        Directory.CreateDirectory(depotcache);
        var configVdf = Path.Combine(SteamPath, "config", "config.vdf");
        var prepared = 0;

        foreach (var game in Games.ToList())
        {
            ActionStatus = $"Fetching manifests for {game.Name}…";
            try
            {
                var zipResult = await _ryuuDownload.DownloadAsync(game.AppId, authCode!, game.Name);
                if (!zipResult.Succeeded) { ActionStatus = $"Ryuu: {zipResult.Message}"; continue; }

                using var zip = ZipFile.OpenRead(zipResult.ArchivePath);

                // Extract LUA
                var luaEntry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
                if (luaEntry is null) continue;

                string luaContent;
                using (var reader = new StreamReader(luaEntry.Open()))
                    luaContent = await reader.ReadToEndAsync();

                var depots = _ryuuService.ParseLua(luaContent);

                // Copy manifest files to depotcache
                foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)))
                {
                    var dest = Path.Combine(depotcache, entry.Name);
                    entry.ExtractToFile(dest, overwrite: true);
                }

                // Inject decryption keys into config.vdf
                if (depots.Count > 0 && File.Exists(configVdf))
                    InjectDecryptionKeys(configVdf, depots);

                // Add depot IDs to the AppList alongside the AppID
                var injector = FindInjector();
                var baseDir = injector != null ? Path.GetDirectoryName(injector)! : HasGlPath ? GlPath : SteamPath;
                var appListDir = Path.Combine(baseDir, "AppList");
                if (Directory.Exists(appListDir))
                {
                    var iniPath = Path.Combine(appListDir, "AppList.ini");
                    var existingIds = new HashSet<string>();
                    if (File.Exists(iniPath))
                    {
                        foreach (var line in File.ReadAllLines(iniPath))
                        {
                            var t = line.Trim();
                            var eq = t.IndexOf('=');
                            if (eq > 0 && !t.StartsWith('[') && !t.StartsWith('#'))
                                existingIds.Add(t[..eq].Trim());
                        }
                    }

                    var newLines = new List<string>();
                    foreach (var depot in depots)
                    {
                        var id = depot.DepotId.ToString();
                        if (existingIds.Add(id))
                            newLines.Add($"{id} = ");
                    }

                    if (newLines.Count > 0)
                    {
                        File.AppendAllLines(iniPath, newLines, new UTF8Encoding(false));
                        // Also add .txt files for legacy compat
                        var txtCount = Directory.EnumerateFiles(appListDir, "*.txt").Count();
                        for (var i = 0; i < newLines.Count; i++)
                        {
                            var depotId = newLines[i].Split('=')[0].Trim();
                            File.WriteAllText(Path.Combine(appListDir, $"{txtCount + i}.txt"), depotId, new UTF8Encoding(false));
                        }
                    }
                }

                prepared++;
                Logging.Add(LogLevel.Info, "GreenLuma", $"Prepared {depots.Count} depot(s) for {game.Name}");
            }
            catch (Exception ex)
            {
                Logging.Add(LogLevel.Error, "GreenLuma", $"Manifest prep failed for {game.AppId}: {ex.Message}");
            }
        }

        ActionStatus = prepared > 0
            ? $"Manifests ready for {prepared} game(s) — depotcache + config.vdf updated."
            : "No manifests could be fetched.";
    }

    private static void InjectDecryptionKeys(string configVdfPath, IReadOnlyList<RyuuDepotInfo> depots)
    {
        try
        {
            var content = File.ReadAllText(configVdfPath, Encoding.UTF8);

            var depotBlockIdx = content.LastIndexOf("\"depots\"", StringComparison.OrdinalIgnoreCase);
            if (depotBlockIdx < 0) return;

            var braceStart = content.IndexOf('{', depotBlockIdx);
            if (braceStart < 0) return;

            // Find the matching closing brace
            var depth = 1;
            var insertPos = braceStart + 1;
            for (var i = braceStart + 1; i < content.Length; i++)
            {
                if (content[i] == '{') depth++;
                else if (content[i] == '}') { depth--; if (depth == 0) { insertPos = i; break; } }
            }

            var sb = new StringBuilder();
            foreach (var depot in depots)
            {
                if (string.IsNullOrEmpty(depot.DecryptionKey)) continue;
                var idStr = depot.DepotId.ToString();
                if (content.Contains($"\"{idStr}\"")) continue;

                sb.AppendLine($"\t\t\t\t\"{idStr}\"");
                sb.AppendLine("\t\t\t\t{");
                sb.AppendLine($"\t\t\t\t\t\"DecryptionKey\"\t\t\"{depot.DecryptionKey}\"");
                sb.AppendLine("\t\t\t\t}");
            }

            if (sb.Length == 0) return;

            var newContent = content.Insert(insertPos, sb.ToString());
            File.WriteAllText(configVdfPath, newContent, new UTF8Encoding(false));
        }
        catch { }
    }

    private void CleanAppList()
    {
        var cleaned = false;
        foreach (var baseDir in new[] { GlPath, SteamPath })
        {
            if (string.IsNullOrWhiteSpace(baseDir)) continue;
            var dir = Path.Combine(baseDir, "AppList");
            if (!Directory.Exists(dir)) continue;
            try { Directory.Delete(dir, true); cleaned = true; } catch { }
        }
        AppListCount = 0;
        ActionStatus = cleaned ? "AppList removed." : "No AppList folder.";
        UpdateStatus();
    }

    private void CleanAllTraces()
    {
        if (!HasSteamPath) return;
        var cleaned = new List<string>();

        var appListDir = Path.Combine(SteamPath, "AppList");
        if (Directory.Exists(appListDir))
            try { Directory.Delete(appListDir, true); cleaned.Add("AppList"); } catch { }

        CleanGreenLumaLogs();
        cleaned.Add("Logs");

        foreach (var name in new[] { "GreenLuma_2024_x64.dll", "GreenLuma_2024_x86.dll", "GreenLuma.log" })
        {
            var f = Path.Combine(SteamPath, name);
            if (File.Exists(f)) try { File.Delete(f); cleaned.Add(name); } catch { }
        }

        foreach (var d in new[] { Path.Combine(SteamPath, "GreenLuma"), Path.Combine(SteamPath, "AppList") })
            if (Directory.Exists(d)) try { Directory.Delete(d, true); cleaned.Add(Path.GetFileName(d)); } catch { }

        AppListCount = 0;
        ActionStatus = cleaned.Count > 0 ? $"Cleaned: {string.Join(", ", cleaned)}" : "Nothing to clean.";
        DetectGreenLuma();
    }

    private void CleanGreenLumaLogs()
    {
        foreach (var dir in new[] { SteamPath, GlPath })
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            foreach (var pattern in new[] { "GreenLuma*.log", "GreenLuma*.txt", "GL_*.log" })
                try { foreach (var f in Directory.EnumerateFiles(dir, pattern)) try { File.Delete(f); } catch { } } catch { }
        }
    }

    // ── Restart Steam ───────────────────────────────────────────────

    private void RestartSteam()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("steam"))
                try { proc.Kill(); } catch { }

            var exe = Path.Combine(SteamPath, "steam.exe");
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                ActionStatus = "Steam restarted.";
            }
            else
                ActionStatus = "steam.exe not found.";
        }
        catch (Exception ex) { ActionStatus = $"Restart failed: {ex.Message}"; }
    }

    // ── Download ────────────────────────────────────────────────────

    private async Task DownloadGameAsync(int appId)
    {
        var entry = Games.FirstOrDefault(g => g.AppId == appId);
        if (entry == null) return;

        var settings = _settings.Load();
        var basePath = settings.DownloadFolder;
        if (string.IsNullOrWhiteSpace(basePath))
            basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamGames");

        var gameName = entry.Name.StartsWith("App ") ? $"App_{appId}" : SanitizeFolderName(entry.Name);
        var folder = Path.Combine(basePath, gameName);

        var job = new DownloadJob
        {
            AppId = appId,
            GameName = entry.Name,
            CoverImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
            TargetFolder = folder,
            Started = DateTime.Now,
            DownloadMode = "DepotDownloaderMod (Ryuu)",
            AuthorizationConfirmed = true
        };
        job.State = DownloadJobState.Preparing;
        job.Status = "Starting download…";

        Store.Downloads.Insert(0, job);
        await _downloadQueue.SaveAsync(job);
        ActionStatus = $"Download queued: {entry.Name} — check Downloads tab.";

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<string>(msg =>
                {
                    if (msg.StartsWith("PROGRESS|", StringComparison.Ordinal))
                    {
                        var parts = msg.Split('|');
                        if (parts.Length >= 10 && double.TryParse(parts[4],
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var pct))
                        {
                            job.Progress = pct;
                            job.State = DownloadJobState.Downloading;
                            if (!string.IsNullOrWhiteSpace(parts[5])) job.Downloaded = parts[5];
                            if (!string.IsNullOrWhiteSpace(parts[6])) job.TotalSize = parts[6];
                            if (!string.IsNullOrWhiteSpace(parts[7])) job.Speed = parts[7];
                            if (!string.IsNullOrWhiteSpace(parts[8])) job.Eta = parts[8];
                            job.Status = $"Downloading — {pct:0.#}%";
                        }
                    }
                    else
                    {
                        job.Status = msg;
                    }
                });

                var result = await _ryuuService.DownloadGameAsync(appId, folder, progress);
                job.State = result.Succeeded ? DownloadJobState.Completed : DownloadJobState.Failed;
                job.Status = result.Succeeded ? "Done" : result.Message;
                job.Finished = DateTime.Now;
                await _downloadQueue.SaveAsync(job);
            }
            catch (Exception ex)
            {
                job.State = DownloadJobState.Failed;
                job.Status = ex.Message;
                job.Finished = DateTime.Now;
            }
        });
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Trim();
    }

    // ── Detection ───────────────────────────────────────────────────

    private void DetectSteam()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var normalized = path.Replace('/', '\\');
                if (Directory.Exists(normalized)) { SteamPath = normalized; return; }
            }
        }
        catch { }

        foreach (var p in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam", @"D:\Steam" })
            if (Directory.Exists(p)) { SteamPath = p; return; }

        StatusMessage = "Steam not found";
    }

    private void DetectGreenLuma()
    {
        // check saved GL path
        if (HasGlPath && HasInjector(GlPath)) { GreenLumaReady = true; UpdateStatus(); return; }

        // check common locations
        var candidates = new List<string>();
        if (HasGlPath) candidates.Add(GlPath);
        candidates.Add(DefaultGlDir);
        if (HasSteamPath) candidates.Add(SteamPath);

        foreach (var dir in candidates)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            if (HasInjector(dir))
            {
                _glPath = dir;
                OnPropertyChanged(nameof(GlPath));
                OnPropertyChanged(nameof(HasGlPath));
                GreenLumaReady = true;
                UpdateStatus();
                return;
            }
            // check one level of subdirs
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (!HasInjector(sub)) continue;
                    _glPath = sub;
                    OnPropertyChanged(nameof(GlPath));
                    OnPropertyChanged(nameof(HasGlPath));
                    GreenLumaReady = true;
                    UpdateStatus();
                    return;
                }
            }
            catch { }
        }

        GreenLumaReady = false;
        UpdateStatus();
    }

    private static bool HasInjector(string dir)
    {
        if (File.Exists(Path.Combine(dir, "DLLInjector.exe")))
            return true;
        // x64launcher.exe only counts if GreenLuma DLLs are also present (avoids Steam's own bin/)
        if (File.Exists(Path.Combine(dir, "x64launcher.exe")))
            return Directory.EnumerateFiles(dir, "GreenLuma*.dll").Any();
        return false;
    }

    private void CheckExistingAppList()
    {
        foreach (var baseDir in new[] { GlPath, SteamPath })
        {
            if (string.IsNullOrWhiteSpace(baseDir)) continue;
            var dir = Path.Combine(baseDir, "AppList");
            if (!Directory.Exists(dir)) continue;

            var iniPath = Path.Combine(dir, "AppList.ini");
            if (File.Exists(iniPath))
            {
                try
                {
                    AppListCount = File.ReadAllLines(iniPath)
                        .Count(l => { var t = l.Trim(); return t.Length > 0 && !t.StartsWith('[') && !t.StartsWith('#'); });
                    return;
                }
                catch { }
            }

            var txtCount = Directory.EnumerateFiles(dir, "*.txt").Count();
            if (txtCount > 0) { AppListCount = txtCount; return; }
        }
        AppListCount = 0;
    }

    private void UpdateStatus()
    {
        if (!HasSteamPath) { StatusMessage = "Steam not found"; return; }
        var sb = new StringBuilder();
        sb.Append(GreenLumaReady ? "GL ready" : "GL not installed");
        if (AppListCount > 0) sb.Append($" · {AppListCount} in AppList");
        StatusMessage = sb.ToString();
    }

    // ── Import existing AppList ─────────────────────────────────────

    private void ImportExistingAppList()
    {
        var existingIds = Games.Select(g => g.AppId).ToHashSet();
        var added = 0;

        foreach (var baseDir in new[] { GlPath, SteamPath })
        {
            if (string.IsNullOrWhiteSpace(baseDir)) continue;
            var dir = Path.Combine(baseDir, "AppList");
            if (!Directory.Exists(dir)) continue;

            // AppList.ini format (GreenLuma 2026)
            var iniPath = Path.Combine(dir, "AppList.ini");
            if (File.Exists(iniPath))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(iniPath))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith('[') || trimmed.StartsWith('#') || trimmed.Length == 0) continue;
                        var eqIdx = trimmed.IndexOf('=');
                        var idStr = eqIdx > 0 ? trimmed[..eqIdx].Trim() : trimmed;
                        if (int.TryParse(idStr, out var appId) && appId > 0 && existingIds.Add(appId))
                        { Games.Add(new AppListEntry { AppId = appId, Name = $"App {appId}" }); added++; _ = ResolveNameAsync(appId); }
                    }
                }
                catch { }
            }

            // Legacy numbered .txt format
            foreach (var file in Directory.EnumerateFiles(dir, "*.txt").OrderBy(f => f))
            {
                try
                {
                    var content = File.ReadAllText(file).Trim();
                    if (int.TryParse(content, out var appId) && appId > 0 && existingIds.Add(appId))
                    { Games.Add(new AppListEntry { AppId = appId, Name = $"App {appId}" }); added++; _ = ResolveNameAsync(appId); }
                }
                catch { }
            }
        }
        if (added > 0) { RenumberGames(); NotifyListChanged(); }
    }

    // ── Persist ─────────────────────────────────────────────────────

    private void SaveGlPath()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(GlPathFile)!); File.WriteAllText(GlPathFile, GlPath); } catch { }
    }

    private void LoadGlPath()
    {
        if (!File.Exists(GlPathFile)) return;
        try
        {
            var p = File.ReadAllText(GlPathFile).Trim();
            if (Directory.Exists(p)) { _glPath = p; OnPropertyChanged(nameof(GlPath)); OnPropertyChanged(nameof(HasGlPath)); }
        }
        catch { }
    }

    private void SaveGameList()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SaveFile)!);
            File.WriteAllText(SaveFile,
                JsonSerializer.Serialize(Games.Select(g => new { g.AppId, g.Name }).ToList(),
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    private void LoadSavedGames()
    {
        if (!File.Exists(SaveFile)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SaveFile));
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var appId = el.GetProperty("AppId").GetInt32();
                var name = el.GetProperty("Name").GetString() ?? $"App {appId}";
                if (appId > 0 && !Games.Any(g => g.AppId == appId))
                    Games.Add(new AppListEntry { AppId = appId, Name = name });
            }
            RenumberGames();
            NotifyListChanged();
        }
        catch { }
    }
}
