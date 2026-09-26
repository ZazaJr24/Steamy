using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Steamy.Models;

namespace Steamy.Services;

public sealed class DepotDownloaderRequest
{
    public Guid JobId { get; init; } = Guid.NewGuid();
    public string ExecutablePath { get; init; } = string.Empty;
    public int AppId { get; init; }
    public int? DepotId { get; init; }
    public string Branch { get; init; } = "public";
    public string ManifestId { get; init; } = string.Empty;
    public string TargetFolder { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public bool AuthorizationConfirmed { get; init; }
    public bool VerifyAfterDownload { get; init; }

    /// <summary>
    /// Optional Steam account name used with <c>-username</c>. It is only an identifier — the
    /// password and Steam Guard code are always typed by the user directly into DepotDownloader.
    /// </summary>
    public string SteamUsername { get; init; } = string.Empty;

    /// <summary>
    /// When true, DepotDownloader keeps its own console window so it can prompt for a password or
    /// Steam Guard code. The app never reads, stores or supplies those values.
    /// </summary>
    public bool InteractiveConsole { get; init; }
}

public sealed record DepotDownloaderCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, bool Interactive = false);

public sealed record DepotDownloaderValidationResult(bool IsValid, string Error)
{
    public static DepotDownloaderValidationResult Valid() => new(true, string.Empty);
    public static DepotDownloaderValidationResult Invalid(string error) => new(false, error);
}

public sealed record DepotDownloaderToolStatus(
    bool IsReady,
    string ExecutablePath,
    string Version,
    string Message,
    DateTime CheckedAt);

public sealed record DepotDownloaderProgress(
    double? Percent,
    string CurrentFile,
    string Downloaded,
    string Total,
    string Speed,
    string Eta,
    string RawLine);

public sealed record DepotDownloaderRunResult(
    int? ExitCode,
    bool WasCancelled,
    bool WasPaused,
    string Output,
    string ErrorOutput)
{
    public bool Succeeded => !WasCancelled && !WasPaused && ExitCode == 0;
}

/// <summary>
/// Builds only the documented, non-sensitive DepotDownloader arguments. There is no
/// raw argument escape hatch and no support for passwords, Steam Guard or session tokens.
/// </summary>
public static class DepotDownloaderArgumentBuilder
{
    private static readonly Regex BranchPattern = new("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UsernamePattern = new("^[A-Za-z0-9._-]{2,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static DepotDownloaderValidationResult Validate(DepotDownloaderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
            return DepotDownloaderValidationResult.Invalid("DepotDownloader executable path is required.");
        if (request.AppId <= 0)
            return DepotDownloaderValidationResult.Invalid("App ID must be a positive integer.");
        if (request.DepotId is <= 0)
            return DepotDownloaderValidationResult.Invalid("Depot ID must be a positive integer when supplied.");
        if (string.IsNullOrWhiteSpace(request.TargetFolder))
            return DepotDownloaderValidationResult.Invalid("A target folder is required.");
        if (!request.AuthorizationConfirmed)
            return DepotDownloaderValidationResult.Invalid("The user must confirm authorization for this content.");

        if (!string.IsNullOrWhiteSpace(request.SteamUsername) && !UsernamePattern.IsMatch(request.SteamUsername.Trim()))
            return DepotDownloaderValidationResult.Invalid("The Steam account name may only contain letters, digits, dot, dash and underscore.");

        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "public" : request.Branch.Trim();
        if (!BranchPattern.IsMatch(branch))
            return DepotDownloaderValidationResult.Invalid("Branch contains unsupported characters.");

        if (!string.IsNullOrWhiteSpace(request.ManifestId)
            && request.ManifestId != "—"
            && !request.ManifestId.All(char.IsAsciiDigit))
            return DepotDownloaderValidationResult.Invalid("Manifest ID must contain digits only.");

        try
        {
            _ = Path.GetFullPath(request.ExecutablePath);
            _ = Path.GetFullPath(request.TargetFolder);
            if (!string.IsNullOrWhiteSpace(request.WorkingDirectory)) _ = Path.GetFullPath(request.WorkingDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DepotDownloaderValidationResult.Invalid("One of the configured paths is invalid.");
        }

        return DepotDownloaderValidationResult.Valid();
    }

    public static DepotDownloaderCommand Build(DepotDownloaderRequest request)
    {
        var validation = Validate(request);
        if (!validation.IsValid) throw new ArgumentException(validation.Error, nameof(request));

        var arguments = new List<string>
        {
            "-app", request.AppId.ToString(CultureInfo.InvariantCulture),
            "-dir", Path.GetFullPath(request.TargetFolder)
        };

        if (request.DepotId is > 0)
        {
            arguments.Add("-depot");
            arguments.Add(request.DepotId.Value.ToString(CultureInfo.InvariantCulture));
        }

        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "public" : request.Branch.Trim();
        if (!branch.Equals("public", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-branch");
            arguments.Add(branch);
        }

        if (!string.IsNullOrWhiteSpace(request.ManifestId) && request.ManifestId != "—")
        {
            arguments.Add("-manifest");
            arguments.Add(request.ManifestId);
        }

        if (!string.IsNullOrWhiteSpace(request.SteamUsername))
        {
            arguments.Add("-username");
            arguments.Add(request.SteamUsername.Trim());

            // Keep the Steam session so the Steam Guard prompt is not repeated for every job.
            // A password argument is never added: DepotDownloader asks the user itself.
            arguments.Add("-remember-password");
        }

        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? Path.GetFullPath(request.TargetFolder)
            : Path.GetFullPath(request.WorkingDirectory);

        return new DepotDownloaderCommand(Path.GetFullPath(request.ExecutablePath), arguments, workingDirectory, request.InteractiveConsole);
    }
}

/// <summary>
/// Builds the DepotDownloader process settings. In the default mode stdout/stderr are captured so
/// progress can be parsed and shown in the app. In interactive mode the tool keeps its own console
/// window, because a console application can only ask for a password or Steam Guard code there.
/// The app itself never supplies, reads or stores credentials.
/// </summary>
public static class DepotDownloaderProcessFactory
{
    public static ProcessStartInfo Create(DepotDownloaderCommand command, bool interactive)
    {
        ArgumentNullException.ThrowIfNull(command);

        var startInfo = interactive
            ? new ProcessStartInfo
            {
                FileName = command.FileName,

                // ShellExecute is what makes Windows give the console application its own console
                // window; CreateProcess without a console only yields a hidden, unusable stdin.
                // ArgumentList is not supported together with UseShellExecute, so the command line is
                // quoted explicitly. No credentials are ever part of it.
                Arguments = string.Join(' ', command.Arguments.Select(QuoteArgument)),
                WorkingDirectory = command.WorkingDirectory,
                UseShellExecute = true
            }
            : new ProcessStartInfo
            {
                FileName = command.FileName,
                WorkingDirectory = command.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

        if (!interactive)
        {
            foreach (var argument in command.Arguments) startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>Quotes one argument following the Windows command line rules.</summary>
    public static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return argument;

        var builder = new System.Text.StringBuilder("\"");
        for (var index = 0; index < argument.Length; index++)
        {
            var backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                backslashes++;
                index++;
            }

            if (index == argument.Length)
            {
                // Double the trailing backslashes so they do not escape the closing quote.
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[index] == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[index]);
            }
        }

        return builder.Append('"').ToString();
    }
}

/// <summary>Parses only observable progress information; it never interprets credentials.</summary>
public static class DepotDownloaderOutputParser
{
    private const string SizeUnit = @"(?:bytes?|B|KB|KiB|MB|MiB|GB|GiB|TB|TiB)";
    private static readonly Regex PercentPattern = new(@"(?<value>\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BytesPattern = new($@"(?<downloaded>\d+(?:[.,]\d+)?\s*{SizeUnit})\s*(?:/|of)\s*(?<total>\d+(?:[.,]\d+)?\s*{SizeUnit})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SpeedPattern = new($@"(?<speed>\d+(?:[.,]\d+)?\s*{SizeUnit}\s*/\s*s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EtaPattern = new(@"(?i)(?:eta|remaining|left|~)\s*[:=]?\s*(?<eta>\d{1,3}(?::\d{2}){1,2}|\d+\s*(?:s|sec|min|m|h))", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FilePattern = new(@"(?i)(?:downloading|download|file)\s*[:=]\s*(?<file>.+)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PctFilePattern = new(@"^\s*\d+[.,]\d+%\s+(?<file>[A-Z]:\\.+)$", RegexOptions.Compiled);

    private const int MaxPendingLogLines = 2_048;
    private const int MaxLinesPerLogFlush = 256;
    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Steamy", "ddmod-output.log");
    private static readonly ConcurrentQueue<string> _pendingLogLines = new();
    private static readonly Timer _logFlushTimer = new(FlushQueuedOutput, null,
        TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    private static int _pendingLogLineCount;
    private static int _logFlushRunning;

    public static DepotDownloaderProgress? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        QueueOutputLog(line);

        double? percent = null;
        var percentMatch = PercentPattern.Match(line);
        if (percentMatch.Success
            && double.TryParse(percentMatch.Groups["value"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedPercent))
        {
            percent = Math.Clamp(parsedPercent, 0, 100);
        }

        var downloaded = string.Empty;
        var total = string.Empty;
        var bytesMatch = BytesPattern.Match(line);
        if (bytesMatch.Success)
        {
            downloaded = bytesMatch.Groups["downloaded"].Value;
            total = bytesMatch.Groups["total"].Value;
        }

        var speedMatch = SpeedPattern.Match(line);
        var speed = speedMatch.Success ? speedMatch.Groups["speed"].Value.Replace(" ", string.Empty) : string.Empty;
        var etaMatch = EtaPattern.Match(line);
        var eta = etaMatch.Success ? etaMatch.Groups["eta"].Value : string.Empty;
        var fileMatch = FilePattern.Match(line);
        var currentFile = fileMatch.Success ? fileMatch.Groups["file"].Value.Trim() : string.Empty;
        if (string.IsNullOrEmpty(currentFile))
        {
            var pctFileMatch = PctFilePattern.Match(line);
            if (pctFileMatch.Success) currentFile = Path.GetFileName(pctFileMatch.Groups["file"].Value.Trim());
        }

        if (percent is null && string.IsNullOrWhiteSpace(downloaded) && string.IsNullOrWhiteSpace(speed)
            && string.IsNullOrWhiteSpace(eta) && string.IsNullOrWhiteSpace(currentFile)) return null;

        return new DepotDownloaderProgress(percent, currentFile, downloaded, total, speed, eta, line);
    }

    private static void QueueOutputLog(string line)
    {
        _ = _logFlushTimer;
        var count = Interlocked.Increment(ref _pendingLogLineCount);
        if (count > MaxPendingLogLines && _pendingLogLines.TryDequeue(out _))
            Interlocked.Decrement(ref _pendingLogLineCount);

        _pendingLogLines.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
    }

    private static void FlushQueuedOutput(object? state) => FlushOutputLog(MaxLinesPerLogFlush);

    public static void FlushPendingOutput() => FlushOutputLog(int.MaxValue);

    private static void FlushOutputLog(int maximumLines)
    {
        if (Interlocked.Exchange(ref _logFlushRunning, 1) != 0) return;

        try
        {
            var batch = new List<string>(Math.Min(maximumLines, MaxPendingLogLines));
            while (batch.Count < maximumLines && _pendingLogLines.TryDequeue(out var line))
            {
                Interlocked.Decrement(ref _pendingLogLineCount);
                batch.Add(line);
            }

            if (batch.Count == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllLines(_logPath, batch);
        }
        catch (Exception)
        {
            // Logging must never interfere with the running download process.
        }
        finally
        {
            Interlocked.Exchange(ref _logFlushRunning, 0);
            if (maximumLines == int.MaxValue && !_pendingLogLines.IsEmpty)
                FlushOutputLog(int.MaxValue);
        }
    }
}

/// <summary>
/// Runs an unchanged, user-selected DepotDownloader executable for explicitly authorized
/// content. Process output is streamed asynchronously and the app never supplies credentials.
/// </summary>
public sealed class DepotDownloaderService : IDepotDownloaderService, IDisposable
{
    private sealed class ActiveProcess
    {
        public ActiveProcess(Process process) => Process = process;
        public Process Process { get; }
        public bool PauseRequested { get; set; }
    }

    private sealed class BoundedOutputBuffer
    {
        private const int MaximumCharacters = 120_000;
        private readonly Queue<string> _lines = new();
        private int _characterCount;

        public void Add(string line)
        {
            line = line.Length > MaximumCharacters ? line[^MaximumCharacters..] : line;
            if (line.Length >= MaximumCharacters)
            {
                _lines.Clear();
                _lines.Enqueue(line[^MaximumCharacters..]);
                _characterCount = MaximumCharacters;
                return;
            }

            _lines.Enqueue(line);
            _characterCount += line.Length + Environment.NewLine.Length;
            while (_characterCount > MaximumCharacters && _lines.TryDequeue(out var oldest))
                _characterCount -= oldest.Length + Environment.NewLine.Length;
        }

        public override string ToString() => string.Join(Environment.NewLine, _lines);
    }

    private readonly ConcurrentDictionary<Guid, ActiveProcess> _activeProcesses = new();

    public async Task<DepotDownloaderToolStatus> CheckAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTime.Now;
        if (string.IsNullOrWhiteSpace(executablePath))
            return new DepotDownloaderToolStatus(false, string.Empty, string.Empty, "Not configured", checkedAt);

        string fullPath;
        try { fullPath = Path.GetFullPath(executablePath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new DepotDownloaderToolStatus(false, executablePath, string.Empty, "Executable path is invalid", checkedAt);
        }

        if (!File.Exists(fullPath))
            return new DepotDownloaderToolStatus(false, fullPath, string.Empty, "Executable not found", checkedAt);

        var fileVersion = FileVersionInfo.GetVersionInfo(fullPath).FileVersion ?? string.Empty;
        var output = string.Empty;
        var exitCode = (int?)null;
        try
        {
            using var process = new Process
            {
                StartInfo = DepotDownloaderProcessFactory.Create(
                    new DepotDownloaderCommand(fullPath, new[] { "-version" }, Path.GetDirectoryName(fullPath)!),
                    interactive: false),
                EnableRaisingEvents = true
            };
            if (!process.Start())
                return new DepotDownloaderToolStatus(false, fullPath, fileVersion, "Could not start executable", checkedAt);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { TryKill(process); }
            try { output = await stdoutTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { _ = await stderrTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            exitCode = process.HasExited ? process.ExitCode : null;
        }
        catch (OperationCanceledException)
        {
            return new DepotDownloaderToolStatus(false, fullPath, fileVersion, "Version check cancelled", checkedAt);
        }
        catch (Exception exception)
        {
            return new DepotDownloaderToolStatus(false, fullPath, fileVersion, $"Version check failed: {exception.GetType().Name}", checkedAt);
        }

        var detectedVersion = ExtractVersion(output) ?? fileVersion;
        var message = exitCode is 0
            ? "Executable found and version check passed"
            : "Executable found; version check is not supported by this build";
        return new DepotDownloaderToolStatus(true, fullPath, detectedVersion, message, checkedAt);
    }

    public async Task<DepotDownloaderRunResult> DownloadAsync(
        DepotDownloaderRequest request,
        IProgress<DepotDownloaderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var command = DepotDownloaderArgumentBuilder.Build(request);
        if (!File.Exists(command.FileName)) throw new FileNotFoundException("DepotDownloader executable was not found.", command.FileName);

        Directory.CreateDirectory(Path.GetFullPath(request.TargetFolder));
        var workingDirectory = Directory.Exists(command.WorkingDirectory)
            ? command.WorkingDirectory
            : Path.GetFullPath(request.TargetFolder);
        var interactive = command.Interactive;
        using var process = new Process
        {
            StartInfo = DepotDownloaderProcessFactory.Create(
                new DepotDownloaderCommand(command.FileName, command.Arguments, workingDirectory, interactive),
                interactive),
            EnableRaisingEvents = true
        };

        if (!process.Start()) throw new InvalidOperationException("DepotDownloader could not be started.");
        var active = new ActiveProcess(process);
        if (!_activeProcesses.TryAdd(request.JobId, active))
        {
            TryKill(process);
            throw new InvalidOperationException("A DepotDownloader process already exists for this job.");
        }

        progress?.Report(new DepotDownloaderProgress(null, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "Process started."));
        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));
        var stdout = new BoundedOutputBuffer();
        var stderr = new BoundedOutputBuffer();

        try
        {
            if (interactive)
            {
                // Nothing is read from the tool: it owns its console window and asks the user
                // directly for a password or Steam Guard code. Only the exit code is evaluated here.
                progress?.Report(new DepotDownloaderProgress(
                    null, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                    "DepotDownloader runs in its own console window — type your password / Steam Guard code there."));

                await process.WaitForExitAsync().ConfigureAwait(false);
                return new DepotDownloaderRunResult(
                    process.HasExited ? process.ExitCode : null,
                    cancellationToken.IsCancellationRequested && !active.PauseRequested,
                    active.PauseRequested,
                    string.Empty,
                    string.Empty);
            }

            var stdoutTask = ReadLinesAsync(process.StandardOutput, stdout, progress);
            var stderrTask = ReadLinesAsync(process.StandardError, stderr, progress);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            var wasPaused = active.PauseRequested;
            var wasCancelled = cancellationToken.IsCancellationRequested && !wasPaused;
            return new DepotDownloaderRunResult(
                process.HasExited ? process.ExitCode : null,
                wasCancelled,
                wasPaused,
                stdout.ToString(),
                stderr.ToString());
        }
        finally
        {
            _activeProcesses.TryRemove(request.JobId, out _);
            DepotDownloaderOutputParser.FlushPendingOutput();
        }
    }

    public Task StopAsync(Guid jobId, bool pause, CancellationToken cancellationToken = default)
    {
        if (_activeProcesses.TryGetValue(jobId, out var active))
        {
            active.PauseRequested = pause;
            TryKill(active.Process);
        }
        return Task.CompletedTask;
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        BoundedOutputBuffer output,
        IProgress<DepotDownloaderProgress>? progress)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            output.Add(line);
            var parsed = DepotDownloaderOutputParser.Parse(line);
            if (parsed is not null) progress?.Report(parsed);
        }
    }

    private static string? ExtractVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = Regex.Match(output, @"\b\d+\.\d+(?:\.\d+){0,2}\b", RegexOptions.CultureInvariant);
        return match.Success ? match.Value : null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        foreach (var active in _activeProcesses.Values) TryKill(active.Process);
        _activeProcesses.Clear();
    }
}
