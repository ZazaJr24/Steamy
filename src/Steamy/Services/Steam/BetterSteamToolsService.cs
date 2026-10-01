using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Steamy.Services;

public sealed record BetterSteamToolsState(string SteamRoot, bool SteamDetected, bool BackendInstalled, IReadOnlyList<int> AddedAppIds, string Message);
public sealed record BetterSteamToolsResult(bool Succeeded, string Message, IReadOnlyList<int> AppIds, int ManifestCount = 0);
public interface IBetterSteamToolsService
{
    BetterSteamToolsState Detect(string? steamRoot = null);
    Task<BetterSteamToolsResult> InstallBackendAsync(string steamRoot, IProgress<string>? progress = null, CancellationToken token = default);
    Task<BetterSteamToolsResult> ImportAsync(string steamRoot, IReadOnlyList<string> paths, int? appId = null, CancellationToken token = default);
    Task<BetterSteamToolsResult> AddFromSourceAsync(string steamRoot, int appId, ManifestSource? source, IProgress<string>? progress = null, CancellationToken token = default);
}

public sealed class BetterSteamToolsService(ISettingsService settings, IManifestSourceService sources) : IBetterSteamToolsService
{
    public const string Repository = "madoiscool/BetterSteamTools";
    private static readonly string[] Payload = ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BetterSteamToolsState Detect(string? steamRoot = null)
    {
        try
        {
            var configured = settings.Load().SteamLibraryPath;
            var root = !string.IsNullOrWhiteSpace(steamRoot) ? steamRoot.Trim() : !string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "steam.exe")) ? configured : SteamLibraryService.FindSteamRoot();
            if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "steam.exe")))
                return new(root ?? "", false, false, [], "Select the Steam folder containing steam.exe.");
            root = Path.GetFullPath(root);
            var installed = Payload.All(file => File.Exists(Path.Combine(root, file)));
            var folder = Path.Combine(root, "config", "stplug-in");
            var ids = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.lua").Select(file => int.TryParse(Path.GetFileNameWithoutExtension(file), out var id) && id > 0 ? id : 0).Where(id => id > 0).Order().ToArray() : [];
            return new(root, true, installed, ids, installed ? "Backend files detected · ready to add game metadata." : "Steam detected · install BetterSteamTools to enable game metadata.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(steamRoot ?? "", false, false, [], "Steam detection failed: " + exception.Message); }
    }

    public async Task<BetterSteamToolsResult> InstallBackendAsync(string steamRoot, IProgress<string>? progress = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!Detect(steamRoot).SteamDetected) throw new IOException("Select a valid Steam installation first.");
            if (SteamIsRunning()) throw new IOException("Close Steam, then click Install BetterSteamTools. Steam will stay closed during installation.");
            using var http = new HttpClient(StableDnsHandler.Create()) { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/" + BuildStamp.Version);
            progress?.Report("Checking the official BetterSteamTools release…");
            using var release = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", token));
            var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$")) throw new InvalidDataException("Invalid BetterSteamTools release tag.");
            var name = $"OpenSteamTool-{tag}-Release.zip";
            var asset = release.RootElement.GetProperty("assets").EnumerateArray().SingleOrDefault(item => item.GetProperty("name").GetString() == name);
            if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("The official release has no matching Windows archive.");
            var digest = asset.GetProperty("digest").GetString() ?? "";
            if (!Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$")) throw new InvalidDataException("The release is missing its SHA-256 checksum.");
            var url = asset.GetProperty("browser_download_url").GetString();
            if (url != $"https://github.com/{Repository}/releases/download/{tag}/{name}") throw new InvalidDataException("Unexpected BetterSteamTools download URL.");
            progress?.Report("Downloading and verifying BetterSteamTools " + tag + "…");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            const int maximum = 64 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("The backend archive is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            using var bytes = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                if (bytes.Length + count > maximum) throw new InvalidDataException("The backend archive is too large.");
                bytes.Write(buffer, 0, count);
            }
            var files = SteamToolsBackend.ReadArchive(bytes.ToArray(), digest[7..]);
            AddConfiguration(steamRoot, files);
            if (SteamIsRunning()) throw new IOException("Steam started during preparation. Close it and try again.");
            var luaFolder = Path.Combine(steamRoot, "config", "stplug-in");
            SteamToolsFiles.RejectLinks(Path.GetFullPath(steamRoot), Path.Combine(luaFolder, "probe.lua"));
            Directory.CreateDirectory(luaFolder);
            await Task.Run(() => SteamToolsFiles.Apply(steamRoot, files, token: token), token);
            return new(true, $"BetterSteamTools {tag} installed. Start Steam to load it; Lua changes can then reload automatically.", []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        { return new(false, exception is OperationCanceledException ? "Installation cancelled." : exception.Message, []); }
        finally { _gate.Release(); }
    }

    public async Task<BetterSteamToolsResult> ImportAsync(string steamRoot, IReadOnlyList<string> paths, int? appId = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!Detect(steamRoot).BackendInstalled) throw new IOException("Install BetterSteamTools before adding game metadata.");
            var plan = await Task.Run(() => SteamToolsMetadata.ReadAsync(paths, appId, token), token);
            var files = new Dictionary<string, byte[]>(plan.Files, StringComparer.OrdinalIgnoreCase);
            AddConfiguration(steamRoot, files);
            await Task.Run(() => SteamToolsFiles.Apply(steamRoot, files, token: token), token);
            var message = plan.AppIds.Count == 0 ? $"Cached {plan.ManifestCount} manifest(s). Add Lua metadata or use a source to register a game."
                : $"Added {plan.AppIds.Count} game configuration(s) and {plan.ManifestCount} manifest(s). Steam reloads the configured Lua folder.";
            return new(true, message, plan.AppIds, plan.ManifestCount);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        { return new(false, exception is OperationCanceledException ? "Import cancelled." : exception.Message, []); }
        finally { _gate.Release(); }
    }

    public async Task<BetterSteamToolsResult> AddFromSourceAsync(string steamRoot, int appId, ManifestSource? source, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (appId <= 0) return new(false, "Enter a valid Steam App ID.", []);
        if (!Detect(steamRoot).BackendInstalled) return new(false, "Install BetterSteamTools before adding games.", []);
        var candidates = source is { } chosen ? sources.Sources.Where(item => item.Source == chosen).ToArray()
            : sources.Sources.OrderBy(item => item.RequiresAuthCode).ThenBy(item => item.Source == ManifestSource.Sushi ? 0 : 1).ToArray();
        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report("Checking " + candidate.Label + "…");
            var availability = await sources.CheckAvailabilityAsync(candidate.Source, appId, token);
            if (availability.Certain && !availability.Available) { errors.Add(candidate.Label + ": " + availability.Message); continue; }
            var download = await sources.DownloadManifestsAsync(candidate.Source, appId, progress, token);
            if (!download.Succeeded || string.IsNullOrWhiteSpace(download.LuaContent)) { errors.Add(candidate.Label + ": " + download.Message); continue; }
            var owned = Path.Combine(Path.GetTempPath(), "Steamy-bst-source-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(owned);
                var lua = Path.Combine(owned, appId + ".lua");
                await File.WriteAllTextAsync(lua, download.LuaContent, token);
                var files = new List<string> { lua };
                if (!string.IsNullOrWhiteSpace(download.WorkDirectory) && Directory.Exists(download.WorkDirectory))
                    files.AddRange(Directory.EnumerateFiles(download.WorkDirectory, "*.manifest", SearchOption.AllDirectories));
                var result = await ImportAsync(steamRoot, files, appId, token);
                if (result.Succeeded) return result with { Message = "From " + candidate.Label + ": " + result.Message };
                errors.Add(candidate.Label + ": " + result.Message);
            }
            finally { if (Directory.Exists(owned)) Directory.Delete(owned, true); }
        }
        return new(false, "No source supplied usable metadata. " + string.Join(" · ", errors), []);
    }

    private static void AddConfiguration(string root, Dictionary<string, byte[]> files)
    {
        var path = Path.Combine(root, "opensteamtool.toml");
        SteamToolsFiles.RejectLinks(Path.GetFullPath(root), path);
        if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("The backend configuration is too large.");
        var current = File.Exists(path) ? File.ReadAllText(path, new UTF8Encoding(false, true)) : "";
        var next = SteamToolsFiles.RegisterLuaPath(current);
        if (next != current) files["opensteamtool.toml"] = new UTF8Encoding(false).GetBytes(next);
    }

    private static bool SteamIsRunning()
    {
        var processes = Process.GetProcessesByName("steam");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
