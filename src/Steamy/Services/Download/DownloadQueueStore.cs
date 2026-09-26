using System.Collections.ObjectModel;
using System.IO;
using Steamy.Models;

namespace Steamy.Services;

/// <summary>
/// Keeps the download queue across restarts. Only non-secret job metadata is stored, and a job
/// that was interrupted by a shutdown is restored as interrupted instead of pretending it
/// finished, so the user decides whether it continues into the same folder.
/// </summary>
public interface IDownloadQueueStore
{
    Task RestoreAsync(ObservableCollection<DownloadJob> jobs, bool autoResume, CancellationToken cancellationToken = default);
    Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default);
    Task RemoveAsync(DownloadJob job, CancellationToken cancellationToken = default);
}

public sealed class DownloadQueueStore : IDownloadQueueStore
{
    private readonly ILocalDatabase _database;
    private readonly ILoggingService _logging;

    public DownloadQueueStore(ILocalDatabase database, ILoggingService logging)
    {
        _database = database;
        _logging = logging;
    }

    public async Task RestoreAsync(ObservableCollection<DownloadJob> jobs, bool autoResume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        IReadOnlyList<PersistedDownloadJob> rows;
        try
        {
            // No ConfigureAwait(false) here: the collection is bound to the UI, so it must be
            // filled on the thread that called this method.
            rows = await _database.LoadDownloadJobsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _logging.Add(LogLevel.Warning, "DownloadManager", $"The stored download queue could not be read: {exception.GetType().Name}.");
            return;
        }

        var restored = 0;
        foreach (var row in rows)
        {
            if (jobs.Any(job => job.Id == row.Id)) continue;
            jobs.Add(ToJob(row, autoResume));
            restored++;
        }

        if (restored > 0)
        {
            _logging.Add(LogLevel.Info, "DownloadManager", $"{restored} stored job(s) restored; interrupted jobs keep their target folder.");
        }
    }

    public async Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            await _database.SaveDownloadJobAsync(ToRow(job), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _logging.Add(LogLevel.Debug, "DownloadManager", $"Queue state could not be stored: {exception.GetType().Name}.");
        }
    }

    public async Task RemoveAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            await _database.DeleteDownloadJobAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _logging.Add(LogLevel.Debug, "DownloadManager", $"Queue entry could not be removed: {exception.GetType().Name}.");
        }
    }

    public static DownloadJob ToJob(PersistedDownloadJob row, bool autoResume)
    {
        ArgumentNullException.ThrowIfNull(row);

        var job = new DownloadJob
        {
            Id = row.Id,
            AppId = row.AppId,
            GameName = row.GameName,
            CoverColor = row.CoverColor,
            CoverGlyph = row.CoverGlyph,
            CoverImageUrl = row.CoverImageUrl,
            TargetFolder = row.TargetFolder,
            DepotId = row.DepotId,
            Branch = row.Branch,
            ManifestId = row.ManifestId,
            AuthorizationConfirmed = true,
            TotalSize = row.TotalSize,
            Started = row.Started,
            Priority = row.Priority,
            Downloaded = row.Downloaded
        };

        if (!Enum.TryParse<DownloadJobState>(row.State, out var state)) state = DownloadJobState.Queued;

        if (state is DownloadJobState.Preparing or DownloadJobState.Downloading or DownloadJobState.Verifying)
        {
            job.State = autoResume ? DownloadJobState.Queued : DownloadJobState.Paused;
            job.Status = autoResume
                ? "Interrupted while the app was closed — queued to continue in the same folder"
                : "Interrupted while the app was closed — press Resume to continue in the same folder";
            job.Progress = Math.Clamp(row.Progress, 0, 100);
            return job;
        }

        job.State = state;
        job.Progress = Math.Clamp(row.Progress, 0, 100);
        job.Status = row.Status;
        return job;
    }

    public static PersistedDownloadJob ToRow(DownloadJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new PersistedDownloadJob(
            job.Id,
            job.AppId,
            job.GameName,
            job.CoverColor,
            job.CoverGlyph,
            job.TargetFolder,
            job.DepotId,
            job.Branch,
            job.ManifestId,
            job.AuthorizationConfirmed,
            job.State.ToString(),
            job.Progress,
            job.Status,
            job.TotalSize,
            job.Downloaded,
            job.Priority,
            job.Started,
            DateTime.Now,
            job.CoverImageUrl);
    }
}
