using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpCompress.Archives;
using SharpCompress.Common;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed record LaunchOption(string Executable, string Arguments, string OsList, string DisplayName);

public sealed record SteamSearchEntry(int AppId, string Name);

public sealed class DenuvoActivationViewModel : ViewModelBase
{
    private readonly ILocalToolRunner _runner;
    private readonly IDenuvoGeneratorDownloadService _downloader;
    private readonly ILoggingService _logging;
    private readonly ICreamApiService _creamApi;
    private readonly ISettingsService _settings;
    private readonly ISecureCredentialService _credentials;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _downloadHttpClient;
    private readonly string _baseCacheDir;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _searchCts;

    private string _searchText = string.Empty;
    private int _resolvedAppId;
    private string _resolvedName = string.Empty;
    private string _toolExecutablePath = string.Empty;
    private string _status = "Enter an AppID or game name.";
    private bool _isBusy;
    private bool _isSearching;
    private string _proxyDllName = "version.dll";
    private bool _isCapcomGame;
    private bool _isDebugColdLoader;
    private bool _createLaunchScript;
    private LaunchOption? _selectedLaunchOption;
    private string _customLaunchParameters = string.Empty;
    private string _manualExePath = string.Empty;
    private string _rawTicket = string.Empty;
    private string _rawSteamId = string.Empty;

    public DenuvoActivationViewModel(
        IAppDataStore store,
        INavigationService navigation,
        ILoggingService logging,
        ILocalToolRunner runner,
        IDenuvoGeneratorDownloadService downloader,
        ISettingsService settings,
        IGameLocatorService gameLocator,
        ICreamApiService creamApi,
        ISecureCredentialService credentials)
        : base(store, navigation, logging)
    {
        _runner = runner;
        _downloader = downloader;
        _logging = logging;
        _creamApi = creamApi;
        _settings = settings;
        _credentials = credentials;

        _baseCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools");

        Directory.CreateDirectory(ColdLoaderCacheDir);
        Directory.CreateDirectory(ColdLoaderProxyCacheDir);
        Directory.CreateDirectory(SteamStubbedCacheDir);

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");

        _downloadHttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _downloadHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");

        if (_downloader.HasCachedExecutable)
            _toolExecutablePath = _downloader.CachedPath!;

        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => CanGenerate);
        BrowseToolCommand = new RelayCommand(BrowseToolExecuted);
        BrowseExeCommand = new RelayCommand(BrowseExeExecuted);
    }

    private string ColdLoaderCacheDir => Path.Combine(_baseCacheDir, _isDebugColdLoader ? "ColdLoaderDebug" : "ColdLoader");
    private string ColdLoaderProxyCacheDir => Path.Combine(_baseCacheDir, "ColdLoaderProxy");
    private string SteamStubbedCacheDir => Path.Combine(_baseCacheDir, "SteamStubbed");
    private string GbeCacheDir => Path.Combine(_baseCacheDir, "Goldberg", "current");

    public ObservableCollection<SteamSearchEntry> Suggestions { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;

            var trimmed = value?.Trim() ?? string.Empty;
            if (int.TryParse(trimmed, out var id) && id > 0)
            {
                _resolvedAppId = id;
                ResolvedName = $"App {id}";
                Suggestions.Clear();
                _ = AutoFillLaunchOptionsAsync(id);
            }
            else
            {
                _resolvedAppId = 0;
                ResolvedName = string.Empty;
                _ = SearchAsync(trimmed);
            }

            GenerateCommand.NotifyCanExecuteChanged();
        }
    }

    public string ResolvedName
    {
        get => _resolvedName;
        private set => SetProperty(ref _resolvedName, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                GenerateCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    public string[] ProxyDllOptions { get; } = { "version.dll", "winmm.dll" };

    public string ProxyDllName
    {
        get => _proxyDllName;
        set => SetProperty(ref _proxyDllName, value);
    }

    public bool IsCapcomGame
    {
        get => _isCapcomGame;
        set => SetProperty(ref _isCapcomGame, value);
    }

    public bool IsDebugColdLoader
    {
        get => _isDebugColdLoader;
        set => SetProperty(ref _isDebugColdLoader, value);
    }

    public bool CreateLaunchScript
    {
        get => _createLaunchScript;
        set => SetProperty(ref _createLaunchScript, value);
    }

    public ObservableCollection<LaunchOption> LaunchOptions { get; } = new();
    public ObservableCollection<string> LaunchExeList { get; } = new();
    public ObservableCollection<string> LaunchConfigList { get; } = new();

    public LaunchOption? SelectedLaunchOption
    {
        get => _selectedLaunchOption;
        set
        {
            if (!SetProperty(ref _selectedLaunchOption, value)) return;
            _manualExePath = string.Empty;
        }
    }

    public string SelectedLaunchExe
    {
        get => _selectedLaunchExe;
        set
        {
            if (!SetProperty(ref _selectedLaunchExe, value)) return;
            UpdateConfigsForExe(value);
        }
    }
    private string _selectedLaunchExe = string.Empty;

    public string SelectedLaunchConfig
    {
        get => _selectedLaunchConfig;
        set => SetProperty(ref _selectedLaunchConfig, value);
    }
    private string _selectedLaunchConfig = string.Empty;

    public string CustomLaunchParameters
    {
        get => _customLaunchParameters;
        set => SetProperty(ref _customLaunchParameters, value);
    }

    private void UpdateConfigsForExe(string? exe)
    {
        LaunchConfigList.Clear();
        if (string.IsNullOrWhiteSpace(exe)) return;
        foreach (var opt in LaunchOptions)
        {
            if (string.Equals(opt.Executable, exe, StringComparison.OrdinalIgnoreCase))
            {
                var label = string.IsNullOrWhiteSpace(opt.Arguments)
                    ? $"(no args)  [{opt.OsList}]"
                    : $"{opt.Arguments}  [{opt.OsList}]";
                if (!LaunchConfigList.Contains(label))
                    LaunchConfigList.Add(label);
            }
        }
        if (LaunchConfigList.Count > 0)
            SelectedLaunchConfig = LaunchConfigList[0];
    }

    public bool HasDownloadError => string.IsNullOrWhiteSpace(_toolExecutablePath) && !_isBusy;

    public bool CanGenerate => !IsBusy && _resolvedAppId > 0;

    public IAsyncRelayCommand GenerateCommand { get; }
    public ICommand BrowseToolCommand { get; }
    public ICommand BrowseExeCommand { get; }

    // ── Init ────────────────────────────────────────────────────────

    public async Task EnsureToolDownloadedAsync()
    {
        if (!string.IsNullOrWhiteSpace(_toolExecutablePath) && File.Exists(_toolExecutablePath))
            return;

        if (_downloader.HasCachedExecutable && !string.IsNullOrWhiteSpace(_downloader.CachedPath))
        {
            _toolExecutablePath = _downloader.CachedPath;
            return;
        }

        try
        {
            Status = "Downloading token generator…";
            var result = await _downloader.DownloadLatestAsync();
            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.CachedPath) && File.Exists(result.CachedPath))
            {
                _toolExecutablePath = result.CachedPath;
                Status = "Enter an AppID or game name.";
            }
            else
                Status = $"Download failed: {result.Message}";
        }
        catch (Exception ex)
        {
            Status = $"Download failed: {ex.Message}";
        }
        OnPropertyChanged(nameof(HasDownloadError));
    }

    // ── Search (Hubcap first, Steam Store fallback) ─────────────────

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

            if (await SearchHubcapAsync(query, ct)) return;
            await SearchSteamStoreAsync(query, ct);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task<bool> SearchHubcapAsync(string query, CancellationToken ct)
    {
        string? key = null;
        try { key = await _credentials.ReadAsync("hubcap-api-key"); } catch { }
        var appSettings = _settings.Load();
        if (string.IsNullOrWhiteSpace(key))
            key = appSettings.HubcapApiKey;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var baseUrl = appSettings.HubcapBaseUrl?.TrimEnd('/') ?? "https://hubcapmanifest.com";

        var encoded = Uri.EscapeDataString(query);
        var url = $"{baseUrl}/api/v1/search?q={encoded}&limit=10";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        using var resp = await _httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return false;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var results))
        {
            if (!doc.RootElement.TryGetProperty("games", out results))
                return false;
        }

        foreach (var item in results.EnumerateArray())
        {
            if (ct.IsCancellationRequested) break;

            var id = item.TryGetProperty("game_id", out var gid)
                ? (int.TryParse(gid.ToString(), out var parsed) ? parsed : 0)
                : (item.TryGetProperty("app_id", out var aid)
                    ? (int.TryParse(aid.ToString(), out var p2) ? p2 : 0) : 0);

            var name = item.TryGetProperty("game_name", out var gn)
                ? gn.GetString() ?? $"App {id}"
                : (item.TryGetProperty("name", out var n) ? n.GetString() ?? $"App {id}" : $"App {id}");

            if (id > 0)
                Suggestions.Add(new SteamSearchEntry(id, name));

            if (Suggestions.Count >= 10) break;
        }

        return Suggestions.Count > 0;
    }

    private async Task SearchSteamStoreAsync(string query, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var url = $"https://store.steampowered.com/api/storesearch/?term={encoded}&l=english&cc=US";
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
        _searchText = entry.AppId.ToString();
        OnPropertyChanged(nameof(SearchText));
        _resolvedAppId = entry.AppId;
        ResolvedName = entry.Name;
        Suggestions.Clear();
        GenerateCommand.NotifyCanExecuteChanged();
        _ = AutoFillLaunchOptionsAsync(entry.AppId);
    }

    private async Task AutoFillLaunchOptionsAsync(int appId)
    {
        try
        {
            var options = await FetchAllLaunchOptionsAsync(appId, CancellationToken.None);
            LaunchOptions.Clear();
            LaunchExeList.Clear();
            LaunchConfigList.Clear();

            var seenExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var opt in options)
            {
                LaunchOptions.Add(opt);
                if (seenExe.Add(opt.Executable))
                    LaunchExeList.Add(opt.Executable);
            }

            if (LaunchExeList.Count > 0)
                SelectedLaunchExe = LaunchExeList[0];
            if (LaunchOptions.Count > 0)
                SelectedLaunchOption = LaunchOptions[0];
        }
        catch { }
    }

    // ── Generate ────────────────────────────────────────────────────

    private async Task GenerateAsync()
    {
        if (_resolvedAppId <= 0) return;
        var appId = _resolvedAppId;
        Suggestions.Clear();

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        _rawTicket = string.Empty;
        _rawSteamId = string.Empty;

        try
        {
            Status = "Generating activation token…";
            await EnsureToolDownloadedAsync();
            if (string.IsNullOrWhiteSpace(_toolExecutablePath) || !File.Exists(_toolExecutablePath))
            {
                Status = "Token generator not available — use Browse to locate it.";
                return;
            }

            var workDir = Path.GetDirectoryName(_toolExecutablePath) ?? string.Empty;
            var stdin = $"{appId}{Environment.NewLine}{Environment.NewLine}{Environment.NewLine}";
            var req = new LocalToolRunRequest(_toolExecutablePath, string.Empty, workDir, stdin);
            var result = await _runner.RunAsync(req, _cts.Token);

            var parsed = ParseToolOutput(result.Output);
            if (parsed is null)
            {
                Status = "Token generation failed — check the generator tool.";
                return;
            }

            _rawSteamId = parsed.Value.steamId;
            _rawTicket = parsed.Value.ticket;

            Status = "Fetching DLC list…";
            List<(int AppId, string Name)> dlcs;
            try
            {
                var items = await _creamApi.FetchDlcListAsync(appId, _cts.Token);
                dlcs = items.Select(d => (d.AppId, d.Name)).ToList();
            }
            catch { dlcs = new(); }

            // Download required components
            var errors = new List<string>();

            Status = _isDebugColdLoader ? "Downloading ColdLoader (Debug)…" : "Downloading ColdLoader…";
            if (!await EnsureCachedAsync("denuvosanctuary/coldloader", ColdLoaderCacheDir, _isDebugColdLoader))
                errors.Add("ColdLoader");

            Status = "Downloading ColdLoader Proxy…";
            if (!await EnsureCachedAsync("denuvosanctuary/coldloader-proxy", ColdLoaderProxyCacheDir))
                errors.Add("ColdLoader Proxy");

            Status = "Downloading Goldberg steamclient64.dll…";
            if (!await EnsureGbeSteamClientAsync())
                errors.Add("steamclient64.dll (Goldberg)");

            if (errors.Count > 0)
            {
                Status = $"Download failed for: {string.Join(", ", errors)}. Check network/GitHub access.";
                _logging.Add(LogLevel.Error, "DenuvoActivation", $"Missing components: {string.Join(", ", errors)}");
                return;
            }

            Status = "Building package…";
            var tempDir = Path.Combine(Path.GetTempPath(), $"RT_Denuvo_{appId}");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            // ── Root: only coldloader.dll, coldloader.ini, steamclient64.dll, proxy dll ──

            File.WriteAllText(Path.Combine(tempDir, "coldloader.ini"),
                $"[settings]\r\nappid={appId}\r\ncleanup_delay = 30\r\n", Encoding.UTF8);

            CopyCachedDlls(ColdLoaderCacheDir, tempDir, "coldloader");
            CopyProxyDll(ColdLoaderProxyCacheDir, tempDir, _proxyDllName);
            CopySingleDll(GbeCacheDir, tempDir, "steamclient64.dll");

            // ── steam_settings ──

            var ssDir = Path.Combine(tempDir, "steam_settings");
            Directory.CreateDirectory(ssDir);
            Directory.CreateDirectory(Path.Combine(ssDir, "controller"));
            Directory.CreateDirectory(Path.Combine(ssDir, "fonts"));
            Directory.CreateDirectory(Path.Combine(ssDir, "image"));
            Directory.CreateDirectory(Path.Combine(ssDir, "img"));
            Directory.CreateDirectory(Path.Combine(ssDir, "sounds"));

            File.WriteAllText(Path.Combine(ssDir, "steam_appid.txt"),
                appId.ToString(), Encoding.UTF8);

            var userIni = new StringBuilder();
            userIni.AppendLine("[user::general]");
            userIni.AppendLine("account_name=Player");
            userIni.AppendLine($"account_steamid={_rawSteamId}");
            userIni.AppendLine($"ticket={_rawTicket}");
            userIni.AppendLine("language=english");
            File.WriteAllText(Path.Combine(ssDir, "configs.user.ini"),
                userIni.ToString(), Encoding.UTF8);

            var appIni = new StringBuilder();
            appIni.AppendLine("[app::dlcs]");
            appIni.AppendLine("unlock_all = 1");
            File.WriteAllText(Path.Combine(ssDir, "configs.app.ini"),
                appIni.ToString(), Encoding.UTF8);

            File.WriteAllText(Path.Combine(ssDir, "configs.main.ini"),
                "[main::connectivity]\r\ndisable_lan_only=1\r\n", Encoding.UTF8);

            File.WriteAllText(Path.Combine(ssDir, "configs.overlay.ini"),
                "[overlay::general]\r\nenable_experimental_overlay = 1\r\n", Encoding.UTF8);

            // achievements.json (empty array)
            File.WriteAllText(Path.Combine(ssDir, "achievements.json"), "[]", Encoding.UTF8);

            // branches.json (empty object)
            File.WriteAllText(Path.Combine(ssDir, "branches.json"), "{}", Encoding.UTF8);

            // steam_interfaces.txt (empty)
            File.WriteAllText(Path.Combine(ssDir, "steam_interfaces.txt"), "", Encoding.UTF8);

            Status = "Fetching depot info…";
            var depots = await FetchDepotsAsync(appId, _cts.Token);
            if (depots.Count > 0)
                File.WriteAllText(Path.Combine(ssDir, "depots.txt"),
                    string.Join(Environment.NewLine, depots) + Environment.NewLine, Encoding.UTF8);

            var languages = await FetchLanguagesAsync(appId, _cts.Token);
            if (languages.Count > 0)
                File.WriteAllText(Path.Combine(ssDir, "supported_languages.txt"),
                    string.Join(Environment.NewLine, languages) + Environment.NewLine, Encoding.UTF8);

            // launch.bat — only when toggle is on and an exe is selected
            if (_createLaunchScript && !string.IsNullOrWhiteSpace(_selectedLaunchExe))
            {
                var bat = new StringBuilder();
                bat.AppendLine("@echo off");

                string args;
                if (!string.IsNullOrWhiteSpace(_customLaunchParameters))
                    args = _customLaunchParameters.Trim();
                else if (!string.IsNullOrWhiteSpace(_selectedLaunchConfig)
                    && !_selectedLaunchConfig.StartsWith("(no args)"))
                {
                    var bracketIdx = _selectedLaunchConfig.IndexOf("  [", StringComparison.Ordinal);
                    args = bracketIdx > 0 ? _selectedLaunchConfig[..bracketIdx].Trim() : _selectedLaunchConfig.Trim();
                }
                else
                    args = "";

                var argsStr = string.IsNullOrWhiteSpace(args) ? "" : $" {args}";
                bat.AppendLine($"start \"\" \"{_selectedLaunchExe}\"{argsStr}");
                File.WriteAllText(Path.Combine(tempDir, "launch.bat"), bat.ToString(), Encoding.UTF8);
            }

            // Validate required files
            var missing = new List<string>();
            if (!Directory.EnumerateFiles(tempDir, "*.dll").Any(f =>
                Path.GetFileName(f).Contains("coldloader", StringComparison.OrdinalIgnoreCase)))
                missing.Add("coldloader.dll");
            if (!File.Exists(Path.Combine(tempDir, _proxyDllName)))
                missing.Add(_proxyDllName);
            if (!File.Exists(Path.Combine(tempDir, "steamclient64.dll")))
                missing.Add("steamclient64.dll");

            if (missing.Count > 0)
            {
                Status = $"Package incomplete — missing: {string.Join(", ", missing)}";
                _logging.Add(LogLevel.Error, "DenuvoActivation", $"Package missing files: {string.Join(", ", missing)}");
                try { Directory.Delete(tempDir, true); } catch { }
                return;
            }

            Status = "Creating .zip…";
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var zipName = $"Denuvo_App{appId}.zip";
            var zipPath = Path.Combine(desktop, zipName);

            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(tempDir, zipPath);
            Directory.Delete(tempDir, true);

            var dlcInfo = dlcs.Count > 0 ? $" ({dlcs.Count} DLCs)" : "";
            Status = $"Done! {zipName} saved to Desktop.{dlcInfo}";
            _logging.Add(LogLevel.Info, "DenuvoActivation", $"Package: {zipPath}");
        }
        catch (OperationCanceledException) { Status = "Cancelled."; }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            _logging.Add(LogLevel.Error, "DenuvoActivation", $"Generate failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ── Steam API helpers ──────────────────────────────────────────

    private async Task<List<string>> FetchDepotsAsync(int appId, CancellationToken ct)
    {
        var depots = new List<string>();
        try
        {
            var url = $"https://api.steamcmd.net/v1/info/{appId}";
            using var resp = await _httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return depots;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty(appId.ToString(), out var appData)
                && appData.TryGetProperty("depots", out var depotsEl))
            {
                foreach (var prop in depotsEl.EnumerateObject())
                {
                    if (int.TryParse(prop.Name, out _))
                        depots.Add(prop.Name);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return depots;
    }

    private async Task<List<LaunchOption>> FetchAllLaunchOptionsAsync(int appId, CancellationToken ct)
    {
        var results = new List<LaunchOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var url = $"https://api.steamcmd.net/v1/info/{appId}";
            using var resp = await _httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return results;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty(appId.ToString(), out var appData)
                && appData.TryGetProperty("config", out var config)
                && config.TryGetProperty("launch", out var launch))
            {
                foreach (var entry in launch.EnumerateObject())
                {
                    var val = entry.Value;
                    var exe = val.TryGetProperty("executable", out var exeEl) ? exeEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(exe)) continue;

                    var args = val.TryGetProperty("arguments", out var argsEl) ? argsEl.GetString() ?? "" : "";
                    var osLabel = "any";
                    if (val.TryGetProperty("config", out var cfg)
                        && cfg.TryGetProperty("oslist", out var osEl))
                        osLabel = osEl.GetString() ?? "any";

                    var key = $"{exe}|{args}|{osLabel}";
                    if (!seen.Add(key)) continue;

                    var parts = new List<string> { exe };
                    if (!string.IsNullOrWhiteSpace(args)) parts.Add(args);
                    parts.Add($"[{osLabel}]");
                    results.Add(new LaunchOption(exe, args, osLabel, string.Join("  ", parts)));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return results;
    }

    private async Task<List<string>> FetchLanguagesAsync(int appId, CancellationToken ct)
    {
        var languages = new List<string>();
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails/?appids={appId}&cc=US";
            using var resp = await _httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return languages;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty(appId.ToString(), out var entry)
                && entry.TryGetProperty("data", out var appData)
                && appData.TryGetProperty("supported_languages", out var langEl))
            {
                var raw = langEl.GetString() ?? "";
                foreach (var part in raw.Split(','))
                {
                    var cleaned = Regex.Replace(part.Trim(), @"<[^>]+>", "").Trim().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        var mapped = MapSteamLanguage(cleaned);
                        if (!languages.Contains(mapped))
                            languages.Add(mapped);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return languages;
    }

    private static string MapSteamLanguage(string lang) => lang switch
    {
        "simplified chinese" => "schinese",
        "traditional chinese" => "tchinese",
        "brazilian portuguese" => "brazilian",
        "spanish - spain" => "spanish",
        "spanish - latin america" => "latam",
        _ => lang
    };

    // ── GitHub download helpers ─────────────────────────────────────

    private async Task<bool> EnsureCachedAsync(string repo, string cacheDir, bool preferDebug = false)
    {
        if (Directory.Exists(cacheDir) && Directory.EnumerateFiles(cacheDir, "*.dll").Any())
            return true;
        return await DownloadFromGitHubAsync(repo, cacheDir, preferDebug);
    }

    private async Task<bool> DownloadFromGitHubAsync(string repo, string cacheDir, bool preferDebug = false)
    {
        try
        {
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Steamy/1.0");
            using var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                _logging.Add(LogLevel.Warning, "DenuvoActivation", $"GitHub API {(int)resp.StatusCode} for {repo}");

                return await TryDirectDownloadAsync(repo, cacheDir);
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var assets = doc.RootElement.GetProperty("assets");

            string? archiveUrl = null;
            string? fallbackArchiveUrl = null;
            var directDlls = new List<(string name, string url)>();

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var dlUrl = asset.GetProperty("browser_download_url").GetString() ?? "";

                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                {
                    var isDebug = name.Contains("debug", StringComparison.OrdinalIgnoreCase);
                    var isRelease = name.Contains("release", StringComparison.OrdinalIgnoreCase);
                    var isX64 = name.Contains("x64", StringComparison.OrdinalIgnoreCase);

                    if (preferDebug && isDebug && isX64)
                        archiveUrl = dlUrl;
                    else if (!preferDebug && (isRelease || !isDebug) && isX64)
                        archiveUrl = dlUrl;

                    fallbackArchiveUrl ??= dlUrl;
                }
                else if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    directDlls.Add((name, dlUrl));
            }

            archiveUrl ??= fallbackArchiveUrl;

            Directory.CreateDirectory(cacheDir);

            if (archiveUrl is not null)
            {
                var bytes = await _downloadHttpClient.GetByteArrayAsync(archiveUrl);
                using var stream = new MemoryStream(bytes);
                using var archive = ArchiveFactory.Open(stream);
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory) continue;
                    var name = Path.GetFileName(entry.Key ?? "");
                    if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        entry.WriteToFile(Path.Combine(cacheDir, name),
                            new ExtractionOptions { Overwrite = true });
                }
            }
            else if (directDlls.Count > 0)
            {
                foreach (var (name, dlUrl) in directDlls)
                {
                    var bytes = await _downloadHttpClient.GetByteArrayAsync(dlUrl);
                    await File.WriteAllBytesAsync(Path.Combine(cacheDir, name), bytes);
                }
            }
            else
            {
                _logging.Add(LogLevel.Warning, "DenuvoActivation", $"No downloadable assets in {repo}");
                return await TryDirectDownloadAsync(repo, cacheDir);
            }

            var hasDlls = Directory.Exists(cacheDir) && Directory.EnumerateFiles(cacheDir, "*.dll").Any();
            if (!hasDlls)
                _logging.Add(LogLevel.Warning, "DenuvoActivation", $"No DLLs extracted from {repo}");
            return hasDlls;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logging.Add(LogLevel.Warning, "DenuvoActivation", $"Download failed {repo}: {ex.Message}");
            return await TryDirectDownloadAsync(repo, cacheDir);
        }
    }

    private async Task<bool> TryDirectDownloadAsync(string repo, string cacheDir)
    {
        // Some repos publish DLLs directly at /releases/latest/download/<name>.dll
        // Try common DLL names based on repo name
        var repoName = repo.Split('/').LastOrDefault() ?? "";
        var dllNames = repoName switch
        {
            "coldloader" => new[] { "coldloader.dll", "coldloader64.dll", "ColdLoader.dll", "ColdLoader64.dll" },
            "coldloader-proxy" => new[] { "version.dll", "winmm.dll", "coldloader_proxy.dll" },
            "steam-stubbed" => new[] { "steam_api.dll", "steam_api64.dll" },
            _ => Array.Empty<string>()
        };

        if (dllNames.Length == 0) return false;

        Directory.CreateDirectory(cacheDir);
        var downloaded = false;

        foreach (var dllName in dllNames)
        {
            try
            {
                var url = $"https://github.com/{repo}/releases/latest/download/{dllName}";
                using var resp = await _downloadHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) continue;

                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)) continue;

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 1024) continue;

                await File.WriteAllBytesAsync(Path.Combine(cacheDir, dllName), bytes);
                downloaded = true;
                _logging.Add(LogLevel.Info, "DenuvoActivation", $"Downloaded {dllName} from {repo}");
            }
            catch { }
        }

        return downloaded;
    }

    // ── GBE (Goldberg) steamclient DLLs ────────────────────────────

    private async Task<bool> EnsureGbeSteamClientAsync()
    {
        var gbeDir = GbeCacheDir;
        var fallbackGbe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steamy", "gbe_fork", "current");

        // Try the Goldberg page's install first
        if ((!Directory.Exists(gbeDir) || !HasSteamClientDlls(gbeDir)) && Directory.Exists(fallbackGbe) && HasSteamClientDlls(fallbackGbe))
        {
            try
            {
                Directory.CreateDirectory(gbeDir);
                foreach (var f in Directory.EnumerateFiles(fallbackGbe, "*", SearchOption.AllDirectories))
                {
                    var fname = Path.GetFileName(f);
                    if (!fname.Contains("steamclient", StringComparison.OrdinalIgnoreCase) &&
                        !fname.Contains("GameOverlayRenderer", StringComparison.OrdinalIgnoreCase))
                        continue;
                    File.Copy(f, Path.Combine(gbeDir, fname), true);
                }
            }
            catch (Exception ex)
            {
                _logging.Add(LogLevel.Warning, "DenuvoActivation", $"Copy from Goldberg cache failed: {ex.Message}");
            }
        }

        if (HasSteamClientDlls(gbeDir))
            return true;

        // Download from gbe_fork releases
        try
        {
            var url = "https://api.github.com/repos/Detanup01/gbe_fork/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Steamy/1.0");
            using var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                _logging.Add(LogLevel.Warning, "DenuvoActivation", $"gbe_fork GitHub API: {(int)resp.StatusCode}");
                return false;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var assets = doc.RootElement.GetProperty("assets").EnumerateArray().ToList();

            // Find the Windows release archive - match broadly
            string? dlUrl = null;
            foreach (var a in assets)
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var isArchive = name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                var isWindows = name.Contains("win", StringComparison.OrdinalIgnoreCase) &&
                                !name.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
                                !name.Contains("mac", StringComparison.OrdinalIgnoreCase);
                var isRelease = !name.Contains("debug", StringComparison.OrdinalIgnoreCase) &&
                                !name.Contains("tool", StringComparison.OrdinalIgnoreCase);

                if (isArchive && isWindows && isRelease)
                {
                    dlUrl = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            // Fallback: any Windows archive
            if (dlUrl == null)
            {
                foreach (var a in assets)
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    var isArchive = name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                    var isWindows = name.Contains("win", StringComparison.OrdinalIgnoreCase);

                    if (isArchive && isWindows)
                    {
                        dlUrl = a.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            if (dlUrl == null)
            {
                _logging.Add(LogLevel.Warning, "DenuvoActivation",
                    $"No Windows archive in gbe_fork release. Assets: {string.Join(", ", assets.Select(a => a.GetProperty("name").GetString()))}");
                return false;
            }

            Status = "Downloading Goldberg emulator…";
            var bytes = await _downloadHttpClient.GetByteArrayAsync(dlUrl);
            using var ms = new MemoryStream(bytes);
            using var arc = ArchiveFactory.Open(ms);
            Directory.CreateDirectory(gbeDir);

            foreach (var entry in arc.Entries)
            {
                if (entry.IsDirectory) continue;
                var entryName = Path.GetFileName(entry.Key ?? "");
                if (entryName.Contains("steamclient", StringComparison.OrdinalIgnoreCase) ||
                    entryName.Contains("GameOverlayRenderer", StringComparison.OrdinalIgnoreCase))
                {
                    // Prefer files from steamclient_experimental subfolder
                    var entryPath = entry.Key?.Replace('/', '\\') ?? "";
                    var isExperimental = entryPath.Contains("experimental", StringComparison.OrdinalIgnoreCase);
                    var destPath = Path.Combine(gbeDir, entryName);

                    if (!File.Exists(destPath) || isExperimental)
                        entry.WriteToFile(destPath, new ExtractionOptions { Overwrite = true });
                }
            }

            if (HasSteamClientDlls(gbeDir))
            {
                _logging.Add(LogLevel.Info, "DenuvoActivation", "Goldberg steamclient DLLs downloaded");
                return true;
            }

            _logging.Add(LogLevel.Warning, "DenuvoActivation", "Downloaded gbe_fork archive but no steamclient DLLs found inside");
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logging.Add(LogLevel.Warning, "DenuvoActivation", $"GBE fetch for steamclient failed: {ex.Message}");
            return false;
        }
    }

    private static bool HasSteamClientDlls(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        return Directory.EnumerateFiles(dir, "steamclient*.dll", SearchOption.AllDirectories).Any();
    }

    private static void CopySingleDll(string cacheDir, string destDir, string dllName)
    {
        if (!Directory.Exists(cacheDir)) return;
        foreach (var f in Directory.EnumerateFiles(cacheDir, "*.dll", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(f), dllName, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(f, Path.Combine(destDir, dllName), true);
                return;
            }
        }
    }

    // ── File copy helpers ──────────────────────────────────────────

    private static void CopyCachedDlls(string cacheDir, string destDir, string nameHint)
    {
        if (!Directory.Exists(cacheDir)) return;
        foreach (var f in Directory.EnumerateFiles(cacheDir, "*.dll"))
        {
            if (Path.GetFileName(f).Contains(nameHint, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(f, Path.Combine(destDir, Path.GetFileName(f)), true);
                return;
            }
        }
        var first = Directory.EnumerateFiles(cacheDir, "*.dll").FirstOrDefault();
        if (first is not null)
            File.Copy(first, Path.Combine(destDir, Path.GetFileName(first)), true);
    }

    private static void CopyProxyDll(string cacheDir, string destDir, string targetName)
    {
        if (!Directory.Exists(cacheDir)) return;
        var exact = Path.Combine(cacheDir, targetName);
        if (File.Exists(exact))
        {
            File.Copy(exact, Path.Combine(destDir, targetName), true);
            return;
        }
        var first = Directory.EnumerateFiles(cacheDir, "*.dll").FirstOrDefault();
        if (first is not null)
            File.Copy(first, Path.Combine(destDir, targetName), true);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void BrowseToolExecuted()
    {
        var ofd = new OpenFileDialog
        {
            Filter = "Steam Ticket Generator|steam-ticket-generator.exe|All Files|*.*",
            Title = "Select steam-ticket-generator"
        };
        if (!string.IsNullOrEmpty(_toolExecutablePath))
            ofd.InitialDirectory = Path.GetDirectoryName(_toolExecutablePath);
        if (ofd.ShowDialog() == true)
        {
            _toolExecutablePath = ofd.FileName;
            Status = "Generator selected.";
            OnPropertyChanged(nameof(HasDownloadError));
        }
    }

    private void BrowseExeExecuted()
    {
        var ofd = new OpenFileDialog
        {
            Filter = "Game Executable|*.exe|All Files|*.*",
            Title = "Select Game Executable"
        };
        if (ofd.ShowDialog() == true)
        {
            var fileName = Path.GetFileName(ofd.FileName);
            if (!LaunchExeList.Contains(fileName))
                LaunchExeList.Add(fileName);
            SelectedLaunchExe = fileName;
        }
    }

    private static (string steamId, string ticket)? ParseToolOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var sid = Regex.Match(output, @"Steam ID:\s*(\d+)", RegexOptions.IgnoreCase);
        var tkt = Regex.Match(output, @"Encrypted App Ticket:\s*([A-Za-z0-9+/=]+)", RegexOptions.IgnoreCase);
        if (!sid.Success || !tkt.Success) return null;
        var s = sid.Groups[1].Value;
        var t = tkt.Groups[1].Value.Trim();
        return string.IsNullOrWhiteSpace(s) || string.IsNullOrWhiteSpace(t) ? null : (s, t);
    }
}
