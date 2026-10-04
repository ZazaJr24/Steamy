using System.IO;
using Steamy.Models;

namespace Steamy.Services;

public interface IDownloadOperationStatus
{
    bool IsOperationRunning(Guid jobId);
}

/// <summary>
/// Coordinates queue jobs and delegates real work to the configured DepotDownloader adapter.
/// A job remains reserved until its process, local checks and queue save have all finished.
/// </summary>
public sealed class DownloadManager : IDownloadManager, IDownloadOperationStatus, IDisposable
{
    private readonly IAppDataStore _store;
    private readonly ISettingsService _settingsService;
    private readonly IDepotDownloaderService _depotDownloader;
    private readonly IFileVerificationService _verification;
    private readonly IDownloadQueueStore _queueStore;
    private readonly ILoggingService _logging;
    private readonly INotificationService _notifications;
    private readonly IRyuuGameDownloadService? _downloaderSetup;
    private readonly DownloadOperationRegistry _operations = new();

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public InlineProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }

    public DownloadManager(
        IAppDataStore store,
        ISettingsService settingsService,
        IDepotDownloaderService depotDownloader,
        IFileVerificationService verification,
        IDownloadQueueStore queueStore,
        ILoggingService logging,
        INotificationService notifications,
        IRyuuGameDownloadService? downloaderSetup = null)
    {
        _store = store;
        _settingsService = settingsService;
        _depotDownloader = depotDownloader;
        _verification = verification;
        _queueStore = queueStore;
        _logging = logging;
        _notifications = notifications;
        _downloaderSetup = downloaderSetup;
    }

    public async Task<bool> StartAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_operations.TryRegister(job.Id, linked)) return false;

        var isResume = job.State == DownloadJobState.Paused;
        SetGameState(job, DownloadJobState.Preparing);
        if (job.Started == default) job.Started = DateTime.Now;
        job.ExitCode = null;
        ClearLiveStats(job);
        job.Finished = null;
        job.Status = isResume ? "Resuming download — continuing from existing files" : "Preparing download";

        try
        {
            linked.Token.ThrowIfCancellationRequested();
            await _queueStore.SaveAsync(job, linked.Token).ConfigureAwait(false);
            var settings = _settingsService.Load();
            var executablePath = BundledModCapabilities.SelectExecutable(settings.DepotDownloaderPath);
            if (string.IsNullOrWhiteSpace(settings.DepotDownloaderPath)
                && string.IsNullOrWhiteSpace(executablePath)
                && _downloaderSetup is not null)
            {
                job.Status = "Downloading the verified DepotDownloaderMod from GitHub";
                var setupProgress = new InlineProgress<string>(message =>
                {
                    job.Status = message;
                    job.AppendLog(message);
                });
                if (await _downloaderSetup.EnsureDepotDownloaderModAsync(setupProgress, linked.Token).ConfigureAwait(false))
                    executablePath = BundledModCapabilities.SelectExecutable(null);
            }
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                job.AppendLog("The verified Steamy DepotDownloaderMod was not found or downloaded. No game files were downloaded.");
                SetFailure(job, "The Steamy DepotDownloaderMod could not be downloaded or verified. Check your connection, then retry.");
                _logging.Add(LogLevel.Warning, "DownloadManager", "Download refused: the Steamy downloader was unavailable and no custom executable was configured.", job.AppId, job.Id);
                return false;
            }

            if (!File.Exists(executablePath))
            {
                job.AppendLog($"DepotDownloader executable not found: {executablePath}");
                SetFailure(job, $"DepotDownloader executable not found — no file was downloaded: {executablePath}");
                _logging.Add(LogLevel.Error, "DownloadManager", "Download refused: the configured DepotDownloader executable does not exist.", job.AppId, job.Id);
                return false;
            }

            var storage = DownloadStorageGuard.Check(job.TargetFolder);
            if (!storage.CanDownload)
            {
                SetFailure(job, storage.Message);
                job.AppendLog(storage.Message);
                return false;
            }

            if (!Directory.Exists(job.TargetFolder))
            {
                try
                {
                    Directory.CreateDirectory(job.TargetFolder);
                }
                catch (Exception exception)
                {
                    job.AppendLog($"Target folder could not be created: {job.TargetFolder}");
                    SetFailure(job, $"Target folder is not available and could not be created: {exception.Message}");
                    _logging.Add(LogLevel.Error, "DownloadManager", "Target folder unavailable.", job.AppId, job.Id);
                    return false;
                }
            }

            job.DownloadMode = "DepotDownloader";
            var targetFolder = string.IsNullOrWhiteSpace(job.TargetFolder) ? settings.DownloadFolder : job.TargetFolder;
            var request = new DepotDownloaderRequest
            {
                JobId = job.Id,
                ExecutablePath = executablePath,
                AppId = job.AppId,
                DepotId = job.DepotId,
                Branch = job.Branch,
                ManifestId = job.ManifestId,
                TargetFolder = targetFolder,
                WorkingDirectory = settings.WorkingDirectory,
                AuthorizationConfirmed = job.AuthorizationConfirmed,
                VerifyAfterDownload = settings.VerifyAfterDownload,
                SteamUsername = settings.SteamUsername,
                InteractiveConsole = settings.InteractiveToolConsole,
                MaxDownloads = settings.DownloadConnections,
                UseLancache = settings.UseLancache
            };

            var validation = DepotDownloaderArgumentBuilder.Validate(request);
            if (!validation.IsValid)
            {
                SetFailure(job, validation.Error);
                return false;
            }

            var command = DepotDownloaderArgumentBuilder.Build(request);
            var commandLine = $"\"{command.FileName}\" {string.Join(' ', command.Arguments.Select(QuoteArgument))}";
            job.AppendLog(isResume ? $"[Resume] {commandLine}" : commandLine);
            _logging.Add(LogLevel.Info, "DownloadManager", isResume ? $"Resumed DepotDownloader job: {commandLine}" : $"Authorized DepotDownloader job started: {commandLine}", job.AppId, job.Id);

            if (!string.IsNullOrWhiteSpace(settings.SteamUsername) && !settings.InteractiveToolConsole)
            {
                job.AppendLog("A Steam account is configured but the DepotDownloader console window is disabled. If the tool asks for a password or Steam Guard code this job will fail — enable the console window under Settings › Downloads.");
            }

            if (command.Interactive)
            {
                // The tool owns its console window, so the user performs the login there. Nothing is read from it.
                job.Status = "Waiting for your login in DepotDownloader's own console window";
                job.AppendLog("Interactive run: DepotDownloader keeps its own console window for your password / Steam Guard code. The app never passes or stores credentials, and retries are disabled for this job.");
                _logging.Add(LogLevel.Info, "DownloadManager", "Interactive DepotDownloader run started; the login is entered by the user in the tool's own console.", job.AppId, job.Id);
            }

            var attempts = command.Interactive ? 1 : Math.Clamp(settings.RetryCount, 0, 10) + 1;
            DepotDownloaderRunResult result = new(null, false, false, string.Empty, string.Empty);

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (attempt > 1)
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * attempt));
                    job.Status = $"Retrying DepotDownloader in {delay.TotalSeconds:0} s (attempt {attempt} of {attempts})";
                    _logging.Add(LogLevel.Warning, "DownloadManager", $"Retrying DepotDownloader (attempt {attempt} of {attempts}).", job.AppId, job.Id);
                    await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                }

                SetGameState(job, DownloadJobState.Downloading);
                job.Status = command.Interactive
                    ? "Downloading — follow DepotDownloader's own console window"
                    : isResume && attempt == 1 ? "Resuming with DepotDownloader"
                    : attempts == 1 ? "Downloading with DepotDownloader" : $"Downloading with DepotDownloader (attempt {attempt} of {attempts})";
                var progress = new InlineProgress<DepotDownloaderProgress>(update =>
                {
                    if (!linked.IsCancellationRequested && job.State == DownloadJobState.Downloading) ApplyProgress(job, update);
                });
                result = await _depotDownloader.DownloadAsync(request, progress, linked.Token).ConfigureAwait(false);
                AppendProcessOutput(job, result);
                job.ExitCode = result.ExitCode;

                if (result.Succeeded || result.WasPaused || result.WasCancelled || _operations.IsPauseRequested(job.Id) || linked.IsCancellationRequested)
                    break;
                if (!DownloadFailurePolicy.CanRetry(result.ErrorOutput + "\n" + result.Output))
                {
                    job.AppendLog("Automatic retry stopped — this failure needs your attention before another attempt.");
                    break;
                }
            }

            if (_operations.IsPauseRequested(job.Id) || result.WasPaused && !linked.IsCancellationRequested)
            {
                SetGameState(job, DownloadJobState.Paused);
                job.Status = "Paused — resume will continue from existing files";
                ClearLiveStats(job);
                _logging.Add(LogLevel.Info, "DownloadManager", "DepotDownloader job paused.", job.AppId, job.Id);
                return false;
            }

            if (result.WasCancelled || linked.IsCancellationRequested)
            {
                SetGameState(job, DownloadJobState.Cancelled);
                job.Status = "Download cancelled";
                ClearLiveStats(job);
                _logging.Add(LogLevel.Warning, "DownloadManager", "DepotDownloader job cancelled.", job.AppId, job.Id);
                return false;
            }

            if (!result.Succeeded)
            {
                var detail = result.ErrorOutput is { Length: > 0 }
                    ? FirstMeaningfulLine(result.ErrorOutput)
                    : DepotDownloaderOutputParser.ExtractFailureReason(result.Output)
                      ?? $"exit code {result.ExitCode?.ToString() ?? "unknown"}";
                SetFailure(job, $"DepotDownloader failed: {detail}. Existing files were kept; retry continues in the same folder.");
                _logging.Add(LogLevel.Error, "DownloadManager", $"DepotDownloader failed ({detail}).", job.AppId, job.Id);
                return false;
            }

            return await CompleteAsync(job, settings.VerifyAfterDownload, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ClearLiveStats(job);
            if (_operations.IsPauseRequested(job.Id))
            {
                SetGameState(job, DownloadJobState.Paused);
                job.Status = "Paused — resume will continue from existing files";
                return false;
            }

            SetGameState(job, DownloadJobState.Cancelled);
            job.Status = "Download cancelled";
            _logging.Add(LogLevel.Warning, "DownloadManager", "Download cancelled.", job.AppId, job.Id);
            return false;
        }
        catch (Exception exception)
        {
            SetFailure(job, DownloadFailurePolicy.Describe(exception));
            _logging.Add(LogLevel.Error, "DownloadManager", $"Download failed: {exception.GetType().Name}.", job.AppId, job.Id);
            return false;
        }
        finally
        {
            job.Finished = job.IsTerminal || job.State == DownloadJobState.Paused ? DateTime.Now : job.Finished;
            try { await SaveFinalStateAsync(job).ConfigureAwait(false); }
            finally { UnregisterJob(job.Id); }
        }
    }

    public void RegisterJob(Guid jobId, CancellationTokenSource cancellationTokenSource)
    {
        ArgumentNullException.ThrowIfNull(cancellationTokenSource);
        if (!_operations.TryRegister(jobId, cancellationTokenSource))
            throw new InvalidOperationException("An operation is already running for this download.");
    }

    public void UnregisterJob(Guid jobId) => _operations.Complete(jobId);

    public bool IsPauseRequested(Guid jobId) => _operations.IsPauseRequested(jobId);
    public bool IsOperationRunning(Guid jobId) => _operations.IsRunning(jobId);

    public Task PauseAsync(DownloadJob job, CancellationToken cancellationToken = default) =>
        StopAsync(job, pause: true, cancellationToken);

    public Task CancelAsync(DownloadJob job, CancellationToken cancellationToken = default) =>
        StopAsync(job, pause: false, cancellationToken);

    private async Task StopAsync(DownloadJob job, bool pause, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        if (job.IsTerminal || pause && !job.IsActive) return;
        var operation = _operations.Find(job.Id);
        operation?.RequestStop(pause);
        SetGameState(job, pause ? DownloadJobState.Paused : DownloadJobState.Cancelled);
        job.Status = pause ? "Pausing — keeping downloaded files" : "Cancelling — keeping downloaded files";
        ClearLiveStats(job);

        try
        {
            await _depotDownloader.StopAsync(job.Id, pause, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operation?.Cancel();
        }

        // Resume may only acquire the job after the old process and its queue save have ended.
        if (operation is not null)
        {
            await operation.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        job.Status = pause ? "Paused — resume will continue from existing files" : "Cancelled — downloaded files were kept";
        job.Finished = DateTime.Now;
        ClearLiveStats(job);
        await _queueStore.SaveAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task RetryAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.IsActive || _operations.IsRunning(job.Id)) return;
        _logging.Add(LogLevel.Info, "DownloadManager", "Retrying in the existing download folder.", job.AppId, job.Id);
        await StartAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task ForgetAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.IsActive || _operations.IsRunning(job.Id))
            throw new InvalidOperationException("Wait until the current download operation has finished before removing it.");
        await _queueStore.RemoveAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> VerifyAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.IsActive) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_operations.TryRegister(job.Id, linked)) return false;
        var previousState = job.State;
        try
        {
            SetGameState(job, DownloadJobState.Verifying);
            job.Status = "Checking local files — no manifest integrity check";
            ClearLiveStats(job);
            var result = await _verification.VerifyAsync(job.TargetFolder, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            // Presence alone never proves a paused or failed download is complete.
            SetGameState(job, DownloadJobPolicy.AfterLocalCheck(previousState, result.HasContent));
            job.Status = result.Message;
            if (result.HasContent && previousState == DownloadJobState.Completed)
            {
                job.InstallationBytes = result.TotalBytes;
            }
            _logging.Add(result.HasContent ? LogLevel.Info : LogLevel.Warning, "Verification", result.Message, job.AppId, job.Id);
            return result.HasContent;
        }
        catch (OperationCanceledException)
        {
            SetGameState(job, previousState);
            job.Status = "Local check cancelled — download state preserved";
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetGameState(job, previousState);
            job.Status = "Local check unavailable — target files could not be read";
            return false;
        }
        finally
        {
            try { await SaveFinalStateAsync(job).ConfigureAwait(false); }
            finally { UnregisterJob(job.Id); }
        }
    }

    private async Task<bool> CompleteAsync(DownloadJob job, bool verifyAfterDownload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearLiveStats(job);
        if (verifyAfterDownload)
        {
            SetGameState(job, DownloadJobState.Verifying);
            job.Status = "Checking local files after the downloader finished";
            var result = await _verification.VerifyAsync(job.TargetFolder, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.HasContent)
            {
                SetFailure(job, result.Message);
                return false;
            }
            job.InstallationBytes = result.TotalBytes;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SetGameState(job, DownloadJobState.Completed);
        job.Progress = 100;
        if (job.TotalSize.StartsWith('~')) job.TotalSize = job.Downloaded;
        job.Status = verifyAfterDownload
            ? "DepotDownloader completed — local game files are present"
            : "DepotDownloader finished without errors";
        job.Finished = DateTime.Now;
        _logging.Add(LogLevel.Info, "DownloadManager", "Download completed.", job.AppId, job.Id);
        _notifications.Show("Download completed", $"{job.GameName} is ready.");
        return true;
    }

    private async Task SaveFinalStateAsync(DownloadJob job)
    {
        try { await _queueStore.SaveAsync(job).ConfigureAwait(false); }
        catch (Exception exception)
        {
            const string warning = "Download state could not be saved — keep this app open and retry before closing it.";
            job.Status = $"{job.Status} · {warning}";
            job.AppendLog(warning);
            _logging.Add(LogLevel.Warning, "DownloadManager", $"Final queue save failed: {exception.GetType().Name}.", job.AppId, job.Id);
        }
    }

    private static void AppendProcessOutput(DownloadJob job, DepotDownloaderRunResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Output)) job.AppendLog(result.Output.TrimEnd());
        if (!string.IsNullOrWhiteSpace(result.ErrorOutput)) job.AppendLog(result.ErrorOutput.TrimEnd());
    }

    private static string FirstMeaningfulLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }

        return "no error output";
    }

    private static string QuoteArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;

    // Updates arrive on a fixed 100 ms cadence from the tool adapter, so nothing is throttled or dropped here.
    private static void ApplyProgress(DownloadJob job, DepotDownloaderProgress update)
    {
        if (!string.IsNullOrWhiteSpace(update.CurrentFile)) job.CurrentFile = update.CurrentFile;
        if (!string.IsNullOrWhiteSpace(update.RawLine)) job.AppendLog(update.RawLine);
        if (update.Percent is not null) job.Progress = update.Percent.Value;
        job.HasMeasuredProgress = update.Percent is not null;
        job.HasWholeGameProgress = update.Percent is not null && update.DepotCount <= 1;
        job.Phase = update.Phase;
        job.TransferredBytes = update.DownloadedBytes;
        job.TransferTotalBytes = update.TransferTotalBytes;
        job.ContentBytes = update.ContentBytes;
        job.InstallationBytes = update.TotalBytes;
        job.ReusedBytes = update.ReusedBytes;
        if (!string.IsNullOrWhiteSpace(update.Downloaded)) job.Downloaded = update.Downloaded;
        if (!string.IsNullOrWhiteSpace(update.Total)) job.TotalSize = update.Total;
        job.Speed = update.Speed;
        job.Eta = update.Eta;
        job.BytesPerSecond = update.BytesPerSecond;
        job.EtaSeconds = update.EtaSeconds;
        if (update.DepotCount > 1) job.Status = $"Downloading depot {update.DepotIndex} of {update.DepotCount}";
    }

    private void SetFailure(DownloadJob job, string status)
    {
        SetGameState(job, DownloadJobState.Failed);
        job.Status = status;
        job.Finished = DateTime.Now;
        ClearLiveStats(job);
    }

    private static void ClearLiveStats(DownloadJob job) => job.ClearLiveStats();

    private void SetGameState(DownloadJob job, DownloadJobState state)
    {
        job.State = state;
        var game = _store.Games.FirstOrDefault(item => item.AppId == job.AppId);
        if (game is not null) game.CurrentState = state;
    }

    public void Dispose() => _operations.CancelAll();
}
