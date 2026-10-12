using System.IO;
using System.Text.Json;

namespace Steamy.Playtime;

public sealed record TrackedGame(int AppId, string InstallDirectory);
public sealed record TrackerConfiguration(bool Enabled, string SteamRoot, IReadOnlyList<TrackedGame> Games);
public sealed record GamePlayTime(int AppId, long Seconds, bool IsRunning);
public sealed record PlayTimeSnapshot(int SchemaVersion, DateTimeOffset UpdatedAt, IReadOnlyList<GamePlayTime> Games)
{
    public static PlayTimeSnapshot Empty { get; } = new(1, DateTimeOffset.MinValue, []);
}

public static class PlayTimeFiles
{
    public static string DefaultDirectory => Steamy.Storage.AppDataPaths.Root;
    public const string ConfigurationFile = "playtime-config.json";
    public const string TimeFile = "playtime.json";
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return default;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 2 * 1024 * 1024) return default;
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public static string Format(long seconds)
    {
        var minutes = Math.Max(0, seconds) / 60;
        return minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60:00}m";
    }
}

/// <summary>Counts observed process runtime once per app. Launch requests alone add no time.</summary>
public sealed class PlayTimeAccumulator
{
    private readonly Dictionary<int, long> _ticks;
    private HashSet<int> _previous = [];
    private TimeSpan? _last;
    public PlayTimeAccumulator(PlayTimeSnapshot? saved = null)
    {
        _ticks = (saved is { SchemaVersion: 1, Games: not null } ? saved.Games : [])
            .Where(g => g.AppId != 0 && g.Seconds >= 0 && g.Seconds < 315_576_000)
            .DistinctBy(g => g.AppId).ToDictionary(g => g.AppId, g => g.Seconds * TimeSpan.TicksPerSecond);
    }
    public bool Sample(IEnumerable<int> appIds, TimeSpan awakeTime)
    {
        var current = appIds.Where(id => id != 0).ToHashSet();
        var changed = !_previous.SetEquals(current);
        if (_last is { } last && awakeTime - last is var elapsed && elapsed > TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(30))
            foreach (var id in current.Intersect(_previous))
                _ticks[id] = _ticks.GetValueOrDefault(id) + elapsed.Ticks;
        _last = awakeTime;
        _previous = current;
        return changed;
    }
    public PlayTimeSnapshot Snapshot(DateTimeOffset now) => new(1, now,
        _ticks.Keys.Concat(_previous).Distinct().Order().Select(id =>
            new GamePlayTime(id, _ticks.GetValueOrDefault(id) / TimeSpan.TicksPerSecond, _previous.Contains(id))).ToArray());
}

public static class GameProcessMatcher
{
    public static int? Match(string executable, IEnumerable<TrackedGame> games)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable)) return null;
        var name = Path.GetFileNameWithoutExtension(executable);
        if (name.Equals("steam", StringComparison.OrdinalIgnoreCase) || name.Equals("Steamy", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PlayTimeTracker", StringComparison.OrdinalIgnoreCase)
            || name.Equals("setup", StringComparison.OrdinalIgnoreCase)
            || new[] { "crashreport", "crashpad", "unitycrashhandler", "unins", "steamclient_loader" }
                .Any(part => name.StartsWith(part, StringComparison.OrdinalIgnoreCase))
            || new[] { "launcher", "updater", "webhelper", "anticheat" }
                .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) return null;
        try
        {
            var full = Path.GetFullPath(executable);
            return games.Where(g => g.AppId != 0 && Path.IsPathFullyQualified(g.InstallDirectory))
                .Select(g => (g.AppId, Root: Path.GetFullPath(g.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .Where(g => g.Root.Length > (Path.GetPathRoot(g.Root)?.Length ?? 0)
                    && full.StartsWith(g.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(g => g.Root.Length).Select(g => (int?)g.AppId).FirstOrDefault();
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
