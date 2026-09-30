using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Steamy.Services;

public static class GitHubManifestIndex
{
    public static HashSet<int> Read(string json, bool zipArchives)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean())
            throw new InvalidDataException("GitHub returned an incomplete source index.");
        var ids = new HashSet<int>();
        foreach (var item in document.RootElement.GetProperty("tree").EnumerateArray())
        {
            var path = item.GetProperty("path").GetString() ?? "";
            var type = item.GetProperty("type").GetString();
            if (zipArchives)
            {
                if (type != "blob" || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                path = path[..^4];
            }
            else if (type != "tree") continue;
            if (int.TryParse(path, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && id > 0 && path == id.ToString(CultureInfo.InvariantCulture)) ids.Add(id);
        }
        return ids;
    }

    public static bool Matches(string downloadMode, string source) => source switch
    {
        "All sources" => true,
        "DepotDownloader" => string.Equals(downloadMode, "DepotDownloader", StringComparison.OrdinalIgnoreCase),
        "Custom archive" => downloadMode.Contains("archive", StringComparison.OrdinalIgnoreCase),
        _ => downloadMode.Contains($"({source})", StringComparison.OrdinalIgnoreCase)
    };
}
