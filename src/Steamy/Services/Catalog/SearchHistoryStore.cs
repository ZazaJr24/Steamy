using System.IO;
using System.Text.Json;

namespace Steamy.Services;

/// <summary>Small, private local search history. Storage work runs away from the dispatcher.</summary>
public sealed class SearchHistoryStore
{
    public const int Capacity = 8;
    public static SearchHistoryStore Default { get; } = new();
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<string>? _queries;

    public SearchHistoryStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy", "search-history.json");

    public Task<IReadOnlyList<string>> GetAsync() => Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return (IReadOnlyList<string>)Load().ToArray(); }
        finally { _gate.Release(); }
    });

    public Task<IReadOnlyList<string>> RecordAsync(string? query) => UpdateAsync(query, clear: false);
    public Task<IReadOnlyList<string>> ClearAsync() => UpdateAsync(null, clear: true);

    private Task<IReadOnlyList<string>> UpdateAsync(string? query, bool clear) => Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var queries = Load();
            var cleaned = Clean(query);
            if (clear) queries.Clear();
            else if (cleaned.Length > 0)
            {
                queries.RemoveAll(existing => string.Equals(existing, cleaned, StringComparison.OrdinalIgnoreCase));
                queries.Insert(0, cleaned);
                if (queries.Count > Capacity) queries.RemoveRange(Capacity, queries.Count - Capacity);
            }
            else return (IReadOnlyList<string>)queries.ToArray();
            Save(queries);
            return (IReadOnlyList<string>)queries.ToArray();
        }
        finally { _gate.Release(); }
    });

    private List<string> Load()
    {
        if (_queries is not null) return _queries;
        _queries = new List<string>();
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 32_768) return _queries;
            var stored = JsonSerializer.Deserialize<List<string?>>(File.ReadAllText(_path));
            if (stored is not null) _queries.AddRange(stored.Select(Clean).Where(query => query.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(Capacity));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Damaged or read-only history must not prevent using search.
        }
        return _queries;
    }

    private void Save(List<string> queries)
    {
        var temporaryPath = _path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(queries));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Keep the in-memory list even when persistence is unavailable.
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Clean(string? query)
    {
        var cleaned = string.Join(" ", (query ?? string.Empty).Split((char[]?)null,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length is > 0 and <= 80 && !cleaned.Any(char.IsControl) ? cleaned : string.Empty;
    }
}
