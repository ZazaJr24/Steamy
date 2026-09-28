using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Steamy.Models;

namespace Steamy.Services;

/// <summary>What a dump produced: the folder it lives in and how much is in it.</summary>
public sealed record ManifestDumpResult(bool Succeeded, string Message, int AppId, string Folder, int FileCount, long TotalBytes);

/// <summary>What a share produced. <see cref="RemotePath"/> is the file inside the private dump repository.</summary>
public sealed record ManifestShareResult(bool Succeeded, string Message, string? RemotePath = null, string? WebUrl = null);

public interface IManifestShareService
{
    /// <summary>Owner/repository the dumps are sent to, for display.</summary>
    string TargetDescription { get; }

    Task<ManifestDumpResult> DumpAsync(int appId, ManifestSource source, string? targetFolder = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    Task<ManifestShareResult> ShareAsync(int appId, string folder,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Depot Dumper: collects the Lua and depot manifests of an app into one folder and can send them,
/// packed as a ZIP, into the project's dump repository.
/// <para>
/// The upload is write-only by design. Steamy only ever needs a fine-grained token with
/// "Contents: Read and write" on that single private repository, so a contributor can push dumps
/// but cannot read what other people sent. Nothing is uploaded until the user presses Share.
/// </para>
/// </summary>
public sealed class ManifestShareService : IManifestShareService, IDisposable
{
    /// <summary>Credential name of the fine-grained token that may push into the dump repository.</summary>
    public const string TokenCredentialName = "manifest-share-token";

    private static readonly string[] SupportedExtensions = { ".lua", ".manifest", ".key", ".acf" };

    private readonly ISettingsService _settings;
    private readonly ISecureCredentialService _credentials;
    private readonly IManifestSourceService _sources;
    private readonly ILoggingService _logging;
    private readonly HttpClient _httpClient;

    public ManifestShareService(
        ISettingsService settings,
        ISecureCredentialService credentials,
        IManifestSourceService sources,
        ILoggingService logging)
    {
        _settings = settings;
        _credentials = credentials;
        _sources = sources;
        _logging = logging;
        _httpClient = new HttpClient(StableDnsHandler.Create()) { Timeout = TimeSpan.FromMinutes(5) };
    }

    public string TargetDescription
    {
        get
        {
            var settings = _settings.Load();
            return $"{ResolveOwner(settings)}/{ResolveRepo(settings)}";
        }
    }

    public async Task<ManifestDumpResult> DumpAsync(int appId, ManifestSource source, string? targetFolder = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (appId <= 0)
            return new ManifestDumpResult(false, "Enter a Steam App ID first.", appId, string.Empty, 0, 0);

        var folder = ResolveDumpFolder(appId, targetFolder);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ManifestDumpResult(false, $"The dump folder cannot be created: {exception.Message}", appId, folder, 0, 0);
        }

        progress?.Report($"Asking {source} for App {appId}…");
        ManifestDownloadResult download;
        try
        {
            download = await _sources.DownloadManifestsAsync(source, appId, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ManifestDumpResult(false, "Dump cancelled.", appId, folder, 0, 0);
        }
        catch (Exception exception)
        {
            _logging.Add(LogLevel.Warning, "DepotDumper", $"Dump failed: {exception.Message}");
            return new ManifestDumpResult(false, $"{exception.GetType().Name}: {exception.Message}", appId, folder, 0, 0);
        }

        progress?.Report("Collecting files…");
        CopyFromWorkDirectory(download.WorkDirectory, folder, appId);

        var files = EnumerateDumpFiles(folder);
        var bytes = files.Sum(file => SafeLength(file));

        if (files.Count == 0)
            return new ManifestDumpResult(false,
                string.IsNullOrWhiteSpace(download.Message) ? "The source returned no Lua or manifest files." : download.Message,
                appId, folder, 0, 0);

        var summary = $"{files.Count} files, {DownloadFormat.Bytes(bytes)}";
        _logging.Add(LogLevel.Info, "DepotDumper", $"Dumped App {appId} from {source}: {summary}");
        return new ManifestDumpResult(true, $"Dumped {summary} for App {appId}.", appId, folder, files.Count, bytes);
    }

    public async Task<ManifestShareResult> ShareAsync(int appId, string folder,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (appId <= 0)
            return new ManifestShareResult(false, "Enter a Steam App ID first.");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new ManifestShareResult(false, "Dump something first — the folder does not exist yet.");

        var files = EnumerateDumpFiles(folder);
        if (files.Count == 0)
            return new ManifestShareResult(false, "There is nothing to share in the dump folder yet.");

        var settings = _settings.Load();
        var token = await ReadTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return new ManifestShareResult(false,
                $"No sharing token stored. Paste one in Settings → Depot Dumper (needs Contents: Read and write on {TargetDescription}).");

        progress?.Report($"Packing {files.Count} files…");
        byte[] archive;
        try
        {
            archive = BuildArchive(folder, files, settings, appId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ManifestShareResult(false, $"The dump could not be packed: {exception.Message}");
        }

        var owner = ResolveOwner(settings);
        var repo = ResolveRepo(settings);
        var branch = string.IsNullOrWhiteSpace(settings.ManifestShareBranch) ? "main" : settings.ManifestShareBranch.Trim();
        var path = $"dumps/{appId}/{appId}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/contents/{path}";

        var payload = JsonSerializer.Serialize(new
        {
            message = $"Dump {appId} from Steamy {BuildStamp.Version}",
            content = Convert.ToBase64String(archive),
            branch
        });

        progress?.Report($"Sending {DownloadFormat.Bytes(archive.LongLength)} to {owner}/{repo}…");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.TryAddWithoutValidation("User-Agent", $"Steamy/{BuildStamp.Version}");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var webUrl = ReadHtmlUrl(body);
                _logging.Add(LogLevel.Info, "DepotDumper", $"Shared App {appId} as {path}");
                progress?.Report("Sent.");
                return new ManifestShareResult(true,
                    $"Sent {DownloadFormat.Bytes(archive.LongLength)} of {appId} manifests. Nobody but the repository owner can open it.", path, webUrl);
            }

            var hint = (int)response.StatusCode switch
            {
                401 => "The token is not valid any more. Create a new fine-grained token in Settings.",
                403 => $"The token has no write access to {owner}/{repo}.",
                404 => $"The repository {owner}/{repo} was not found, or the token cannot see it. Fine-grained tokens must be granted access to this repository.",
                409 or 422 => "The repository rejected the file (branch missing or a conflicting file exists). Check the branch name.",
                _ => $"GitHub answered HTTP {(int)response.StatusCode}."
            };

            var detail = ReadErrorMessage(body);
            return new ManifestShareResult(false, detail.Length > 0 ? $"{hint} GitHub said: {detail}" : hint);
        }
        catch (OperationCanceledException)
        {
            return new ManifestShareResult(false, "Sharing cancelled.");
        }
        catch (HttpRequestException exception)
        {
            return new ManifestShareResult(false, $"The dump could not be sent: {exception.Message}");
        }
    }

    private async Task<string?> ReadTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _credentials.ReadAsync(TokenCredentialName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static byte[] BuildArchive(string folder, IReadOnlyList<string> files, Models.AppSettings settings, int appId)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(Path.GetRelativePath(folder, file).Replace('\\', '/'), CompressionLevel.Optimal);
                using var target = entry.Open();
                using var source = File.OpenRead(file);
                source.CopyTo(target);
            }

            var readme = archive.CreateEntry("README.txt", CompressionLevel.NoCompression);
            using var writer = new StreamWriter(readme.Open(), Encoding.UTF8);
            writer.WriteLine($"Steamy dump for App {appId}");
            writer.WriteLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine($"Source: {settings.HubcapBaseUrl}");
            writer.WriteLine("Contains the Lua script and depot manifests dumped by Steamy.");
        }

        return buffer.ToArray();
    }

    private static void CopyFromWorkDirectory(string? workDirectory, string folder, int appId)
    {
        if (string.IsNullOrWhiteSpace(workDirectory) || !Directory.Exists(workDirectory)) return;
        var target = Path.GetFullPath(folder);
        foreach (var file in EnumerateDumpFiles(workDirectory))
        {
            try
            {
                if (Path.GetFullPath(file).StartsWith(target, StringComparison.OrdinalIgnoreCase)) continue;
                var destination = Path.Combine(folder, Path.GetFileName(file));
                if (!File.Exists(destination)) File.Copy(file, destination);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A single unreadable file must not stop the dump.
            }
        }

        // The Lua is what DepotDownloaderMod needs first, so keep its canonical name in place.
        var lua = Path.Combine(folder, $"{appId}.lua");
        if (File.Exists(lua)) return;
        var anyLua = Directory.EnumerateFiles(folder, "*.lua", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (anyLua is not null)
        {
            try { File.Copy(anyLua, lua, overwrite: false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static List<string> EnumerateDumpFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(file => SupportedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return 0; }
    }

    private string ResolveDumpFolder(int appId, string? targetFolder)
    {
        if (!string.IsNullOrWhiteSpace(targetFolder)) return Path.Combine(targetFolder, $"app-{appId}");

        var settings = _settings.Load();
        var root = !string.IsNullOrWhiteSpace(settings.ManifestDumpFolder) ? settings.ManifestDumpFolder
            : !string.IsNullOrWhiteSpace(settings.DownloadFolder) ? settings.DownloadFolder
            : !string.IsNullOrWhiteSpace(settings.WorkingDirectory) ? settings.WorkingDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy");
        return Path.Combine(root, "DepotDumps", $"app-{appId}");
    }

    private static string ResolveOwner(Models.AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.ManifestShareOwner) ? "ZazaJr24" : settings.ManifestShareOwner.Trim();

    private static string ResolveRepo(Models.AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.ManifestShareRepo) ? "Steamy-Dumps" : settings.ManifestShareRepo.Trim();

    private static string? ReadHtmlUrl(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("content", out var content)
                && content.TryGetProperty("html_url", out var html)
                ? html.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
