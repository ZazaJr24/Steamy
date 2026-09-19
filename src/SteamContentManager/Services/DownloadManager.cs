using System.Collections.Concurrent;
using System.IO;
using SteamContentManager.Models;

namespace SteamContentManager.Services;

/// <summary>
/// Coordinates queue jobs and delegates real work to the configured, unchanged
/// DepotDownloader adapter. Without a configured executable it uses a local demo
/// fallback so the UI remains usable and never starts an unknown process.
/// </summary>
public sealed class DownloadManager : IDownloadManager, IDisposable
{
    private readonly IAppDataStore _store;
    private readonly ISettingsService _settingsService;
    private readonly IDepotDownloaderService _depotDownloader;
    private readonly IFileVerificationService _verification;
    private readonly IDownloadQueueStore _queueStore;
    private readonly ILoggingService _logging;
    private readonly INotificationService _notifications;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<Guid, bool> _pauseRequested = new();
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public DownloadManager(
        IAppDataStore store,
        ISettingsService settingsService,
        IDepotDownloaderService depotDownloader,
        IFileVerificationService verification,
        IDownloadQueueStore queueStore,
        ILoggingService logging,
        INotificationService notifications)
    {
        _store = store;
        _settingsService = settingsService;
        _depotDownloader = depotDownloader;
        _verification = verification;
        _queueStore = queueStore;
        _logging = logging;
        _notifications = notifications;
    }

    public async Task<bool> StartAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!_running.TryAdd(job.Id, Task.CompletedTask)) return false;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellations[job.Id] = linked;
        _pauseRequested.TryRemove(job.Id, out _);
        SetGameState(job, DownloadJobState.Preparing);
        job.Started = DateTime.Now;
        job.Finished = null;
        job.Status = "Preparing download";

        try
        {
            var settings = _settingsService.Load();
            if (string.IsNullOrWhiteSpace(settings.DepotDownloaderPath))
            {
                job.DownloadMode = "Not configured";
                job.AppendLog("DepotDownloader is not configured. No file was downloaded.");
                SetFailure(job, "DepotDownloader is not configured — no file was downloaded. Select the tool under Settings › Downloads.");
                _logging.Add(LogLevel.Warning, "DownloadManager", "Download refused: no DepotDownloader executable is configured.", job.AppId, job.Id);
                return false;
            }

            if (!File.Exists(settings.DepotDownloaderPath))
            {
                job.DownloadMode = "Not configured";
                job.AppendLog($"DepotDownloader executable not found: {settings.DepotDownloaderPath}");
                SetFailure(job, $"DepotDownloader executable not found — no file was downloaded: {settings.DepotDownloaderPath}");
                _logging.Add(LogLevel.Error, "DownloadManager", "Download refused: the configured DepotDownloader executable does not exist.", job.AppId, job.Id);
                return false;
            }

            if (string.IsNullOrWhiteSpace(job.TargetFolder) || !Directory.Exists(job.TargetFolder))
            {
                try
                {
                    Directory.CreateDirectory(job.TargetFolder);
                }
                catch (Exception exception)
                {
                    job.DownloadMode = "Not configured";
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
                ExecutablePath = settings.DepotDownloaderPath,
                AppId = job.AppId,
                DepotId = job.DepotId,
                Branch = job.Branch,
                ManifestId = job.ManifestId,
                TargetFolder = targetFolder,
                WorkingDirectory = settings.WorkingDirectory,
                AuthorizationConfirmed = job.AuthorizationConfirmed,
                VerifyAfterDownload = settings.VerifyAfterDownload,
                SteamUsername = settings.SteamUsername,
                InteractiveConsole = settings.InteractiveToolConsole
            };

            var validation = DepotDownloaderArgumentBuilder.Validate(request);
            if (!validation.IsValid)
            {
                SetFailure(job, validation.Error);
                return false;
            }

            // The exact command line (arguments only, never credentials) is logged so the user
            // can see what actually ran.
            var command = DepotDownloaderArgumentBuilder.Build(request);
            var commandLine = $"\"{command.FileName}\" {string.Join(' ', command.Arguments.Select(QuoteArgument))}";
            job.AppendLog(commandLine);
            _logging.Add(LogLevel.Info, "DownloadManager", $"Authorized DepotDownloader job started: {commandLine}", job.AppId, job.Id);

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
                    : attempts == 1 ? "Downloading with DepotDownloader" : $"Downloading with DepotDownloader (attempt {attempt} of {attempts})";
                var progress = new Progress<DepotDownloaderProgress>(update => ApplyProgress(job, update));
                result = await _depotDownloader.DownloadAsync(request, progress, linked.Token).ConfigureAwait(false);
                AppendProcessOutput(job, result);
                job.ExitCode = result.ExitCode;

                if (result.Succeeded || result.WasPaused || result.WasCancelled || _pauseRequested.ContainsKey(job.Id) || linked.IsCancellationRequested)
                    break;
            }

            if (result.WasPaused || _pauseRequested.ContainsKey(job.Id))
            {
                SetGameState(job, DownloadJobState.Paused);
                job.Status = "Paused — resume will reuse the existing target folder";
                _logging.Add(LogLevel.Info, "DownloadManager", "DepotDownloader job paused.", job.AppId, job.Id);
                return false;
            }

            if (result.WasCancelled || linked.IsCancellationRequested)
            {
                SetGameState(job, DownloadJobState.Cancelled);
                job.Status = "Download cancelled";
                _logging.Add(LogLevel.Warning, "DownloadManager", "DepotDownloader job cancelled.", job.AppId, job.Id);
                return false;
            }

            if (!result.Succeeded)
            {
                var detail = result.ErrorOutput is { Length: > 0 }
                    ? FirstMeaningfulLine(result.ErrorOutput)
                    : $"exit code {result.ExitCode?.ToString() ?? "unknown"}";
                SetFailure(job, $"DepotDownloader failed: {detail}. No completed download was reported.");
                _logging.Add(LogLevel.Error, "DownloadManager", $"DepotDownloader failed ({detail}).", job.AppId, job.Id);
                return false;
            }

            return await CompleteAsync(job, settings.VerifyAfterDownload).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_pauseRequested.ContainsKey(job.Id))
            {
                SetGameState(job, DownloadJobState.Paused);
                job.Status = "Paused — resume available";
                return false;
            }

            SetGameState(job, DownloadJobState.Cancelled);
            job.Status = "Download cancelled";
            _logging.Add(LogLevel.Warning, "DownloadManager", "Download cancelled.", job.AppId, job.Id);
            return false;
        }
        catch (Exception exception)
        {
            SetFailure(job, $"Download failed: {exception.GetType().Name}");
            _logging.Add(LogLevel.Error, "DownloadManager", $"Download failed: {exception.GetType().Name}.", job.AppId, job.Id);
            return false;
        }
        finally
        {
            job.Finished = job.IsTerminal || job.State == DownloadJobState.Paused ? DateTime.Now : job.Finished;
            _cancellations.TryRemove(job.Id, out _);
            _pauseRequested.TryRemove(job.Id, out _);
            _running.TryRemove(job.Id, out _);
            await _queueStore.SaveAsync(job).ConfigureAwait(false);
            linked.Dispose();
        }
    }

    public async Task PauseAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_cancellations.TryGetValue(job.Id, out var cancellation))
        {
            _pauseRequested[job.Id] = true;
            await _depotDownloader.StopAsync(job.Id, pause: true, cancellationToken).ConfigureAwait(false);
            cancellation.Cancel();
            return;
        }

        if (!job.IsTerminal)
        {
            SetGameState(job, DownloadJobState.Paused);
            job.Status = "Paused by user";
        }
    }

    public async Task CancelAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        _pauseRequested.TryRemove(job.Id, out _);
        if (_cancellations.TryGetValue(job.Id, out var cancellation))
        {
            await _depotDownloader.StopAsync(job.Id, pause: false, cancellationToken).ConfigureAwait(false);
            cancellation.Cancel();
            return;
        }

        SetGameState(job, DownloadJobState.Cancelled);
        job.Status = "Cancelled by user";
    }

    public Task RetryAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_running.ContainsKey(job.Id)) return Task.CompletedTask;
        job.State = DownloadJobState.Queued;
        job.Status = "Queued for retry";
        job.Progress = 0;
        job.ExitCode = null;
        job.Finished = null;
        _logging.Add(LogLevel.Info, "DownloadManager", "Job queued for retry.", job.AppId, job.Id);
        _ = _queueStore.SaveAsync(job);
        return Task.CompletedTask;
    }

    public async Task ForgetAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await _queueStore.RemoveAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> VerifyAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.TargetFolder) || !Directory.Exists(job.TargetFolder))
        {
            job.Status = "Verification unavailable — target folder does not exist";
            _logging.Add(LogLevel.Warning, "Verification", "Verification unavailable because the target folder does not exist.", job.AppId, job.Id);
            return false;
        }

        SetGameState(job, DownloadJobState.Verifying);
        job.Status = "Checking the local target folder";
        var result = await _verification.VerifyAsync(job.TargetFolder, cancellationToken).ConfigureAwait(false);
        SetGameState(job, result.HasContent ? DownloadJobState.Completed : DownloadJobState.Failed);
        job.Status = result.Message;
        _logging.Add(result.HasContent ? LogLevel.Info : LogLevel.Warning, "Verification", result.Message, job.AppId, job.Id);
        return result.HasContent;
    }

    private async Task<bool> CompleteAsync(DownloadJob job, bool verifyAfterDownload)
    {
        if (verifyAfterDownload && !string.IsNullOrWhiteSpace(job.TargetFolder) && Directory.Exists(job.TargetFolder))
        {
            var verified = await VerifyAsync(job).ConfigureAwait(false);
            if (!verified) return false;

            job.Progress = 100;
            job.Finished = DateTime.Now;
            _logging.Add(LogLevel.Info, "DownloadManager", "Download completed and locally checked.", job.AppId, job.Id);
            _notifications.Show("Download completed", $"{job.GameName}: {job.Status}");
            return true;
        }

        SetGameState(job, DownloadJobState.Completed);
        job.Progress = 100;
        job.Status = verifyAfterDownload
            ? "DepotDownloader finished without errors — no local folder to check"
            : "DepotDownloader finished without errors";
        job.Finished = DateTime.Now;
        _logging.Add(LogLevel.Info, "DownloadManager", "Download completed.", job.AppId, job.Id);
        _notifications.Show("Download completed", $"{job.GameName} is ready.");
        return true;
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

    private static void ApplyProgress(DownloadJob job, DepotDownloaderProgress update)
    {
        if (update.Percent is not null) job.Progress = update.Percent.Value;
        if (!string.IsNullOrWhiteSpace(update.CurrentFile)) job.CurrentFile = update.CurrentFile;
        if (!string.IsNullOrWhiteSpace(update.Downloaded)) job.Downloaded = update.Downloaded;
        if (!string.IsNullOrWhiteSpace(update.Total)) job.TotalSize = update.Total;
        if (!string.IsNullOrWhiteSpace(update.Speed)) job.Speed = update.Speed;
        if (!string.IsNullOrWhiteSpace(update.Eta)) job.Eta = update.Eta;
        if (!string.IsNullOrWhiteSpace(update.RawLine)) job.AppendLog(update.RawLine);
    }

    private void SetFailure(DownloadJob job, string status)
    {
        SetGameState(job, DownloadJobState.Failed);
        job.Status = status;
        job.Finished = DateTime.Now;
    }

    private void SetGameState(DownloadJob job, DownloadJobState state)
    {
        job.State = state;
        var game = _store.Games.FirstOrDefault(item => item.AppId == job.AppId);
        if (game is not null) game.CurrentState = state;
    }

    public void Dispose()
    {
        foreach (var cancellation in _cancellations.Values) cancellation.Cancel();
        _cancellations.Clear();
        _pauseRequested.Clear();
        _running.Clear();
    }
}
