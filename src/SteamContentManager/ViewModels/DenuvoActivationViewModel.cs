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
using SteamContentManager.Models;
using SteamContentManager.Services;

namespace SteamContentManager.ViewModels;

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

        _baseCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ResonanceTools");

        Directory.CreateDirectory(ColdLoaderCacheDir);
        Directory.CreateDirectory(ColdLoaderProxyCacheDir);
        Directory.CreateDirectory(SteamStubbedCacheDir);

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ResonanceTools/1.0");

        if (_downloader.HasCachedExecutable)
            _toolExecutablePath = _downloader.CachedPath!;

        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => CanGenerate);
        BrowseToolCommand = new RelayCommand(BrowseToolExecuted);
    }

    private string ColdLoaderCacheDir => Path.Combine(_baseCacheDir, "ColdLoader");
    private string ColdLoaderProxyCacheDir => Path.Combine(_baseCacheDir, "ColdLoaderProxy");
    private string SteamStubbedCacheDir => Path.Combine(_baseCacheDir, "SteamStubbed");

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

    public bool HasDownloadError => string.IsNullOrWhiteSpace(_toolExecutablePath) && !_isBusy;

    public bool CanGenerate => !IsBusy && _resolvedAppId > 0;

    public IAsyncRelayCommand GenerateCommand { get; }
    public ICommand BrowseToolCommand { get; }

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
        var key = await _credentials.ReadAsync("hubcap-api-key");
        if (string.IsNullOrWhiteSpace(key)) return false;

        var appSettings = _settings.Load();
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

            Status = "Downloading ColdLoader files…";
            await Task.WhenAll(
                EnsureCachedAsync("denuvosanctuary/coldloader", ColdLoaderCacheDir),
                EnsureCachedAsync("denuvosanctuary/coldloader-proxy", ColdLoaderProxyCacheDir),
                EnsureCachedAsync("denuvosanctuary/steam-stubbed", SteamStubbedCacheDir));

            Status = "Building steam_settings…";
            var tempDir = Path.Combine(Path.GetTempPath(), $"RT_Denuvo_{appId}");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            // steam_appid.txt in root (ColdLoader reads it here)
            File.WriteAllText(Path.Combine(tempDir, "steam_appid.txt"),
                appId.ToString(), Encoding.UTF8);

            // coldloader.ini in root
            var clIni = new StringBuilder();
            clIni.AppendLine("[coldloader]");
            clIni.AppendLine($"appid={appId}");
            File.WriteAllText(Path.Combine(tempDir, "coldloader.ini"),
                clIni.ToString(), Encoding.UTF8);

            var ssDir = Path.Combine(tempDir, "steam_settings");
            Directory.CreateDirectory(ssDir);
            Directory.CreateDirectory(Path.Combine(ssDir, "controller"));
            Directory.CreateDirectory(Path.Combine(ssDir, "image"));
            Directory.CreateDirectory(Path.Combine(ssDir, "sounds"));

            // steam_appid.txt also in steam_settings
            File.WriteAllText(Path.Combine(ssDir, "steam_appid.txt"),
                appId.ToString(), Encoding.UTF8);

            // configs.user.ini
            var userIni = new StringBuilder();
            userIni.AppendLine("[user::general]");
            userIni.AppendLine("account_name=Player");
            userIni.AppendLine($"account_steamid={_rawSteamId}");
            userIni.AppendLine($"ticket={_rawTicket}");
            userIni.AppendLine("language=english");
            File.WriteAllText(Path.Combine(ssDir, "configs.user.ini"),
                userIni.ToString(), Encoding.UTF8);

            // configs.app.ini — unlock all DLCs
            var appIni = new StringBuilder();
            appIni.AppendLine("[app::dlcs]");
            appIni.AppendLine("unlock_all = 1");
            File.WriteAllText(Path.Combine(ssDir, "configs.app.ini"),
                appIni.ToString(), Encoding.UTF8);

            // configs.main.ini
            File.WriteAllText(Path.Combine(ssDir, "configs.main.ini"),
                "[main::connectivity]\r\ndisable_lan_only=1\r\n", Encoding.UTF8);

            // configs.overlay.ini
            File.WriteAllText(Path.Combine(ssDir, "configs.overlay.ini"),
                "[overlay::general]\r\nenable_experimental_overlay = 1\r\n", Encoding.UTF8);

            // depots.txt — fetch from SteamCMD API
            Status = "Fetching depot info…";
            var depots = await FetchDepotsAsync(appId, _cts.Token);
            if (depots.Count > 0)
                File.WriteAllText(Path.Combine(ssDir, "depots.txt"),
                    string.Join(Environment.NewLine, depots) + Environment.NewLine, Encoding.UTF8);

            // supported_languages.txt — fetch from Steam Store API
            var languages = await FetchLanguagesAsync(appId, _cts.Token);
            if (languages.Count > 0)
                File.WriteAllText(Path.Combine(ssDir, "supported_languages.txt"),
                    string.Join(Environment.NewLine, languages) + Environment.NewLine, Encoding.UTF8);

            // Copy DLLs
            CopyCachedDlls(ColdLoaderCacheDir, tempDir, "coldloader");
            CopyProxyDll(ColdLoaderProxyCacheDir, tempDir, _proxyDllName);

            // steam_stubbed DLLs (steam_api*.dll + steamclient*.dll) go into steam_settings
            var stubbedDest = ssDir;
            if (_isCapcomGame)
            {
                stubbedDest = Path.Combine(ssDir, "load_dlls");
                Directory.CreateDirectory(stubbedDest);
            }
            CopyAllDlls(SteamStubbedCacheDir, stubbedDest);

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

    private async Task EnsureCachedAsync(string repo, string cacheDir)
    {
        if (Directory.Exists(cacheDir) && Directory.EnumerateFiles(cacheDir, "*.dll").Any())
            return;
        await DownloadFromGitHubAsync(repo, cacheDir);
    }

    private async Task DownloadFromGitHubAsync(string repo, string cacheDir)
    {
        try
        {
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "ResonanceTools/1.0");
            using var resp = await _httpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var assets = doc.RootElement.GetProperty("assets");

            string? archiveUrl = null;
            var directDlls = new List<(string name, string url)>();

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var dlUrl = asset.GetProperty("browser_download_url").GetString() ?? "";

                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                    archiveUrl ??= dlUrl;
                else if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    directDlls.Add((name, dlUrl));
            }

            Directory.CreateDirectory(cacheDir);

            if (archiveUrl is not null)
            {
                var bytes = await _httpClient.GetByteArrayAsync(archiveUrl);
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
            else
            {
                foreach (var (name, dlUrl) in directDlls)
                {
                    var bytes = await _httpClient.GetByteArrayAsync(dlUrl);
                    await File.WriteAllBytesAsync(Path.Combine(cacheDir, name), bytes);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logging.Add(LogLevel.Warning, "DenuvoActivation", $"Download failed {repo}: {ex.Message}");
        }
    }

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

    private static void CopyAllDlls(string cacheDir, string destDir)
    {
        if (!Directory.Exists(cacheDir)) return;
        foreach (var f in Directory.EnumerateFiles(cacheDir, "*.dll"))
            File.Copy(f, Path.Combine(destDir, Path.GetFileName(f)), true);
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
