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
    private static readonly Regex AddAppIdPattern = new(
        @"addappid\(\s*(\d+)\s*(?:,\s*\d+\s*,\s*""([a-fA-F0-9]+)"")?\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SetManifestPattern = new(
        @"setManifestid\(\s*(\d+)\s*,\s*""(\d+)""\s*\)",
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
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/1.0");

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steamy");
        _toolsFolder = Path.Combine(appData, "tools", "DepotDownloaderMod");
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

        return depots
            .Where(kv => !string.IsNullOrEmpty(kv.Value.Manifest) && !string.IsNullOrEmpty(kv.Value.Key))
            .Select(kv => new RyuuDepotInfo(kv.Key, kv.Value.Manifest, kv.Value.Key))
            .ToList();
    }

    public async Task<RyuuGameDownloadResult> DownloadGameAsync(
        int appId,
        string targetFolder,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var appSettings = _settings.Load();
        var authCode = appSettings.RyuuApiKey;
        if (string.IsNullOrWhiteSpace(authCode))
        {
            var stored = await _credentials.ReadAsync("ryuu-auth-key");
            authCode = stored ?? string.Empty;
        }

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

        var keyFilePath = Path.Combine(appWorkDir, $"{appId}.key");
        var keyLines = depots.Select(d => $"{d.DepotId};{d.DecryptionKey}");
        await File.WriteAllLinesAsync(keyFilePath, keyLines, cancellationToken);

        Directory.CreateDirectory(targetFolder);
        var failedDepots = new List<string>();
        var completedDepots = 0;

        foreach (var depot in depots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Downloading depot {depot.DepotId} ({completedDepots + 1}/{depots.Count})...");

            var manifestFile = $"{depot.DepotId}_{depot.ManifestId}.manifest";
            var manifestPath = Path.Combine(appWorkDir, manifestFile);
            var keyFile = Path.Combine(appWorkDir, $"{appId}.key");

            var args = new List<string>
            {
                "-app", appId.ToString(CultureInfo.InvariantCulture),
                "-depot", depot.DepotId.ToString(CultureInfo.InvariantCulture),
                "-manifest", depot.ManifestId,
                "-depotkeys", keyFile,
                "-dir", Path.GetFullPath(targetFolder)
            };
            if (File.Exists(manifestPath))
            {
                args.Add("-manifestfile");
                args.Add(manifestPath);
            }

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

                var (exitCode, stdoutLines) = await RunProcessWithWatchdogAsync(
                    process, depot.DepotId, completedDepots + 1, depots.Count, targetFolder, progress, cancellationToken);

                var totalLine = stdoutLines.LastOrDefault(l => l.StartsWith("Total downloaded:", StringComparison.Ordinal));
                if (exitCode != 0 || (totalLine is not null && totalLine.Contains("0 bytes")))
                {
                    var lastLines = string.Join(" | ", stdoutLines.TakeLast(5));
                    failedDepots.Add($"Depot {depot.DepotId}: exit {exitCode} — {lastLines}");
                    _logging.Add(Models.LogLevel.Error, "RyuuDownload",
                        $"Depot {depot.DepotId} failed (exit {exitCode}): {lastLines}", appId);
                }
                else
                {
                    completedDepots++;
                    progress?.Report($"Depot {depot.DepotId} completed ({completedDepots}/{depots.Count})");
                    _logging.Add(Models.LogLevel.Info, "RyuuDownload",
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
            return new RyuuGameDownloadResult(false, "No depots with keys and manifests found from this source.");

        _logging.Add(Models.LogLevel.Info, "GameDownload",
            $"Parsed {depots.Count} depot(s) for App {appId} from {source}.", appId);

        var appWorkDir = manifestResult.WorkDirectory ?? _workFolder;
        var keyFilePath = Path.Combine(appWorkDir, $"{appId}.key");
        var keyLines = depots.Select(d => $"{d.DepotId};{d.DecryptionKey}");
        await File.WriteAllLinesAsync(keyFilePath, keyLines, cancellationToken);

        Directory.CreateDirectory(targetFolder);
        return await RunDepotDownloaderModAsync(ddPath, appId, depots, appWorkDir, targetFolder, progress, cancellationToken);
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

        var keyFile = Path.Combine(appWorkDir, $"{appId}.key");

        foreach (var depot in depots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Downloading depot {depot.DepotId} ({completedDepots + 1}/{depots.Count})...");

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

                var (exitCode, stdoutLines) = await RunProcessWithWatchdogAsync(
                    process, depot.DepotId, completedDepots + 1, depots.Count, targetFolder, progress, cancellationToken);

                var totalLine = stdoutLines.LastOrDefault(l => l.StartsWith("Total downloaded:", StringComparison.Ordinal));
                if (exitCode != 0 || (totalLine is not null && totalLine.Contains("0 bytes")))
                {
                    var lastLines = string.Join(" | ", stdoutLines.TakeLast(5));
                    failedDepots.Add($"Depot {depot.DepotId}: exit {exitCode} — {lastLines}");
                    _logging.Add(Models.LogLevel.Error, "GameDownload",
                        $"Depot {depot.DepotId} failed (exit {exitCode}): {lastLines}", appId);
                }
                else
                {
                    completedDepots++;
                    progress?.Report($"Depot {depot.DepotId} completed ({completedDepots}/{depots.Count})");
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

    private static async Task<(int ExitCode, List<string> StdoutLines)> RunProcessWithWatchdogAsync(
        Process process, int depotId, int depotIndex, int totalDepots,
        string targetFolder, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var stdoutLines = new List<string>();
        var lastActivityUtc = DateTime.UtcNow;

        var lastPct = "0";
        var lastDl = "";
        var lastTot = "";
        var lastSpd = "";
        var lastEta = "";
        var lastFile = "";

        var folderBytes = 0L;
        var prevFolderBytes = 0L;
        var prevFolderCheck = DateTime.UtcNow;

        var readStdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                stdoutLines.Add(line);
                lastActivityUtc = DateTime.UtcNow;
                var parsed = DepotDownloaderOutputParser.Parse(line);
                if (parsed is not null)
                {
                    if (parsed.Percent is not null) lastPct = parsed.Percent.Value.ToString("F1", CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(parsed.Downloaded)) lastDl = parsed.Downloaded;
                    if (!string.IsNullOrWhiteSpace(parsed.Total)) lastTot = parsed.Total;
                    if (!string.IsNullOrWhiteSpace(parsed.Speed)) lastSpd = parsed.Speed;
                    if (!string.IsNullOrWhiteSpace(parsed.Eta)) lastEta = parsed.Eta;
                    if (!string.IsNullOrWhiteSpace(parsed.CurrentFile)) lastFile = parsed.CurrentFile;
                    progress?.Report($"PROGRESS|{depotId}|{depotIndex}|{totalDepots}|{lastPct}|{lastDl}|{lastTot}|{lastSpd}|{lastEta}|{lastFile}");
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    progress?.Report(line);
                }
            }
        }, cancellationToken);

        var readStderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var reg = timeoutCts.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });

        // Background task: watchdog + periodic folder-size stats
        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.HasExited && !timeoutCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), timeoutCts.Token);

                    if ((DateTime.UtcNow - lastActivityUtc).TotalMinutes > 3)
                    {
                        progress?.Report("No output for 3 minutes — killing stuck process.");
                        timeoutCts.Cancel();
                        return;
                    }

                    // Calculate stats from folder size when DDMod doesn't provide them
                    if (string.IsNullOrWhiteSpace(lastDl) && Directory.Exists(targetFolder))
                    {
                        try
                        {
                            var dirInfo = new DirectoryInfo(targetFolder);
                            folderBytes = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories)
                                .Sum(f => { try { return f.Length; } catch { return 0; } });

                            lastDl = FormatBytes(folderBytes);

                            if (double.TryParse(lastPct, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && pct > 0.1)
                            {
                                var estimatedTotal = (long)(folderBytes / (pct / 100.0));
                                lastTot = FormatBytes(estimatedTotal);
                            }

                            var now = DateTime.UtcNow;
                            var elapsed = (now - prevFolderCheck).TotalSeconds;
                            if (elapsed >= 2.0 && folderBytes > prevFolderBytes)
                            {
                                var bytesPerSec = (folderBytes - prevFolderBytes) / elapsed;
                                lastSpd = FormatBytes((long)bytesPerSec) + "/s";

                                if (double.TryParse(lastPct, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p > 0.1)
                                {
                                    var estimatedTotal = folderBytes / (p / 100.0);
                                    var remaining = estimatedTotal - folderBytes;
                                    if (remaining > 0 && bytesPerSec > 0)
                                    {
                                        var secs = remaining / bytesPerSec;
                                        lastEta = secs < 60 ? $"{secs:F0}s"
                                                : secs < 3600 ? $"{secs / 60:F0}m {secs % 60:F0}s"
                                                : $"{secs / 3600:F0}h {(secs % 3600) / 60:F0}m";
                                    }
                                }

                                prevFolderBytes = folderBytes;
                                prevFolderCheck = now;
                            }

                            progress?.Report($"PROGRESS|{depotId}|{depotIndex}|{totalDepots}|{lastPct}|{lastDl}|{lastTot}|{lastSpd}|{lastEta}|{lastFile}");
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Watchdog killed the process
        }
        timeoutCts.Cancel();
        await readStdout;
        _ = await readStderr;

        var exitCode = process.HasExited ? process.ExitCode : -1;
        return (exitCode, stdoutLines);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024L => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B"
    };

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

        var exePath = Directory.GetFiles(_toolsFolder, "DepotDownloader*.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (exePath is not null && File.Exists(exePath))
            return exePath;

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
            using (var archive = ArchiveFactory.Open(archivePath))
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
