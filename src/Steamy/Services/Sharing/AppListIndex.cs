using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Steamy.Services;

/// <summary>
/// Names and depot owners for app ids, read from the bundled Steam app list (<c>Data/appid.json</c>).
/// The list is streamed and only the ids a scan asks for are kept, so a lookup never holds the
/// whole 250 000-entry list in memory. Answers are cached for the rest of the session.
/// </summary>
public sealed class AppListIndex
{
    // Depots of a game are numbered right after its app id (app 1245620 → depots 1245621, 1245622 …).
    public const int DepotSearchWindow = 10;

    private readonly string? _path;
    private readonly Dictionary<int, string?> _known = new();
    private readonly object _gate = new();

    public AppListIndex(string? path) => _path = path;

    /// <summary>Bundled list next to the executable, if it shipped.</summary>
    public static string? BundledPath
    {
        get
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, "Data", "appid.json"),
                         Path.Combine(AppContext.BaseDirectory, "appid.json")
                     })
            {
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }
    }

    /// <summary>Makes sure the given app ids (and the ids a depot could belong to) are looked up.</summary>
    public void Prepare(IEnumerable<int> appIds, IEnumerable<uint> depots)
    {
        var wanted = new HashSet<int>(appIds.Where(id => id > 0));
        foreach (var depot in depots)
        {
            if (depot > int.MaxValue) continue;
            for (var id = (int)depot; id >= Math.Max(1, (int)depot - DepotSearchWindow); id--) wanted.Add(id);
        }

        lock (_gate)
        {
            wanted.RemoveWhere(_known.ContainsKey);
            if (wanted.Count == 0) return;
            foreach (var id in wanted) _known[id] = null;
            if (_path is null || !File.Exists(_path)) return;

            try
            {
                using var stream = File.OpenRead(_path);
                foreach (var entry in JsonSerializer.DeserializeAsyncEnumerable<Entry>(stream).ToBlockingEnumerable())
                {
                    if (entry is not null && wanted.Contains(entry.AppId) && !string.IsNullOrWhiteSpace(entry.Name))
                        _known[entry.AppId] = entry.Name.Trim();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Without the list, apps keep their placeholder names.
            }
        }
    }

    /// <summary>The app's name, or null when the list does not know it (call <see cref="Prepare"/> first).</summary>
    public string? NameOf(int appId)
    {
        lock (_gate) return _known.TryGetValue(appId, out var name) ? name : null;
    }

    /// <summary>
    /// The app a depot most likely belongs to: the depot itself when it is an app (DLC depots are),
    /// otherwise the closest app id right below it. Null when none is close enough.
    /// </summary>
    public int? AppForDepot(uint depot)
    {
        if (depot > int.MaxValue) return null;
        lock (_gate)
        {
            for (var id = (int)depot; id >= Math.Max(1, (int)depot - DepotSearchWindow); id--)
            {
                if (_known.TryGetValue(id, out var name) && name is not null) return id;
            }
        }

        return null;
    }

    private sealed record Entry([property: JsonPropertyName("appid")] int AppId, [property: JsonPropertyName("name")] string? Name);
}
