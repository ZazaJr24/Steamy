using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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

    // Present after Uninstall so startup doesn't silently re-detect GL from Downloads
    private static readonly string UninstalledMarker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ResonanceTools", "greenluma_uninstalled");

    public FamilyShareViewModel(
        IAppDataStore store,
        INavigationService navigation,
        ILoggingService logging,
        ISettingsService settings,
        ISecureCredentialService credentials)
        : base(store, navigation, logging)
    {
        _settings = settings;
        _credentials = credentials;
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
        UninstallGreenLumaCommand = new RelayCommand(UninstallGreenLuma, () => GreenLumaReady);
        AutoDetectCommand = new AsyncRelayCommand(async () =>
        {
            try { File.Delete(UninstalledMarker); } catch { }
            ActionStatus = "Searching Downloads for GreenLuma…";
            await TryAutoInstallAsync();
            if (!GreenLumaReady) ActionStatus = "No GreenLuma found in Downloads.";
        });
        DetectSteam();
        LoadGlPath();
        DetectGreenLuma();
        ActionStatus = GreenLumaReady ? $"GreenLuma ready → {GlPath}" : "GreenLuma not installed.";
        if (!GreenLumaReady && !File.Exists(UninstalledMarker)) _ = TryAutoInstallAsync();
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
            if (!string.IsNullOrWhiteSpace(value)) try { File.Delete(UninstalledMarker); } catch { }
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
    public bool GreenLumaReady { get => _greenLumaReady; private set { if (SetProperty(ref _greenLumaReady, value)) { OnPropertyChanged(nameof(GreenLumaMissing)); LaunchCommand.NotifyCanExecuteChanged(); UninstallGreenLumaCommand?.NotifyCanExecuteChanged(); } } }
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
    public RelayCommand UninstallGreenLumaCommand { get; }
    public IAsyncRelayCommand AutoDetectCommand { get; }
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

    // ── Uninstall ───────────────────────────────────────────────────

    private void UninstallGreenLuma()
    {
        var answer = System.Windows.MessageBox.Show(
            "Remove GreenLuma?\n\n• Steam restarts without GreenLuma\n• GL files in the Steam folder are deleted\n• The AppList is reset\n\nYour GL download folder itself is kept.",
            "Uninstall GreenLuma", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        var removed = new List<string>();
        var steamWasRunning = Process.GetProcessesByName("steam").Length > 0;

        foreach (var name in new[] { "DLLInjector", "steam" })
            foreach (var proc in Process.GetProcessesByName(name))
                try { proc.Kill(); proc.WaitForExit(5000); } catch { }

        if (HasSteamPath)
        {
            var files = new List<string>();
            foreach (var pattern in new[] { "GreenLuma*.dll", "GreenLuma*.exe", "GreenLuma*.log", "DLLInjector.exe", "DLLInjector.ini", "user32SF.dll", "StealthMode.bin" })
                try { files.AddRange(Directory.EnumerateFiles(SteamPath, pattern)); } catch { }
            // Steam ships no user32.dll in its root — one there is always GL stealth mode
            var stealthDll = Path.Combine(SteamPath, "user32.dll");
            if (File.Exists(stealthDll)) files.Add(stealthDll);

            foreach (var f in files)
                try { File.Delete(f); removed.Add(Path.GetFileName(f)); } catch { }

            foreach (var d in new[] { "AppList", "GreenLuma2026_Files", "GreenLuma" })
            {
                var dir = Path.Combine(SteamPath, d);
                if (Directory.Exists(dir)) try { Directory.Delete(dir, true); removed.Add(d + "\\"); } catch { }
            }
        }

        if (Directory.Exists(DefaultGlDir))
            try { Directory.Delete(DefaultGlDir, true); removed.Add("app GL copy"); } catch { }

        // User-provided GL folder: keep the files, just clear the unlock list
        if (HasGlPath && !GlPath.Equals(SteamPath, StringComparison.OrdinalIgnoreCase))
        {
            var ini = Path.Combine(GlPath, "AppList", "AppList.ini");
            if (File.Exists(ini))
            {
                var slots = ReadSlots(ini);
                var lines = new List<string> { "[AppList]", "# Format:", "# Old AppID = New AppID to unlock", "# Remove the # before the old AppID", "" };
                lines.AddRange(slots.Select(s => $"#{s} = "));
                try { File.WriteAllLines(ini, lines, new UTF8Encoding(false)); removed.Add("AppList reset"); } catch { }
            }
        }

        try { File.Delete(GlPathFile); } catch { }
        try { Directory.CreateDirectory(Path.GetDirectoryName(UninstalledMarker)!); File.WriteAllText(UninstalledMarker, DateTime.Now.ToString("O")); } catch { }

        _glPath = string.Empty;
        OnPropertyChanged(nameof(GlPath));
        OnPropertyChanged(nameof(HasGlPath));
        GreenLumaReady = false;
        AppListCount = 0;
        UpdateStatus();

        if (steamWasRunning && HasSteamPath)
        {
            var exe = Path.Combine(SteamPath, "steam.exe");
            if (File.Exists(exe)) try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); } catch { }
        }

        ActionStatus = removed.Count > 0
            ? $"GreenLuma uninstalled: {string.Join(", ", removed)}"
            : "GreenLuma uninstalled (nothing left to delete).";
        Logging.Add(LogLevel.Info, "GreenLuma", ActionStatus);
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

            ActionStatus = "GreenLuma running — shared games are unlocked. Downloads are blocked while GL runs; use Restart Steam to install or update.";
            if (StealthMode) CleanGreenLumaLogs();
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

    // GreenLuma 2026 AppList.ini maps an already-licensed "old" AppID slot to the AppID to unlock:
    // "90 = 2947440". These are the slots shipped in the official template.
    private static readonly int[] DefaultSlots =
    {
        90, 205, 219, 310, 410, 570, 575, 635, 640, 740, 1213, 1273, 1840, 2145, 2403, 4270, 4940, 8680, 8710,
        8730, 8770, 13180, 17505, 17515, 17525, 17535, 17555, 17575, 17585, 18010, 18030, 22150, 34120, 41005,
        41015, 41040, 41080, 42300, 42320, 42750, 43210, 55280, 63220, 70010, 72310, 72780, 91720, 96810, 111710,
        203300, 203600, 208050, 212542, 215350, 215360, 216280, 216840, 220070, 221410, 222840, 223160, 223240,
        223250, 223350, 223910, 224620, 229950, 230030, 231390, 233780, 236600, 236650, 237410, 238670, 238690,
        255470, 258680, 261020, 261140, 261310, 265360, 266910, 294420, 302530, 302550, 312070, 313250, 315420,
        316000, 319070, 320420, 321770, 322050, 323010, 332850, 366490, 373300, 374980, 381690, 382030, 401530,
        405270, 407350, 443030, 476580, 551410, 568880, 613220, 733580, 807210, 858280, 875860, 944490, 961940,
        1042420, 1054830, 1070560, 1070910, 1113280, 1161040, 1182480, 1245040, 1391110, 1420170, 1493710,
        1580130, 1628350, 1635560, 1826330, 1874900, 1887720, 1977700, 2180100, 2230260, 2348590, 2676230,
        2738040, 2805730, 3029110, 3043620, 3086180, 3127680, 3340990, 3658110, 4183110, 4185400, 4333400,
        4427310, 4628710, 4628740, 4690330, 4862110
    };

    private static readonly Regex AppListLine = new(@"^\s*(#?)\s*(\d+)\s*=\s*(\d*)\s*$", RegexOptions.Compiled);

    private static List<int> ReadSlots(string iniPath)
    {
        var slots = new List<int>();
        if (File.Exists(iniPath))
        {
            foreach (var line in File.ReadAllLines(iniPath))
            {
                var m = AppListLine.Match(line);
                if (!m.Success) continue;
                // "N = " without '#' is the broken pre-slot format, not a slot
                var commented = m.Groups[1].Length > 0;
                var hasValue = m.Groups[3].Length > 0;
                if (commented || hasValue) slots.Add(int.Parse(m.Groups[2].Value));
            }
        }
        return slots.Count >= 10 ? slots.Distinct().ToList() : DefaultSlots.ToList();
    }

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
            Directory.CreateDirectory(appListDir);
            var iniPath = Path.Combine(appListDir, "AppList.ini");

            var steamapps = HasSteamPath ? Path.Combine(SteamPath, "steamapps") : null;
            var gameIds = Games.Select(g => g.AppId).ToHashSet();
            var slots = ReadSlots(iniPath);
            // Remapping a slot the user really has installed would hide that app
            var usable = slots.Where(s => !gameIds.Contains(s) &&
                (steamapps == null || !File.Exists(Path.Combine(steamapps, $"appmanifest_{s}.acf")))).ToList();

            if (Games.Count > usable.Count)
            {
                ActionStatus = $"Too many games: GreenLuma has {usable.Count} free slots.";
                return;
            }

            var assigned = new Dictionary<int, int>();
            for (var i = 0; i < Games.Count; i++)
                assigned[usable[i]] = Games[i].AppId;

            var lines = new List<string>
            {
                "[AppList]",
                "# Format:",
                "# Old AppID = New AppID to unlock",
                "# Remove the # before the old AppID",
                ""
            };
            foreach (var slot in slots)
                lines.Add(assigned.TryGetValue(slot, out var appId) ? $"{slot} = {appId}" : $"#{slot} = ");

            File.WriteAllLines(iniPath, lines, new UTF8Encoding(false));

            // GL 2026 only reads AppList.ini; leftover numbered .txt files are from the old format
            foreach (var f in Directory.EnumerateFiles(appListDir, "*.txt"))
                try { File.Delete(f); } catch { }

            AppListCount = Games.Count;
            ActionStatus = $"AppList.ini: {Games.Count} game(s) mapped → {appListDir}";
            if (StealthMode) CleanGreenLumaLogs();
        }
        catch (Exception ex) { ActionStatus = $"Failed: {ex.Message}"; }
        finally { IsBusy = false; UpdateStatus(); }
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

        var steamapps = Path.Combine(SteamPath, "steamapps");
        foreach (var game in Games)
        {
            var acf = Path.Combine(steamapps, $"appmanifest_{game.AppId}.acf");
            if (IsPlaceholderAcf(acf)) try { File.Delete(acf); cleaned.Add($"acf {game.AppId}"); } catch { }
        }

        AppListCount = 0;
        ActionStatus = cleaned.Count > 0 ? $"Cleaned: {string.Join(", ", cleaned)}" : "Nothing to clean.";
        DetectGreenLuma();
    }

    // Manifest with nothing on disk (e.g. hand-made "fake ACF"): shows as installed + Buy in Steam
    private static bool IsPlaceholderAcf(string path)
    {
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path);
        var noDepots = !text.Contains("\"InstalledDepots\"") || Regex.IsMatch(text, "\"InstalledDepots\"\\s*\\{\\s*\\}");
        var noSize = !Regex.IsMatch(text, "\"SizeOnDisk\"\\s*\"[1-9]");
        return noDepots && noSize;
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
                try { AppListCount = ReadMappedAppIds(iniPath).Count; return; }
                catch { }
            }

            var txtCount = Directory.EnumerateFiles(dir, "*.txt").Count();
            if (txtCount > 0) { AppListCount = txtCount; return; }
        }
        AppListCount = 0;
    }

    private static List<int> ReadMappedAppIds(string iniPath)
    {
        var ids = new List<int>();
        foreach (var line in File.ReadAllLines(iniPath))
        {
            var m = AppListLine.Match(line);
            if (m.Success && m.Groups[1].Length == 0 && int.TryParse(m.Groups[3].Value, out var appId) && appId > 0)
                ids.Add(appId);
        }
        return ids;
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
                    foreach (var appId in ReadMappedAppIds(iniPath))
                    {
                        if (existingIds.Add(appId))
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
