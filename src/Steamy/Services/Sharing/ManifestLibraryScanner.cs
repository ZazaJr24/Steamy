using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Steamy.Services;

/// <summary>Where the scanner looks. Missing folders are simply skipped.</summary>
public sealed record ShareScanInput(
    string SteamRoot,
    IReadOnlyList<string> SteamAppsFolders,
    IReadOnlyList<string> LuaFolders,
    IReadOnlyList<string> ExtraManifestFolders);

/// <summary>What a Lua script declares: its app, the depots it adds and the manifest ids it pins.</summary>
public sealed record LuaScript(int AppId, IReadOnlySet<uint> Depots, IReadOnlyDictionary<uint, string> Manifests);

/// <summary>
/// Finds everything the user can share — installed games, Lua scripts and every depot manifest
/// in the local caches, installed or not — and groups it into one candidate per app. Read-only:
/// nothing is modified, and nothing from Steam's configuration (accounts, keys, paths) is read.
/// </summary>
public static class ManifestLibraryScanner
{
    // Shared runtimes every game pulls in; they are not games and would only add noise.
    private static readonly HashSet<int> IgnoredApps = new() { 228980 };

    private static readonly Regex ManifestName = new(@"^(\d+)_(\d+)\.manifest$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AddAppId = new(@"addappid\s*\(\s*(\d+)\s*(,)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SetManifestId = new(@"setManifestid\s*\(\s*(\d+)\s*,\s*""?(\d+)""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AppFolderPattern = new(@"^app-(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Reads the app id from an old Depot Dumper folder name such as <c>app-730</c>.</summary>
    public static int? ParseAppFolder(string folderName)
    {
        var match = AppFolderPattern.Match(folderName ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var appId) && appId > 0 ? appId : null;
    }

    /// <summary>
    /// Scans everything and returns one candidate per app. With an <paramref name="apps"/> list,
    /// manifests that belong to no installed game and no Lua are assigned to their game, and
    /// placeholder names ("App 123") are replaced by the real ones.
    /// </summary>
    public static IReadOnlyList<ShareCandidate> ScanAll(ShareScanInput input, AppListIndex? apps = null)
    {
        var steamApps = input.SteamAppsFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Steam's own caches first, so their copy wins when the same manifest exists twice.
        var cacheFolders = new List<(string Folder, bool Recursive)>();
        if (input.SteamRoot.Length > 0)
        {
            cacheFolders.Add((Path.Combine(input.SteamRoot, "depotcache"), false));
            cacheFolders.Add((Path.Combine(input.SteamRoot, "config", "depotcache"), false));
        }
        cacheFolders.AddRange(steamApps.Select(folder => (Path.Combine(folder, "depotcache"), false)));
        cacheFolders.AddRange(input.ExtraManifestFolders.Select(folder => (folder, true)));
        cacheFolders.AddRange(input.LuaFolders.Select(folder => (folder, true)));
        var cache = IndexManifests(cacheFolders);

        var candidates = new List<ShareCandidate>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in steamApps)
        {
            foreach (var acf in SafeFiles(folder, "appmanifest_*.acf", recursive: false).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var candidate = ReadInstalledApp(acf, folder, cache);
                if (candidate is null) continue;
                candidates.Add(candidate);
                foreach (var file in candidate.Files) claimed.Add(file.EntryName);
            }
        }

        foreach (var lua in ScanLuas(input.LuaFolders, cache))
        {
            candidates.Add(lua);
            foreach (var file in lua.Files) claimed.Add(file.EntryName);
        }

        if (apps is not null)
        {
            var orphanDepots = cache.Keys.Where(name => !claimed.Contains(name))
                .Select(ParseManifestName).Where(parsed => parsed is not null).Select(parsed => parsed!.Value.Depot);
            apps.Prepare(candidates.Where(candidate => !HasRealName(candidate.Name)).Select(candidate => candidate.AppId), orphanDepots);
        }

        candidates.AddRange(GroupOrphans(cache, claimed, apps is null ? null : apps.AppForDepot));
        var merged = Merge(candidates);
        return apps is null ? merged : Merge(merged.Select(candidate => HasRealName(candidate.Name) || apps.NameOf(candidate.AppId) is not { } name
            ? candidate
            : candidate with { Name = name }));
    }

    /// <summary>File name → file of every <c>*.manifest</c> in the given folders.</summary>
    public static IReadOnlyDictionary<string, FileInfo> IndexManifests(IEnumerable<(string Folder, bool Recursive)> folders)
    {
        var index = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, recursive) in folders)
        {
            foreach (var path in SafeFiles(folder, "*.manifest", recursive))
            {
                var info = TryInfo(path);
                if (info is not null && ManifestName.IsMatch(info.Name)) index.TryAdd(info.Name, info);
            }
        }

        return index;
    }

    /// <summary>
    /// The installed app described by one appmanifest, with the depot manifests the cache has for
    /// it; null when the cache has none of them.
    /// </summary>
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
            if (!depotCache.TryGetValue($"{depotId}_{manifest}.manifest", out var info)) continue;
            files.Add(new ShareFile(info.FullName, info.Name, info.Length));
            if (info.LastWriteTimeUtc > newest) newest = info.LastWriteTimeUtc;
        }

        if (files.Count == 0) return null;

        var name = state.GetString("name");
        return new ShareCandidate(appId, string.IsNullOrWhiteSpace(name) ? $"App {appId}" : name!.Trim(),
            ShareSourceKind.SteamLibrary, steamAppsFolder, files, depots, newest);
    }

    /// <summary>
    /// Reads what a Lua script declares. The app is the first <c>addappid(id)</c> without further
    /// arguments (a depot line carries a key), falling back to the first id or the file name.
    /// </summary>
    public static LuaScript? ParseLua(string text, string? fileName = null)
    {
        int? mainApp = null;
        int? firstId = null;
        var depots = new HashSet<uint>();

        foreach (Match match in AddAppId.Matches(text ?? string.Empty))
        {
            if (!uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0 || id > int.MaxValue) continue;
            firstId ??= (int)id;
            if (!match.Groups[2].Success) mainApp ??= (int)id;
            else depots.Add(id);
        }

        var manifests = new Dictionary<uint, string>();
        foreach (Match match in SetManifestId.Matches(text ?? string.Empty))
        {
            if (!uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var depot)) continue;
            manifests[depot] = match.Groups[2].Value;
            depots.Add(depot);
        }

        var appId = mainApp ?? firstId;
        if (appId is null && fileName is not null
            && int.TryParse(Path.GetFileNameWithoutExtension(fileName), NumberStyles.None, CultureInfo.InvariantCulture, out var fromName) && fromName > 0)
            appId = fromName;

        if (appId is null) return null;
        depots.Remove((uint)appId.Value);
        return new LuaScript(appId.Value, depots, manifests);
    }

    /// <summary>
    /// One candidate per app that has a Lua script. When several scripts exist for the same app,
    /// the newest one is used. The manifests pinned with <c>setManifestid</c> come from the cache;
    /// a script without pins takes the manifests of its depots lying next to it.
    /// </summary>
    public static IReadOnlyList<ShareCandidate> ScanLuas(IEnumerable<string> luaFolders, IReadOnlyDictionary<string, FileInfo> cache)
    {
        var newestPerApp = new Dictionary<int, (FileInfo File, LuaScript Script)>();
        foreach (var path in luaFolders.SelectMany(folder => SafeFiles(folder, "*.lua", recursive: true)))
        {
            var info = TryInfo(path);
            if (info is null || info.Length > 4 * 1024 * 1024) continue;

            LuaScript? script;
            try { script = ParseLua(File.ReadAllText(info.FullName), info.Name); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }

            if (script is null || IgnoredApps.Contains(script.AppId)) continue;
            if (!newestPerApp.TryGetValue(script.AppId, out var known) || info.LastWriteTimeUtc > known.File.LastWriteTimeUtc)
                newestPerApp[script.AppId] = (info, script);
        }

        var result = new List<ShareCandidate>();
        foreach (var (appId, (lua, script)) in newestPerApp)
        {
            var files = new List<ShareFile> { new(lua.FullName, $"{appId}.lua", lua.Length) };
            var depots = new List<DepotManifestRef>();
            var newest = lua.LastWriteTimeUtc;

            void Add(FileInfo manifest, uint depot, string manifestId)
            {
                if (files.Any(file => string.Equals(file.EntryName, manifest.Name, StringComparison.OrdinalIgnoreCase))) return;
                files.Add(new ShareFile(manifest.FullName, manifest.Name, manifest.Length));
                depots.Add(new DepotManifestRef(depot, manifestId, 0));
                if (manifest.LastWriteTimeUtc > newest) newest = manifest.LastWriteTimeUtc;
            }

            foreach (var (depot, manifestId) in script.Manifests)
            {
                if (cache.TryGetValue($"{depot}_{manifestId}.manifest", out var manifest)) Add(manifest, depot, manifestId);
                else depots.Add(new DepotManifestRef(depot, manifestId, 0));
            }

            if (script.Manifests.Count == 0)
            {
                foreach (var path in SafeFiles(lua.DirectoryName!, "*.manifest", recursive: false))
                {
                    var manifest = TryInfo(path);
                    var parsed = manifest is null ? null : ParseManifestName(manifest.Name);
                    if (parsed is { } value && script.Depots.Contains(value.Depot)) Add(manifest!, value.Depot, value.ManifestId);
                }
            }

            result.Add(new ShareCandidate(appId, $"App {appId}", ShareSourceKind.Lua, lua.DirectoryName!,
                files, depots, newest));
        }

        return result;
    }

    /// <summary>Groups manifests nobody claimed into one candidate per app (or per depot when unknown).</summary>
    public static IReadOnlyList<ShareCandidate> GroupOrphans(IReadOnlyDictionary<string, FileInfo> cache, IReadOnlySet<string> claimed,
        Func<uint, int?>? depotToApp)
    {
        var groups = new Dictionary<int, List<(FileInfo File, uint Depot, string ManifestId)>>();
        foreach (var (name, info) in cache)
        {
            if (claimed.Contains(name) || ParseManifestName(name) is not { } parsed) continue;
            var appId = depotToApp?.Invoke(parsed.Depot) ?? (parsed.Depot <= int.MaxValue ? (int)parsed.Depot : 0);
            if (appId <= 0 || IgnoredApps.Contains(appId)) continue;
            if (!groups.TryGetValue(appId, out var list)) groups[appId] = list = new();
            list.Add((info, parsed.Depot, parsed.ManifestId));
        }

        return groups.Select(group =>
        {
            var ordered = group.Value.OrderBy(entry => entry.File.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var single = ordered.Select(entry => entry.Depot).Distinct().Count() == 1 && ordered[0].Depot == group.Key;
            return new ShareCandidate(group.Key, single ? $"Depot {group.Key}" : $"App {group.Key}", ShareSourceKind.Manifests,
                ordered[0].File.DirectoryName!,
                ordered.Select(entry => new ShareFile(entry.File.FullName, entry.File.Name, entry.File.Length)).ToList(),
                ordered.Select(entry => new DepotManifestRef(entry.Depot, entry.ManifestId, 0)).ToList(),
                ordered.Max(entry => entry.File.LastWriteTimeUtc));
        }).ToList();
    }

    /// <summary>
    /// Folds candidates of the same app into one: files and depots are joined, the strongest source
    /// (installed > Lua > manifests) and the best known name are kept.
    /// </summary>
    public static IReadOnlyList<ShareCandidate> Merge(IEnumerable<ShareCandidate> candidates) =>
        candidates.GroupBy(candidate => candidate.AppId).Select(group =>
        {
            var parts = group.OrderBy(candidate => candidate.Source).ToList();
            if (parts.Count == 1) return parts[0];

            var files = parts.SelectMany(candidate => candidate.Files)
                .GroupBy(file => file.EntryName, StringComparer.OrdinalIgnoreCase)
                .Select(same => same.First())
                .ToList();
            var depots = parts.SelectMany(candidate => candidate.Depots)
                .GroupBy(depot => (depot.DepotId, depot.ManifestId))
                .Select(same => same.OrderByDescending(depot => depot.Size).First())
                .ToList();
            var name = parts.Select(candidate => candidate.Name).FirstOrDefault(HasRealName) ?? parts[0].Name;
            return new ShareCandidate(group.Key, name, parts[0].Source, parts[0].Folder, files, depots,
                parts.Max(candidate => candidate.LastModifiedUtc));
        })
        .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>False for the placeholder names the scanner gives when it knows no real one.</summary>
    public static bool HasRealName(string name) =>
        !string.IsNullOrWhiteSpace(name) && !Regex.IsMatch(name, @"^(App|Depot) \d+$", RegexOptions.CultureInvariant);

    /// <summary>Depot and manifest id from a <c>depot_manifest.manifest</c> file name.</summary>
    public static (uint Depot, string ManifestId)? ParseManifestName(string fileName)
    {
        var match = ManifestName.Match(fileName ?? string.Empty);
        return match.Success && uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var depot)
            ? (depot, match.Groups[2].Value)
            : null;
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(folder)) return Array.Empty<string>();
        try
        {
            if (!Directory.Exists(folder)) return Array.Empty<string>();
            return Directory.EnumerateFiles(folder, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 6
            }).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<string>();
        }
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
