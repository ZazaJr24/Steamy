using System.IO;
using System.Text.RegularExpressions;

namespace Steamy.Services;

/// <summary>
/// Finds everything the user can share: the folders the Depot Dumper wrote and the manifests
/// Steam keeps for the games installed in the local library. Read-only; nothing is modified.
/// </summary>
public static class ManifestLibraryScanner
{
    /// <summary>File types a dump folder contributes to a share.</summary>
    public static readonly IReadOnlyList<string> DumpExtensions = new[] { ".lua", ".manifest", ".key", ".acf" };

    // Shared runtimes every game pulls in; they are not games and would only add noise.
    private static readonly HashSet<int> IgnoredApps = new() { 228980 };

    private static readonly Regex AppFolderPattern = new(@"^app-(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Reads the app id from a dump folder name such as <c>app-730</c>.</summary>
    public static int? ParseAppFolder(string folderName)
    {
        var match = AppFolderPattern.Match(folderName ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var appId) && appId > 0 ? appId : null;
    }

    /// <summary>Every <c>app-*</c> folder below the given roots that holds at least one dump file.</summary>
    public static IReadOnlyList<ShareCandidate> ScanDumpRoots(IEnumerable<string> roots, Func<int, string?>? nameLookup = null)
    {
        var result = new List<ShareCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] folders;
            try
            {
                if (!Directory.Exists(root)) continue;
                folders = Directory.GetDirectories(root, "app-*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            foreach (var folder in folders)
            {
                var appId = ParseAppFolder(Path.GetFileName(folder));
                if (appId is null || !seen.Add(Path.GetFullPath(folder))) continue;

                var candidate = ScanDumpFolder(folder, appId.Value, nameLookup);
                if (candidate is not null) result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>One dump folder as a candidate, or null when it holds nothing to share.</summary>
    public static ShareCandidate? ScanDumpFolder(string folder, int appId, Func<int, string?>? nameLookup = null)
    {
        List<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path => DumpExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var files = new List<ShareFile>();
        var newest = DateTime.MinValue;
        foreach (var path in paths)
        {
            var info = TryInfo(path);
            if (info is null) continue;
            files.Add(new ShareFile(path, Path.GetRelativePath(folder, path).Replace('\\', '/'), info.Length));
            if (info.LastWriteTimeUtc > newest) newest = info.LastWriteTimeUtc;
        }

        if (files.Count == 0) return null;

        var name = nameLookup?.Invoke(appId);
        return new ShareCandidate(appId, string.IsNullOrWhiteSpace(name) ? $"App {appId}" : name!, ShareSourceKind.Dump,
            folder, files.OrderBy(file => file.EntryName, StringComparer.OrdinalIgnoreCase).ToList(),
            Array.Empty<DepotManifestRef>(), newest);
    }

    /// <summary>
    /// The installed games of the local Steam library whose depot manifests Steam still has in a
    /// <c>depotcache</c> folder. Only manifests are collected; nothing from Steam's configuration
    /// (accounts, keys, paths) is read.
    /// </summary>
    public static IReadOnlyList<ShareCandidate> ScanSteamLibrary(string steamRoot, IEnumerable<string> steamAppsFolders)
    {
        var folders = steamAppsFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var cache = IndexDepotCaches(new[] { Path.Combine(steamRoot, "depotcache") }
            .Concat(folders.Select(folder => Path.Combine(folder, "depotcache"))));

        var result = new List<ShareCandidate>();
        var seen = new HashSet<int>();

        foreach (var folder in folders)
        {
            string[] acfs;
            try
            {
                acfs = Directory.GetFiles(folder, "appmanifest_*.acf", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var acf in acfs.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var candidate = ReadInstalledApp(acf, folder, cache);
                if (candidate is not null && seen.Add(candidate.AppId)) result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>Reads the InstalledDepots block of one appmanifest and matches it with the cache.</summary>
    public static ShareCandidate? ReadInstalledApp(string acfPath, string steamAppsFolder, IReadOnlyDictionary<string, FileInfo> depotCache)
    {
        VdfNode state;
        try
        {
            var root = VdfParser.Parse(File.ReadAllText(acfPath));
            state = root["AppState"] ?? root;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var appId = state.GetInt("appid");
        if (appId <= 0 || IgnoredApps.Contains(appId)) return null;

        var depots = new List<DepotManifestRef>();
        var files = new List<ShareFile>();
        var newest = DateTime.MinValue;

        foreach (var depot in state["InstalledDepots"]?.Children ?? Array.Empty<VdfNode>())
        {
            if (!uint.TryParse(depot.Key, out var depotId)) continue;
            var manifest = depot.GetString("manifest")?.Trim();
            if (string.IsNullOrEmpty(manifest) || manifest == "0") continue;

            depots.Add(new DepotManifestRef(depotId, manifest, depot.GetLong("size")));

            var fileName = $"{depotId}_{manifest}.manifest";
            if (!depotCache.TryGetValue(fileName, out var info)) continue;
            files.Add(new ShareFile(info.FullName, fileName, info.Length));
            if (info.LastWriteTimeUtc > newest) newest = info.LastWriteTimeUtc;
        }

        if (files.Count == 0) return null;

        var name = state.GetString("name");
        return new ShareCandidate(appId, string.IsNullOrWhiteSpace(name) ? $"App {appId}" : name!.Trim(),
            ShareSourceKind.SteamLibrary, steamAppsFolder, files, depots, newest);
    }

    /// <summary>File name → file of every <c>*.manifest</c> in the given cache folders.</summary>
    public static IReadOnlyDictionary<string, FileInfo> IndexDepotCaches(IEnumerable<string> cacheFolders)
    {
        var index = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in cacheFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var path in Directory.EnumerateFiles(folder, "*.manifest", SearchOption.TopDirectoryOnly))
                {
                    var info = TryInfo(path);
                    if (info is not null) index.TryAdd(info.Name, info);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unreadable cache folder only means fewer candidates.
            }
        }

        return index;
    }

    private static FileInfo? TryInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
