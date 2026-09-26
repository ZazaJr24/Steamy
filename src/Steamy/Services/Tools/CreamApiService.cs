using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Steamy.Services;

public sealed record DlcEntry(int AppId, string Name, bool IsSelected = true);

public sealed record CreamApiApplyResult(bool Succeeded, string Message);

public enum DlcUnlockerMode { CreamAPI, SmokeAPI, Koalageddon, UplayR2Unlocker }

public interface ICreamApiService
{
    bool HasCachedDlls(DlcUnlockerMode mode);
    string ProxyAddress { get; set; }
    Task EnsureDllsAvailableAsync(DlcUnlockerMode mode, CancellationToken ct = default);
    Task<bool> ExtractDllsFromArchiveAsync(string archivePath, CancellationToken ct = default);
    Task<IReadOnlyList<DlcEntry>> FetchDlcListAsync(int appId, CancellationToken ct = default);
    CreamApiApplyResult ApplyToGameFolder(string gameFolder, int appId, IReadOnlyList<DlcEntry> dlcs,
        DlcUnlockerMode mode, string language, bool unlockAll, bool extraProtection, bool forceOffline);
    CreamApiApplyResult RestoreOriginalDlls(string gameFolder);
}

public sealed class CreamApiService : ICreamApiService, IDisposable
{
    private const string SmokeApiRepo = "acidicoala/SmokeAPI";
    private const int MaxConcurrentNameFetches = 10;

    private readonly string _baseCacheDir;
    private readonly ILoggingService _logging;
    private readonly SemaphoreSlim _nameGate = new(MaxConcurrentNameFetches, MaxConcurrentNameFetches);
    private HttpClient _httpClient;
    private string _proxyAddress = string.Empty;

    public CreamApiService(ILoggingService logging)
    {
        _logging = logging;
        _baseCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steamy");

        Directory.CreateDirectory(CreamApiCacheDir);
        Directory.CreateDirectory(SmokeApiCacheDir);
        Directory.CreateDirectory(KoalageddonCacheDir);
        Directory.CreateDirectory(UplayR2CacheDir);

        _httpClient = CreateHttpClient(string.Empty);
    }

    private string CreamApiCacheDir => Path.Combine(_baseCacheDir, "CreamAPI");
    private string SmokeApiCacheDir => Path.Combine(_baseCacheDir, "SmokeAPI");
    private string KoalageddonCacheDir => Path.Combine(_baseCacheDir, "Koalageddon");
    private string UplayR2CacheDir => Path.Combine(_baseCacheDir, "UplayR2Unlocker");

    public bool HasCachedDlls(DlcUnlockerMode mode) => mode switch
    {
        DlcUnlockerMode.CreamAPI =>
            File.Exists(Path.Combine(CreamApiCacheDir, "steam_api.dll")) ||
            File.Exists(Path.Combine(CreamApiCacheDir, "steam_api64.dll")),
        DlcUnlockerMode.SmokeAPI =>
            File.Exists(Path.Combine(SmokeApiCacheDir, "smoke_api32.dll")) ||
            File.Exists(Path.Combine(SmokeApiCacheDir, "smoke_api64.dll")),
        DlcUnlockerMode.Koalageddon =>
            File.Exists(Path.Combine(KoalageddonCacheDir, "Koalageddon.dll")) ||
            File.Exists(Path.Combine(KoalageddonCacheDir, "Koalageddon64.dll")) ||
            Directory.Exists(KoalageddonCacheDir),
        DlcUnlockerMode.UplayR2Unlocker =>
            File.Exists(Path.Combine(UplayR2CacheDir, "UplayR2Unlocker.dll")) ||
            File.Exists(Path.Combine(UplayR2CacheDir, "UplayR2Unlocker64.dll")) ||
            Directory.Exists(UplayR2CacheDir),
        _ => false
    };

    public string ProxyAddress
    {
        get => _proxyAddress;
        set
        {
            if (_proxyAddress == value) return;
            _proxyAddress = value;
            _httpClient.Dispose();
            _httpClient = CreateHttpClient(value);
        }
    }

    public async Task<bool> ExtractDllsFromArchiveAsync(string archivePath, CancellationToken ct)
    {
        if (!File.Exists(archivePath)) return false;

        try
        {
            var bytes = await File.ReadAllBytesAsync(archivePath, ct);
            using var stream = new MemoryStream(bytes);
            using var archive = ArchiveFactory.Open(stream);

            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                var name = Path.GetFileName(entry.Key ?? string.Empty);

                string? destDir = null;
                if (name.Equals("steam_api.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase))
                    destDir = CreamApiCacheDir;
                else if (name.Equals("smoke_api32.dll", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("smoke_api64.dll", StringComparison.OrdinalIgnoreCase))
                    destDir = SmokeApiCacheDir;

                if (destDir is not null)
                    entry.WriteToFile(Path.Combine(destDir, name.ToLowerInvariant()), new ExtractionOptions { Overwrite = true });
            }

            _logging.Add(Models.LogLevel.Info, "DLCUnlocker", $"DLLs extracted from {Path.GetFileName(archivePath)}");
            return HasCachedDlls(DlcUnlockerMode.CreamAPI) || HasCachedDlls(DlcUnlockerMode.SmokeAPI);
        }
        catch (Exception ex)
        {
            _logging.Add(Models.LogLevel.Error, "DLCUnlocker", $"Failed to extract DLLs: {ex.Message}");
            return false;
        }
    }

    // ── DLC fetching ────────────────────────────────────────────────

    public async Task<IReadOnlyList<DlcEntry>> FetchDlcListAsync(int appId, CancellationToken ct)
    {
        var list = await FetchDlcViaSteamCmdAsync(appId, ct);
        if (list.Count > 0) return list;

        list = await FetchDlcFromStoreApiAsync(appId, ct);
        if (list.Count > 0) return list;

        _logging.Add(Models.LogLevel.Warning, "DLCUnlocker", $"No DLCs found for app {appId} from any source.");
        return list;
    }

    private async Task<List<DlcEntry>> FetchDlcViaSteamCmdAsync(int appId, CancellationToken ct)
    {
        var list = new List<DlcEntry>();
        var appIdStr = appId.ToString(CultureInfo.InvariantCulture);

        try
        {
            var url = $"https://api.steamcmd.net/v1/info/{appIdStr}";
            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
            if (!data.TryGetProperty(appIdStr, out var appData)) return list;

            string? dlcCsv = null;
            if (appData.TryGetProperty("extended", out var extended) &&
                extended.TryGetProperty("listofdlc", out var dlcProp))
            {
                dlcCsv = dlcProp.GetString();
            }

            if (string.IsNullOrWhiteSpace(dlcCsv))
            {
                _logging.Add(Models.LogLevel.Info, "DLCUnlocker", $"SteamCMD API returned no DLC list for {appId}.");
                return list;
            }

            var dlcIds = dlcCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => int.TryParse(s, out _))
                .Select(s => int.Parse(s, CultureInfo.InvariantCulture))
                .ToList();

            _logging.Add(Models.LogLevel.Info, "DLCUnlocker", $"SteamCMD: {dlcIds.Count} DLC IDs for app {appId}. Fetching names…");

            var tasks = dlcIds.Select(id => FetchDlcNameAsync(id, ct)).ToList();
            var entries = await Task.WhenAll(tasks);
            list.AddRange(entries);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logging.Add(Models.LogLevel.Warning, "DLCUnlocker", $"SteamCMD fetch failed for {appId}: {ex.Message}");
        }

        return list;
    }

    private async Task<DlcEntry> FetchDlcNameAsync(int dlcId, CancellationToken ct)
    {
        await _nameGate.WaitAsync(ct);
        try
        {
            var url = $"https://api.steamcmd.net/v1/info/{dlcId.ToString(CultureInfo.InvariantCulture)}";
            var json = await _httpClient.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);

            var dlcIdStr = dlcId.ToString(CultureInfo.InvariantCulture);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty(dlcIdStr, out var appData) &&
                appData.TryGetProperty("common", out var common) &&
                common.TryGetProperty("name", out var nameProp))
            {
                var name = nameProp.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    return new DlcEntry(dlcId, name);
            }
        }
        catch { }
        finally
        {
            _nameGate.Release();
        }

        return new DlcEntry(dlcId, $"DLC {dlcId}");
    }

    private async Task<List<DlcEntry>> FetchDlcFromStoreApiAsync(int appId, CancellationToken ct)
    {
        var list = new List<DlcEntry>();
        var appIdStr = appId.ToString(CultureInfo.InvariantCulture);

        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appIdStr}";
            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return list;

            var json = await response.Content.ReadAsStringAsync(ct);
            if (json is null or "null") return list;
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty(appIdStr, out var appData)) return list;
            if (!appData.TryGetProperty("success", out var success) || !success.GetBoolean()) return list;
            if (!appData.TryGetProperty("data", out var appInfo)) return list;
            if (!appInfo.TryGetProperty("dlc", out var dlcArray)) return list;

            foreach (var dlcId in dlcArray.EnumerateArray())
            {
                var id = dlcId.GetInt32();
                list.Add(new DlcEntry(id, $"DLC {id}"));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logging.Add(Models.LogLevel.Warning, "DLCUnlocker", $"Store API failed for {appId}: {ex.Message}");
        }

        return list;
    }

    // ── DLL provisioning ────────────────────────────────────────────

    public async Task EnsureDllsAvailableAsync(DlcUnlockerMode mode, CancellationToken ct = default)
    {
        if (HasCachedDlls(mode)) return;

        switch (mode)
        {
            case DlcUnlockerMode.CreamAPI:
                ExtractEmbeddedCreamApiDlls();
                break;
            case DlcUnlockerMode.SmokeAPI:
                await DownloadSmokeApiAsync(ct);
                break;
            case DlcUnlockerMode.Koalageddon:
                await DownloadFromGitHubAsync("acidicoala/Koalageddon2", KoalageddonCacheDir, ct);
                break;
            case DlcUnlockerMode.UplayR2Unlocker:
                await DownloadFromGitHubAsync("acidicoala/UplayR2Unlocker", UplayR2CacheDir, ct);
                break;
        }
    }

    private void ExtractEmbeddedCreamApiDlls()
    {
        var assembly = Assembly.GetExecutingAssembly();
        ExtractEmbeddedResource(assembly, "CreamAPI.steam_api.dll", Path.Combine(CreamApiCacheDir, "steam_api.dll"));
        ExtractEmbeddedResource(assembly, "CreamAPI.steam_api64.dll", Path.Combine(CreamApiCacheDir, "steam_api64.dll"));

        if (HasCachedDlls(DlcUnlockerMode.CreamAPI))
            _logging.Add(Models.LogLevel.Info, "DLCUnlocker", "CreamAPI DLLs extracted from embedded resources.");
        else
            _logging.Add(Models.LogLevel.Warning, "DLCUnlocker", "Failed to extract embedded CreamAPI DLLs.");
    }

    private static void ExtractEmbeddedResource(Assembly assembly, string resourceName, string destPath)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return;

        using var fs = File.Create(destPath);
        stream.CopyTo(fs);
    }

    private async Task DownloadSmokeApiAsync(CancellationToken ct)
    {
        _logging.Add(Models.LogLevel.Info, "DLCUnlocker", "Downloading SmokeAPI from GitHub…");

        try
        {
            var url = $"https://api.github.com/repos/{SmokeApiRepo}/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Steamy/1.0");
            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                await DownloadSmokeApiDirectAsync(ct);
                return;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var assets = doc.RootElement.GetProperty("assets");

            string? downloadUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("SmokeAPI", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (downloadUrl is not null)
                await DownloadAndExtractSmokeApiAsync(downloadUrl, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logging.Add(Models.LogLevel.Warning, "DLCUnlocker", $"SmokeAPI download failed: {ex.Message}");
        }
    }

    private async Task DownloadSmokeApiDirectAsync(CancellationToken ct)
    {
        try
        {
            await DownloadAndExtractSmokeApiAsync(
                $"https://github.com/{SmokeApiRepo}/releases/latest/download/SmokeAPI.zip", ct);
        }
        catch (Exception ex)
        {
            _logging.Add(Models.LogLevel.Warning, "DLCUnlocker", $"Direct SmokeAPI download failed: {ex.Message}");
        }
    }

    private async Task DownloadAndExtractSmokeApiAsync(string downloadUrl, CancellationToken ct)
    {
        var archiveBytes = await _httpClient.GetByteArrayAsync(downloadUrl, ct);
        using var stream = new MemoryStream(archiveBytes);
        using var archive = ArchiveFactory.Open(stream);

        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;
            var name = Path.GetFileName(entry.Key ?? "");
            if (name.Equals("smoke_api32.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("smoke_api64.dll", StringComparison.OrdinalIgnoreCase))
            {
                var dest = Path.Combine(SmokeApiCacheDir, name.ToLowerInvariant());
                entry.WriteToFile(dest, new ExtractionOptions { Overwrite = true });
            }
        }

        if (HasCachedDlls(DlcUnlockerMode.SmokeAPI))
            _logging.Add(Models.LogLevel.Info, "DLCUnlocker", "SmokeAPI DLLs downloaded and cached.");
    }

    private async Task DownloadFromGitHubAsync(string repo, string cacheDir, CancellationToken ct)
    {
        _logging.Add(Models.LogLevel.Info, "DLCUnlocker", $"Downloading from {repo}…");
        try
        {
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Steamy/1.0");
            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logging.Add(Models.LogLevel.Warning, "DLCUnlocker", $"GitHub API returned {response.StatusCode} for {repo}");
                return;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var assets = doc.RootElement.GetProperty("assets");

            string? downloadUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (downloadUrl is null) return;

            var archiveBytes = await _httpClient.GetByteArrayAsync(downloadUrl, ct);
            using var stream = new MemoryStream(archiveBytes);
            using var archive = ArchiveFactory.Open(stream);

            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                var name = Path.GetFileName(entry.Key ?? "");
                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".config", StringComparison.OrdinalIgnoreCase))
                {
                    entry.WriteToFile(Path.Combine(cacheDir, name), new ExtractionOptions { Overwrite = true });
                }
            }

            _logging.Add(Models.LogLevel.Info, "DLCUnlocker", $"{repo} files downloaded and cached.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logging.Add(Models.LogLevel.Warning, "DLCUnlocker", $"Download failed for {repo}: {ex.Message}");
        }
    }

    // ── Apply / Restore ─────────────────────────────────────────────

    public CreamApiApplyResult ApplyToGameFolder(
        string gameFolder, int appId, IReadOnlyList<DlcEntry> dlcs,
        DlcUnlockerMode mode, string language, bool unlockAll, bool extraProtection, bool forceOffline)
    {
        if (!Directory.Exists(gameFolder))
            return new CreamApiApplyResult(false, "Game folder does not exist.");

        if (!HasCachedDlls(mode))
            return new CreamApiApplyResult(false, $"{mode} DLLs not available.");

        try
        {
            var dllDirs = FindSteamApiDirectories(gameFolder);
            if (dllDirs.Count == 0)
                return new CreamApiApplyResult(false, "No steam_api.dll / steam_api64.dll found in game folder.");

            var messages = new List<string>();

            foreach (var dir in dllDirs)
            {
                var relDir = Path.GetRelativePath(gameFolder, dir);
                if (relDir == ".") relDir = "(root)";
                messages.Add($"— {relDir}");

                switch (mode)
                {
                    case DlcUnlockerMode.CreamAPI:
                        ApplyCreamApi(dir, appId, dlcs, messages, language, unlockAll, extraProtection, forceOffline);
                        break;
                    case DlcUnlockerMode.SmokeAPI:
                        ApplySmokeApi(dir, appId, dlcs, messages, unlockAll);
                        break;
                    case DlcUnlockerMode.Koalageddon:
                    case DlcUnlockerMode.UplayR2Unlocker:
                        messages.Add($"{mode} is configured globally — no per-game DLL swap needed.");
                        break;
                }
            }

            _logging.Add(Models.LogLevel.Info, "DLCUnlocker",
                $"Applied {mode} to {Path.GetFileName(gameFolder)} (App {appId}) in {dllDirs.Count} location(s) with {dlcs.Count(d => d.IsSelected)} DLCs");

            return new CreamApiApplyResult(true, string.Join(Environment.NewLine, messages));
        }
        catch (Exception ex)
        {
            return new CreamApiApplyResult(false, $"Error: {ex.Message}");
        }
    }

    private static List<string> FindSteamApiDirectories(string gameFolder)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(gameFolder, "steam_api.dll", SearchOption.AllDirectories))
            dirs.Add(Path.GetDirectoryName(file)!);
        foreach (var file in Directory.EnumerateFiles(gameFolder, "steam_api64.dll", SearchOption.AllDirectories))
            dirs.Add(Path.GetDirectoryName(file)!);

        return dirs.OrderBy(d => d).ToList();
    }

    private void ApplyCreamApi(string targetDir, int appId, IReadOnlyList<DlcEntry> dlcs,
        List<string> messages, string language, bool unlockAll, bool extraProtection, bool forceOffline)
    {
        BackupAndReplace(targetDir, "steam_api.dll", Path.Combine(CreamApiCacheDir, "steam_api.dll"), messages);
        BackupAndReplace(targetDir, "steam_api64.dll", Path.Combine(CreamApiCacheDir, "steam_api64.dll"), messages);

        var ini = BuildCreamApiIni(appId, dlcs, language, unlockAll, extraProtection, forceOffline);
        File.WriteAllText(Path.Combine(targetDir, "cream_api.ini"), ini, new UTF8Encoding(false));
        messages.Add("cream_api.ini written");
    }

    private void ApplySmokeApi(string targetDir, int appId, IReadOnlyList<DlcEntry> dlcs,
        List<string> messages, bool unlockAll)
    {
        BackupAndReplace(targetDir, "steam_api.dll", Path.Combine(SmokeApiCacheDir, "smoke_api32.dll"), messages);
        BackupAndReplace(targetDir, "steam_api64.dll", Path.Combine(SmokeApiCacheDir, "smoke_api64.dll"), messages);

        var config = BuildSmokeApiConfig(dlcs, unlockAll);
        File.WriteAllText(Path.Combine(targetDir, "SmokeAPI.config.json"), config, new UTF8Encoding(false));
        messages.Add("SmokeAPI.config.json written");
    }

    public CreamApiApplyResult RestoreOriginalDlls(string gameFolder)
    {
        if (!Directory.Exists(gameFolder))
            return new CreamApiApplyResult(false, "Game folder does not exist.");

        var dllDirs = FindSteamApiDirectories(gameFolder);
        // Also check dirs that have backup _o.dll files even if the originals are already restored
        foreach (var file in Directory.EnumerateFiles(gameFolder, "steam_api_o.dll", SearchOption.AllDirectories))
            dllDirs.Add(Path.GetDirectoryName(file)!);
        foreach (var file in Directory.EnumerateFiles(gameFolder, "steam_api64_o.dll", SearchOption.AllDirectories))
            dllDirs.Add(Path.GetDirectoryName(file)!);

        if (dllDirs.Count == 0)
            return new CreamApiApplyResult(false, "No steam_api DLLs or backups found.");

        var restored = new List<string>();

        foreach (var dir in dllDirs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d))
        {
            RestoreBackup(dir, "steam_api.dll", restored);
            RestoreBackup(dir, "steam_api64.dll", restored);

            foreach (var configName in new[] { "cream_api.ini", "SmokeAPI.config.json", "SmokeAPI.cache.json" })
            {
                var path = Path.Combine(dir, configName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    restored.Add($"{configName} removed");
                }
            }
        }

        return restored.Count > 0
            ? new CreamApiApplyResult(true, string.Join(Environment.NewLine, restored))
            : new CreamApiApplyResult(false, "No backup files found.");
    }

    private static void BackupAndReplace(string gameFolder, string originalDll, string replacementPath, List<string> messages)
    {
        var gameDll = Path.Combine(gameFolder, originalDll);
        var backupDll = Path.Combine(gameFolder, Path.GetFileNameWithoutExtension(originalDll) + "_o.dll");

        if (!File.Exists(replacementPath)) return;
        if (!File.Exists(gameDll)) return;

        if (!File.Exists(backupDll))
        {
            File.Copy(gameDll, backupDll, overwrite: false);
            messages.Add($"{originalDll} → {Path.GetFileName(backupDll)} (Backup)");
        }

        File.Copy(replacementPath, gameDll, overwrite: true);
        messages.Add($"{originalDll} replaced");
    }

    private static void RestoreBackup(string gameFolder, string dllName, List<string> messages)
    {
        var gameDll = Path.Combine(gameFolder, dllName);
        var backupName = Path.GetFileNameWithoutExtension(dllName) + "_o.dll";
        var backupDll = Path.Combine(gameFolder, backupName);
        var legacyBak = gameDll + ".bak";

        if (File.Exists(backupDll))
        {
            File.Copy(backupDll, gameDll, overwrite: true);
            File.Delete(backupDll);
            messages.Add($"Original {dllName} restored");
        }
        else if (File.Exists(legacyBak))
        {
            File.Copy(legacyBak, gameDll, overwrite: true);
            File.Delete(legacyBak);
            messages.Add($"Original {dllName} restored");
        }
    }

    private static string BuildCreamApiIni(int appId, IReadOnlyList<DlcEntry> dlcs,
        string language, bool unlockAll, bool extraProtection, bool forceOffline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[steam]");
        sb.AppendLine($"appid = {appId.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"language = {language}");
        sb.AppendLine($"unlockall = {(unlockAll ? "true" : "false")}");
        sb.AppendLine("orgapi = steam_api_o.dll");
        sb.AppendLine("orgapi64 = steam_api64_o.dll");
        sb.AppendLine($"extraprotection = {(extraProtection ? "true" : "false")}");
        sb.AppendLine($"forceoffline = {(forceOffline ? "true" : "false")}");
        sb.AppendLine();
        sb.AppendLine("[steam_misc]");
        sb.AppendLine("disableuserinterface = false");
        sb.AppendLine();
        sb.AppendLine("[dlc]");

        foreach (var dlc in dlcs.Where(d => d.IsSelected))
            sb.AppendLine($"{dlc.AppId.ToString(CultureInfo.InvariantCulture)} = {dlc.Name}");

        return sb.ToString();
    }

    private static string BuildSmokeApiConfig(IReadOnlyList<DlcEntry> dlcs, bool unlockAll)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"$version\": 4,");
        sb.AppendLine("  \"logging\": false,");
        sb.AppendLine($"  \"default_app_status\": \"{(unlockAll ? "unlocked" : "original")}\",");
        sb.AppendLine("  \"override_app_status\": {},");

        var lockedDlcs = dlcs.Where(d => !d.IsSelected).ToList();
        if (lockedDlcs.Count > 0)
        {
            sb.AppendLine("  \"override_dlc_status\": {");
            for (int i = 0; i < lockedDlcs.Count; i++)
            {
                var dlc = lockedDlcs[i];
                var comma = i < lockedDlcs.Count - 1 ? "," : "";
                sb.AppendLine($"    \"{dlc.AppId.ToString(CultureInfo.InvariantCulture)}\": \"locked\"{comma}");
            }
            sb.AppendLine("  },");
        }
        else
        {
            sb.AppendLine("  \"override_dlc_status\": {},");
        }

        sb.AppendLine("  \"auto_inject_inventory\": true,");
        sb.AppendLine("  \"extra_inventory_items\": [],");
        sb.AppendLine("  \"extra_dlcs\": {}");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static HttpClient CreateHttpClient(string proxyAddress)
    {
        HttpClientHandler handler;
        if (!string.IsNullOrWhiteSpace(proxyAddress))
        {
            handler = new HttpClientHandler
            {
                Proxy = new WebProxy(proxyAddress),
                UseProxy = true,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            };
        }
        else
        {
            handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            };
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");
        return client;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _nameGate.Dispose();
    }
}
