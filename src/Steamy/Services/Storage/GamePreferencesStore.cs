using System.IO;
using System.Text.Json;

namespace Steamy.Services;

public sealed record GamePreferences(string Notes = "", string LaunchArguments = "", string LaunchExecutable = "");

public interface IGamePreferencesStore
{
    Task<GamePreferences> ReadAsync(int appId, CancellationToken token = default);
    Task SaveAsync(int appId, GamePreferences preferences, CancellationToken token = default);
}

public sealed class GamePreferencesStore(string? directory = null) : IGamePreferencesStore
{
    private readonly string _directory = directory ?? Path.Combine(Steamy.Storage.AppDataPaths.Root, "games");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string FilePath(int appId)
    {
        if (appId == 0) throw new ArgumentOutOfRangeException(nameof(appId));
        return Path.Combine(_directory, appId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
    }
    public async Task<GamePreferences> ReadAsync(int appId, CancellationToken token = default)
    {
        var path = FilePath(appId);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) return new();
            var value = JsonSerializer.Deserialize<GamePreferences>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
            return value is null ? new() : Normalize(value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(int appId, GamePreferences preferences, CancellationToken token = default)
    {
        var path = FilePath(appId);
        var value = Normalize(preferences);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            _gate.Release();
        }
    }
    private static GamePreferences Normalize(GamePreferences value)
    {
        var notes = value.Notes ?? "";
        var arguments = (value.LaunchArguments ?? "").Replace("\0", "", StringComparison.Ordinal);
        var executable = value.LaunchExecutable ?? "";
        return new(notes[..Math.Min(notes.Length, 32000)], arguments[..Math.Min(arguments.Length, 2048)], executable[..Math.Min(executable.Length, 32767)]);
    }
}
