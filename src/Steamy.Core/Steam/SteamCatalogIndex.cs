using System.Text.Json;
using System.Text.Json.Serialization;

namespace Steamy.Services;

public sealed record SteamCatalogEntry(
    [property: JsonPropertyName("appid")] int AppId,
    [property: JsonPropertyName("name")] string Name);
public sealed record SteamCatalogSearch(IReadOnlyList<SteamCatalogEntry> Items, int Total);

/// <summary>Reads Steamy's existing bundled catalog off the UI thread; search results are bounded.</summary>
public sealed class SteamCatalogIndex
{
    private readonly SteamCatalogEntry[] _entries;
    private SteamCatalogIndex(SteamCatalogEntry[] entries) => _entries = entries;
    public int Count => _entries.Length;

    public static async Task<SteamCatalogIndex> LoadAsync(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (stream.Length > 128 * 1024 * 1024) throw new InvalidDataException("The game catalog is too large.");
        var entries = await JsonSerializer.DeserializeAsync<SteamCatalogEntry[]>(stream, cancellationToken: token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The game catalog is empty.");
        return await Task.Run(() => new SteamCatalogIndex(entries.Where(e => e.AppId > 0 && !string.IsNullOrWhiteSpace(e.Name))
            .DistinctBy(e => e.AppId).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray()), token).ConfigureAwait(false);
    }

    public SteamCatalogSearch Search(string? input, int offset = 0, int limit = 120, CancellationToken token = default)
    {
        if (offset < 0 || limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var text = input?.Trim() ?? "";
        var id = TryParseAppId(text);
        var items = new List<SteamCatalogEntry>(limit);
        var total = 0;
        foreach (var entry in _entries)
        {
            token.ThrowIfCancellationRequested();
            if (id is { } selectedId ? entry.AppId != selectedId
                : text.Length > 0 && !entry.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            if (total++ >= offset && items.Count < limit) items.Add(entry);
        }
        return new(items, total);
    }

    public static int? TryParseAppId(string input)
    {
        if (int.TryParse(input.Trim(), out var id) && id > 0) return id;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Host != "store.steampowered.com") return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "app" && int.TryParse(parts[1], out id) && id > 0 ? id : null;
    }
}
