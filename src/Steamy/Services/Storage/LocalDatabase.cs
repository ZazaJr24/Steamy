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
    string CoverImageUrl = "");

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

/// <summary>
/// Small local SQLite store for non-secret application state. Secrets are deliberately
/// excluded and are handled by ISecureCredentialService instead.
/// </summary>
public sealed class SqliteLocalDatabase : ILocalDatabase
{
    private readonly string _databasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Steamy",
        "content-manager.db");
    private readonly object _initializeLock = new();
    private bool _initialized;

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
                CREATE TABLE IF NOT EXISTS QueueJobs (Id TEXT PRIMARY KEY, AppId INTEGER NOT NULL, GameName TEXT NOT NULL, CoverColor TEXT NOT NULL, CoverGlyph TEXT NOT NULL, TargetFolder TEXT NOT NULL, DepotId INTEGER NULL, Branch TEXT NOT NULL, ManifestId TEXT NOT NULL, AuthorizationConfirmed INTEGER NOT NULL, State TEXT NOT NULL, Progress REAL NOT NULL, Status TEXT NOT NULL, TotalSize TEXT NOT NULL, Downloaded TEXT NOT NULL, Priority TEXT NOT NULL, Started TEXT NOT NULL, UpdatedAt TEXT NOT NULL, CoverImageUrl TEXT NOT NULL DEFAULT '');
                """;
            command.ExecuteNonQuery();

            // Migrate existing databases that lack the CoverImageUrl column.
            using var migrate = connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE QueueJobs ADD COLUMN CoverImageUrl TEXT NOT NULL DEFAULT '';";
            try { migrate.ExecuteNonQuery(); } catch (Microsoft.Data.Sqlite.SqliteException) { }

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

    public async Task AppendLogAsync(LogEntry entry, CancellationToken cancellationToken = default)
    {
        Initialize();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Logs(Timestamp, Level, Component, Message, AppId, JobId) VALUES ($timestamp, $level, $component, $message, $appid, $jobid);";
        command.Parameters.AddWithValue("$timestamp", entry.Timestamp.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$level", entry.Level.ToString());
        command.Parameters.AddWithValue("$component", entry.Component);
        command.Parameters.AddWithValue("$message", entry.Message);
        command.Parameters.AddWithValue("$appid", entry.AppId is null ? DBNull.Value : entry.AppId);
        command.Parameters.AddWithValue("$jobid", entry.JobId is null ? DBNull.Value : entry.JobId.ToString()!);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveDownloadJobAsync(PersistedDownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        Initialize();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO QueueJobs(Id, AppId, GameName, CoverColor, CoverGlyph, TargetFolder, DepotId, Branch, ManifestId, AuthorizationConfirmed, State, Progress, Status, TotalSize, Downloaded, Priority, Started, UpdatedAt, CoverImageUrl)
            VALUES ($id, $appid, $game, $color, $glyph, $folder, $depot, $branch, $manifest, $authorized, $state, $progress, $status, $total, $downloaded, $priority, $started, $updated, $coverurl)
            ON CONFLICT(Id) DO UPDATE SET AppId = excluded.AppId, GameName = excluded.GameName, CoverColor = excluded.CoverColor, CoverGlyph = excluded.CoverGlyph, TargetFolder = excluded.TargetFolder, DepotId = excluded.DepotId, Branch = excluded.Branch, ManifestId = excluded.ManifestId, AuthorizationConfirmed = excluded.AuthorizationConfirmed, State = excluded.State, Progress = excluded.Progress, Status = excluded.Status, TotalSize = excluded.TotalSize, Downloaded = excluded.Downloaded, Priority = excluded.Priority, Started = excluded.Started, UpdatedAt = excluded.UpdatedAt, CoverImageUrl = excluded.CoverImageUrl;
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
        command.CommandText = "SELECT Id, AppId, GameName, CoverColor, CoverGlyph, TargetFolder, DepotId, Branch, ManifestId, AuthorizationConfirmed, State, Progress, Status, TotalSize, Downloaded, Priority, Started, UpdatedAt, CoverImageUrl FROM QueueJobs ORDER BY UpdatedAt DESC;";
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
                reader.IsDBNull(18) ? string.Empty : reader.GetString(18)));
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
