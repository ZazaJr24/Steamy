using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamContentManager.Services;

public enum ManifestSource
{
    Ryuu,
    Zaza,
    Hubcap,
    Resonance
}

public sealed record ManifestSourceInfo(
    ManifestSource Source,
    string Label,
    string Description,
    string BaseUrl,
    bool RequiresAuthCode,
    bool IsEnabled = true);

public sealed record ManifestDownloadResult(
    bool Succeeded,
    string Message,
    string? LuaContent = null,
    string? WorkDirectory = null);

public interface IManifestSourceService
{
    IReadOnlyList<ManifestSourceInfo> Sources { get; }

    Task<bool> IsAvailableAsync(ManifestSource source, int appId, CancellationToken cancellationToken = default);

    Task<ManifestDownloadResult> DownloadManifestsAsync(
        ManifestSource source,
        int appId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ManifestSourceService : IManifestSourceService, IDisposable
{
    private static readonly IReadOnlyList<ManifestSourceInfo> AllSources = new List<ManifestSourceInfo>
    {
        new(ManifestSource.Ryuu, "Ryuu", "Ryuu Generator API (requires auth code)", "https://generator.ryuu.lol/", RequiresAuthCode: true),
        new(ManifestSource.Zaza, "Zaza", "ZazaJr24 Game-Files-UpdateR on GitHub", "https://raw.githubusercontent.com/ZazaJr24/Game-Files-UpdateR/main/", RequiresAuthCode: false),
        new(ManifestSource.Hubcap, "Hubcap", "Hubcap Manifest API (requires API key)", "https://hubcapmanifest.com/", RequiresAuthCode: true),
        new(ManifestSource.Resonance, "Resonance", "ResonanceManifests on GitHub", "https://raw.githubusercontent.com/Dev12434/ResonanceManifests/main/", RequiresAuthCode: false),
    };

    private readonly ISettingsService _settings;
    private readonly ISecureCredentialService _credentials;
    private readonly IRyuuSecureDownloadService _ryuuDownload;
    private readonly ILoggingService _logging;
    private readonly HttpClient _httpClient;
    private readonly string _workFolder;

    public IReadOnlyList<ManifestSourceInfo> Sources => AllSources;

    public ManifestSourceService(
        ISettingsService settings,
        ISecureCredentialService credentials,
        IRyuuSecureDownloadService ryuuDownload,
        ILoggingService logging)
    {
        _settings = settings;
        _credentials = credentials;
        _ryuuDownload = ryuuDownload;
        _logging = logging;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SteamContentManager/1.0");

        _workFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamContentManager",
            "manifest-workdir");
    }

    public async Task<bool> IsAvailableAsync(ManifestSource source, int appId, CancellationToken cancellationToken = default)
    {
        return source switch
        {
            ManifestSource.Ryuu => true,
            ManifestSource.Zaza => await CheckGitHubAvailabilityAsync("ZazaJr24/Game-Files-UpdateR", appId, cancellationToken),
            ManifestSource.Resonance => await CheckGitHubAvailabilityAsync("Dev12434/ResonanceManifests", appId, cancellationToken),
            ManifestSource.Hubcap => await CheckHubcapAvailabilityAsync(appId, cancellationToken),
            _ => false
        };
    }

    private async Task<bool> CheckGitHubAvailabilityAsync(string repoPath, int appId, CancellationToken ct)
    {
        var appIdText = appId.ToString(CultureInfo.InvariantCulture);
        foreach (var path in GitHubManifestPaths(appIdText))
        {
            try
            {
                var url = $"https://api.github.com/repos/{repoPath}/contents/{path}";
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        return false;
    }

    private static IEnumerable<string> GitHubManifestPaths(string appId)
    {
        // ResonanceManifests currently has no published schema. These are the common layouts
        // supported by the importer, in order from the documented layout to legacy variants.
        yield return $"Manifests/{appId}";
        yield return appId;
        yield return $"manifests/{appId}";
    }

    public async Task<ManifestDownloadResult> DownloadManifestsAsync(
        ManifestSource source,
        int appId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return source switch
        {
            ManifestSource.Ryuu => await DownloadFromRyuuAsync(appId, progress, cancellationToken),
            ManifestSource.Zaza => await DownloadFromGitHubAsync(
                "ZazaJr24/Game-Files-UpdateR", appId, progress, cancellationToken),
            ManifestSource.Hubcap => await DownloadFromHubcapAsync(appId, progress, cancellationToken),
            ManifestSource.Resonance => await DownloadFromGitHubAsync(
                "Dev12434/ResonanceManifests", appId, progress, cancellationToken),
            _ => new ManifestDownloadResult(false, $"Unknown source: {source}")
        };
    }

    private async Task<ManifestDownloadResult> DownloadFromRyuuAsync(
        int appId, IProgress<string>? progress, CancellationToken ct)
    {
        var appSettings = _settings.Load();
        var authCode = appSettings.RyuuApiKey;
        if (string.IsNullOrWhiteSpace(authCode))
        {
            var stored = await _credentials.ReadAsync("ryuu-auth-key");
            authCode = stored ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(authCode))
            return new ManifestDownloadResult(false, "No Ryuu auth code configured. Set it in Settings → Ryuu Auth Key.");

        progress?.Report("Downloading manifest archive from Ryuu...");
        var downloadProgress = new Progress<RyuuSecureDownloadProgress>(p =>
            progress?.Report($"Downloading archive... {p.Downloaded} / {p.Total} ({p.Percent:F0}%)"));

        var zipResult = await _ryuuDownload.DownloadAsync(appId, authCode, $"App {appId}", downloadProgress, ct);
        if (!zipResult.Succeeded)
            return new ManifestDownloadResult(false, $"Failed to download Ryuu archive: {zipResult.Message}");

        var appWorkDir = Path.Combine(_workFolder, "ryuu", appId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(appWorkDir);

        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(zipResult.ArchivePath);

            var luaEntry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            if (luaEntry is null)
                return new ManifestDownloadResult(false, "No Lua script found in the Ryuu archive.");

            string luaContent;
            using (var reader = new StreamReader(luaEntry.Open()))
                luaContent = await reader.ReadToEndAsync(ct);

            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)))
            {
                var destPath = Path.Combine(appWorkDir, entry.Name);
                entry.ExtractToFile(destPath, overwrite: true);
            }

            return new ManifestDownloadResult(true, "Ryuu archive extracted.", luaContent, appWorkDir);
        }
        catch (Exception ex)
        {
            return new ManifestDownloadResult(false, $"Failed to extract Ryuu archive: {ex.Message}");
        }
    }

    private async Task<bool> CheckHubcapAvailabilityAsync(int appId, CancellationToken ct)
    {
        var key = await _credentials.ReadAsync("hubcap-api-key");
        if (string.IsNullOrWhiteSpace(key)) return false;

        try
        {
            var baseUrl = _settings.Load().HubcapBaseUrl?.TrimEnd('/') ?? "https://hubcapmanifest.com";
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/api/v1/search?q={appId}&limit=1");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                return true;
            if (root.TryGetProperty("games", out var games) && games.GetArrayLength() > 0)
                return true;
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private async Task<ManifestDownloadResult> DownloadFromHubcapAsync(
        int appId, IProgress<string>? progress, CancellationToken ct)
    {
        var key = await _credentials.ReadAsync("hubcap-api-key");
        if (string.IsNullOrWhiteSpace(key))
            return new ManifestDownloadResult(false, "No Hubcap API key configured. Set it in Settings → Hubcap API Key.");

        var baseUrl = _settings.Load().HubcapBaseUrl?.TrimEnd('/') ?? "https://hubcapmanifest.com";
        var appWorkDir = Path.Combine(_workFolder, "hubcap", appId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(appWorkDir);

        progress?.Report("Downloading manifest from Hubcap...");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/api/v1/manifest/{appId}");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return new ManifestDownloadResult(false,
                    $"Hubcap returned HTTP {(int)resp.StatusCode} for App {appId}.");

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

            if (contentType.Contains("zip", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                var zipPath = Path.Combine(appWorkDir, $"{appId}_hubcap.zip");
                await File.WriteAllBytesAsync(zipPath, bytes, ct);

                string? luaContent = null;
                using var zip = ZipFile.OpenRead(zipPath);
                foreach (var entry in zip.Entries)
                {
                    var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (ext is ".lua" or ".key" or ".manifest")
                    {
                        var destPath = Path.Combine(appWorkDir, entry.Name);
                        entry.ExtractToFile(destPath, overwrite: true);
                        if (ext == ".lua")
                            using (var reader = new StreamReader(entry.Open()))
                                luaContent = await reader.ReadToEndAsync(ct);
                    }
                }

                if (luaContent is null)
                    return new ManifestDownloadResult(false, $"No Lua script found in Hubcap archive for App {appId}.");

                _logging.Add(Models.LogLevel.Info, "ManifestSource",
                    $"Downloaded manifest from Hubcap for App {appId}.", appId);
                return new ManifestDownloadResult(true, "Downloaded from Hubcap.", luaContent, appWorkDir);
            }

            return new ManifestDownloadResult(false, "Hubcap returned unexpected content type.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ManifestDownloadResult(false, $"Hubcap download failed: {ex.Message}");
        }
    }

    private async Task<ManifestDownloadResult> DownloadFromGitHubAsync(
        string repoPath, int appId, IProgress<string>? progress, CancellationToken ct)
    {
        var appIdStr = appId.ToString(CultureInfo.InvariantCulture);
        var sourceName = repoPath.Split('/')[0];
        var appWorkDir = Path.Combine(_workFolder, sourceName.ToLowerInvariant(), appIdStr);
        Directory.CreateDirectory(appWorkDir);

        progress?.Report($"Listing files from {sourceName}...");
        _logging.Add(Models.LogLevel.Info, "ManifestSource", $"Listing {repoPath} for App {appId}.", appId);

        List<(string Name, string DownloadUrl)> files = new();
        var sawNonNotFoundError = false;
        try
        {
            foreach (var path in GitHubManifestPaths(appIdStr))
            {
                var contentsUrl = $"https://api.github.com/repos/{repoPath}/contents/{path}";
                using var listResponse = await _httpClient.GetAsync(contentsUrl, ct);
                if (listResponse.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
                if (!listResponse.IsSuccessStatusCode)
                {
                    sawNonNotFoundError = true;
                    continue;
                }

                var json = await listResponse.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    files = doc.RootElement.EnumerateArray()
                        .Where(e => e.GetProperty("type").GetString() == "file")
                        .Select(e => (
                            Name: e.GetProperty("name").GetString()!,
                            DownloadUrl: e.GetProperty("download_url").GetString()!))
                        .ToList();
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object
                         && doc.RootElement.GetProperty("type").GetString() == "file")
                {
                    files.Add((
                        doc.RootElement.GetProperty("name").GetString()!,
                        doc.RootElement.GetProperty("download_url").GetString()!));
                }

                if (files.Count > 0) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ManifestDownloadResult(false, $"Failed to list files from {sourceName}: {ex.Message}");
        }

        if (files.Count == 0 && sawNonNotFoundError)
            return new ManifestDownloadResult(false, $"{sourceName} could not list files for App {appId}.");

        if (files.Count == 0)
            return new ManifestDownloadResult(false, $"No files found for App {appId} on {sourceName}.");

        string? luaContent = null;
        var downloadedCount = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file.Name).ToLowerInvariant();
            if (ext is not ".lua" and not ".key" and not ".manifest") continue;

            progress?.Report($"Downloading {file.Name}...");
            try
            {
                using var resp = await _httpClient.GetAsync(file.DownloadUrl, ct);
                if (!resp.IsSuccessStatusCode) continue;

                if (ext == ".lua")
                {
                    luaContent = await resp.Content.ReadAsStringAsync(ct);
                    await File.WriteAllTextAsync(Path.Combine(appWorkDir, file.Name), luaContent, ct);
                }
                else
                {
                    var destPath = Path.Combine(appWorkDir, file.Name);
                    await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await resp.Content.CopyToAsync(fs, ct);
                }
                downloadedCount++;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        if (luaContent is null)
            return new ManifestDownloadResult(false, $"No Lua script found for App {appId} on {sourceName}.");

        _logging.Add(Models.LogLevel.Info, "ManifestSource",
            $"Downloaded {downloadedCount} file(s) from {sourceName} for App {appId}.", appId);

        return new ManifestDownloadResult(true, $"Downloaded from {sourceName}.", luaContent, appWorkDir);
    }

    public void Dispose() => _httpClient.Dispose();
}
