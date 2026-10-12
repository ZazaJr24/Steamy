using System.IO;
using System.Text.Json;

namespace Steamy.Storage;

/// <summary>The locator stays in Roaming; all application-owned data uses one stable root per process.</summary>
public static class AppDataPaths
{
    public static string RoamingRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy");
    public static string LegacyLocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy");
    // An explicit launch override also allows native tests to use an isolated profile.
    private static string BootstrapRoot => Environment.GetEnvironmentVariable("STEAMY_DATA_DIR") is { } path && Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : RoamingRoot;
    public static StorageLocationManager Locations { get; } = new(BootstrapRoot);
    private static string? _root;
    public static string Root => _root ??= Locations.Read().ActiveRoot;
    public static string TemporaryRoot => Path.Combine(Root, "temp");
    public static string ToolsRoot => Path.Combine(Root, "Tools");
    public static string CreateTemporaryRoot() { Directory.CreateDirectory(TemporaryRoot); return TemporaryRoot; }
    public static void Initialize() => _root = Locations.Apply(LegacyLocalRoot, Path.Combine(AppContext.BaseDirectory, "Tools"));
}

public sealed record DataLocation(string ActiveRoot, string? PendingRoot = null, bool LegacyImported = false, string? OperationId = null);

/// <summary>Copies before committing the locator. Original files remain as a recovery copy.</summary>
public sealed class StorageLocationManager(string roamingRoot)
{
    public string LocatorPath => Path.Combine(roamingRoot, "storage-location.json");
    public DataLocation Read()
    {
        if (!File.Exists(LocatorPath)) return new(Path.GetFullPath(roamingRoot));
        var state = JsonSerializer.Deserialize<DataLocation>(File.ReadAllText(LocatorPath))
            ?? throw new IOException("The storage location file is invalid. Restore storage-location.json before starting Steamy.");
        if (!Path.IsPathFullyQualified(state.ActiveRoot) || state.PendingRoot is { } pending && !Path.IsPathFullyQualified(pending))
            throw new IOException("The stored data folder must be an absolute path.");
        return state;
    }
    public void Schedule(string selectedFolder)
    {
        if (!Path.IsPathFullyQualified(selectedFolder)) throw new IOException("Choose an absolute data folder.");
        var state = Read();
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedFolder));
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.ActiveRoot));
        if (Same(target, source)) { Cancel(); return; }
        if (Same(target, Path.GetPathRoot(target)!) || Inside(target, source) || Inside(source, target))
            throw new IOException("Choose a separate, empty folder, outside the current data folder.");
        if (File.Exists(target) || Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("Choose an empty folder so existing files will not be overwritten.");
        RejectLinks(target);
        // Probe just this selected directory. No existing data is touched and no migration runs here.
        Directory.CreateDirectory(target);
        var probe = Path.Combine(target, ".steamy-write-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, ""); } finally { File.Delete(probe); }
        Save(state with { PendingRoot = target, OperationId = Guid.NewGuid().ToString("N") });
    }
    public void Cancel() { var state = Read(); Save(state with { PendingRoot = null, OperationId = null }); }
    public bool NeedsPreparation(string legacyRoot)
    {
        var state = Read();
        return state.PendingRoot is not null || !state.LegacyImported && Directory.Exists(legacyRoot);
    }
    public string Apply(string legacyRoot, string? legacyToolsRoot = null)
    {
        var state = Read();
        RejectLinks(state.ActiveRoot);
        if (!Same(state.ActiveRoot, roamingRoot) && !Directory.Exists(state.ActiveRoot))
            throw new IOException("Your data folder is unavailable. Reconnect its drive before starting Steamy.");
        if (state.PendingRoot is { } destination)
        {
            RejectLinks(destination);
            var marker = Path.Combine(destination, ".steamy-migration.json");
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()
                && (!File.Exists(marker) || File.ReadAllText(marker) != state.OperationId))
                throw new IOException("The selected folder is no longer empty. Choose another folder in Settings → Transparency.");
            Directory.CreateDirectory(destination);
            File.WriteAllText(marker, state.OperationId);
            CopyTree(state.ActiveRoot, destination, overwrite: true);
            RebasePreferences(destination, state.ActiveRoot);
            // Commit only after the full copy succeeds. Partial attempts can be retried using the marker.
            state = state with { ActiveRoot = destination, PendingRoot = null, OperationId = null };
            Save(state);
            try { File.Delete(marker); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (!state.LegacyImported)
        {
            if (Directory.Exists(legacyRoot) && !Same(legacyRoot, state.ActiveRoot))
            {
                RejectLinks(legacyRoot);
                CopyTree(legacyRoot, state.ActiveRoot, overwrite: false);
                RebasePreferences(state.ActiveRoot, legacyRoot);
            }
            if (legacyToolsRoot is not null)
                foreach (var name in new[] { "ColdLoader", "ColdLoaderDebug", "ColdLoaderProxy", "SteamStubbed", "Goldberg" })
                {
                    var source = Path.Combine(legacyToolsRoot, name);
                    if (!Directory.Exists(source)) continue;
                    RejectLinks(source);
                    CopyTree(source, Path.Combine(state.ActiveRoot, "Tools", name), overwrite: false);
                }
            state = state with { LegacyImported = true };
            Save(state);
        }
        return state.ActiveRoot;
    }
    private void Save(DataLocation state)
    {
        Directory.CreateDirectory(roamingRoot);
        var temporary = LocatorPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(state)); File.Move(temporary, LocatorPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void CopyTree(string source, string target, bool overwrite)
    {
        if (!Directory.Exists(source)) return;
        RejectLinks(target);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (name == "storage-location.json" || name == ".steamy-migration.json" || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            RejectLinks(file);
            var destination = Path.Combine(target, name);
            RejectLinks(destination);
            if (overwrite || !File.Exists(destination)) File.Copy(file, destination, overwrite);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            RejectLinks(directory);
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)), overwrite);
        }
    }
    private static void RebasePreferences(string root, string oldRoot)
    {
        // Only structured path values change; free-form notes and secrets are never rewritten.
        foreach (var file in new[] { "settings.json", "installed-downloads.json", "playtime-config.json" })
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path)) continue;
            System.Text.Json.Nodes.JsonNode? node;
            try { node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)); }
            catch (JsonException) { continue; } // Preserve a damaged optional file for its service's recovery policy.
            Rewrite(node, oldRoot, root);
            File.WriteAllText(path, node?.ToJsonString(new() { WriteIndented = true }));
        }
    }
    private static void Rewrite(System.Text.Json.Nodes.JsonNode? node, string oldRoot, string root)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
            foreach (var entry in obj.ToArray())
            {
                if (entry.Value is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text)
                    && (entry.Key.Contains("Path", StringComparison.OrdinalIgnoreCase) || entry.Key.Contains("Folder", StringComparison.OrdinalIgnoreCase)
                        || entry.Key.Contains("Directory", StringComparison.OrdinalIgnoreCase) || entry.Key == "Executable"))
                {
                    if (Same(text, oldRoot)) obj[entry.Key] = root;
                    else if (Inside(text, oldRoot)) obj[entry.Key] = Path.Combine(root, Path.GetRelativePath(oldRoot, text));
                }
                else Rewrite(entry.Value, oldRoot, root);
            }
        else if (node is System.Text.Json.Nodes.JsonArray array) foreach (var item in array) Rewrite(item, oldRoot, root);
    }
    public static void RejectLinks(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
            if ((Directory.Exists(item.FullName) || File.Exists(item.FullName)) && (File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Storage migration does not follow symbolic links or junctions. Choose a regular folder.");
    }
    private static bool Same(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
    private static bool Inside(string path, string root) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
