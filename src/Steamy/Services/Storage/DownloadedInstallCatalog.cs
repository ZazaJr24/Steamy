using System.IO;
using Steamy.Playtime;

namespace Steamy.Services;

public sealed record DownloadedInstallation(int AppId, string Name, string Folder, string Source = "DepotDownloader", string Executable = "");

/// <summary>Durable local installations, independent of removable download-queue rows.</summary>
public sealed class DownloadedInstallCatalog(string path)
{
    private readonly SemaphoreSlim _gate = new(1);

    public async Task<IReadOnlyList<DownloadedInstallation>> DiscoverAsync(IEnumerable<DownloadedInstallation> completed,
        IEnumerable<string> roots, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var saved = await PlayTimeFiles.ReadAsync<List<DownloadedInstallation>>(path, token).ConfigureAwait(false) ?? [];
            var found = completed.Concat(saved).Where(IsPresent).ToList();
            foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var folder in Folders(root, 3, token))
                {
                    var marker = Path.Combine(folder, "steam_appid.txt");
                    try
                    {
                        if (!File.Exists(marker) || new FileInfo(marker).Length > 32
                            || !int.TryParse(File.ReadAllText(marker).Trim(), out var id) || id <= 0) continue;
                        // App identity alone does not prove a playable install; do not list metadata-only or empty downloads.
                        if (!Folders(folder, 3, token).Any(directory => Directory.EnumerateFiles(directory, "*.exe")
                            .Take(4096).Any(file => new FileInfo(file).Length >= 100_000 && Steamy.Playtime.GameProcessMatcher.Match(file, [new(id, folder)]) == id))) continue;
                        if (found.Any(game => game.AppId == id && (folder.Equals(game.Folder, StringComparison.OrdinalIgnoreCase)
                            || folder.StartsWith(Path.TrimEndingDirectorySeparator(game.Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))) continue;
                        found.Add(new(id, Path.GetFileName(folder), folder));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            var result = found.Select(g => g with { Folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.Folder)) })
                .GroupBy(g => g.Folder, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderBy(g => g.AppId < 0).First())
                .DistinctBy(g => (g.AppId, g.Folder.ToUpperInvariant())).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Take(5000).ToArray();
            if (!saved.SequenceEqual(result)) await PlayTimeFiles.WriteAsync(path, result, token).ConfigureAwait(false);
            return result;
        }
        finally { _gate.Release(); }
    }

    private static bool IsPresent(DownloadedInstallation game)
    {
        if (game is null || game.AppId == 0 || string.IsNullOrWhiteSpace(game.Folder) || !Path.IsPathFullyQualified(game.Folder) || !Directory.Exists(game.Folder)) return false;
        try
        {
            return Folders(game.Folder, 3, default).Any(directory => Directory.EnumerateFiles(directory).Take(4096)
                .Any(file => !Path.GetFileName(file).Equals("steam_appid.txt", StringComparison.OrdinalIgnoreCase) && new FileInfo(file).Length > 0));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> Folders(string root, int depth, CancellationToken token)
    {
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        while (pending.TryDequeue(out var item) && visited++ < 2048)
        {
            token.ThrowIfCancellationRequested();
            string[] children;
            try
            {
                if ((File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) != 0) continue;
                children = item.Depth < depth ? Directory.EnumerateDirectories(item.Path).Take(2048).ToArray() : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            yield return item.Path;
            foreach (var child in children.Take(2048))
                if (!new[] { ".DepotDownloader", ".git", "_CommonRedist", "redist", "Support", "Tools" }.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                    pending.Enqueue((child, item.Depth + 1));
        }
    }
}
