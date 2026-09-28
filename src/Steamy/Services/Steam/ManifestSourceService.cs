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

/// <summary>
/// The answer to "can this source deliver this app?".
/// <para>
/// <see cref="Certain"/> is what keeps the Library page honest: a source that answered the
/// question (a manifest exists / does not exist, or a key is missing) is certain and may block
/// the Start button, while a source we could not reach is not certain and must not block a
/// download the user explicitly asked for.
/// </para>
/// </summary>
public sealed record ManifestAvailability(bool Available, bool Certain, string Message);

public interface IManifestSourceService
{
    IReadOnlyList<ManifestSourceInfo> Sources { get; }

    Task<ManifestAvailability> CheckAvailabilityAsync(ManifestSource source, int appId, CancellationToken cancellationToken = default);

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

    public async Task<ManifestAvailability> CheckAvailabilityAsync(ManifestSource source, int appId, CancellationToken cancellationToken = default)
    {
        return source switch
        {
            ManifestSource.Ryuu => await CheckRyuuAvailabilityAsync(cancellationToken),
            ManifestSource.Zaza => await CheckGitHubAvailabilityAsync("ZazaJr24/Game-Files-UpdateR", appId, cancellationToken),
            ManifestSource.Hubcap => await CheckHubcapAvailabilityAsync(appId, cancellationToken),
            ManifestSource.DepotBox => await CheckDepotBoxAvailabilityAsync(appId, cancellationToken),
            _ => new ManifestAvailability(false, true, $"Unknown source: {source}")
        };
    }

    private async Task<ManifestAvailability> CheckRyuuAvailabilityAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string? authCode = null;
        try { authCode = await _credentials.ReadAsync("ryuu-auth-key"); } catch { }
        if (string.IsNullOrWhiteSpace(authCode))
            authCode = _settings.Load().RyuuApiKey;

        return string.IsNullOrWhiteSpace(authCode)
            ? new ManifestAvailability(false, true, "No Ryuu auth code configured — add it in Settings → Ryuu Generator")
            : new ManifestAvailability(true, true, "Available on Ryuu");
    }

    private async Task<ManifestAvailability> CheckGitHubAvailabilityAsync(string repoPath, int appId, CancellationToken ct)
    {
        var appIdText = appId.ToString(CultureInfo.InvariantCulture);
        var sawError = false;

        foreach (var path in GitHubManifestPaths(appIdText))
        {
            try
            {
                var url = $"https://api.github.com/repos/{repoPath}/contents/{path}";
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode) return new ManifestAvailability(true, true, "Available on Zaza");
                if (response.StatusCode != System.Net.HttpStatusCode.NotFound) sawError = true;
            }
            catch (OperationCanceledException) { throw; }
            catch { sawError = true; }
        }

        return sawError
            ? new ManifestAvailability(true, false, "Zaza could not be reached right now — you can still try")
            : new ManifestAvailability(false, true, "Not available on Zaza");
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

    private async Task<ManifestAvailability> CheckHubcapAvailabilityAsync(int appId, CancellationToken ct)
    {
        var key = await ResolveHubcapKeyAsync();
        if (key is null)
            return new ManifestAvailability(false, true, "No Hubcap API key configured — add it in Settings → Hubcap Manifest");

        try
        {
            var baseUrl = _settings.Load().HubcapBaseUrl?.TrimEnd('/') ?? "https://hubcapmanifest.com";
            // /api/v1/status is the free "does a manifest exist" endpoint. Never probe
            // /api/v1/manifest here — that one downloads the zip and counts against the
            // daily quota on every single availability check.
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/api/v1/status/{appId}");
            AddHubcapAuth(req, key);
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                // 404 simply means "Hubcap has no manifest for this app"; anything else is
                // inconclusive and must not lock the download button.
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return new ManifestAvailability(false, true, $"Hubcap has no manifest for App {appId}");

                return new ManifestAvailability(true, false,
                    ExtractApiErrorMessage(body, $"Hubcap check failed (HTTP {(int)resp.StatusCode}) — you can still try"));
            }

            using var doc = JsonDocument.Parse(body);
            return ReadAvailability(doc.RootElement)
                ? new ManifestAvailability(true, true, $"Available on Hubcap")
                : new ManifestAvailability(false, true, $"Hubcap has no manifest for App {appId} yet");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ManifestAvailability(true, false,
                $"Hubcap check failed ({ex.GetType().Name}) — you can still try");
        }
    }

    /// <summary>Hubcap accepts the key as a Bearer token and as an X-API-Key header — send both.</summary>
    private static void AddHubcapAuth(HttpRequestMessage request, string key)
    {
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        request.Headers.TryAddWithoutValidation("X-API-Key", key);
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

    private async Task<ManifestAvailability> CheckDepotBoxAvailabilityAsync(int appId, CancellationToken ct)
    {
        var key = await ResolveDepotBoxKeyAsync();
        if (key is null)
            return new ManifestAvailability(false, true, "No DepotBox API key configured — add it in Settings → DepotBox");

        try
        {
            // Lightweight availability endpoint — never the generator endpoint, which would
            // trigger a full file generation just to answer "is it there?".
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://depotbox.org/api/games/{appId}/availability");
            AddDepotBoxAuth(req, key);
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return new ManifestAvailability(false, true, $"DepotBox has no manifest for App {appId}");

                return new ManifestAvailability(true, false,
                    ExtractApiErrorMessage(body, $"DepotBox check failed (HTTP {(int)resp.StatusCode}) — you can still try"));
            }

            using var doc = JsonDocument.Parse(body);
            return ReadAvailability(doc.RootElement)
                ? new ManifestAvailability(true, true, $"Available on DepotBox")
                : new ManifestAvailability(false, true, $"DepotBox has no manifest for App {appId} yet");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ManifestAvailability(true, false,
                $"DepotBox check failed ({ex.GetType().Name}) — you can still try");
        }
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

        // The Lua script (depot ids, manifest ids, decryption keys) lives on its own endpoint —
        // the manifest zip carries the .manifest files. Both are pulled, and either one alone is
        // enough to continue: the zip often contains the Lua too, and DepotDownloaderMod can fetch
        // a manifest from Steam when we only have the Lua.
        string? luaContent = null;
        string? luaError = null;

        progress?.Report("Fetching Lua manifest from Hubcap...");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/lua/{appId}");
            AddHubcapAuth(req, key);
            using var resp = await _httpClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var text = (await resp.Content.ReadAsStringAsync(ct)).TrimStart('\uFEFF');
                if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith('{') && !text.StartsWith('['))
                {
                    luaContent = text;
                    await File.WriteAllTextAsync(Path.Combine(appWorkDir, $"{appId}.lua"), luaContent, ct);
                }
                else
                {
                    luaError = $"Hubcap returned no Lua script for App {appId}.";
                }
            }
            else
            {
                luaError = ExtractApiErrorMessage(
                    await resp.Content.ReadAsStringAsync(ct),
                    $"Hubcap returned HTTP {(int)resp.StatusCode} for the Lua of App {appId}.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            luaError = $"Hubcap Lua download failed: {ex.Message}";
        }

        progress?.Report("Downloading manifest files from Hubcap...");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/manifest/{appId}");
            AddHubcapAuth(req, key);
            using var resp = await _httpClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (IsZipArchive(bytes))
                {
                    var zipPath = Path.Combine(appWorkDir, $"{appId}_hubcap.zip");
                    await File.WriteAllBytesAsync(zipPath, bytes, ct);

                    var (zipLua, fileCount) = await ExtractManifestArchiveAsync(zipPath, appWorkDir, ct);
                    luaContent ??= zipLua;
                    _logging.Add(Models.LogLevel.Info, "ManifestSource",
                        $"Hubcap archive for App {appId} unpacked: {fileCount} file(s).", appId);
                }
                else
                {
                    _logging.Add(Models.LogLevel.Warning, "ManifestSource",
                        $"Hubcap sent no archive for App {appId} ({(int)resp.StatusCode}). Continuing with the Lua only.", appId);
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
            return new ManifestDownloadResult(false, luaError ?? $"No Lua script found for App {appId} on Hubcap.");

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

        // /api/direct-download is DepotBox's documented "one request, one file" flow: the ZIP
        // carries the Lua *and* every depot .manifest for the app, which is exactly what
        // DepotDownloaderMod needs. DepotBox builds the package while the request is open, so the
        // long timeout on the shared client is what keeps large games from timing out.
        progress?.Report($"Requesting the depot package for App {appId} from DepotBox...");
        string? packageError = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://depotbox.org/api/direct-download?appid={appId}");
            AddDepotBoxAuth(req, key);
            using var resp = await _httpClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                var contentType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var unpacked = await HandleDepotBoxPayloadAsync(appWorkDir, appId, bytes, contentType, ct);
                if (unpacked.Succeeded) return unpacked;
                packageError = unpacked.Message;
            }
            else
            {
                packageError = ExtractApiErrorMessage(
                    await resp.Content.ReadAsStringAsync(ct),
                    $"DepotBox returned HTTP {(int)resp.StatusCode} for App {appId}.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            packageError = $"DepotBox download failed: {ex.Message}";
        }

        // Fallback: the Lua-only endpoint. DepotDownloaderMod can pull the manifests from Steam
        // itself with just the depot ids, manifest ids and keys.
        progress?.Report("Trying the DepotBox Lua endpoint instead...");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://depotbox.org/api/direct-lua?appid={appId}");
            AddDepotBoxAuth(req, key);
            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return new ManifestDownloadResult(false, packageError ?? ExtractApiErrorMessage(
                    await resp.Content.ReadAsStringAsync(ct),
                    $"DepotBox returned HTTP {(int)resp.StatusCode} for App {appId}."));

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var result = await HandleDepotBoxPayloadAsync(appWorkDir, appId, bytes, contentType, ct);
            return result.Succeeded ? result : new ManifestDownloadResult(false, packageError ?? result.Message);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ManifestDownloadResult(false, packageError ?? $"DepotBox download failed: {ex.Message}");
        }
    }

    private static bool IsZipArchive(byte[] bytes)
        => bytes.Length > 3 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K';

    /// <summary>
    /// Unpacks a manifest archive into the work directory. Returns the Lua text it contained (when
    /// there was one) and how many files were written.
    /// </summary>
    private static async Task<(string? Lua, int Files)> ExtractManifestArchiveAsync(
        string zipPath, string appWorkDir, CancellationToken ct)
    {
        string? luaContent = null;
        var files = 0;

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (ext is not ".lua" and not ".key" and not ".manifest") continue;

            entry.ExtractToFile(Path.Combine(appWorkDir, entry.Name), overwrite: true);
            files++;

            if (ext == ".lua")
            {
                using var reader = new StreamReader(entry.Open());
                luaContent = await reader.ReadToEndAsync(ct);
            }
        }

        return (luaContent, files);
    }

    /// <summary>Finds any Lua script that already sits in the work directory.</summary>
    private static string? FindLuaInWorkDir(string appWorkDir, int appId)
    {
        var direct = Path.Combine(appWorkDir, $"{appId}.lua");
        if (File.Exists(direct))
        {
            var text = File.ReadAllText(direct);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        foreach (var path in Directory.EnumerateFiles(appWorkDir, "*.lua"))
        {
            var text = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return null;
    }

    /// <summary>Turns a DepotBox payload (zip archive or plain Lua text) into a download result.</summary>
    private async Task<ManifestDownloadResult> HandleDepotBoxPayloadAsync(
        string appWorkDir, int appId, byte[] bytes, string contentType, CancellationToken ct)
    {
        if (bytes.Length == 0)
            return new ManifestDownloadResult(false, "DepotBox returned empty content.");

        var looksLikeZip = IsZipArchive(bytes)
            || contentType.Contains("zip", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase);

        if (looksLikeZip)
        {
            var zipPath = Path.Combine(appWorkDir, $"{appId}_depotbox.zip");
            await File.WriteAllBytesAsync(zipPath, bytes, ct);

            string? luaContent;
            int fileCount;
            try
            {
                (luaContent, fileCount) = await ExtractManifestArchiveAsync(zipPath, appWorkDir, ct);
            }
            catch (Exception ex)
            {
                return new ManifestDownloadResult(false, $"DepotBox archive could not be unpacked: {ex.Message}");
            }

            luaContent ??= FindLuaInWorkDir(appWorkDir, appId);
            if (luaContent is null)
                return new ManifestDownloadResult(false, $"No Lua script found in DepotBox archive for App {appId}.");

            _logging.Add(Models.LogLevel.Info, "ManifestSource",
                $"DepotBox package for App {appId} unpacked: {fileCount} file(s).", appId);
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
