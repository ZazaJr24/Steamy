using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Steamy.Models;

namespace Steamy.Services;

/// <summary>Everything found to share, and where it was looked for.</summary>
public sealed record ShareScanResult(IReadOnlyList<ShareCandidate> Items, string SteamRoot, IReadOnlyList<string> LuaFolders);

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
}

/// <summary>
/// Share: finds everything shareable (installed games, Lua scripts and every cached depot
/// manifest, installed or not) and sends any selection, packed as one ZIP per app, to the dump
/// repository in a single commit.
/// <para>
/// Steamy only ever needs a fine-grained token with "Contents: Read and write" on that single
/// repository. Nothing is uploaded until the user presses Share.
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
    private readonly ILoggingService _logging;
    private readonly HttpClient _httpClient;
    private readonly ShareHistoryStore _history = new(ShareHistoryStore.DefaultPath);
    private readonly AppListIndex _apps = new(AppListIndex.BundledPath);

    public ManifestShareService(
        ISettingsService settings,
        ISecureCredentialService credentials,
        ILoggingService logging)
    {
        _settings = settings;
        _credentials = credentials;
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
        var steamRoot = !string.IsNullOrWhiteSpace(settings.SteamLibraryPath) && Directory.Exists(settings.SteamLibraryPath)
            ? settings.SteamLibraryPath.Trim()
            : SteamLibraryService.FindSteamRoot() ?? string.Empty;

        var steamApps = steamRoot.Length > 0
            ? SteamLibraryService.ReadLibraryFolders(Path.Combine(steamRoot, "steamapps"))
            : Array.Empty<string>();

        // Lua scripts: SteamTools' plug-in folder, Steamy's own manifest downloads and the folders
        // the old Depot Dumper wrote. Their manifests are searched next to them as well.
        var luaFolders = new List<string>();
        if (steamRoot.Length > 0) luaFolders.Add(Path.Combine(steamRoot, "config", "stplug-in"));
        luaFolders.Add(ManifestWorkFolder);
        luaFolders.AddRange(LegacyDumpRoots(settings));
        luaFolders = luaFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        cancellationToken.ThrowIfCancellationRequested();
        var input = new ShareScanInput(steamRoot, steamApps, luaFolders, Array.Empty<string>());
        var items = ManifestLibraryScanner.ScanAll(input, _apps);
        return new ShareScanResult(items, steamRoot, luaFolders);
    }, cancellationToken);

    /// <summary>Where Steamy's manifest sources put the Lua and manifests of a download.</summary>
    private static string ManifestWorkFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy", "manifest-workdir");

    public async Task<ManifestBatchShareResult> ShareManyAsync(IReadOnlyList<ShareCandidate> items,
        IProgress<ShareBatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return new ManifestBatchShareResult(false, "Select at least one game to share.");

        var token = await ReadTokenAsync(cancellationToken).ConfigureAwait(false);
        var target = Target;
        if (string.IsNullOrWhiteSpace(token))
            return new ManifestBatchShareResult(false,
                $"No sharing token stored. Paste one in Settings → Sharing (needs Contents: Read and write on {target}).");

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

    /// <summary>Folders the Depot Dumper of earlier versions wrote into; their Lua files still count.</summary>
    private static IReadOnlyList<string> LegacyDumpRoots(Models.AppSettings settings)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.ManifestDumpFolder) && Directory.Exists(settings.ManifestDumpFolder))
        {
            // That folder can be any user folder, so only its app-* dump folders are searched.
            var folder = settings.ManifestDumpFolder.Trim();
            try
            {
                roots.AddRange(Directory.EnumerateDirectories(folder, "app-*", SearchOption.TopDirectoryOnly)
                    .Where(path => ManifestLibraryScanner.ParseAppFolder(Path.GetFileName(path)) is not null));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            roots.Add(Path.Combine(folder, "DepotDumps"));
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
