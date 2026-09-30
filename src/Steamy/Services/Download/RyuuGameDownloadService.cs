using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Steamy.Services;

public sealed record RyuuDepotInfo(int DepotId, string ManifestId, string DecryptionKey);

public sealed record RyuuGameDownloadResult(bool Succeeded, string Message);

public interface IRyuuGameDownloadService
{
    IReadOnlyList<RyuuDepotInfo> ParseLua(string luaContent);

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
    // Lua manifests come in several dialects. Hubcap, DepotBox and Ryuu all emit
    //   addappid(<depot>, 1, "<key>")
    //   setManifestid(<depot>, "<manifest>")
    //   setManifestid(<depot>, "<manifest>", <size>)     <-- DepotBox adds a third argument
    // so every trailing argument after the one we need is allowed here. Requiring an exact
    // closing parenthesis right after the manifest id is what broke DepotBox and Hubcap.
    private static readonly Regex AddAppIdPattern = new(
        @"addappid\(\s*(\d+)(?:\s*,\s*\d+\s*,\s*""([a-fA-F0-9]{8,})"")?[^)]*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SetManifestPattern = new(
        @"setManifestid\(\s*(\d+)\s*,\s*""(\d+)""[^)]*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string GitHubReleasesApi = "https://api.github.com/repos/SteamAutoCracks/DepotDownloaderMod/releases/latest";

    private readonly ISettingsService _settings;
    private readonly ISecureCredentialService _credentials;
    private readonly IRyuuSecureDownloadService _ryuuDownload;
    private readonly IManifestSourceService _manifestSource;
    private readonly ILoggingService _logging;
    private readonly HttpClient _httpClient;
    private readonly string _toolsFolder;
    private readonly string _workFolder;

    public RyuuGameDownloadService(
        ISettingsService settings,
        ISecureCredentialService credentials,
        IRyuuSecureDownloadService ryuuDownload,
        IManifestSourceService manifestSource,
        ILoggingService logging)
    {
        _settings = settings;
        _credentials = credentials;
        _ryuuDownload = ryuuDownload;
        _manifestSource = manifestSource;
        _logging = logging;
        _httpClient = new HttpClient(StableDnsHandler.Create()) { Timeout = TimeSpan.FromMinutes(5) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steamy");
        var appTools = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "DepotDownloaderMod");
        _toolsFolder = appTools;
        _workFolder = Path.Combine(appData, "ryuu-workdir");
    }

    public IReadOnlyList<RyuuDepotInfo> ParseLua(string luaContent)
    {
        if (string.IsNullOrWhiteSpace(luaContent)) return [];

        var depots = new Dictionary<int, (string Key, string Manifest)>();

        foreach (Match m in AddAppIdPattern.Matches(luaContent))
        {
            var depotId = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var key = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
            depots[depotId] = (key, string.Empty);
        }

        foreach (Match m in SetManifestPattern.Matches(luaContent))
        {
            var depotId = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var manifestId = m.Groups[2].Value;
            if (depots.TryGetValue(depotId, out var existing))
                depots[depotId] = (existing.Key, manifestId);
            else
                depots[depotId] = (string.Empty, manifestId);
        }

        // A depot without a manifest id cannot be requested at all. A depot without a key can
        // still be downloaded when the app does not encrypt it, so only the manifest is required.
        return depots
            .Where(kv => !string.IsNullOrEmpty(kv.Value.Manifest))
            .Select(kv => new RyuuDepotInfo(kv.Key, kv.Value.Manifest, kv.Value.Key))
            .ToList();
    }

    public async Task<RyuuGameDownloadResult> DownloadGameAsync(
        int appId,
        string targetFolder,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? authCode = null;
        try { authCode = await _credentials.ReadAsync("ryuu-auth-key"); } catch { }
        if (string.IsNullOrWhiteSpace(authCode))
            authCode = _settings.Load().RyuuApiKey;

        if (string.IsNullOrWhiteSpace(authCode))
            return new RyuuGameDownloadResult(false, "No Ryuu auth code configured. Set it in Settings.");

        var ddPath = await EnsureDepotDownloaderModAsync(progress, cancellationToken);
        if (ddPath is null)
            return new RyuuGameDownloadResult(false, "Could not find or download DepotDownloaderMod.");

        progress?.Report("Downloading manifest archive from Ryuu...");
        var downloadProgress = new Progress<RyuuSecureDownloadProgress>(p =>
            progress?.Report($"Downloading archive... {p.Downloaded} / {p.Total} ({p.Percent:F0}%)"));

        var zipResult = await _ryuuDownload.DownloadAsync(appId, authCode, $"App {appId}", downloadProgress, cancellationToken);
        if (!zipResult.Succeeded)
            return new RyuuGameDownloadResult(false, $"Failed to download Ryuu archive: {zipResult.Message}");

        progress?.Report("Extracting manifests and keys...");
        var appWorkDir = Path.Combine(_workFolder, appId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(appWorkDir);

        string luaContent;
        try
        {
            using var zip = ZipFile.OpenRead(zipResult.ArchivePath);

            var luaEntry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            if (luaEntry is null)
                return new RyuuGameDownloadResult(false, "No Lua script found in the Ryuu archive.");

            using (var reader = new StreamReader(luaEntry.Open()))
                luaContent = await reader.ReadToEndAsync(cancellationToken);

            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)))
            {
                var destPath = Path.Combine(appWorkDir, entry.Name);
                entry.ExtractToFile(destPath, overwrite: true);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new RyuuGameDownloadResult(false, $"Failed to extract archive: {ex.Message}");
        }

        await File.WriteAllTextAsync(Path.Combine(appWorkDir, $"{appId}.lua"), luaContent, cancellationToken);
        var depots = ParseLua(luaContent);
        if (depots.Count == 0)
            return new RyuuGameDownloadResult(false, "No depots with keys and manifests found in the Lua script.");

        _logging.Add(Models.LogLevel.Info, "RyuuDownload", $"Parsed {depots.Count} depot(s) for App {appId}.", appId);

        await WriteDepotKeysAsync(appWorkDir, appId, depots, cancellationToken);

        Directory.CreateDirectory(targetFolder);
        var sessionDirectory = await PrepareResumeSessionAsync(appId, targetFolder, depots, appWorkDir, cancellationToken);
        return await RunDepotDownloaderModAsync(ddPath, appId, depots, sessionDirectory, targetFolder, progress, cancellationToken);
    }

    public async Task<RyuuGameDownloadResult> DownloadGameAsync(
        int appId,
        string targetFolder,
        ManifestSource source,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (source == ManifestSource.Ryuu)
            return await DownloadGameAsync(appId, targetFolder, progress, cancellationToken);

        var ddPath = await EnsureDepotDownloaderModAsync(progress, cancellationToken);
        if (ddPath is null)
            return new RyuuGameDownloadResult(false, "Could not find or download DepotDownloaderMod.");

        var manifestResult = await _manifestSource.DownloadManifestsAsync(source, appId, progress, cancellationToken);
        if (!manifestResult.Succeeded || string.IsNullOrWhiteSpace(manifestResult.LuaContent))
            return new RyuuGameDownloadResult(false, manifestResult.Message);

        var depots = ParseLua(manifestResult.LuaContent);
        if (depots.Count == 0)
            depots = BuildDepotsFromDirectory(manifestResult.LuaContent, manifestResult.WorkDirectory);
        if (depots.Count == 0)
            return new RyuuGameDownloadResult(false,
                $"{source} delivered a Lua script without any usable depot, so there is nothing to download for App {appId}.");

        _logging.Add(Models.LogLevel.Info, "GameDownload",
            $"Parsed {depots.Count} depot(s) for App {appId} from {source}.", appId);

        var appWorkDir = manifestResult.WorkDirectory ?? _workFolder;
        await WriteDepotKeysAsync(appWorkDir, appId, depots, cancellationToken);

        Directory.CreateDirectory(targetFolder);
        var sessionDirectory = await PrepareResumeSessionAsync(appId, targetFolder, depots, appWorkDir, cancellationToken);
        return await RunDepotDownloaderModAsync(ddPath, appId, depots, sessionDirectory, targetFolder, progress, cancellationToken);
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
        var ddPath = await EnsureDepotDownloaderModAsync(progress, cancellationToken);
        if (ddPath is null)
            return new RyuuGameDownloadResult(false, "Could not find DepotDownloaderMod.");

        var sessionDirectory = DepotResumeStateStore.SessionDirectory(_workFolder, appId, targetFolder);
        var pinned = DepotResumeStateStore.Read(sessionDirectory, appId, targetFolder);
        IReadOnlyList<RyuuDepotInfo> depots;
        if (pinned is not null)
        {
            var keys = ReadDepotKeys(sessionDirectory, TryReadLua(sessionDirectory, appId) ?? string.Empty);
            depots = pinned.Depots.Select(depot => new RyuuDepotInfo(depot.DepotId, depot.ManifestId,
                keys.GetValueOrDefault(depot.DepotId, string.Empty))).ToArray();
        }
        else
        {
            // Migrate older jobs from one coherent source cache. Combining different caches can
            // silently mix old and new game versions and discard keys required by some depots.
            var appWorkDirs = FindManifestWorkDirs(appId).Where(Directory.Exists)
                .OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
            depots = [];
            string? selectedDirectory = null;
            foreach (var directory in appWorkDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lua = TryReadLua(directory, appId) ?? string.Empty;
                var candidates = ParseLua(lua);
                if (candidates.Count == 0) candidates = BuildDepotsFromDirectory(lua, directory);
                if (candidates.Count == 0) continue;
                depots = candidates;
                selectedDirectory = directory;
                break;
            }

            if (selectedDirectory is null)
                return new RyuuGameDownloadResult(false,
                    "No cached manifests found. Start a new download to select a manifest version.");
            sessionDirectory = await PrepareResumeSessionAsync(appId, targetFolder, depots,
                selectedDirectory, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _logging.Add(Models.LogLevel.Info, "GameDownload",
            $"Resuming {depots.Count} pinned depot(s) for App {appId}.", appId);
        progress?.Report($"Checking existing files against {depots.Count} saved depot manifest(s)…");
        Directory.CreateDirectory(targetFolder);
        return await RunDepotDownloaderModAsync(ddPath, appId, depots, sessionDirectory, targetFolder, progress, cancellationToken);
    }

    private async Task<string> PrepareResumeSessionAsync(int appId, string targetFolder,
        IReadOnlyList<RyuuDepotInfo> depots, string sourceDirectory, CancellationToken cancellationToken)
    {
        var directory = DepotResumeStateStore.SessionDirectory(_workFolder, appId, targetFolder);
        Directory.CreateDirectory(directory);
        foreach (var depot in depots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = $"{depot.DepotId}_{depot.ManifestId}.manifest";
            var source = Path.Combine(sourceDirectory, fileName);
            // Another cache is useful only when it has this exact pinned manifest version.
            if (!File.Exists(source))
                source = FindManifestWorkDirs(appId).Select(folder => Path.Combine(folder, fileName))
                    .FirstOrDefault(File.Exists);
            if (source is not null && File.Exists(source))
                File.Copy(source, Path.Combine(directory, fileName), overwrite: true);
        }
        await WriteDepotKeysAsync(directory, appId, depots, cancellationToken);
        await DepotResumeStateStore.WriteAsync(directory,
            new DepotResumeState(appId, Path.GetFullPath(targetFolder),
                depots.Select(depot => new CachedDepotManifest(depot.DepotId, depot.ManifestId)).ToArray()), cancellationToken);
        return directory;
    }

    /// <summary>
    /// Every folder a download may have left its manifests in: Ryuu's own work directory and the
    /// shared manifest work directory with one sub-folder per source (Zaza, Hubcap, DepotBox).
    /// A download resumed through a different source than it was started with still finds its data.
    /// </summary>
    private IEnumerable<string> FindManifestWorkDirs(int appId)
    {
        var app = appId.ToString(CultureInfo.InvariantCulture);
        yield return Path.Combine(_workFolder, app);

        var manifestRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steamy", "manifest-workdir");
        if (!Directory.Exists(manifestRoot)) yield break;

        foreach (var sourceDir in Directory.EnumerateDirectories(manifestRoot))
            yield return Path.Combine(sourceDir, app);
        yield return Path.Combine(manifestRoot, app);
    }

    private static string? TryReadLua(string folder, int appId)
    {
        try
        {
            var direct = Path.Combine(folder, $"{appId}.lua");
            if (File.Exists(direct))
            {
                var text = File.ReadAllText(direct);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }

            foreach (var path in Directory.EnumerateFiles(folder, "*.lua"))
            {
                var text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
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
                    process, depot.DepotId, index + 1, depots.Count, completedDepotsBytes, Path.GetFullPath(targetFolder), progress, cancellationToken);

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

        try
        {
            progress?.Report($"Fetching manifest {manifestId} for depot {depotId} from ManifestHub...");
            var url = "https://api.manifesthub2.filegear-sg.me/manifest"
                + $"?apikey={Uri.EscapeDataString(key)}"
                + $"&depotid={depotId.ToString(CultureInfo.InvariantCulture)}"
                + $"&manifestid={Uri.EscapeDataString(manifestId)}";

            using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            // A JSON body means the API answered with an error, not a manifest.
            if (bytes.Length < 8 || bytes[0] == (byte)'{' || bytes[0] == (byte)'[') return;

            await File.WriteAllBytesAsync(destPath, bytes, ct);
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
    }

    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(3);

    private static async Task<(int ExitCode, List<string> StdoutLines, long DepotBytesDownloaded)> RunProcessWithWatchdogAsync(
        Process process, int depotId, int depotIndex, int totalDepots, long completedDepotsBytes, string targetFolder,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        const int maximumLogLines = 2_000;
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var tracker = new DownloadProgressTracker(DownloadByteSource.For(process, targetFolder));
        long lastDepotBytes = 0;
        var stalled = false;

        async Task ReadOutputAsync(StreamReader reader)
        {
            // Cancellation kills the process, then both pipes are drained before the job can
            // release its slot. Readers must not outlive or access a disposed Process instance.
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
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
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
                    if (snapshot.Percent is { } percent && !cancellationToken.IsCancellationRequested)
                        progress?.Report(GameDownloadProgressMessage.Format(depotId, depotIndex, totalDepots,
                            percent, snapshot, completedDepotsBytes + lastDepotBytes));

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

    private static readonly Regex ManifestFilePattern = new(
        @"^(\d+)_(\d+)\.manifest$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private IReadOnlyList<RyuuDepotInfo> BuildDepotsFromDirectory(string luaContent, string? workDir)
    {
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir)) return [];
        var keys = ReadDepotKeys(workDir, luaContent);
        return Directory.EnumerateFiles(workDir, "*.manifest")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(file => ManifestFilePattern.Match(Path.GetFileName(file)))
            .Where(match => match.Success && int.TryParse(match.Groups[1].Value, out _))
            .Select(match => new RyuuDepotInfo(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                match.Groups[2].Value, keys.GetValueOrDefault(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), string.Empty)))
            .DistinctBy(depot => depot.DepotId)
            .ToArray();
    }

    private static Dictionary<int, string> ReadDepotKeys(string directory, string luaContent)
    {
        var keys = new Dictionary<int, string>();
        foreach (Match match in AddAppIdPattern.Matches(luaContent))
            if (match.Groups[2].Success && int.TryParse(match.Groups[1].Value, out var depotId))
                keys[depotId] = match.Groups[2].Value;

        foreach (var keyFile in Directory.EnumerateFiles(directory, "*.key"))
            foreach (var line in File.ReadLines(keyFile))
            {
                var parts = line.Split(';', 2);
                if (parts.Length == 2 && int.TryParse(parts[0], out var depotId)
                    && !string.IsNullOrWhiteSpace(parts[1]))
                    keys.TryAdd(depotId, parts[1].Trim());
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
                .FirstOrDefault();
            if (exePath is not null && File.Exists(exePath))
                return exePath;
        }

        var fallbackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy", "tools", "DepotDownloaderMod");
        if (Directory.Exists(fallbackDir))
        {
            var fallbackExe = Directory.GetFiles(fallbackDir, "DepotDownloader*.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (fallbackExe is not null && File.Exists(fallbackExe))
                return fallbackExe;
        }

        progress?.Report("DepotDownloaderMod not found — downloading from GitHub...");
        var stagingDirectory = Path.Combine(Path.GetDirectoryName(fallbackDir)!, ".depot-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var releaseResponse = await _httpClient.GetAsync(GitHubReleasesApi, ct);
            if (!releaseResponse.IsSuccessStatusCode)
            {
                _logging.Add(Models.LogLevel.Error, "RyuuDownload", $"GitHub API returned {(int)releaseResponse.StatusCode}");
                return null;
            }

            var json = await releaseResponse.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var assets = doc.RootElement.GetProperty("assets");
            string? downloadUrl = null;
            string? assetName = null;
            string? assetDigest = null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    assetName = Path.GetFileName(name);
                    assetDigest = asset.TryGetProperty("digest", out var digest) ? digest.GetString() : null;
                    break;
                }
            }

            if (downloadUrl is null || assetName is null)
            {
                _logging.Add(Models.LogLevel.Error, "RyuuDownload", "No archive asset found in latest release.");
                return null;
            }

            progress?.Report($"Downloading {assetName}...");
            // Keep incomplete downloads out of every executable search path. Installation goes
            // into writable app data, which also supports a read-only application directory.
            Directory.CreateDirectory(stagingDirectory);
            var archivePath = Path.Combine(stagingDirectory, assetName);
            await using (var stream = await _httpClient.GetStreamAsync(downloadUrl, ct))
            await using (var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.CopyToAsync(file, ct);

            if (assetDigest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
            {
                await using var file = File.OpenRead(archivePath);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                if (!actual.Equals(assetDigest[7..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("DepotDownloaderMod archive checksum does not match the release digest.");
            }

            progress?.Report("Extracting DepotDownloaderMod...");
            var extractionDirectory = Path.Combine(stagingDirectory, "files");
            var extracted = await Task.Run(() => ArchiveExtractor.Extract(archivePath, extractionDirectory,
                cancellationToken: ct), ct);
            if (!extracted.Succeeded) throw new InvalidDataException(extracted.Message);
            var stagedExecutable = Directory.EnumerateFiles(extractionDirectory, "DepotDownloader*.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (stagedExecutable is null) throw new InvalidDataException("The release archive contains no DepotDownloader executable.");
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

    public void Dispose() => _httpClient.Dispose();
}

/// <summary>
/// The compact progress message the per-depot DepotDownloaderMod runner reports, and the single
/// place that applies it to a job, so every page shows the same numbers.
/// </summary>
public static class GameDownloadProgressMessage
{
    private const string Prefix = "PROGRESS|";

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
            cumulativeDownloadedBytes.ToString(CultureInfo.InvariantCulture));

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

        if (hasDepots && count > 1)
        {
            // Every depot reports its own size, so the job can show the real amount of data the whole
            // download covers. Finished depots are added up, the running one is added on top.
            var depotBytes = DownloadFormat.TryParseSize(depotTotal);
            if (job.DepotsSeen != index)
            {
                if (job.DepotTotalBytes > 0) job.DepotBytesCompleted += job.DepotTotalBytes;
                job.DepotsSeen = index;
            }
            if (depotBytes > 0) job.DepotTotalBytes = depotBytes;

            if (cumulativeBytes > 0)
                job.Downloaded = DownloadFormat.Bytes(cumulativeBytes);
            else if (depotDownloaded.Length > 0)
                job.Downloaded = depotDownloaded;

            var exactTotal = job.DepotBytesCompleted + job.DepotTotalBytes;
            job.TotalSize = exactTotal > 0
                ? $"{DownloadFormat.Bytes(exactTotal)} in {count} depots"
                : $"{count} depots";

            if (depotDownloaded.Length > 0 && depotTotal.Length > 0)
                job.Status = $"Downloading depot {index} of {count}  ·  {depotDownloaded} / {depotTotal}";
            else
                job.Status = $"Downloading depot {index} of {count}";
        }
        else
        {
            if (depotDownloaded.Length > 0) job.Downloaded = depotDownloaded;
            if (depotTotal.Length > 0) job.TotalSize = depotTotal;
            job.Status = "Downloading";
        }

        // Prefer the measured, smoothed rate so the speed readout and the graph glide.
        var measuredRate = parts.Length >= 11
            && double.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRate)
                ? parsedRate
                : 0;
        if (measuredRate > 4096) job.Speed = DownloadFormat.Speed(measuredRate);
        else if (parts[7].Length > 0) job.Speed = parts[7];

        if (parts[8].Length > 0)
        {
            job.Eta = hasDepots && count > 1 ? $"{parts[8]} (depot {index}/{count})" : parts[8];
        }
        if (parts[9].Length > 0) job.CurrentFile = parts[9];
        if (parts.Length >= 12)
        {
            job.BytesPerSecond = double.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : 0;
            job.EtaSeconds = double.TryParse(parts[11], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
        }

        // The percentage uses the very same two numbers the size readout shows (downloaded / total),
        // so the bar, the GB text and the % can never disagree. A byte-based value is preferred; the
        // depot-averaged one is only a fallback while no total size is known yet.
        var downloadedBytes = cumulativeBytes > 0 ? cumulativeBytes : DownloadFormat.TryParseSize(job.Downloaded);
        var totalBytes = hasDepots && count > 1
            ? job.DepotBytesCompleted + job.DepotTotalBytes
            : DownloadFormat.TryParseSize(job.TotalSize);
        double overall = downloadedBytes > 0 && totalBytes > 0
            ? downloadedBytes * 100.0 / totalBytes
            : hasDepots && count > 0 ? ((index - 1) * 100.0 + percent) / count
            : percent;
        job.Progress = Math.Max(job.Progress, Math.Clamp(overall, 0, 100));
        return true;
    }

    private static string Clean(string? value) => value?.Replace('|', '/') ?? string.Empty;
}
