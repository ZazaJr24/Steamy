using System.IO;
using System.Text.Json;

namespace Steamy.Services;

/// <summary>One app that was shared: which exact file set, when and where to.</summary>
public sealed record ShareHistoryEntry(int AppId, string Fingerprint, string Target, DateTime SharedUtc, string? RemotePath);

/// <summary>
/// Remembers what was already shared, so "share everything new" never sends the same manifests
/// to the same repository twice. Stored as a small JSON file next to the settings.
/// </summary>
public sealed class ShareHistoryStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, ShareHistoryEntry>? _entries;

    public ShareHistoryStore(string path) => _path = path;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy", "share-history.json");

    public ShareHistoryEntry? Find(string target, string fingerprint)
    {
        lock (_gate) return Load().TryGetValue(Key(target, fingerprint), out var entry) ? entry : null;
    }

    public int Count
    {
        get { lock (_gate) return Load().Count; }
    }

    public DateTime? LastSharedUtc
    {
        get
        {
            lock (_gate)
            {
                var entries = Load();
                return entries.Count == 0 ? null : entries.Values.Max(entry => entry.SharedUtc);
            }
        }
    }

    public void Record(IEnumerable<ShareHistoryEntry> shared)
    {
        lock (_gate)
        {
            var entries = Load();
            foreach (var entry in shared) entries[Key(entry.Target, entry.Fingerprint)] = entry;
            Save(entries);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries = new Dictionary<string, ShareHistoryEntry>(StringComparer.OrdinalIgnoreCase);
            Save(_entries);
        }
    }

    private static string Key(string target, string fingerprint) => $"{target.Trim().ToLowerInvariant()}|{fingerprint}";

    private Dictionary<string, ShareHistoryEntry> Load()
    {
        if (_entries is not null) return _entries;
        _entries = new Dictionary<string, ShareHistoryEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_path))
            {
                var list = JsonSerializer.Deserialize<List<ShareHistoryEntry>>(File.ReadAllText(_path)) ?? new List<ShareHistoryEntry>();
                foreach (var entry in list.Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Fingerprint)))
                    _entries[Key(entry.Target ?? string.Empty, entry.Fingerprint)] = entry;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged history only means items show as "new" again.
        }

        return _entries;
    }

    private void Save(Dictionary<string, ShareHistoryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries.Values.OrderBy(entry => entry.SharedUtc).ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember a share is not worth failing the share for.
        }
    }
}
