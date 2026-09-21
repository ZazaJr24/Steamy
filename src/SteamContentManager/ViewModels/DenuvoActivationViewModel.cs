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
    private string _customZipPath = string.Empty;
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
        BrowseZipCommand = new RelayCommand(BrowseZipExecuted);
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

    public string CustomZipPath
    {
        get => _customZipPath;
        set
        {
            if (SetProperty(ref _customZipPath, value))
                OnPropertyChanged(nameof(CustomZipName));
        }
    }

    public string CustomZipName => string.IsNullOrWhiteSpace(_customZipPath)
        ? string.Empty
        : Path.GetFileName(_customZipPath);

    public bool HasDownloadError => string.IsNullOrWhiteSpace(_toolExecutablePath) && !_isBusy;

    public bool CanGenerate => !IsBusy && _resolvedAppId > 0;

    public IAsyncRelayCommand GenerateCommand { get; }
    public ICommand BrowseToolCommand { get; }
    public ICommand BrowseZipCommand { get; }

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

            var ssDir = Path.Combine(tempDir, "steam_settings");
            Directory.CreateDirectory(ssDir);

            File.WriteAllText(Path.Combine(ssDir, "steam_appid.txt"),
                appId.ToString(), Encoding.UTF8);

            var ini = new StringBuilder();
            ini.AppendLine("[user::general]");
            ini.AppendLine($"account_steamid={_rawSteamId}");
            ini.AppendLine($"ticket={_rawTicket}");
            File.WriteAllText(Path.Combine(ssDir, "config.user.ini"),
                ini.ToString(), Encoding.UTF8);

            if (dlcs.Count > 0)
            {
                var dlcTxt = new StringBuilder();
                foreach (var d in dlcs)
                    dlcTxt.AppendLine($"{d.AppId}={d.Name}");
                File.WriteAllText(Path.Combine(ssDir, "DLC.txt"),
                    dlcTxt.ToString(), Encoding.UTF8);
            }

            CopyCachedDlls(ColdLoaderCacheDir, tempDir, "coldloader");
            CopyProxyDll(ColdLoaderProxyCacheDir, tempDir, _proxyDllName);

            if (_isCapcomGame)
            {
                var loadDlls = Path.Combine(ssDir, "load_dlls");
                Directory.CreateDirectory(loadDlls);
                CopyCachedDlls(SteamStubbedCacheDir, loadDlls, "steam_api");
            }
            else
            {
                CopyCachedDlls(SteamStubbedCacheDir, tempDir, "steam_api");
            }

            if (!string.IsNullOrWhiteSpace(_customZipPath) && File.Exists(_customZipPath))
            {
                Status = "Extracting custom archive…";
                ExtractArchive(_customZipPath, tempDir);
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

    private void BrowseZipExecuted()
    {
        var ofd = new OpenFileDialog
        {
            Filter = "Archives|*.zip;*.7z;*.rar|ZIP|*.zip|7-Zip|*.7z|RAR|*.rar|All Files|*.*",
            Title = "Select an archive to include in the package"
        };
        if (ofd.ShowDialog() == true)
            CustomZipPath = ofd.FileName;
    }

    private static void ExtractArchive(string archivePath, string destDir)
    {
        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        if (ext == ".zip")
        {
            ZipFile.ExtractToDirectory(archivePath, destDir, true);
            return;
        }

        using var stream = File.OpenRead(archivePath);
        using var archive = ArchiveFactory.Open(stream);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;
            entry.WriteToDirectory(destDir, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });
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
