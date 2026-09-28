using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Steamy.Models;

namespace Steamy.Services;

/// <summary>What a dump produced: the folder it lives in and how much is in it.</summary>
public sealed record ManifestDumpResult(bool Succeeded, string Message, int AppId, string Folder, int FileCount, long TotalBytes);

/// <summary>What a share produced. <see cref="RemotePath"/> is the file inside the private dump repository.</summary>
public sealed record ManifestShareResult(bool Succeeded, string Message, string? RemotePath = null, string? WebUrl = null);

/// <summary>Everything found to share, and where it was looked for.</summary>
public sealed record ShareScanResult(IReadOnlyList<ShareCandidate> Items, string SteamRoot, IReadOnlyList<string> DumpRoots);

/// <summary>Progress of a batch share: <see cref="Fraction"/> runs from 0 to 1.</summary>
public sealed record ShareBatchProgress(double Fraction, string Message);

/// <summary>Result of sharing many apps at once.</summary>
public sealed record ManifestBatchShareResult(
    bool Succeeded,
    string Message,
    int AppsSent = 0,
    int FilesSent = 0,
    long BytesSent = 0,
    string? CommitUrl = null,
    IReadOnlyList<string>? RemotePaths = null);

/// <summary>Result of writing a share bundle to a local ZIP.</summary>
public sealed record ManifestExportResult(bool Succeeded, string Message, string Path, int Apps = 0, long Bytes = 0);

public interface IManifestShareService
{
    /// <summary>Owner/repository the dumps are sent to, for display.</summary>
    string TargetDescription { get; }

    /// <summary>Owner, repository and branch shares are committed to.</summary>
    GitHubTarget Target { get; }

    /// <summary>Raised when a dump or a share changed what the Share page would list.</summary>
    event EventHandler? ShareablesChanged;

    /// <summary>When the last share to any repository happened, or null if never.</summary>
    DateTime? LastSharedUtc { get; }

    /// <summary>True when a sharing token is stored.</summary>
    Task<bool> HasTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds everything the user can share: dump folders and the local Steam library.</summary>
    Task<ShareScanResult> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>The earlier share of exactly this file set to the current target, if there was one.</summary>
    ShareHistoryEntry? FindShared(ShareCandidate candidate);

    /// <summary>Sends many apps to the repository in one commit.</summary>
    Task<ManifestBatchShareResult> ShareManyAsync(IReadOnlyList<ShareCandidate> items,
        IProgress<ShareBatchProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Writes the given apps into one local ZIP (no token needed).</summary>
    Task<ManifestExportResult> ExportAsync(IReadOnlyList<ShareCandidate> items, string zipPath,
        CancellationToken cancellationToken = default);

    Task<ManifestDumpResult> DumpAsync(int appId, ManifestSource source, string? targetFolder = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Dump variant that additionally runs the tool under the user's own Steam account.</summary>
    Task<ManifestDumpResult> DumpAsync(int appId, ManifestSource source, string? targetFolder,
        string? steamUsername, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    Task<ManifestShareResult> ShareAsync(int appId, string folder,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Depot Dumper and Share: collects the Lua and depot manifests of an app into one folder, finds
/// everything shareable (dump folders and the manifests of installed games) and sends any
/// selection, packed as one ZIP per app, to the dump repository in a single commit.
/// <para>
/// Steamy only ever needs a fine-grained token with "Contents: Read and write" on that single
/// repository. Nothing is uploaded until the user presses Share or Send.
/// </para>
/// </summary>
public sealed class ManifestShareService : IManifestShareService, IDisposable
{
    /// <summary>Credential name of the fine-grained token that may push into the dump repository.</summary>
    public const string TokenCredentialName = "manifest-share-token";

    // Largest single pack that is sent; GitHub rejects blobs above 100 MB and big packs are slow.
    private const long MaxArchiveBytes = 50L * 1024 * 1024;

    private readonly ISettingsService _settings;
    private readonly ISecureCredentialService _credentials;
    private readonly IManifestSourceService _sources;
    private readonly ILoggingService _logging;
    private readonly HttpClient _httpClient;
    private readonly ShareHistoryStore _history = new(ShareHistoryStore.DefaultPath);

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

    public string TargetDescription => Target.ToString();

    public GitHubTarget Target
    {
        get
        {
            var settings = _settings.Load();
            var branch = string.IsNullOrWhiteSpace(settings.ManifestShareBranch) ? "main" : settings.ManifestShareBranch.Trim();
            return new GitHubTarget(ResolveOwner(settings), ResolveRepo(settings), branch);
        }
    }

    public DateTime? LastSharedUtc => _history.LastSharedUtc;

    public event EventHandler? ShareablesChanged;

    public async Task<bool> HasTokenAsync(CancellationToken cancellationToken = default) =>
        !string.IsNullOrWhiteSpace(await ReadTokenAsync(cancellationToken).ConfigureAwait(false));

    public ShareHistoryEntry? FindShared(ShareCandidate candidate) => _history.Find(TargetDescription, candidate.Fingerprint);

    public Task<ShareScanResult> ScanAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var settings = _settings.Load();
        var dumpRoots = DumpRoots(settings);

        var steamRoot = !string.IsNullOrWhiteSpace(settings.SteamLibraryPath) && Directory.Exists(settings.SteamLibraryPath)
            ? settings.SteamLibraryPath.Trim()
            : SteamLibraryService.FindSteamRoot() ?? string.Empty;

        IReadOnlyList<ShareCandidate> steam = Array.Empty<ShareCandidate>();
        if (steamRoot.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folders = SteamLibraryService.ReadLibraryFolders(Path.Combine(steamRoot, "steamapps"));
            steam = ManifestLibraryScanner.ScanSteamLibrary(steamRoot, folders);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var names = steam.GroupBy(item => item.AppId).ToDictionary(group => group.Key, group => group.First().Name);
        var dumps = ManifestLibraryScanner.ScanDumpRoots(dumpRoots, appId => names.TryGetValue(appId, out var name) ? name : null);

        var items = dumps.Concat(steam)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Source)
            .ToList();
        return new ShareScanResult(items, steamRoot, dumpRoots);
    }, cancellationToken);

    public async Task<ManifestBatchShareResult> ShareManyAsync(IReadOnlyList<ShareCandidate> items,
        IProgress<ShareBatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return new ManifestBatchShareResult(false, "Select at least one game to share.");

        var token = await ReadTokenAsync(cancellationToken).ConfigureAwait(false);
        var target = Target;
        if (string.IsNullOrWhiteSpace(token))
            return new ManifestBatchShareResult(false,
                $"No sharing token stored. Paste one in Settings → Depot Dumper & sharing (needs Contents: Read and write on {target}).");

        var created = DateTime.UtcNow;
        var stamp = created.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var packed = new List<(ShareCandidate Item, string Path, long Bytes)>();
        var files = new List<UploadFile>();
        var skipped = new List<string>();

        try
        {
            await Task.Run(() =>
            {
                for (var index = 0; index < items.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = items[index];
                    progress?.Report(new ShareBatchProgress(0.2 * index / items.Count, $"Packing {item.Name}…"));

                    var archive = ShareArchiveBuilder.BuildAppArchive(item, BuildStamp.Version, created);
                    if (archive.LongLength > MaxArchiveBytes)
                    {
                        skipped.Add($"{item.Name} ({DownloadFormat.Bytes(archive.LongLength)} is too big)");
                        continue;
                    }

                    var path = $"dumps/{item.AppId}/{item.AppId}-{stamp}-{ShareArchiveBuilder.SourceLabel(item.Source)}.zip";
                    files.Add(new UploadFile(path, archive));
                    packed.Add((item, path, archive.LongLength));
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ManifestBatchShareResult(false, "Sharing cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ManifestBatchShareResult(false, $"A pack could not be built: {exception.Message}");
        }

        if (files.Count == 0)
            return new ManifestBatchShareResult(false, skipped.Count > 0 ? $"Nothing could be packed: {string.Join(", ", skipped)}." : "Nothing to send.");

        // A small index per batch lets the repository owner see at a glance what arrived together.
        files.Add(new UploadFile($"batches/{stamp}-{Guid.NewGuid().ToString("N")[..8]}.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "steamy-share-batch/1",
            createdUtc = created.ToString("O"),
            steamy = BuildStamp.Version,
            apps = packed.Select(entry => new
            {
                appId = entry.Item.AppId,
                name = entry.Item.Name,
                source = ShareArchiveBuilder.SourceLabel(entry.Item.Source),
                path = entry.Path,
                fingerprint = entry.Item.Fingerprint,
                files = entry.Item.Files.Count,
                bytes = entry.Bytes
            })
        }, new JsonSerializerOptions { WriteIndented = true })));

        var message = packed.Count == 1
            ? $"Share {packed[0].Item.Name} ({packed[0].Item.AppId}) from Steamy {BuildStamp.Version}"
            : $"Share {packed.Count} apps from Steamy {BuildStamp.Version}";
        var totalBytes = packed.Sum(entry => entry.Bytes);
        var uploadProgress = new Progress<UploadProgress>(update =>
            progress?.Report(new ShareBatchProgress(0.2 + 0.8 * update.Done / Math.Max(1, update.Total), update.Message)));

        BatchUploadResult upload;
        try
        {
            var uploader = new GitHubBatchUploader(_httpClient, $"Steamy/{BuildStamp.Version}");
            upload = await uploader.UploadAsync(target, token!, message, files, uploadProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ManifestBatchShareResult(false, "Sharing cancelled.");
        }
        catch (HttpRequestException exception)
        {
            return new ManifestBatchShareResult(false, $"GitHub could not be reached: {exception.Message}");
        }

        // With the file-by-file fallback a failure can leave the first packs sent; remember those.
        var sent = upload.Succeeded ? packed : packed.Take(Math.Min(upload.FilesSent, packed.Count)).ToList();
        if (sent.Count > 0)
        {
            _history.Record(sent.Select(entry =>
                new ShareHistoryEntry(entry.Item.AppId, entry.Item.Fingerprint, target.ToString(), created, entry.Path)));
            ShareablesChanged?.Invoke(this, EventArgs.Empty);
        }

        if (!upload.Succeeded)
        {
            _logging.Add(LogLevel.Warning, "Sharing", $"Batch share to {target} failed: {upload.Message}");
            return new ManifestBatchShareResult(false, upload.Message, sent.Count, upload.FilesSent);
        }

        var summary = packed.Count == 1 ? packed[0].Item.Name : $"{packed.Count} games";
        var text = $"Sent {summary} · {DownloadFormat.Bytes(totalBytes)} to {target}"
            + (upload.UsedContentsApi ? " (first share on an empty repository, sent file by file)." : " in one commit.");
        if (skipped.Count > 0) text += $" Skipped: {string.Join(", ", skipped)}.";

        _logging.Add(LogLevel.Info, "Sharing", $"Shared {packed.Count} app(s) to {target}: {upload.CommitSha}");
        progress?.Report(new ShareBatchProgress(1, "Done."));
        return new ManifestBatchShareResult(true, text, packed.Count, upload.FilesSent, totalBytes, upload.CommitUrl,
            packed.Select(entry => entry.Path).ToList());
    }

    public Task<ManifestExportResult> ExportAsync(IReadOnlyList<ShareCandidate> items, string zipPath,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (items.Count == 0) return new ManifestExportResult(false, "Select at least one game to export.", zipPath);

        var temp = zipPath + ".partial";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                ShareArchiveBuilder.WriteBundle(stream, items, BuildStamp.Version, DateTime.UtcNow);
            File.Move(temp, zipPath, overwrite: true);

            var bytes = new FileInfo(zipPath).Length;
            _logging.Add(LogLevel.Info, "Sharing", $"Exported {items.Count} app(s) to {zipPath}");
            return new ManifestExportResult(true,
                $"Saved {(items.Count == 1 ? items[0].Name : $"{items.Count} games")} · {DownloadFormat.Bytes(bytes)} to {Path.GetFileName(zipPath)}.",
                zipPath, items.Count, bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            return new ManifestExportResult(false, $"The ZIP could not be written: {exception.Message}", zipPath);
        }
    }, cancellationToken);

    public async Task<ManifestDumpResult> DumpAsync(int appId, ManifestSource source, string? targetFolder = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => await DumpAsync(appId, source, targetFolder, steamUsername: null, progress, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Dumps the Lua and manifests of an app. When <paramref name="steamUsername"/> is set, the
    /// DepotDownloaderMod run is done with that account afterwards, so licensed depots can be
    /// dumped from the user's own Steam login. The password and the 2FA/Steam Guard code are typed
    /// by the user in the tool's own console window — Steamy never asks for, reads or stores them.
    /// </summary>
    public async Task<ManifestDumpResult> DumpAsync(int appId, ManifestSource source, string? targetFolder,
        string? steamUsername, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
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

        if (!string.IsNullOrWhiteSpace(steamUsername))
        {
            progress?.Report($"Depot keys from your own account ({steamUsername})…");
            await RunLicensedDumpAsync(appId, steamUsername, download, folder, progress, cancellationToken).ConfigureAwait(false);
        }

        var files = EnumerateDumpFiles(folder);
        var bytes = files.Sum(file => SafeLength(file));

        if (files.Count == 0)
            return new ManifestDumpResult(false,
                string.IsNullOrWhiteSpace(download.Message) ? "The source returned no Lua or manifest files." : download.Message,
                appId, folder, 0, 0);

        var summary = $"{files.Count} files, {DownloadFormat.Bytes(bytes)}";
        _logging.Add(LogLevel.Info, "DepotDumper", $"Dumped App {appId} from {source}: {summary}");
        ShareablesChanged?.Invoke(this, EventArgs.Empty);
        return new ManifestDumpResult(true, $"Dumped {summary} for App {appId}.", appId, folder, files.Count, bytes);
    }

    /// <summary>
    /// Licensed part of the dump: runs DepotDownloaderMod once for the app with the user's account
    /// so its console window can ask for the password and Steam Guard code. The tool writes its
    /// session data next to the executable, nothing is captured or stored by Steamy.
    /// </summary>
    private async Task RunLicensedDumpAsync(int appId, string steamUsername, ManifestDownloadResult download,
        string folder, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var workDirectory = !string.IsNullOrWhiteSpace(download.WorkDirectory) ? download.WorkDirectory : folder;
        var tool = Directory.EnumerateFiles(AppDomain.CurrentDomain.BaseDirectory, "DepotDownloader*.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (tool is null)
        {
            progress?.Report("DepotDownloaderMod not found — licensed depots were skipped.");
            return;
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = tool,
                WorkingDirectory = workDirectory,
                UseShellExecute = true,
                CreateNoWindow = false
            };
            startInfo.ArgumentList.Add("-app");
            startInfo.ArgumentList.Add(appId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-username");
            startInfo.ArgumentList.Add(steamUsername);
            startInfo.ArgumentList.Add("-remember-password");
            startInfo.ArgumentList.Add("-manifest-only");
            startInfo.ArgumentList.Add("-dir");
            startInfo.ArgumentList.Add(Path.GetFullPath(folder));

            progress?.Report("DepotDownloaderMod opens in its own window — log in there (password + Steam Guard). It closes when the dump is done.");
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null) return;
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            CopyFromWorkDirectory(workDirectory, folder, appId);
            progress?.Report(process.ExitCode == 0
                ? "Account dump finished."
                : "Account dump ended with an error — check the tool's window.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            progress?.Report($"Account dump could not run: {exception.Message}");
            _logging.Add(LogLevel.Warning, "DepotDumper", $"Licensed dump failed for App {appId}: {exception.Message}");
        }
    }

    public async Task<ManifestShareResult> ShareAsync(int appId, string folder,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (appId <= 0)
            return new ManifestShareResult(false, "Enter a Steam App ID first.");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new ManifestShareResult(false, "Dump something first — the folder does not exist yet.");

        var candidate = ManifestLibraryScanner.ScanDumpFolder(folder, appId);
        if (candidate is null)
            return new ManifestShareResult(false, "There is nothing to share in the dump folder yet.");

        // The single-app share is a one-item batch, so both produce exactly the same pack.
        var batchProgress = progress is null ? null : new Progress<ShareBatchProgress>(update => progress.Report(update.Message));
        var result = await ShareManyAsync(new[] { candidate }, batchProgress, cancellationToken).ConfigureAwait(false);
        return new ManifestShareResult(result.Succeeded, result.Message, result.RemotePaths?.FirstOrDefault(), result.CommitUrl);
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
                .Where(file => ManifestLibraryScanner.DumpExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
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

    /// <summary>Every folder the Depot Dumper may have written <c>app-*</c> folders into.</summary>
    private static IReadOnlyList<string> DumpRoots(Models.AppSettings settings)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.ManifestDumpFolder))
        {
            roots.Add(settings.ManifestDumpFolder.Trim());
            roots.Add(Path.Combine(settings.ManifestDumpFolder.Trim(), "DepotDumps"));
        }

        foreach (var root in new[] { settings.DownloadFolder, settings.WorkingDirectory })
        {
            if (!string.IsNullOrWhiteSpace(root)) roots.Add(Path.Combine(root.Trim(), "DepotDumps"));
        }

        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy", "DepotDumps"));
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ResolveOwner(Models.AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.ManifestShareOwner) ? "ZazaJr24" : settings.ManifestShareOwner.Trim();

    private static string ResolveRepo(Models.AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.ManifestShareRepo) ? "Steamy-Dumps" : settings.ManifestShareRepo.Trim();

    public void Dispose() => _httpClient.Dispose();
}
