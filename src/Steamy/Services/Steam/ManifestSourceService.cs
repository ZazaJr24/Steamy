using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Steamy.Services;

public enum ManifestSource
{
    Ryuu,
    Zaza,
    Hubcap,
    DepotBox
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
        new(ManifestSource.DepotBox, "DepotBox", "DepotBox manifest generator (requires API key)", "https://depotbox.org/", RequiresAuthCode: true),
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
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");

        _workFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steamy",
            "manifest-workdir");
    }

    public async Task<bool> IsAvailableAsync(ManifestSource source, int appId, CancellationToken cancellationToken = default)
    {
        return source switch
        {
            ManifestSource.Ryuu => true,
            ManifestSource.Zaza => await CheckGitHubAvailabilityAsync("ZazaJr24/Game-Files-UpdateR", appId, cancellationToken),
            ManifestSource.Hubcap => await CheckHubcapAvailabilityAsync(appId, cancellationToken),
            ManifestSource.DepotBox => await CheckDepotBoxAvailabilityAsync(appId, cancellationToken),
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
            ManifestSource.DepotBox => await DownloadFromDepotBoxAsync(appId, progress, cancellationToken),
            _ => new ManifestDownloadResult(false, $"Unknown source: {source}")
        };
    }

    private async Task<ManifestDownloadResult> DownloadFromRyuuAsync(
        int appId, IProgress<string>? progress, CancellationToken ct)
    {
        string? authCode = null;
        try { authCode = await _credentials.ReadAsync("ryuu-auth-key"); } catch { }
        if (string.IsNullOrWhiteSpace(authCode))
            authCode = _settings.Load().RyuuApiKey;

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

    private async Task<string?> ResolveHubcapKeyAsync()
    {
        string? key = null;
        try { key = await _credentials.ReadAsync("hubcap-api-key"); } catch { }
        if (string.IsNullOrWhiteSpace(key))
            key = _settings.Load().HubcapApiKey;
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private async Task<bool> CheckHubcapAvailabilityAsync(int appId, CancellationToken ct)
    {
        var key = await ResolveHubcapKeyAsync();
        if (key is null) return false;

        try
        {
            var baseUrl = _settings.Load().HubcapBaseUrl?.TrimEnd('/') ?? "https://hubcapmanifest.com";
            // /api/v1/status is the free "does a manifest exist" endpoint. Never probe
            // /api/v1/manifest here — that one downloads the zip and counts against the
            // daily quota on every single availability check.
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/api/v1/status/{appId}");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return false;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return ReadAvailability(doc.RootElement);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>
    /// Reads the various "is there a manifest for this app" answer shapes the APIs use:
    /// {"available": true}, {"manifest_file_exists": true} or {"status": "available"}.
    /// </summary>
    private static bool ReadAvailability(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("available", out var avail))
            return avail.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("manifest_file_exists", out var exists))
            return exists.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
        {
            var text = status.GetString() ?? string.Empty;
            return text.Equals("available", StringComparison.OrdinalIgnoreCase)
                || text.Equals("ok", StringComparison.OrdinalIgnoreCase)
                || text.Equals("ready", StringComparison.OrdinalIgnoreCase)
                || text.Equals("exists", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>Pulls a human-readable error text out of an API JSON body when there is one.</summary>
    private static string ExtractApiErrorMessage(string? body, string fallback)
    {
        if (string.IsNullOrWhiteSpace(body)) return fallback;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return fallback;
            foreach (var name in new[] { "detail", "message", "error", "reason" })
            {
                if (!doc.RootElement.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind != JsonValueKind.String) continue;
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return $"{fallback} {text}";
            }
        }
        catch { }
        return fallback;
    }

    private async Task<string?> ResolveDepotBoxKeyAsync()
    {
        string? key = null;
        try { key = await _credentials.ReadAsync("depotbox-api-key"); } catch { }
        if (string.IsNullOrWhiteSpace(key))
            key = _settings.Load().DepotBoxApiKey;
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private async Task<bool> CheckDepotBoxAvailabilityAsync(int appId, CancellationToken ct)
    {
        var key = await ResolveDepotBoxKeyAsync();
        if (key is null) return false;

        try
        {
            // Lightweight availability endpoint — never the generator endpoint, which would
            // trigger a full file generation just to answer "is it there?".
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://depotbox.org/api/games/{appId}/availability");
            AddDepotBoxAuth(req, key);
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return false;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return ReadAvailability(doc.RootElement);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>DepotBox accepts the key both as X-API-Key and as a Bearer token — send both.</summary>
    private static void AddDepotBoxAuth(HttpRequestMessage request, string key)
    {
        request.Headers.TryAddWithoutValidation("X-API-Key", key);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
    }

    private async Task<ManifestDownloadResult> DownloadFromHubcapAsync(
        int appId, IProgress<string>? progress, CancellationToken ct)
    {
        var key = await ResolveHubcapKeyAsync();
        if (string.IsNullOrWhiteSpace(key))
            return new ManifestDownloadResult(false, "No Hubcap API key configured. Set it in Settings → Hubcap API Key.");

        var baseUrl = _settings.Load().HubcapBaseUrl?.TrimEnd('/') ?? "https://hubcapmanifest.com";
        var appWorkDir = Path.Combine(_workFolder, "hubcap", appId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(appWorkDir);

        // The Lua script (depot ids, manifest ids, decryption keys) lives on its own endpoint
        // — the manifest zip only carries the .manifest files.
        progress?.Report("Fetching Lua manifest from Hubcap...");
        string? luaContent = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/lua/{appId}");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return new ManifestDownloadResult(false, ExtractApiErrorMessage(
                    await resp.Content.ReadAsStringAsync(ct),
                    $"Hubcap returned HTTP {(int)resp.StatusCode} for the Lua of App {appId}."));

            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                luaContent = text;
                await File.WriteAllTextAsync(Path.Combine(appWorkDir, $"{appId}.lua"), luaContent, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ManifestDownloadResult(false, $"Hubcap Lua download failed: {ex.Message}");
        }

        // The depot .manifest files come from the zip endpoint. A failure here is not fatal:
        // DepotDownloaderMod falls back to pulling the manifests from Steam itself.
        progress?.Report("Downloading manifest files from Hubcap...");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/manifest/{appId}");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
            using var resp = await _httpClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                var zipPath = Path.Combine(appWorkDir, $"{appId}_hubcap.zip");
                await File.WriteAllBytesAsync(zipPath, bytes, ct);

                try
                {
                    using var zip = ZipFile.OpenRead(zipPath);
                    foreach (var entry in zip.Entries)
                    {
                        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                        if (ext is not ".lua" and not ".key" and not ".manifest") continue;

                        var destPath = Path.Combine(appWorkDir, entry.Name);
                        entry.ExtractToFile(destPath, overwrite: true);
                        if (ext == ".lua" && luaContent is null)
                            using (var reader = new StreamReader(entry.Open()))
                                luaContent = await reader.ReadToEndAsync(ct);
                    }
                }
                catch (Exception ex)
                {
                    _logging.Add(Models.LogLevel.Warning, "ManifestSource",
                        $"Hubcap archive for App {appId} could not be unpacked: {ex.Message}", appId);
                }
            }
            else
            {
                _logging.Add(Models.LogLevel.Warning, "ManifestSource",
                    $"Hubcap manifest zip for App {appId}: HTTP {(int)resp.StatusCode}. Continuing with the Lua only.", appId);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logging.Add(Models.LogLevel.Warning, "ManifestSource",
                $"Hubcap manifest zip for App {appId} failed: {ex.Message}. Continuing with the Lua only.", appId);
        }

        if (luaContent is null)
            return new ManifestDownloadResult(false, $"No Lua script found for App {appId} on Hubcap.");

        _logging.Add(Models.LogLevel.Info, "ManifestSource",
            $"Downloaded manifest from Hubcap for App {appId}.", appId);
        return new ManifestDownloadResult(true, "Downloaded from Hubcap.", luaContent, appWorkDir);
    }

    private async Task<ManifestDownloadResult> DownloadFromDepotBoxAsync(
        int appId, IProgress<string>? progress, CancellationToken ct)
    {
        var key = await ResolveDepotBoxKeyAsync();
        if (string.IsNullOrWhiteSpace(key))
            return new ManifestDownloadResult(false, "No DepotBox API key configured. Set it in Settings → DepotBox API Key.");

        var appWorkDir = Path.Combine(_workFolder, "depotbox", appId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(appWorkDir);

        progress?.Report("Generating manifest from DepotBox...");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://depotbox.org/api/direct-lua?appid={appId}");
            AddDepotBoxAuth(req, key);
            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return new ManifestDownloadResult(false, ExtractApiErrorMessage(
                    await resp.Content.ReadAsStringAsync(ct),
                    $"DepotBox returned HTTP {(int)resp.StatusCode} for App {appId}."));

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                // DepotBox answers JSON both for errors and for "your file is ready" links.
                string? luaText = null, fileUrl = null, error = null;
                try
                {
                    using var doc = JsonDocument.Parse(bytes);
                    var root = doc.RootElement;
                    foreach (var name in new[] { "lua", "content", "text" })
                        if (root.TryGetProperty(name, out var luaField) && luaField.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(luaField.GetString())) { luaText = luaField.GetString(); break; }
                    foreach (var name in new[] { "download_url", "file_url", "url" })
                        if (root.TryGetProperty(name, out var urlField) && urlField.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(urlField.GetString())) { fileUrl = urlField.GetString(); break; }
                    foreach (var name in new[] { "detail", "message", "error" })
                        if (root.TryGetProperty(name, out var errorField) && errorField.ValueKind == JsonValueKind.String) { error = errorField.GetString(); break; }
                }
                catch { }

                if (luaText is not null)
                    return await SaveDepotBoxLuaAsync(appWorkDir, appId, luaText, ct);

                if (fileUrl is not null)
                {
                    progress?.Report("Downloading the generated file from DepotBox...");
                    using var fileResp = await _httpClient.GetAsync(fileUrl, ct);
                    if (!fileResp.IsSuccessStatusCode)
                        return new ManifestDownloadResult(false,
                            $"DepotBox file download returned HTTP {(int)fileResp.StatusCode} for App {appId}.");
                    bytes = await fileResp.Content.ReadAsByteArrayAsync(ct);
                    contentType = fileResp.Content.Headers.ContentType?.MediaType ?? "";
                }
                else
                {
                    return new ManifestDownloadResult(false,
                        error is null ? "DepotBox returned unexpected JSON." : $"DepotBox: {error}");
                }
            }

            return await HandleDepotBoxPayloadAsync(appWorkDir, appId, bytes, contentType, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ManifestDownloadResult(false, $"DepotBox download failed: {ex.Message}");
        }
    }

    /// <summary>Turns a DepotBox payload (zip archive or plain Lua text) into a download result.</summary>
    private async Task<ManifestDownloadResult> HandleDepotBoxPayloadAsync(
        string appWorkDir, int appId, byte[] bytes, string contentType, CancellationToken ct)
    {
        if (bytes.Length == 0)
            return new ManifestDownloadResult(false, "DepotBox returned empty content.");

        var looksLikeZip = contentType.Contains("zip", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)
            || (bytes.Length > 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K');

        if (looksLikeZip)
        {
            var zipPath = Path.Combine(appWorkDir, $"{appId}_depotbox.zip");
            await File.WriteAllBytesAsync(zipPath, bytes, ct);

            string? luaContent = null;
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                foreach (var entry in zip.Entries)
                {
                    var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (ext is not ".lua" and not ".key" and not ".manifest") continue;

                    var destPath = Path.Combine(appWorkDir, entry.Name);
                    entry.ExtractToFile(destPath, overwrite: true);
                    if (ext == ".lua")
                        using (var reader = new StreamReader(entry.Open()))
                            luaContent = await reader.ReadToEndAsync(ct);
                }
            }
            catch (Exception ex)
            {
                return new ManifestDownloadResult(false, $"DepotBox archive could not be unpacked: {ex.Message}");
            }

            if (luaContent is null)
                return new ManifestDownloadResult(false, $"No Lua script found in DepotBox archive for App {appId}.");

            _logging.Add(Models.LogLevel.Info, "ManifestSource",
                $"Downloaded manifest from DepotBox for App {appId}.", appId);
            return new ManifestDownloadResult(true, "Downloaded from DepotBox.", luaContent, appWorkDir);
        }

        var luaText = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        if (luaText.StartsWith('{') || luaText.StartsWith('['))
            return new ManifestDownloadResult(false,
                ExtractApiErrorMessage(luaText, $"DepotBox returned unexpected content for App {appId}."));

        return await SaveDepotBoxLuaAsync(appWorkDir, appId, luaText, ct);
    }

    private async Task<ManifestDownloadResult> SaveDepotBoxLuaAsync(
        string appWorkDir, int appId, string luaText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(luaText))
            return new ManifestDownloadResult(false, "DepotBox returned empty content.");

        var luaPath = Path.Combine(appWorkDir, $"{appId}.lua");
        await File.WriteAllTextAsync(luaPath, luaText, ct);
        _logging.Add(Models.LogLevel.Info, "ManifestSource",
            $"Downloaded Lua from DepotBox for App {appId}.", appId);
        return new ManifestDownloadResult(true, "Downloaded from DepotBox.", luaText, appWorkDir);
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
