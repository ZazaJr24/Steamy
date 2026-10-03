using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Steamy.Services;

public interface IRyuuGameDownloadService
{
    IReadOnlyList<RyuuDepotInfo> ParseLua(string luaContent);

    Task<GameDownloadPreparation> PrepareDownloadAsync(int appId, ManifestSource source,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GameDownloadPreparation(false, "This downloader does not support depot selection."));

    Task<RyuuGameDownloadResult> DownloadPreparedAsync(PreparedGameDownload plan,
        IReadOnlyList<CachedDepotManifest> selectedDepots, string targetFolder,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RyuuGameDownloadResult(false, "This downloader does not support prepared downloads."));

    Task<GameDownloadPreparation> PrepareLocalPackageAsync(int appId, string packagePath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GameDownloadPreparation(false, "Local packages are not supported by this downloader."));

    void DiscardPreparedDownload(Guid id) { }

    Task<RyuuGameDownloadResult> DownloadGameAsync(
        int appId,
        string targetFolder,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RyuuGameDownloadResult> DownloadGameAsync(
        int appId,
        string targetFolder,
        ManifestSource source,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RyuuGameDownloadResult> ResumeDownloadAsync(
        int appId,
        string targetFolder,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class RyuuGameDownloadService : IRyuuGameDownloadService, IDisposable
{
    private const string GitHubReleasesApi = "https://api.github.com/repos/ZazaJr24/Steamy/releases/latest";
    private const long MaximumManifestBytes = 512L * 1024 * 1024;

    private readonly ISettingsService _settings;
    private readonly ISecureCredentialService _credentials;
    private readonly IRyuuSecureDownloadService _ryuuDownload;
    private readonly IManifestSourceService _manifestSource;
    private readonly ILoggingService _logging;
    private readonly ISteamDepotMetadataService? _depotMetadata;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _toolsFolder;
    private readonly string _workFolder;
    private readonly string _fallbackToolsFolder;

    public RyuuGameDownloadService(
        ISettingsService settings,
        ISecureCredentialService credentials,
        IRyuuSecureDownloadService ryuuDownload,
        IManifestSourceService manifestSource,
        ILoggingService logging, HttpClient? httpClient = null, string? workFolder = null, string? toolsFolder = null,
        ISteamDepotMetadataService? depotMetadata = null)
    {
        _settings = settings;
        _credentials = credentials;
        _ryuuDownload = ryuuDownload;
        _manifestSource = manifestSource;
        _logging = logging;
        _depotMetadata = depotMetadata;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(StableDnsHandler.Create()) { Timeout = TimeSpan.FromMinutes(5) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steamy");
        var appTools = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "DepotDownloaderMod");
        _toolsFolder = toolsFolder ?? appTools;
        _workFolder = workFolder ?? Path.Combine(appData, "ryuu-workdir");
        _fallbackToolsFolder = workFolder is null ? Path.Combine(appData, "tools", "DepotDownloaderMod") : Path.Combine(workFolder, "tools");
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, PreparedSnapshot> _prepared = new();
    public const int MaximumPreparedDownloads = 8;
    private readonly SemaphoreSlim _preparationSlots = new(MaximumPreparedDownloads, MaximumPreparedDownloads);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, ManifestSource), SemaphoreSlim> _sourceGates = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.OrdinalIgnoreCase);

    private sealed class PreparedSnapshot(PreparedGameDownload plan, DownloadPreparationReader.Catalog catalog, string directory)
    {
        public PreparedGameDownload Plan { get; } = plan;
        public DownloadPreparationReader.Catalog Catalog { get; } = catalog;
        public string Directory { get; } = directory;
        public object Sync { get; } = new();
        public int ActiveUsers { get; set; }
        public bool Discarded { get; set; }
    }

    public IReadOnlyList<RyuuDepotInfo> ParseLua(string luaContent)
    {
        if (string.IsNullOrWhiteSpace(luaContent)) return [];
        var catalog = DownloadPreparationReader.Read(luaContent);
        return catalog.Depots.Select(depot => catalog.Manifests.Single(manifest =>
            manifest.DepotId == depot.DepotId && manifest.ManifestId == depot.DefaultManifestId)).ToArray();
    }

    public Task<GameDownloadPreparation> PrepareDownloadAsync(int appId, ManifestSource source,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(appId, source, null, progress, cancellationToken);

    public Task<GameDownloadPreparation> PrepareLocalPackageAsync(int appId, string packagePath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(appId, ManifestSource.Local, packagePath, progress, cancellationToken);

    private async Task<GameDownloadPreparation> PrepareCoreAsync(int appId, ManifestSource source, string? packagePath,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (appId <= 0 || !Enum.IsDefined(source)) return new(false, "Choose a valid game and manifest source.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!_preparationSlots.Wait(0))
            return new(false, "Too many source selections are open. Close another selection or wait for a download to finish, then try again.");
        var gate = _sourceGates.GetOrAdd((appId, source), _ => new SemaphoreSlim(1, 1));
        var gateEntered = false;
        var snapshotDirectory = Path.Combine(_workFolder, "prepared", Guid.NewGuid().ToString("N"));
        var importDirectory = snapshotDirectory + "-import";
        var published = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            progress?.Report($"Loading available depots from {source}…");
            ManifestDownloadResult result;
            if (source == ManifestSource.Local)
            {
                if (string.IsNullOrWhiteSpace(packagePath)) return new(false, "Select your ZIP or Lua package first.");
                progress?.Report("Checking local package metadata and hashes…");
                var imported = await LocalManifestPackage.ImportAsync(packagePath, appId, importDirectory, cancellationToken).ConfigureAwait(false);
                result = new(true, "Local package verified.", imported.Lua, imported.Directory);
            }
            else result = await _manifestSource.DownloadManifestsAsync(source, appId, progress, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.LuaContent)) return new(false, result.Message);
            cancellationToken.ThrowIfCancellationRequested();
            var lua = result.LuaContent;
            if (lua.Length > DownloadPreparationReader.MaximumLuaCharacters)
                return new(false, "The source Lua metadata exceeds the parsing limit.");
            var additionalLua = new List<string>();
            var manifestNames = new List<string>();
            var keys = new Dictionary<int, string>();
            Directory.CreateDirectory(snapshotDirectory);
            if (!string.IsNullOrWhiteSpace(result.WorkDirectory) && Directory.Exists(result.WorkDirectory))
            {
                var files = Directory.EnumerateFiles(result.WorkDirectory).Take(DownloadPreparationReader.MaximumEntries + 1).ToArray();
                if (files.Length > DownloadPreparationReader.MaximumEntries)
                    throw new InvalidDataException("The source package contains too many files.");
                long copiedBytes = 0;
                long luaBytes = System.Text.Encoding.UTF8.GetByteCount(lua);
                long keyBytes = 0;
                var keyEntries = 0;
                if (luaBytes > DownloadPreparationReader.MaximumLuaCharacters)
                    throw new InvalidDataException("The source Lua metadata exceeds the parsing limit.");
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("The source package contains a linked file.");
                    var extension = Path.GetExtension(file);
                    if (extension.Equals(".lua", StringComparison.OrdinalIgnoreCase))
                    {
                        var info = new FileInfo(file);
                        if (info.Length > DownloadPreparationReader.MaximumLuaCharacters)
                            throw new InvalidDataException("The source Lua metadata exceeds the parsing limit.");
                        var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                        if (text == lua) continue;
                        luaBytes += System.Text.Encoding.UTF8.GetByteCount(text);
                        if (luaBytes > DownloadPreparationReader.MaximumLuaCharacters)
                            throw new InvalidDataException("The source Lua metadata exceeds the parsing limit.");
                        additionalLua.Add(text);
                    }
                    else if (extension.Equals(".manifest", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = Path.GetFileName(file);
                        if (!ManifestFilePattern.IsMatch(name)) continue;
                        if (new FileInfo(file).Length > MaximumManifestBytes - copiedBytes)
                            throw new InvalidDataException("The source manifests exceed the preparation limit.");
                        copiedBytes += await CopyBoundedAsync(file, Path.Combine(snapshotDirectory, name), MaximumManifestBytes - copiedBytes, cancellationToken).ConfigureAwait(false);
                        manifestNames.Add(name);
                    }
                    else if (extension.Equals(".key", StringComparison.OrdinalIgnoreCase))
                    {
                        keyBytes += new FileInfo(file).Length;
                        if (keyBytes > DownloadPreparationReader.MaximumLuaCharacters)
                            throw new InvalidDataException("The source depot keys exceed the parsing limit.");
                        foreach (var line in await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false))
                        {
                            if (++keyEntries > DownloadPreparationReader.MaximumEntries)
                                throw new InvalidDataException("Too many source depot key entries.");
                            var parts = line.Split(';', 2);
                            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var depotId)
                                && depotId > 0 && parts[1].Trim() is { Length: >= 8 and <= 128 } key && key.All(Uri.IsHexDigit))
                                keys.TryAdd(depotId, key);
                        }
                    }
                }
            }
            var catalog = DownloadPreparationReader.Read(lua, additionalLua, manifestNames, keys);
            if (catalog.Depots.Count == 0) return new(false, $"{source} returned no downloadable depot manifests for App {appId}.");
            await File.WriteAllTextAsync(Path.Combine(snapshotDirectory, $"{appId}.lua"), lua, cancellationToken).ConfigureAwait(false);
            // Public app-info adds display labels only. The private source catalog, keys and
            // manifest IDs remain authoritative for the selected download and every resume.
            if (_depotMetadata is not null) progress?.Report("Loading optional Steam depot information…");
            var displayDepots = _depotMetadata is null
                ? SteamDepotMetadataReader.Enrich(catalog.Depots, new Dictionary<int, SteamDepotMetadata>())
                : await _depotMetadata.EnrichAsync(appId, catalog.Depots, cancellationToken).ConfigureAwait(false);
            var plan = new PreparedGameDownload(Guid.NewGuid(), appId, source, displayDepots);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_prepared.TryAdd(plan.Id, new PreparedSnapshot(plan, catalog, snapshotDirectory)))
                throw new InvalidOperationException("The download preparation could not be registered.");
            published = true;
            _logging.Add(Models.LogLevel.Info, "GameDownload", $"Prepared {catalog.Depots.Count} depot(s) from {source} for App {appId}.", appId);
            return new(true, $"Choose the depots and manifest versions provided by {source}.", plan);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or HttpRequestException or RegexMatchTimeoutException or OperationCanceledException)
        { return new(false, $"The source depots could not be prepared: {exception.Message}"); }
        finally
        {
            DeletePreparedDirectory(importDirectory);
            if (!published)
            {
                DeletePreparedDirectory(snapshotDirectory);
                _preparationSlots.Release();
            }
            if (gateEntered) gate.Release();
        }
    }

    public async Task<RyuuGameDownloadResult> DownloadPreparedAsync(PreparedGameDownload plan,
        IReadOnlyList<CachedDepotManifest> selectedDepots, string targetFolder,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan is null) return new(false, "Select a source before choosing its depots.");
        if (!_prepared.TryGetValue(plan.Id, out var snapshot) || snapshot.Plan.AppId != plan.AppId || snapshot.Plan.Source != plan.Source)
            return new(false, "This source selection has expired. Select the source again to prepare its depots.");
        lock (snapshot.Sync)
        {
            if (snapshot.Discarded) return new(false, "This source selection has been closed. Select the source again.");
            snapshot.ActiveUsers++;
        }
        try
        {
            IReadOnlyList<RyuuDepotInfo> depots;
            try { depots = DownloadPreparationReader.Select(snapshot.Catalog, selectedDepots); }
            catch (ArgumentException exception) { return new(false, exception.Message); }
            if (string.IsNullOrWhiteSpace(targetFolder) || !Path.IsPathFullyQualified(targetFolder))
                return new(false, "Choose an absolute download location.");
            var canonicalTarget = Path.GetFullPath(targetFolder);
            var sessionDirectory = DepotResumeStateStore.SessionDirectory(_workFolder, plan.AppId, canonicalTarget);
            var gate = _sessionGates.GetOrAdd(sessionDirectory, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var storage = DownloadStorageGuard.Check(canonicalTarget);
                if (!storage.CanDownload) return new(false, storage.Message);
                progress?.Report($"Saving {depots.Count} selected depot manifest(s) for resume…");
                await PrepareResumeSessionAsync(plan.AppId, canonicalTarget, depots,
                    snapshot.Directory, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var ddPath = await EnsureDepotDownloaderModAsync(progress, cancellationToken).ConfigureAwait(false);
                if (ddPath is null) return new(false, "Could not find or download DepotDownloaderMod.");
                Directory.CreateDirectory(canonicalTarget);
                return await RunDepotDownloaderModAsync(ddPath, plan.AppId, depots, sessionDirectory,
                    canonicalTarget, progress, cancellationToken).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        finally
        {
            lock (snapshot.Sync)
            {
                snapshot.ActiveUsers--;
                if (snapshot.Discarded && snapshot.ActiveUsers == 0) ReleasePreparedSnapshot(snapshot);
            }
        }
    }

    public void DiscardPreparedDownload(Guid id)
    {
        if (!_prepared.TryRemove(id, out var snapshot)) return;
        lock (snapshot.Sync)
        {
            snapshot.Discarded = true;
            if (snapshot.ActiveUsers == 0) ReleasePreparedSnapshot(snapshot);
        }
    }

    private void ReleasePreparedSnapshot(PreparedSnapshot snapshot)
    {
        DeletePreparedDirectory(snapshot.Directory);
        _preparationSlots.Release();
    }

    private static void DeletePreparedDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<long> CopyBoundedAsync(string source, string destination, long maximumBytes, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return await CopyBoundedAsync(input, destination, maximumBytes, token).ConfigureAwait(false);
    }

    private static async Task<long> CopyBoundedAsync(Stream input, string destination, long maximumBytes, CancellationToken token)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            copied += read;
            if (copied > maximumBytes) throw new InvalidDataException("A manifest exceeds the preparation limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        return copied;
    }

    public Task<RyuuGameDownloadResult> DownloadGameAsync(int appId, string targetFolder,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        DownloadGameAsync(appId, targetFolder, ManifestSource.Ryuu, progress, cancellationToken);

    public async Task<RyuuGameDownloadResult> DownloadGameAsync(int appId, string targetFolder, ManifestSource source,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var preparation = await PrepareDownloadAsync(appId, source, progress, cancellationToken).ConfigureAwait(false);
        if (!preparation.Succeeded || preparation.Plan is null) return new(false, preparation.Message);
        try
        {
            var selected = preparation.Plan.Depots.Select(depot => new CachedDepotManifest(depot.DepotId, depot.DefaultManifestId)).ToArray();
            return await DownloadPreparedAsync(preparation.Plan, selected, targetFolder, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { DiscardPreparedDownload(preparation.Plan.Id); }
    }

    /// <summary>Writes the "<depot>;<key>" depot-keys file DepotDownloaderMod reads via -depotkeys.</summary>
    private static async Task WriteDepotKeysAsync(
        string appWorkDir, int appId, IReadOnlyList<RyuuDepotInfo> depots, CancellationToken ct)
    {
        var keyFilePath = Path.Combine(appWorkDir, $"{appId}.key");
        var keyLines = depots
            .Where(d => !string.IsNullOrWhiteSpace(d.DecryptionKey))
            .Select(d => $"{d.DepotId};{d.DecryptionKey}")
            .ToList();

        if (keyLines.Count == 0)
        {
            try { File.Delete(keyFilePath); } catch { }
            return;
        }

        await File.WriteAllLinesAsync(keyFilePath, keyLines, ct);
    }

    public async Task<RyuuGameDownloadResult> ResumeDownloadAsync(
        int appId, string targetFolder,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (appId <= 0) return new(false, "Choose a valid game to resume.");
        var storage = DownloadStorageGuard.Check(targetFolder);
        if (!storage.CanDownload) return new(false, storage.Message);
        var canonicalTarget = Path.GetFullPath(targetFolder);
        var sessionDirectory = DepotResumeStateStore.SessionDirectory(_workFolder, appId, canonicalTarget);
        var gate = _sessionGates.GetOrAdd(sessionDirectory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A missing or invalid state is not a legacy job: a source cache may already contain
            // another build. Resuming must never replace the user's version with that cache.
            var pinned = DepotResumeStateStore.Read(sessionDirectory, appId, canonicalTarget);
            if (pinned is null)
                return new(false, "The saved depot selection is missing or invalid. Existing game files were kept; start a new download to explicitly select the source and manifest versions.");
            var keys = ReadDepotKeys(sessionDirectory, appId);
            var depots = pinned.Depots.Select(depot => new RyuuDepotInfo(depot.DepotId, depot.ManifestId,
                keys.GetValueOrDefault(depot.DepotId, string.Empty))).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            var ddPath = await EnsureDepotDownloaderModAsync(progress, cancellationToken).ConfigureAwait(false);
            if (ddPath is null) return new(false, "Could not find DepotDownloaderMod.");
            _logging.Add(Models.LogLevel.Info, "GameDownload",
                $"Resuming {depots.Length} pinned depot(s) for App {appId}.", appId);
            progress?.Report($"Checking existing files against {depots.Length} saved depot manifest(s)…");
            Directory.CreateDirectory(canonicalTarget);
            return await RunDepotDownloaderModAsync(ddPath, appId, depots, sessionDirectory, canonicalTarget, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<string> PrepareResumeSessionAsync(int appId, string targetFolder,
        IReadOnlyList<RyuuDepotInfo> depots, string sourceDirectory, CancellationToken cancellationToken)
    {
        var directory = DepotResumeStateStore.SessionDirectory(_workFolder, appId, targetFolder);
        var staging = directory + ".stage-" + Guid.NewGuid().ToString("N");
        var backup = directory + ".previous-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            long copiedBytes = 0;
            foreach (var depot in depots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = $"{depot.DepotId}_{depot.ManifestId}.manifest";
                var source = Path.Combine(sourceDirectory, fileName);
                // Copy only this prepared source snapshot. A Lua-only source still pins the exact
                // manifest ID; the tool may retrieve that ID from Steam, never a newer default.
                if (File.Exists(source))
                    copiedBytes += await CopyBoundedAsync(source, Path.Combine(staging, fileName), MaximumManifestBytes - copiedBytes, cancellationToken).ConfigureAwait(false);
            }
            await WriteDepotKeysAsync(staging, appId, depots, cancellationToken).ConfigureAwait(false);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(staging))
            {
                await using var input = File.OpenRead(file);
                hashes.Add(Path.GetFileName(file), Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)));
            }
            await DepotResumeStateStore.WriteAsync(staging,
                new DepotResumeState(appId, Path.GetFullPath(targetFolder),
                    depots.Select(depot => new CachedDepotManifest(depot.DepotId, depot.ManifestId)).ToArray(), hashes), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // No cancellation between the two renames: publish files, keys and pins together.
            // The caller holds this session's gate until its tool exits.
            if (Directory.Exists(directory)) Directory.Move(directory, backup);
            try { Directory.Move(staging, directory); }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(directory)) Directory.Move(backup, directory);
                throw;
            }
            DeletePreparedDirectory(backup);
            return directory;
        }
        finally { DeletePreparedDirectory(staging); }
    }

    private async Task<RyuuGameDownloadResult> RunDepotDownloaderModAsync(
        string ddPath, int appId, IReadOnlyList<RyuuDepotInfo> depots,
        string appWorkDir, string targetFolder,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var failedDepots = new List<string>();
        var completedDepots = 0;
        long completedDepotsBytes = 0;
        var settings = _settings.Load();
        var rateArguments = new List<string>();
        var hasRateLimit = BundledModCapabilities.AddRateLimit(rateArguments, ddPath, settings.DownloadRateLimitMiB);
        if (settings.DownloadRateLimitMiB > 0 && !hasRateLimit)
            progress?.Report("The selected DepotDownloaderMod tool does not support the configured speed limit; this download will use the tool's normal speed.");

        var keyFile = Path.Combine(appWorkDir, $"{appId}.key");

        for (var index = 0; index < depots.Count; index++)
        {
            var depot = depots[index];
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Preparing depot {depot.DepotId} ({index + 1}/{depots.Count})...");

            var manifestFileName = $"{depot.DepotId}_{depot.ManifestId}.manifest";
            var manifestFilePath = Path.Combine(appWorkDir, manifestFileName);

            // ManifestHub is a free, always-on manifest API (one request per depot). When a depot's
            // .manifest is not on disk yet — a fresh job, or a source that only shipped the Lua — pull
            // it from there so DepotDownloaderMod gets an exact manifest instead of guessing from Steam.
            if (!File.Exists(manifestFilePath)
                && !string.IsNullOrWhiteSpace(depot.ManifestId)
                && depot.ManifestId.Any(char.IsAsciiDigit))
            {
                await TryFetchManifestHubManifestAsync(depot.DepotId, depot.ManifestId, manifestFilePath, progress, cancellationToken);
            }

            var args = new List<string>
            {
                "-app", appId.ToString(CultureInfo.InvariantCulture),
                "-depot", depot.DepotId.ToString(CultureInfo.InvariantCulture),
                "-manifest", depot.ManifestId,
                "-dir", Path.GetFullPath(targetFolder)
            };
            if (File.Exists(keyFile))
            {
                args.Add("-depotkeys");
                args.Add(keyFile);
            }
            if (File.Exists(manifestFilePath))
            {
                args.Add("-manifestfile");
                args.Add(manifestFilePath);
            }
            DepotDownloaderArgumentBuilder.AddTransferOptions(args, settings.DownloadConnections, settings.UseLancache);
            args.AddRange(rateArguments);
            string? stopSignalPath = null;
            if (BundledModCapabilities.SupportsOwnFork(ddPath))
            {
                stopSignalPath = Path.Combine(Path.GetFullPath(appWorkDir), $".steamy-stop-{Guid.NewGuid():N}.signal");
                args.AddRange(["-max-retries", "5", "-steamy-progress", "-steamy-cancel-file", stopSignalPath]);
            }
            else if (BundledModCapabilities.SupportsProgress(ddPath)) args.Add("-steamy-progress");
            // Reuse only chunks that match the saved manifest; repair interrupted writes and
            // fetch missing chunks without changing the version selected for this download.
            args.Add("-verify-all");

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = ddPath,
                    WorkingDirectory = appWorkDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
                foreach (var arg in args)
                    startInfo.ArgumentList.Add(arg);

                using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                if (!process.Start())
                {
                    failedDepots.Add($"Depot {depot.DepotId}: failed to start DepotDownloaderMod");
                    continue;
                }

                var (exitCode, stdoutLines, depotBytesDownloaded) = await RunProcessWithWatchdogAsync(
                    process, depot.DepotId, index + 1, depots.Count, completedDepotsBytes, Path.GetFullPath(targetFolder),
                    progress, cancellationToken, stopSignalPath);

                cancellationToken.ThrowIfCancellationRequested();
                var totalLine = stdoutLines.LastOrDefault(l => l.StartsWith("Total downloaded:", StringComparison.Ordinal));
                var failureReason = DepotDownloaderOutputParser.ExtractFailureReason(string.Join('\n', stdoutLines));
                var isFailure = exitCode != 0;

                // If exit code is 0 but 0 bytes were downloaded, only treat as failure if an error keyword was logged
                if (!isFailure && totalLine is not null && totalLine.StartsWith("Total downloaded: 0 bytes", StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(failureReason))
                        isFailure = true;
                }

                if (isFailure)
                {
                    var reason = failureReason ?? string.Join(" | ", stdoutLines.TakeLast(5));
                    failedDepots.Add($"Depot {depot.DepotId}: exit {exitCode} — {reason}");
                    _logging.Add(Models.LogLevel.Error, "GameDownload",
                        $"Depot {depot.DepotId} failed (exit {exitCode}): {reason}", appId);
                }
                else
                {
                    completedDepots++;
                    long parsedBytes = 0;
                    if (totalLine is not null)
                    {
                        var match = Regex.Match(totalLine, @"Total downloaded:\s*(\d+)\s*bytes", RegexOptions.IgnoreCase);
                        if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
                            parsedBytes = b;
                    }
                    completedDepotsBytes += (parsedBytes > 0 ? parsedBytes : depotBytesDownloaded);
                    progress?.Report(GameDownloadProgressMessage.Format(depot.DepotId, index + 1, depots.Count, 100, null, completedDepotsBytes));
                    _logging.Add(Models.LogLevel.Info, "GameDownload",
                        $"Depot {depot.DepotId} downloaded for App {appId}.", appId);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failedDepots.Add($"Depot {depot.DepotId}: {ex.Message}");
            }
            finally
            {
                if (stopSignalPath is not null) try { File.Delete(stopSignalPath); } catch (IOException) { }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (failedDepots.Count > 0 && completedDepots == 0)
            return new RyuuGameDownloadResult(false, $"All depots failed:\n{string.Join("\n", failedDepots)}");

        if (failedDepots.Count > 0)
            return new RyuuGameDownloadResult(false,
                $"{completedDepots}/{depots.Count} depots downloaded. Resume to repair the incomplete download. Failed:\n{string.Join("\n", failedDepots)}");

        return new RyuuGameDownloadResult(true, $"All {depots.Count} depots downloaded to {targetFolder}.");
    }

    /// <summary>
    /// ManifestHub (api.manifesthub2.filegear-sg.me) is a free, always-on manifest API: one request
    /// per depot returns the exact .manifest file. The app asks for it whenever the manifest is not
    /// already on disk, so a source that only ships the Lua still gets a precise manifest. No key is
    /// required to be present — without one the normal flow runs unchanged.
    /// </summary>
    private async Task TryFetchManifestHubManifestAsync(
        int depotId, string manifestId, string destPath, IProgress<string>? progress, CancellationToken ct)
    {
        string? key = null;
        try { key = await _credentials.ReadAsync("manifesthub-api-key"); } catch { }
        if (string.IsNullOrWhiteSpace(key)) key = _settings.Load().ManifestHubApiKey;
        if (string.IsNullOrWhiteSpace(key)) return; // No key configured: stay quiet and use the normal flow.

        var temporary = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            progress?.Report($"Fetching manifest {manifestId} for depot {depotId} from ManifestHub...");
            var url = "https://api.manifesthub2.filegear-sg.me/manifest"
                + $"?apikey={Uri.EscapeDataString(key)}"
                + $"&depotid={depotId.ToString(CultureInfo.InvariantCulture)}"
                + $"&manifestid={Uri.EscapeDataString(manifestId)}";

            using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return;

            const long maximumBytes = 128L * 1024 * 1024;
            if (resp.Content.Headers.ContentLength > maximumBytes) return;
            await using (var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await CopyBoundedAsync(input, temporary, maximumBytes, ct).ConfigureAwait(false);
            // A JSON body means the API answered with an error, not a manifest.
            using (var file = File.OpenRead(temporary))
                if (file.Length < 8 || file.ReadByte() is '{' or '[') return;

            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destPath, overwrite: true);
            _logging.Add(Models.LogLevel.Info, "GameDownload",
                $"ManifestHub supplied manifest {manifestId} for depot {depotId}.", depotId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Non-fatal: DepotDownloaderMod can still fetch the manifest from Steam itself.
            _logging.Add(Models.LogLevel.Debug, "GameDownload",
                $"ManifestHub manifest fetch for depot {depotId} failed: {ex.Message}", depotId);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(3);

    private static async Task<(int ExitCode, List<string> StdoutLines, long DepotBytesDownloaded)> RunProcessWithWatchdogAsync(
        Process process, int depotId, int depotIndex, int totalDepots, long completedDepotsBytes, string targetFolder,
        IProgress<string>? progress, CancellationToken cancellationToken, string? stopSignalPath)
    {
        const int maximumLogLines = 2_000;
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var tracker = new DownloadProgressTracker(DownloadByteSource.For(process, targetFolder));
        long lastDepotBytes = 0;
        var stalled = false;

        async Task ReadOutputAsync(StreamReader reader)
        {
            // Ask the Steamy fork to stop between requests, then bound the wait before killing.
            // The pipes are always drained before the job releases its slot.
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 8_192) line = line[..8_192];
                output.Enqueue(line);
                while (output.Count > maximumLogLines) output.TryDequeue(out _);
                if (tracker.ObserveLine(line) is null && !string.IsNullOrWhiteSpace(line)
                    && !cancellationToken.IsCancellationRequested)
                    progress?.Report(line);
            }
        }

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = watchdog.Token.Register(() =>
        {
            RequestProcessStop(process, stopSignalPath);
        });
        var readStdout = ReadOutputAsync(process.StandardOutput);
        var readStderr = ReadOutputAsync(process.StandardError);
        var ticker = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(DepotDownloaderService.ProgressInterval);
            var lastCpuSample = Stopwatch.GetTimestamp();
            var lastCpuActivity = lastCpuSample;
            var previousCpu = TimeSpan.Zero;
            try
            {
                while (await timer.WaitForNextTickAsync(watchdog.Token).ConfigureAwait(false))
                {
                    var snapshot = tracker.Snapshot();
                    if (snapshot.DownloadedBytes > 0) lastDepotBytes = snapshot.DownloadedBytes;
                    if ((snapshot.Percent is not null || snapshot.Phase.Length > 0) && !cancellationToken.IsCancellationRequested)
                        progress?.Report(GameDownloadProgressMessage.Format(depotId, depotIndex, totalDepots,
                            snapshot.Percent ?? -1, snapshot, completedDepotsBytes + lastDepotBytes));

                    // Manifest/chunk verification may read and hash a large file without writing
                    // or printing. CPU activity means the verifier is still working.
                    if (Stopwatch.GetElapsedTime(lastCpuSample).TotalSeconds >= 1)
                    {
                        lastCpuSample = Stopwatch.GetTimestamp();
                        try
                        {
                            var cpu = process.TotalProcessorTime;
                            if (cpu > previousCpu) lastCpuActivity = lastCpuSample;
                            previousCpu = cpu;
                        }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                    }

                    if (tracker.SecondsSinceActivity > StallTimeout.TotalSeconds
                        && Stopwatch.GetElapsedTime(lastCpuActivity) > StallTimeout)
                    {
                        stalled = true;
                        var message = "No download or verification activity for 3 minutes. Resume to retry the saved manifests.";
                        output.Enqueue(message);
                        progress?.Report(message);
                        watchdog.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(readStdout, readStderr).ConfigureAwait(false);
        }
        finally
        {
            watchdog.Cancel();
            await ticker.ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (stalled ? -1 : process.ExitCode, output.ToList(), lastDepotBytes);
    }

    private static void RequestProcessStop(Process process, string? signalPath)
    {
        if (signalPath is null) { TryKill(process); return; }
        try { using var signal = new FileStream(signalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _ = KillAfterGraceAsync(process);
    }

    private static async Task KillAfterGraceAsync(Process process)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            if (!process.HasExited) TryKill(process);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static readonly Regex ManifestFilePattern = new(
        @"^(\d+)_(\d+)\.manifest$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private static Dictionary<int, string> ReadDepotKeys(string directory, int appId)
    {
        var keys = new Dictionary<int, string>();
        var path = Path.Combine(directory, $"{appId}.key");
        if (!File.Exists(path)) return keys;
        if (new FileInfo(path).Length > DownloadPreparationReader.MaximumLuaCharacters)
            throw new InvalidDataException("The saved depot keys exceed the parsing limit.");
        var entries = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (++entries > DownloadPreparationReader.MaximumEntries)
                throw new InvalidDataException("Too many saved depot key entries.");
            var parts = line.Split(';', 2);
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var depotId) && depotId > 0
                && parts[1].Trim() is { Length: >= 8 and <= 128 } key && key.All(Uri.IsHexDigit))
                keys.TryAdd(depotId, key);
        }
        return keys;
    }

    private async Task<string?> EnsureDepotDownloaderModAsync(IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? exePath = null;
        if (Directory.Exists(_toolsFolder))
        {
            exePath = Directory.GetFiles(_toolsFolder, "DepotDownloader*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(BundledModCapabilities.SupportsOwnFork);
            if (exePath is not null)
                return exePath;
        }

        var fallbackDir = _fallbackToolsFolder;
        if (Directory.Exists(fallbackDir))
        {
            var fallbackExe = Directory.GetFiles(fallbackDir, "DepotDownloader*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(BundledModCapabilities.SupportsOwnFork);
            if (fallbackExe is not null)
                return fallbackExe;
        }

        progress?.Report("DepotDownloaderMod not found — downloading from GitHub...");
        var stagingDirectory = Path.Combine(Path.GetDirectoryName(fallbackDir)!, ".depot-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var releaseResponse = await _httpClient.GetAsync(GitHubReleasesApi, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!releaseResponse.IsSuccessStatusCode)
            {
                _logging.Add(Models.LogLevel.Error, "RyuuDownload", $"GitHub API returned {(int)releaseResponse.StatusCode}");
                return null;
            }

            if (releaseResponse.Content.Headers.ContentLength > 4L * 1024 * 1024)
                throw new InvalidDataException("The DepotDownloaderMod release metadata exceeds the size limit.");
            await using var releaseInput = await releaseResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var releaseOutput = new MemoryStream();
            var releaseBuffer = new byte[81920];
            int releaseRead;
            while ((releaseRead = await releaseInput.ReadAsync(releaseBuffer, ct).ConfigureAwait(false)) > 0)
            {
                if (releaseOutput.Length + releaseRead > 4L * 1024 * 1024)
                    throw new InvalidDataException("The DepotDownloaderMod release metadata exceeds the size limit.");
                releaseOutput.Write(releaseBuffer, 0, releaseRead);
            }
            using var doc = JsonDocument.Parse(releaseOutput.ToArray());
            var assets = doc.RootElement.GetProperty("assets");
            string? downloadUrl = null;
            string? assetName = null;
            string? assetDigest = null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && name.StartsWith("Steamy-", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    assetName = Path.GetFileName(name);
                    assetDigest = asset.TryGetProperty("digest", out var digest) ? digest.GetString() : null;
                    break;
                }
            }

            // GitHub's asset digest field is not populated on every release API response.
            // Steamy publishes the same SHA-256 in the release body; bind it to this exact asset.
            if (assetDigest is null && assetName is not null
                && doc.RootElement.TryGetProperty("body", out var releaseBody)
                && releaseBody.GetString() is { } notes)
            {
                var checksum = Regex.Match(notes,
                    $"(?im)^([0-9a-f]{{64}})\\s+{Regex.Escape(assetName)}\\s*$",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                if (checksum.Success) assetDigest = "sha256:" + checksum.Groups[1].Value;
            }

            if (downloadUrl is null || assetName is null || string.IsNullOrWhiteSpace(assetDigest)
                || !assetDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                _logging.Add(Models.LogLevel.Error, "RyuuDownload", "No verifiable Steamy release ZIP was found.");
                return null;
            }

            progress?.Report($"Downloading {assetName}...");
            // Keep incomplete downloads out of every executable search path. Installation goes
            // into writable app data, which also supports a read-only application directory.
            Directory.CreateDirectory(stagingDirectory);
            var archivePath = Path.Combine(stagingDirectory, assetName);
            using (var downloadResponse = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                downloadResponse.EnsureSuccessStatusCode();
                if (downloadResponse.Content.Headers.ContentLength > MaximumManifestBytes)
                    throw new InvalidDataException("The DepotDownloaderMod archive exceeds the size limit.");
                await using var stream = await downloadResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await CopyBoundedAsync(stream, archivePath, MaximumManifestBytes, ct).ConfigureAwait(false);
            }

            await using (var file = File.OpenRead(archivePath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                if (!actual.Equals(assetDigest[7..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Steamy release ZIP checksum does not match the release digest.");
            }

            progress?.Report("Extracting DepotDownloaderMod...");
            var extractionDirectory = Path.Combine(stagingDirectory, "files");
            var extracted = await Task.Run(() => ArchiveExtractor.Extract(archivePath, extractionDirectory,
                cancellationToken: ct), ct);
            if (!extracted.Succeeded) throw new InvalidDataException(extracted.Message);
            var stagedExecutable = Directory.EnumerateFiles(extractionDirectory, "DepotDownloaderMod.exe", SearchOption.AllDirectories)
                .FirstOrDefault(BundledModCapabilities.SupportsOwnFork);
            if (stagedExecutable is null) throw new InvalidDataException("The Steamy release does not contain a verified Steamy DepotDownloaderMod.");
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(fallbackDir);
            var installedDirectory = Path.Combine(fallbackDir, "release-" + Guid.NewGuid().ToString("N"));
            var relativeExecutable = Path.GetRelativePath(extractionDirectory, stagedExecutable);
            Directory.Move(extractionDirectory, installedDirectory);
            exePath = Path.Combine(installedDirectory, relativeExecutable);

            if (exePath is not null)
            {
                _logging.Add(Models.LogLevel.Info, "RyuuDownload", $"DepotDownloaderMod installed: {exePath}");
                progress?.Report("DepotDownloaderMod ready.");
            }

            return exePath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logging.Add(Models.LogLevel.Error, "RyuuDownload", $"Failed to download DepotDownloaderMod: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        foreach (var id in _prepared.Keys) DiscardPreparedDownload(id);
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}

/// <summary>
/// The compact progress message the per-depot DepotDownloaderMod runner reports, and the single
/// place that applies it to a job, so every page shows the same numbers.
/// </summary>
public static class GameDownloadProgressMessage
{
    public const string Prefix = "PROGRESS|";

    public static string Format(int depotId, int depotIndex, int depotCount, double percent, DepotDownloaderProgress? snapshot, long cumulativeDownloadedBytes = 0) =>
        string.Join('|',
            "PROGRESS",
            depotId.ToString(CultureInfo.InvariantCulture),
            depotIndex.ToString(CultureInfo.InvariantCulture),
            depotCount.ToString(CultureInfo.InvariantCulture),
            percent.ToString("0.00", CultureInfo.InvariantCulture),
            Clean(snapshot?.Downloaded),
            Clean(snapshot?.Total),
            Clean(snapshot?.Speed),
            Clean(snapshot?.Eta),
            Clean(snapshot?.CurrentFile),
            (snapshot?.BytesPerSecond ?? 0).ToString("0", CultureInfo.InvariantCulture),
            snapshot?.EtaSeconds is { } eta ? eta.ToString("0", CultureInfo.InvariantCulture) : string.Empty,
            cumulativeDownloadedBytes.ToString(CultureInfo.InvariantCulture),
            snapshot?.Phase ?? "finalizing", (snapshot?.TotalBytes ?? 0).ToString(CultureInfo.InvariantCulture),
            (snapshot?.ContentBytes ?? 0).ToString(CultureInfo.InvariantCulture),
            snapshot?.TransferTotalBytes?.ToString(CultureInfo.InvariantCulture) ?? "",
            (snapshot?.ReusedBytes ?? 0).ToString(CultureInfo.InvariantCulture));

    /// <summary>Applies a progress message to the job. Returns false for plain status text.</summary>
    public static bool TryApply(Models.DownloadJob job, string message)
    {
        if (!message.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var parts = message.Split('|');
        if (parts.Length < 10
            || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return true;

        job.State = Models.DownloadJobState.Downloading;
        var hasDepots = int.TryParse(parts[2], out var index) & int.TryParse(parts[3], out var count) && count > 0;

        var depotDownloaded = parts[5];
        var depotTotal = parts[6];

        long cumulativeBytes = 0;
        if (parts.Length >= 13 && long.TryParse(parts[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCumulative))
        {
            cumulativeBytes = parsedCumulative;
        }

        var phase = parts.Length >= 18 ? parts[13] : "";
        job.Phase = phase;
        job.CurrentDepotDetail = count > 1 ? $"Depot {index}/{count} · {(double.IsFinite(percent) && percent >= 0 && phase == "downloading" ? percent.ToString("0.0", CultureInfo.InvariantCulture) + "%" : job.PhaseLabel)}"
            + (depotDownloaded.Length > 0 && depotTotal.Length > 0 ? $" · {depotDownloaded} / {depotTotal}" : "") : "";
        job.Status = hasDepots ? $"{job.PhaseLabel} · depot {index} of {count}" : job.PhaseLabel;
        job.Downloaded = cumulativeBytes > 0 ? DownloadFormat.Bytes(cumulativeBytes) : string.IsNullOrEmpty(depotDownloaded) ? "0 B" : depotDownloaded;
        // A runner starts one process per depot. Transfer totals and ETA therefore describe
        // the current depot; do not claim the unknown remainder of the whole game is known.
        job.TotalSize = count > 1 ? "Unknown · multiple depots" : depotTotal;
        job.TransferredBytes = cumulativeBytes;
        job.TransferTotalBytes = count <= 1 && parts.Length >= 18 && long.TryParse(parts[16], out var total) ? total : null;
        if (parts.Length >= 18 && long.TryParse(parts[14], out var install) && install > 0) job.InstallationBytes = install;
        job.ContentBytes = parts.Length >= 18 && long.TryParse(parts[15], out var content) ? content : 0;
        job.ReusedBytes = parts.Length >= 18 && long.TryParse(parts[17], out var reused) ? reused : 0;
        var rate = parts.Length >= 11 && double.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRate)
            && double.IsFinite(parsedRate) && parsedRate >= 0 ? parsedRate : 0;
        job.BytesPerSecond = rate;
        job.Speed = parts[7];
        job.Eta = parts[8].Length > 0 && count > 1 ? $"{parts[8]} · this depot" : parts[8];
        job.EtaSeconds = count <= 1 && parts.Length >= 12 && double.TryParse(parts[11], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) && seconds >= 0 ? seconds : null;
        if (parts[9].Length > 0) job.CurrentFile = parts[9];
        if (count <= 1 && double.IsFinite(percent) && percent >= 0) job.Progress = Math.Min(99.9, percent);
        job.HasMeasuredProgress = count <= 1 && double.IsFinite(percent) && percent >= 0;
        return true;
    }

    private static string Clean(string? value) => value?.Replace('|', '/') ?? string.Empty;
}
