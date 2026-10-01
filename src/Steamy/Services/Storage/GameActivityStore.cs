using System.IO;
using System.Text.Json;

namespace Steamy.Services;

public sealed record GameLaunch(int AppId, DateTimeOffset OpenedAt);

/// <summary>Local preferences and launch requests made through Steamy; this is not Steam playtime.</summary>
public sealed record GameActivitySnapshot(IReadOnlyList<int> FavoriteAppIds, IReadOnlyList<GameLaunch> Launches)
{
    public static GameActivitySnapshot Empty { get; } = new(Array.Empty<int>(), Array.Empty<GameLaunch>());
}

public interface IGameActivityService
{
    event EventHandler? Changed;
    Task<GameActivitySnapshot> GetAsync(CancellationToken cancellationToken = default);
    Task<bool> ToggleFavoriteAsync(int appId, CancellationToken cancellationToken = default);
    Task RecordLaunchAsync(int appId, DateTimeOffset? launchedAt = null, CancellationToken cancellationToken = default);
}

public sealed class JsonGameActivityService(string? path = null, TimeProvider? timeProvider = null) : IGameActivityService
{
    private const int MaximumFileBytes = 512 * 1024;
    private const int MaximumFavorites = 2000;
    private const int MaximumLaunches = 512;
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy", "game-activity.json");
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GameActivitySnapshot? _snapshot;
    public event EventHandler? Changed;

    public async Task<GameActivitySnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _snapshot ??= await ReadAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<bool> ToggleFavoriteAsync(int appId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(appId);
        bool favorite;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _snapshot ??= await ReadAsync(cancellationToken).ConfigureAwait(false);
            var favorites = previous.FavoriteAppIds.ToList();
            favorite = !favorites.Remove(appId);
            if (favorite)
            {
                if (favorites.Count >= MaximumFavorites) throw new InvalidOperationException("The favorite list is full.");
                favorites.Add(appId);
            }
            var next = new GameActivitySnapshot(favorites.AsReadOnly(), previous.Launches);
            await WriteAsync(next, cancellationToken).ConfigureAwait(false);
            _snapshot = next;
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        return favorite;
    }

    public async Task RecordLaunchAsync(int appId, DateTimeOffset? launchedAt = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(appId);
        var timestamp = (launchedAt ?? _clock.GetUtcNow()).ToUniversalTime();
        if (timestamp <= DateTimeOffset.UnixEpoch || timestamp > _clock.GetUtcNow().AddMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(launchedAt), "The launch date must describe a past or current request.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _snapshot ??= await ReadAsync(cancellationToken).ConfigureAwait(false);
            // Importing an older launch never makes the most recent request appear older.
            var existing = previous.Launches.FirstOrDefault(item => item.AppId == appId);
            if (existing is not null && existing.OpenedAt >= timestamp) return;
            var launches = previous.Launches.Where(item => item.AppId != appId)
                .Append(new GameLaunch(appId, timestamp)).OrderByDescending(item => item.OpenedAt)
                .Take(MaximumLaunches).ToList().AsReadOnly();
            var next = new GameActivitySnapshot(previous.FavoriteAppIds, launches);
            await WriteAsync(next, cancellationToken).ConfigureAwait(false);
            _snapshot = next;
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<GameActivitySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > MaximumFileBytes) return GameActivitySnapshot.Empty;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length > MaximumFileBytes) return GameActivitySnapshot.Empty;
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1)
                return GameActivitySnapshot.Empty;
            var favorites = new List<int>();
            if (root.TryGetProperty("favorites", out var savedFavorites) && savedFavorites.ValueKind == JsonValueKind.Array)
                foreach (var value in savedFavorites.EnumerateArray().Take(MaximumFavorites))
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id) && id > 0 && !favorites.Contains(id)) favorites.Add(id);
            var launches = new List<GameLaunch>();
            if (root.TryGetProperty("launches", out var savedLaunches) && savedLaunches.ValueKind == JsonValueKind.Array)
                foreach (var value in savedLaunches.EnumerateArray().Take(MaximumLaunches))
                    if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("appId", out var idElement) && idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt32(out var id) && id > 0
                        && value.TryGetProperty("openedAt", out var dateElement) && dateElement.ValueKind == JsonValueKind.String && dateElement.TryGetDateTimeOffset(out var date)
                        && date > DateTimeOffset.UnixEpoch && date <= _clock.GetUtcNow().AddMinutes(5))
                        launches.Add(new GameLaunch(id, date.ToUniversalTime()));
            return new GameActivitySnapshot(favorites.AsReadOnly(), launches.OrderByDescending(item => item.OpenedAt)
                .DistinctBy(item => item.AppId).ToList().AsReadOnly());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        { return GameActivitySnapshot.Empty; }
    }

    private async Task WriteAsync(GameActivitySnapshot snapshot, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var data = new
            {
                schemaVersion = 1,
                favorites = snapshot.FavoriteAppIds,
                launches = snapshot.Launches.Select(item => new { appId = item.AppId, openedAt = item.OpenedAt })
            };
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(data), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
