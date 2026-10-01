using System.IO;
using Microsoft.Data.Sqlite;
using Steamy.Models;

namespace Steamy.Services;

/// <summary>One persisted queue row. Only non-secret job metadata is stored.</summary>
public sealed record PersistedDownloadJob(
    Guid Id,
    int AppId,
    string GameName,
    string CoverColor,
    string CoverGlyph,
    string TargetFolder,
    int? DepotId,
    string Branch,
    string ManifestId,
    bool AuthorizationConfirmed,
    string State,
    double Progress,
    string Status,
    string TotalSize,
    string Downloaded,
    DownloadPriority Priority,
    DateTime Started,
    DateTime UpdatedAt,
    string CoverImageUrl = "",
    string DownloadMode = "DepotDownloader",
    long QueuePosition = 0);

public interface ILocalDatabase
{
    void Initialize();
    Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default);
    Task AppendLogAsync(LogEntry entry, CancellationToken cancellationToken = default);
    Task SaveDownloadJobAsync(PersistedDownloadJob job, CancellationToken cancellationToken = default);
    Task DeleteDownloadJobAsync(Guid id, CancellationToken cancellationToken = default);
    Task ClearAllDownloadJobsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PersistedDownloadJob>> LoadDownloadJobsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Optional batching capability; older database adapters can retain AppendLogAsync.</summary>
public interface ILogBatchDatabase
{
    Task AppendLogsAsync(IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken = default);
}

public interface IDownloadQueueOrderingDatabase
{
    Task SaveDownloadPositionsAsync(IReadOnlyList<DownloadQueuePosition> positions, CancellationToken cancellationToken = default);
    Task SaveDownloadPriorityAsync(DownloadQueuePriority priority, CancellationToken cancellationToken = default);
}

/// <summary>
/// Small local SQLite store for non-secret application state. Secrets are deliberately
/// excluded and are handled by ISecureCredentialService instead.
/// </summary>
public sealed class SqliteLocalDatabase : ILocalDatabase, ILogBatchDatabase, IDownloadQueueOrderingDatabase
{
    private readonly string _databasePath;
    private readonly object _initializeLock = new();
    private bool _initialized;

    public SqliteLocalDatabase(string? databasePath = null) => _databasePath = databasePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy", "content-manager.db");

    public void Initialize()
    {
        if (_initialized) return;
        lock (_initializeLock)
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS Games (AppId INTEGER PRIMARY KEY, Name TEXT NOT NULL, InstallState TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Depots (DepotId INTEGER PRIMARY KEY, AppId INTEGER NOT NULL, Name TEXT NOT NULL, Status TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Manifests (Id INTEGER PRIMARY KEY AUTOINCREMENT, AppId INTEGER NOT NULL, DepotId INTEGER NOT NULL, FileName TEXT NOT NULL, Hash TEXT NOT NULL, ValidationStatus TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Branches (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Build TEXT NOT NULL, Status TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS DownloadJobs (Id TEXT PRIMARY KEY, AppId INTEGER NOT NULL, State TEXT NOT NULL, Progress REAL NOT NULL, UpdatedAt TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS DownloadFiles (Id INTEGER PRIMARY KEY AUTOINCREMENT, JobId TEXT NOT NULL, Path TEXT NOT NULL, Size INTEGER NOT NULL, State TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Achievements (Id INTEGER PRIMARY KEY AUTOINCREMENT, AppId INTEGER NOT NULL, Name TEXT NOT NULL, Unlocked INTEGER NOT NULL, Progress INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS Providers (Name TEXT PRIMARY KEY, BaseUrl TEXT NOT NULL, State TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Logs (Id INTEGER PRIMARY KEY AUTOINCREMENT, Timestamp TEXT NOT NULL, Level TEXT NOT NULL, Component TEXT NOT NULL, Message TEXT NOT NULL, AppId INTEGER NULL, JobId TEXT NULL);
                CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS QueueJobs (Id TEXT PRIMARY KEY, AppId INTEGER NOT NULL, GameName TEXT NOT NULL, CoverColor TEXT NOT NULL, CoverGlyph TEXT NOT NULL, TargetFolder TEXT NOT NULL, DepotId INTEGER NULL, Branch TEXT NOT NULL, ManifestId TEXT NOT NULL, AuthorizationConfirmed INTEGER NOT NULL, State TEXT NOT NULL, Progress REAL NOT NULL, Status TEXT NOT NULL, TotalSize TEXT NOT NULL, Downloaded TEXT NOT NULL, Priority TEXT NOT NULL, Started TEXT NOT NULL, UpdatedAt TEXT NOT NULL, CoverImageUrl TEXT NOT NULL DEFAULT '', DownloadMode TEXT NOT NULL DEFAULT 'DepotDownloader');
                """;
            command.ExecuteNonQuery();

            // Check existing columns explicitly: a locked or unwritable database must not
            // be mistaken for an already-applied migration.
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "PRAGMA table_info(QueueJobs);";
                using var reader = schema.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(1));
            }
            foreach (var (name, definition) in new[]
            {
                ("CoverImageUrl", "TEXT NOT NULL DEFAULT ''"),
                ("DownloadMode", "TEXT NOT NULL DEFAULT 'DepotDownloader'"),
                ("QueuePosition", "INTEGER NOT NULL DEFAULT 0")
            })
            {
                if (columns.Contains(name)) continue;
                using var migration = connection.CreateCommand();
                migration.CommandText = $"ALTER TABLE QueueJobs ADD COLUMN {name} {definition};";
                migration.ExecuteNonQuery();
            }

            _initialized = true;
        }
    }

    public async Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Initialize();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Settings(Key, Value, UpdatedAt) VALUES ($key, $value, $updated) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value, UpdatedAt = excluded.UpdatedAt;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task AppendLogAsync(LogEntry entry, CancellationToken cancellationToken = default) =>
        AppendLogsAsync(new[] { entry }, cancellationToken);

    public async Task AppendLogsAsync(IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0) return;
        Initialize();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Logs(Timestamp, Level, Component, Message, AppId, JobId) VALUES ($timestamp, $level, $component, $message, $appid, $jobid);";
        var timestamp = command.Parameters.Add("$timestamp", SqliteType.Text);
        var level = command.Parameters.Add("$level", SqliteType.Text);
        var component = command.Parameters.Add("$component", SqliteType.Text);
        var message = command.Parameters.Add("$message", SqliteType.Text);
        var appId = command.Parameters.Add("$appid", SqliteType.Integer);
        var jobId = command.Parameters.Add("$jobid", SqliteType.Text);
        command.Prepare();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timestamp.Value = entry.Timestamp.ToUniversalTime().ToString("O");
            level.Value = entry.Level.ToString();
            component.Value = entry.Component;
            message.Value = entry.Message;
            appId.Value = entry.AppId is null ? DBNull.Value : entry.AppId;
            jobId.Value = entry.JobId is null ? DBNull.Value : entry.JobId.ToString()!;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveDownloadJobAsync(PersistedDownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        Initialize();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO QueueJobs(Id, AppId, GameName, CoverColor, CoverGlyph, TargetFolder, DepotId, Branch, ManifestId, AuthorizationConfirmed, State, Progress, Status, TotalSize, Downloaded, Priority, Started, UpdatedAt, CoverImageUrl, DownloadMode, QueuePosition)
            VALUES ($id, $appid, $game, $color, $glyph, $folder, $depot, $branch, $manifest, $authorized, $state, $progress, $status, $total, $downloaded, $priority, $started, $updated, $coverurl, $mode, $position)
            ON CONFLICT(Id) DO UPDATE SET AppId = excluded.AppId, GameName = excluded.GameName, CoverColor = excluded.CoverColor, CoverGlyph = excluded.CoverGlyph, TargetFolder = excluded.TargetFolder, DepotId = excluded.DepotId, Branch = excluded.Branch, ManifestId = excluded.ManifestId, AuthorizationConfirmed = excluded.AuthorizationConfirmed, State = excluded.State, Progress = excluded.Progress, Status = excluded.Status, TotalSize = excluded.TotalSize, Downloaded = excluded.Downloaded, Priority = excluded.Priority, Started = excluded.Started, UpdatedAt = excluded.UpdatedAt, CoverImageUrl = excluded.CoverImageUrl, DownloadMode = excluded.DownloadMode, QueuePosition = excluded.QueuePosition;
            """;
        command.Parameters.AddWithValue("$id", job.Id.ToString());
        command.Parameters.AddWithValue("$appid", job.AppId);
        command.Parameters.AddWithValue("$game", job.GameName);
        command.Parameters.AddWithValue("$color", job.CoverColor);
        command.Parameters.AddWithValue("$glyph", job.CoverGlyph);
        command.Parameters.AddWithValue("$folder", job.TargetFolder);
        command.Parameters.AddWithValue("$depot", job.DepotId is null ? DBNull.Value : job.DepotId.Value);
        command.Parameters.AddWithValue("$branch", job.Branch);
        command.Parameters.AddWithValue("$manifest", job.ManifestId);
        command.Parameters.AddWithValue("$authorized", job.AuthorizationConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$state", job.State.ToString());
        command.Parameters.AddWithValue("$progress", job.Progress);
        command.Parameters.AddWithValue("$status", job.Status);
        command.Parameters.AddWithValue("$total", job.TotalSize);
        command.Parameters.AddWithValue("$downloaded", job.Downloaded);
        command.Parameters.AddWithValue("$priority", job.Priority.ToString());
        command.Parameters.AddWithValue("$started", job.Started.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$coverurl", job.CoverImageUrl);
        command.Parameters.AddWithValue("$mode", job.DownloadMode);
        command.Parameters.AddWithValue("$position", job.QueuePosition);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteDownloadJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Initialize();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM QueueJobs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveDownloadPositionsAsync(IReadOnlyList<DownloadQueuePosition> positions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(positions);
        Initialize();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE QueueJobs SET QueuePosition = $position WHERE Id = $id;";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var position = command.Parameters.Add("$position", SqliteType.Integer);
        foreach (var row in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            id.Value = row.JobId.ToString();
            position.Value = row.Position;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("A waiting download no longer exists in the stored queue.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveDownloadPriorityAsync(DownloadQueuePriority priority, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(priority);
        Initialize();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE QueueJobs SET Priority = $priority WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", priority.JobId.ToString());
        command.Parameters.AddWithValue("$priority", priority.Priority.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("This download no longer exists in the stored queue.");
    }

    public async Task ClearAllDownloadJobsAsync(CancellationToken cancellationToken = default)
    {
        Initialize();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM QueueJobs;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersistedDownloadJob>> LoadDownloadJobsAsync(CancellationToken cancellationToken = default)
    {
        Initialize();
        var jobs = new List<PersistedDownloadJob>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, AppId, GameName, CoverColor, CoverGlyph, TargetFolder, DepotId, Branch, ManifestId, AuthorizationConfirmed, State, Progress, Status, TotalSize, Downloaded, Priority, Started, UpdatedAt, CoverImageUrl, DownloadMode, QueuePosition FROM QueueJobs ORDER BY UpdatedAt DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Guid.TryParse(reader.GetString(0), out var id)) continue;
            
            jobs.Add(new PersistedDownloadJob(
                id,
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt32(9) != 0,
                reader.GetString(10),
                reader.GetDouble(11),
                reader.GetString(12),
                reader.GetString(13),
                reader.GetString(14),
                Enum.TryParse<DownloadPriority>(reader.GetString(15), out var priority) ? priority : DownloadPriority.Normal,
                DateTime.TryParse(reader.GetString(16), null, System.Globalization.DateTimeStyles.RoundtripKind, out var started) ? started.ToLocalTime() : DateTime.Now,
                DateTime.TryParse(reader.GetString(17), null, System.Globalization.DateTimeStyles.RoundtripKind, out var updated) ? updated.ToLocalTime() : DateTime.Now,
                reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
                reader.IsDBNull(19) ? "DepotDownloader" : reader.GetString(19),
                reader.IsDBNull(20) ? 0 : reader.GetInt64(20)));
        }

        return jobs;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        return connection;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadWriteCreate;Cache=Shared");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
