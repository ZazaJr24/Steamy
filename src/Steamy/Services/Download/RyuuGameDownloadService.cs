using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;

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
        catch (Exception ex)
        {
            return new RyuuGameDownloadResult(false, $"Failed to extract archive: {ex.Message}");
        }

        var depots = ParseLua(luaContent);
        if (depots.Count == 0)
            return new RyuuGameDownloadResult(false, "No depots with keys and manifests found in the Lua script.");

        _logging.Add(Models.LogLevel.Info, "RyuuDownload", $"Parsed {depots.Count} depot(s) for App {appId}.", appId);

        await WriteDepotKeysAsync(appWorkDir, appId, depots, cancellationToken);

        Directory.CreateDirectory(targetFolder);
        return await RunDepotDownloaderModAsync(ddPath, appId, depots, appWorkDir, targetFolder, progress, cancellationToken);
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
        return await RunDepotDownloaderModAsync(ddPath, appId, depots, appWorkDir, targetFolder, progress, cancellationToken);
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

        var appWorkDir = Path.Combine(_workFolder, appId.ToString(CultureInfo.InvariantCulture));
        if (!Directory.Exists(appWorkDir))
            return new RyuuGameDownloadResult(false, "No cached manifests found — start a fresh download.");

        var depots = BuildDepotsFromDirectory(string.Empty, appWorkDir);
        if (depots.Count == 0)
            return new RyuuGameDownloadResult(false, "No cached depots found — start a fresh download.");

        _logging.Add(Models.LogLevel.Info, "GameDownload",
            $"Resuming with {depots.Count} cached depot(s) for App {appId}.", appId);
        progress?.Report($"Resuming — found {depots.Count} cached depot(s)");

        Directory.CreateDirectory(targetFolder);
        return await RunDepotDownloaderModAsync(ddPath, appId, depots, appWorkDir, targetFolder, progress, cancellationToken);
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

            var args = new List<string>
            {
                "-app", appId.ToString(CultureInfo.InvariantCulture),
                "-depot", depot.DepotId.ToString(CultureInfo.InvariantCulture),
                "-manifest", depot.ManifestId,
                "-depotkeys", keyFile,
                "-dir", Path.GetFullPath(targetFolder)
            };
            if (File.Exists(manifestFilePath))
            {
                args.Add("-manifestfile");
                args.Add(manifestFilePath);
            }
            DepotDownloaderArgumentBuilder.AddTransferOptions(args, settings.DownloadConnections, settings.UseLancache);

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

        if (failedDepots.Count > 0 && completedDepots == 0)
            return new RyuuGameDownloadResult(false, $"All depots failed:\n{string.Join("\n", failedDepots)}");

        if (failedDepots.Count > 0)
            return new RyuuGameDownloadResult(true,
                $"{completedDepots}/{depots.Count} depots downloaded. Failed:\n{string.Join("\n", failedDepots)}");

        return new RyuuGameDownloadResult(true, $"All {depots.Count} depots downloaded to {targetFolder}.");
    }

    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(3);

    private static async Task<(int ExitCode, List<string> StdoutLines, long DepotBytesDownloaded)> RunProcessWithWatchdogAsync(
        Process process, int depotId, int depotIndex, int totalDepots, long completedDepotsBytes, string targetFolder,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var stdoutLines = new List<string>();
        var tracker = new DownloadProgressTracker(DownloadByteSource.For(process, targetFolder));
        long lastDepotBytes = 0;

        var readStdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                stdoutLines.Add(line);
                if (tracker.ObserveLine(line) is null && !string.IsNullOrWhiteSpace(line))
                    progress?.Report(line);
            }
        }, cancellationToken);

        var readStderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var reg = watchdog.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });

        // Progress ticks every 100 ms; the process only counts as stuck when it neither prints
        // nor writes anything, so one large file can no longer trip the watchdog.
        var ticker = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(DepotDownloaderService.ProgressInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(watchdog.Token))
                {
                    var snapshot = tracker.Snapshot();
                    if (snapshot.DownloadedBytes > 0)
                        lastDepotBytes = snapshot.DownloadedBytes;

                    if (snapshot.Percent is { } percent)
                    {
                        var cumulativeBytes = completedDepotsBytes + lastDepotBytes;
                        progress?.Report(GameDownloadProgressMessage.Format(depotId, depotIndex, totalDepots, percent, snapshot, cumulativeBytes));
                    }

                    if (tracker.SecondsSinceActivity > StallTimeout.TotalSeconds)
                    {
                        progress?.Report("No download activity for 3 minutes — stopping the stuck process.");
                        watchdog.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        try
        {
            await process.WaitForExitAsync(watchdog.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Watchdog killed the process
        }
        watchdog.Cancel();
        await ticker;
        await readStdout;
        _ = await readStderr;

        var exitCode = process.HasExited ? process.ExitCode : -1;
        return (exitCode, stdoutLines, lastDepotBytes);
    }

    private static readonly Regex ManifestFilePattern = new(
        @"^(\d+)_(\d+)\.manifest$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private IReadOnlyList<RyuuDepotInfo> BuildDepotsFromDirectory(string luaContent, string? workDir)
    {
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
            return [];

        var keys = new Dictionary<int, string>();
        foreach (Match m in AddAppIdPattern.Matches(luaContent))
        {
            var depotId = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (m.Groups[2].Success)
                keys[depotId] = m.Groups[2].Value;
        }

        var result = new List<RyuuDepotInfo>();
        foreach (var file in Directory.GetFiles(workDir, "*.manifest"))
        {
            var match = ManifestFilePattern.Match(Path.GetFileName(file));
            if (!match.Success) continue;
            var depotId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var manifestId = match.Groups[2].Value;
            if (keys.TryGetValue(depotId, out var key) && !string.IsNullOrEmpty(key))
                result.Add(new RyuuDepotInfo(depotId, manifestId, key));
        }

        // Also try .key file in work dir
        if (result.Count == 0)
        {
            foreach (var keyFile in Directory.GetFiles(workDir, "*.key"))
            {
                foreach (var line in File.ReadAllLines(keyFile))
                {
                    var parts = line.Split(';', 2);
                    if (parts.Length == 2 && int.TryParse(parts[0], out var depotId))
                        keys.TryAdd(depotId, parts[1].Trim());
                }
            }
            foreach (var file in Directory.GetFiles(workDir, "*.manifest"))
            {
                var match = ManifestFilePattern.Match(Path.GetFileName(file));
                if (!match.Success) continue;
                var depotId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var manifestId = match.Groups[2].Value;
                if (keys.TryGetValue(depotId, out var key) && !string.IsNullOrEmpty(key))
                    result.Add(new RyuuDepotInfo(depotId, manifestId, key));
            }
        }

        return result;
    }

    private async Task<string?> EnsureDepotDownloaderModAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_toolsFolder);

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

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    assetName = name;
                    break;
                }
            }

            if (downloadUrl is null || assetName is null)
            {
                _logging.Add(Models.LogLevel.Error, "RyuuDownload", "No archive asset found in latest release.");
                return null;
            }

            progress?.Report($"Downloading {assetName}...");
            var archivePath = Path.Combine(_toolsFolder, assetName);
            await using (var stream = await _httpClient.GetStreamAsync(downloadUrl, ct))
            await using (var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.CopyToAsync(fs, ct);
            }

            progress?.Report("Extracting DepotDownloaderMod...");
            using (var archive = ArchiveFactory.OpenArchive(archivePath))
            {
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    entry.WriteToDirectory(_toolsFolder, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                }
            }

            try { File.Delete(archivePath); } catch { }

            exePath = Directory.GetFiles(_toolsFolder, "DepotDownloader*.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (exePath is not null)
            {
                _logging.Add(Models.LogLevel.Info, "RyuuDownload", $"DepotDownloaderMod installed: {exePath}");
                progress?.Report("DepotDownloaderMod ready.");
            }

            return exePath;
        }
        catch (Exception ex)
        {
            _logging.Add(Models.LogLevel.Error, "RyuuDownload", $"Failed to download DepotDownloaderMod: {ex.Message}");
            return null;
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
        var overall = hasDepots ? ((index - 1) * 100.0 + percent) / count : percent;
        job.Progress = Math.Max(job.Progress, overall);

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
        return true;
    }

    private static string Clean(string? value) => value?.Replace('|', '/') ?? string.Empty;
}
