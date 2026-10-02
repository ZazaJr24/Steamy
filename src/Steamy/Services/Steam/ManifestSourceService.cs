using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Steamy.Services;

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
        new(ManifestSource.Sushi, "Sushi", "Free Lua metadata and depot manifests · no API key", FreeManifestCatalogService.SushiUrl, RequiresAuthCode: false),
    };

    private readonly ISettingsService _settings;
    private readonly ISecureCredentialService _credentials;
    private readonly IRyuuSecureDownloadService _ryuuDownload;
    private readonly ILoggingService _logging;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _workFolder;

    public IReadOnlyList<ManifestSourceInfo> Sources => AllSources;
    private static HttpClient CreateSourceClient()
    {
        var handler = StableDnsHandler.Create();
        // Automatic cross-host redirects can forward custom API-key headers.
        if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
    }

    public ManifestSourceService(
        ISettingsService settings,
        ISecureCredentialService credentials,
        IRyuuSecureDownloadService ryuuDownload,
        ILoggingService logging, HttpClient? httpClient = null, string? workFolder = null)
    {
        _settings = settings;
        _credentials = credentials;
        _ryuuDownload = ryuuDownload;
        _logging = logging;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? CreateSourceClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");

        _workFolder = workFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steamy",
            "manifest-workdir");
    }

    public async Task<ManifestAvailability> CheckAvailabilityAsync(ManifestSource source, int appId, CancellationToken cancellationToken = default)
    {
        if (appId <= 0) return new(false, true, "Choose a valid app ID.");
        return source switch
        {
            ManifestSource.Ryuu => await CheckRyuuAvailabilityAsync(cancellationToken),
            ManifestSource.Zaza => await CheckGitHubAvailabilityAsync("ZazaJr24/Game-Files-UpdateR", appId, cancellationToken),
            ManifestSource.Hubcap => await CheckHubcapAvailabilityAsync(appId, cancellationToken),
            ManifestSource.DepotBox => await CheckDepotBoxAvailabilityAsync(appId, cancellationToken),
            ManifestSource.Sushi => await CheckSushiAvailabilityAsync(appId, cancellationToken),
            _ => new ManifestAvailability(false, true, $"Unknown source: {source}")
        };
    }

    private async Task<ManifestAvailability> CheckSushiAvailabilityAsync(int appId, CancellationToken token)
    {
        if (appId <= 0) return new(false, true, "Choose a valid app ID.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await _httpClient.GetAsync($"https://api.github.com/repos/{FreeManifestCatalogService.SushiRepository}/contents/{appId}.zip?ref=main", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return new(true, true, "Available on Sushi · free, no API key");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new(false, true, "This app is not available on Sushi.");
            return new(true, false, "Sushi could not be checked right now — you can still try.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        { return new(true, false, "Sushi could not be reached right now — you can still try."); }
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
        if (appId <= 0) return new(false, "Choose a valid app ID.");
        return source switch
        {
            ManifestSource.Ryuu => await DownloadFromRyuuAsync(appId, progress, cancellationToken),
            ManifestSource.Zaza => await DownloadFromGitHubAsync(
                "ZazaJr24/Game-Files-UpdateR", appId, progress, cancellationToken),
            ManifestSource.Hubcap => await DownloadFromHubcapAsync(appId, progress, cancellationToken),
            ManifestSource.DepotBox => await DownloadFromDepotBoxAsync(appId, progress, cancellationToken),
            ManifestSource.Sushi => await DownloadFromSushiAsync(appId, progress, cancellationToken),
            _ => new ManifestDownloadResult(false, $"Unknown source: {source}")
        };
    }

    private async Task<ManifestDownloadResult> DownloadFromSushiAsync(int appId, IProgress<string>? progress, CancellationToken token)
    {
        if (appId <= 0) return new(false, "Choose a valid app ID.");
        var directory = Path.Combine(_workFolder, "sushi", appId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, $"{Guid.NewGuid():N}.zip");
        var staging = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report("Downloading free Sushi manifests.");
            var url = $"https://raw.githubusercontent.com/{FreeManifestCatalogService.SushiRepository}/main/{appId}.zip";
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(false, $"Sushi archive unavailable (HTTP {(int)response.StatusCode}).");
            const long maximumBytes = 128L * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximumBytes) return new(false, "Sushi archive exceeds the size limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = File.Create(archive))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > maximumBytes) throw new InvalidDataException("Sushi archive exceeds the size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
            }
            var (lua, files) = await ManifestArchiveReader.ExtractAsync(archive, staging, token).ConfigureAwait(false);
            lua = FindLuaInWorkDir(staging, appId) ?? lua;
            if (string.IsNullOrWhiteSpace(lua)) return new(false, "The Sushi archive has no Lua metadata for DepotDownloaderMod.");
            token.ThrowIfCancellationRequested();
            // Publish a whole coherent package at once. Another target for the same app cannot
            // replace these manifests while DepotDownloaderMod copies its resume snapshot.
            var published = Path.Combine(directory, "pack-" + Guid.NewGuid().ToString("N"));
            Directory.Move(staging, published);
            return new(true, $"Loaded {files} manifest files from Sushi · free.", lua, published);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
        { return new(false, $"Sushi manifests could not be loaded: {exception.Message}"); }
        finally
        {
            try { File.Delete(archive); if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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

        var appWorkDir = NewPackageDirectory("ryuu", appId);
        Directory.CreateDirectory(appWorkDir);

        try
        {
            var (luaContent, files) = await ManifestArchiveReader.ExtractAsync(zipResult.ArchivePath, appWorkDir, ct).ConfigureAwait(false);
            luaContent = FindLuaInWorkDir(appWorkDir, appId) ?? luaContent;
            if (string.IsNullOrWhiteSpace(luaContent))
                return new ManifestDownloadResult(false, "No Lua script found in the Ryuu archive.");
            return new ManifestDownloadResult(true, $"Ryuu archive extracted: {files} files.", luaContent, appWorkDir);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
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

    private Task<ManifestDownloadResult> DownloadFromHubcapAsync(
        int appId, IProgress<string>? progress, CancellationToken ct) =>
        WithProviderPackageAsync("Hubcap", appId, ct, async appWorkDir =>
    {
        var key = await ResolveHubcapKeyAsync();
        if (string.IsNullOrWhiteSpace(key))
            return new(false, "No Hubcap API key configured. Set it in Settings → Hubcap API Key.");
        var baseUrl = _settings.Load().HubcapBaseUrl?.TrimEnd('/') ?? "https://hubcapmanifest.com";
        string? luaContent = null;
        string? luaError = null;
        progress?.Report("Fetching Lua metadata from Hubcap...");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/lua/{appId}");
            AddHubcapAuth(req, key);
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var bytes = await ReadPayloadAsync(resp.Content, DownloadPreparationReader.MaximumLuaCharacters, ct);
            if (resp.IsSuccessStatusCode)
                luaContent = ReadProviderLua("Hubcap", appId, bytes, resp.Content.Headers.ContentType?.MediaType ?? "");
            else luaError = $"Hubcap returned HTTP {(int)resp.StatusCode} for the Lua of App {appId}.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or System.Text.DecoderFallbackException)
        { luaError = exception.Message; }

        progress?.Report("Downloading manifest files from Hubcap...");
        using var manifestRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/manifest/{appId}");
        AddHubcapAuth(manifestRequest, key);
        try
        {
            using var response = await _httpClient.SendAsync(manifestRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await ReadPayloadAsync(response.Content, 128L * 1024 * 1024, ct);
                if (IsZipArchive(bytes))
                {
                    var archive = Path.Combine(appWorkDir, $"{appId}_hubcap.zip");
                    await File.WriteAllBytesAsync(archive, bytes, ct);
                    await ExtractManifestArchiveAsync(archive, appWorkDir, ct);
                    // An invalid standalone response was never saved, so it cannot shadow ZIP Lua.
                    var zipLua = FindLuaInWorkDir(appWorkDir, appId);
                    if (zipLua is not null)
                        luaContent = SteamToolsMetadata.ReadLua(zipLua, $"{appId}.lua", appId).Lua;
                    File.Delete(archive);
                }
                else if (luaContent is null)
                    return new(false, luaError ?? "Hubcap returned no ZIP package or valid Lua metadata.");
            }
            else if (luaContent is null)
                return new(false, luaError ?? $"Hubcap returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) when (luaContent is not null) { }
        // Damaged archives fail closed: no partially extracted package is published.
        if (luaContent is null) return new(false, luaError ?? $"No valid Lua metadata found for App {appId} on Hubcap.");
        await File.WriteAllTextAsync(Path.Combine(appWorkDir, $"{appId}.lua"), luaContent, ct);
        return new(true, "Hubcap metadata downloaded and checked.", luaContent, appWorkDir);
    });

    private static string ReadProviderLua(string provider, int appId, byte[] bytes, string contentType)
    {
        if (bytes.Length is 0 or > DownloadPreparationReader.MaximumLuaCharacters)
            throw new InvalidDataException($"{provider} returned empty or oversized Lua metadata.");
        var text = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Trim();
        if (text.StartsWith('<') || contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{provider} returned a web page instead of metadata. Check the API key and retry; browser verification may be required.");
        if (text.StartsWith('{') || text.StartsWith('['))
            throw new InvalidDataException($"{provider} returned an API response instead of Lua metadata.");
        return SteamToolsMetadata.ReadLua(text, $"{appId}.lua", appId).Lua;
    }

    private async Task<ManifestDownloadResult> WithProviderPackageAsync(string provider, int appId,
        CancellationToken ct, Func<string, Task<ManifestDownloadResult>> download)
    {
        var directory = NewPackageDirectory(provider.ToLowerInvariant(), appId);
        var keep = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            var result = await download(directory);
            if (!result.Succeeded) return result;
            var lua = SteamToolsMetadata.ReadLua(result.LuaContent ?? "", $"{appId}.lua", appId).Lua;
            var files = Directory.EnumerateFiles(directory).Where(path => Path.GetExtension(path).Equals(".manifest", StringComparison.OrdinalIgnoreCase)).ToArray();
            var luaPath = Path.Combine(directory, $"{appId}.lua");
            await File.WriteAllTextAsync(luaPath, lua, ct);
            var plan = await SteamToolsMetadata.ReadAsync(files.Append(luaPath).ToArray(), appId, ct);
            var catalog = DownloadPreparationReader.Read(lua);
            foreach (var manifest in plan.Files.Keys.Where(name => name.StartsWith("depotcache/", StringComparison.Ordinal)))
                if (!catalog.Depots.Any(depot => depot.Versions.Any(version => manifest.EndsWith($"/{depot.DepotId}_{version.ManifestId}.manifest", StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException("The archive contains a manifest that does not match its Lua metadata.");
            ct.ThrowIfCancellationRequested();
            keep = true;
            return result with { LuaContent = lua, WorkDirectory = directory };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, $"{provider} timed out. Retry later; no metadata was installed."); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or System.Text.DecoderFallbackException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        { return new(false, $"{provider}: {exception.Message}"); }
        finally
        {
            if (!keep)
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private Task<ManifestDownloadResult> DownloadFromDepotBoxAsync(
        int appId, IProgress<string>? progress, CancellationToken ct) =>
        WithProviderPackageAsync("DepotBox", appId, ct, async appWorkDir =>
    {
        var key = await ResolveDepotBoxKeyAsync();
        if (string.IsNullOrWhiteSpace(key))
            return new ManifestDownloadResult(false, "No DepotBox API key configured. Set it in Settings → DepotBox API Key.");


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
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode)
            {
                var bytes = await ReadPayloadAsync(resp.Content, 128L * 1024 * 1024, ct);
                var contentType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var unpacked = await HandleDepotBoxPayloadAsync(appWorkDir, appId, bytes, contentType, ct);
                if (unpacked.Succeeded) return unpacked;
                packageError = unpacked.Message;
            }
            else
            {
                packageError = ExtractApiErrorMessage(
                    "",
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
        foreach (var file in Directory.EnumerateFiles(appWorkDir)) File.Delete(file);
        progress?.Report("Trying the DepotBox Lua endpoint instead...");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://depotbox.org/api/direct-lua?appid={appId}");
            AddDepotBoxAuth(req, key);
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return new ManifestDownloadResult(false, packageError ?? ExtractApiErrorMessage(
                    "",
                    $"DepotBox returned HTTP {(int)resp.StatusCode} for App {appId}."));

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var bytes = await ReadPayloadAsync(resp.Content, 128L * 1024 * 1024, ct);
            var result = await HandleDepotBoxPayloadAsync(appWorkDir, appId, bytes, contentType, ct);
            return result.Succeeded ? result : new ManifestDownloadResult(false, packageError ?? result.Message);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ManifestDownloadResult(false, packageError ?? $"DepotBox download failed: {ex.Message}");
        }
    });

    private static bool IsZipArchive(byte[] bytes)
        => bytes.Length > 3 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K';

    /// <summary>
    /// Unpacks a manifest archive into the work directory. Returns the Lua text it contained (when
    /// there was one) and how many files were written.
    /// </summary>
    private static Task<(string? Lua, int Files)> ExtractManifestArchiveAsync(
        string zipPath, string appWorkDir, CancellationToken ct) =>
        ManifestArchiveReader.ExtractAsync(zipPath, appWorkDir, ct);

    private static async Task<byte[]> ReadPayloadAsync(HttpContent content, long maximumBytes, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("The source response exceeds the download size limit.");
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maximumBytes) throw new InvalidDataException("The source response exceeds the download size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private string NewPackageDirectory(string source, int appId) => Path.Combine(_workFolder, source,
        appId.ToString(CultureInfo.InvariantCulture), "pack-" + Guid.NewGuid().ToString("N"));

    /// <summary>Finds any Lua script that already sits in the work directory.</summary>
    private static string? FindLuaInWorkDir(string appWorkDir, int appId)
    {
        var direct = Path.Combine(appWorkDir, $"{appId}.lua");
        if (File.Exists(direct) && new FileInfo(direct).Length <= DownloadPreparationReader.MaximumLuaCharacters)
        {
            var text = new System.Text.UTF8Encoding(false, true).GetString(File.ReadAllBytes(direct));
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        foreach (var path in Directory.EnumerateFiles(appWorkDir, "*.lua").Take(DownloadPreparationReader.MaximumEntries))
        {
            if (new FileInfo(path).Length > DownloadPreparationReader.MaximumLuaCharacters) continue;
            var text = new System.Text.UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
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

        // Lua attachments are also served as application/octet-stream. The actual bytes,
        // rather than the generic MIME type, identify a ZIP package.
        var looksLikeZip = IsZipArchive(bytes) || contentType.Contains("zip", StringComparison.OrdinalIgnoreCase);

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
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return new ManifestDownloadResult(false, $"DepotBox archive could not be unpacked: {ex.Message}");
            }

            luaContent = FindLuaInWorkDir(appWorkDir, appId) ?? luaContent;
            if (luaContent is null)
                return new ManifestDownloadResult(false, $"No Lua script found in DepotBox archive for App {appId}.");

            _logging.Add(Models.LogLevel.Info, "ManifestSource",
                $"DepotBox package for App {appId} unpacked: {fileCount} file(s).", appId);
            return new ManifestDownloadResult(true, "Downloaded from DepotBox.", luaContent, appWorkDir);
        }

        if (bytes.Length > DownloadPreparationReader.MaximumLuaCharacters)
            return new(false, "DepotBox Lua content exceeds the size limit.");
        string luaText;
        try { luaText = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Trim(); }
        catch (System.Text.DecoderFallbackException)
        { return new(false, "DepotBox returned a binary file instead of a ZIP package or Lua metadata."); }
        if (luaText.StartsWith('<') || contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return new(false, "DepotBox returned a web page instead of metadata. Check the API key and retry; the provider may require browser verification.");
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

        luaText = SteamToolsMetadata.ReadLua(luaText, $"{appId}.lua", appId).Lua;
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
        var appWorkDir = NewPackageDirectory(sourceName.ToLowerInvariant(), appId);
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
                using var listResponse = await _httpClient.GetAsync(contentsUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (listResponse.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
                if (!listResponse.IsSuccessStatusCode)
                {
                    sawNonNotFoundError = true;
                    continue;
                }

                var json = await ReadPayloadAsync(listResponse.Content, 4L * 1024 * 1024, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    files = doc.RootElement.EnumerateArray()
                        .Where(e => e.GetProperty("type").GetString() == "file")
                        .Select(e => (
                            Name: e.GetProperty("name").GetString()!,
                            DownloadUrl: e.GetProperty("download_url").GetString()!))
                        .Take(DownloadPreparationReader.MaximumEntries + 1).ToList();
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object
                         && doc.RootElement.GetProperty("type").GetString() == "file")
                {
                    files.Add((
                        doc.RootElement.GetProperty("name").GetString()!,
                        doc.RootElement.GetProperty("download_url").GetString()!));
                }

                if (files.Count > DownloadPreparationReader.MaximumEntries)
                    throw new InvalidDataException("The source package contains too many files.");
                if (files.Any(file => !DownloadPreparationReader.IsSafePackageFileName(file.Name)))
                    throw new InvalidDataException("The source package contains an unsafe filename.");
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
        long downloadedBytes = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file.Name).ToLowerInvariant();
            if (ext is not ".lua" and not ".key" and not ".manifest") continue;

            progress?.Report($"Downloading {file.Name}...");
            try
            {
                // Try the primary CDN first, then the mirrors. A slow or blocked host no longer
                // fails or crawls the download — it just moves on to the next one.
                byte[]? payload = null;
                foreach (var url in RawUrlCandidates(file.DownloadUrl))
                {
                    try
                    {
                        using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                        if (!resp.IsSuccessStatusCode) continue;
                        payload = await ReadPayloadAsync(resp.Content, ext is ".lua" or ".key" ? 4L * 1024 * 1024 : 128L * 1024 * 1024, ct);
                        if (payload.Length > 0) break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }

                if (payload is null) continue;
                downloadedBytes += payload.Length;
                if (downloadedBytes > 512L * 1024 * 1024)
                    return new(false, "The source package exceeds the manifest download size limit.");

                if (ext == ".lua")
                {
                    var text = System.Text.Encoding.UTF8.GetString(payload).TrimStart('\uFEFF');
                    if (luaContent is null || file.Name.Equals($"{appId}.lua", StringComparison.OrdinalIgnoreCase)) luaContent = text;
                    await File.WriteAllTextAsync(Path.Combine(appWorkDir, file.Name), text, ct);
                }
                else
                {
                    await File.WriteAllBytesAsync(Path.Combine(appWorkDir, file.Name), payload, ct);
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

    /// <summary>
    /// One raw GitHub file can be fetched from several mirrors. The primary CDN is tried first and,
    /// when it is slow or unreachable, the download continues from the next host. The first two
    /// mirrors are the ones DepotDownloaderMod's own scripts fall back to and are the fastest for
    /// users far from GitHub's CDN.
    /// </summary>
    private static IEnumerable<string> RawUrlCandidates(string downloadUrl)
    {
        yield return downloadUrl;

        // raw.githubusercontent.com/{owner}/{repo}/{ref}/{path...}
        var match = Regex.Match(downloadUrl,
            @"^https?://raw\.githubusercontent\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/(?<ref>[^/]+)/(?<path>.+)$",
            RegexOptions.CultureInvariant);
        if (!match.Success) yield break;

        var slug = $"{match.Groups["owner"].Value}/{match.Groups["repo"].Value}";
        var reference = match.Groups["ref"].Value;
        var path = match.Groups["path"].Value;

        yield return $"https://raw.gitmirror.com/{slug}/{reference}/{path}";
        yield return $"https://cdn.jsdmirror.com/gh/{slug}@{reference}/{path}";
        yield return $"https://raw.dgithub.xyz/{slug}/{reference}/{path}";
    }

    public void Dispose() { if (_ownsHttpClient) _httpClient.Dispose(); }
}
