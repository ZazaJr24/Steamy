using System.IO;
using System.Text;
using Steamy.Models;
using Steamy.Playtime;

namespace Steamy.Services;

public sealed record StorageLocation(string Name, string Path, string Purpose, string CreatedWhen, string RemovalImpact, string Files = "")
{
    public bool Exists => File.Exists(Path) || Directory.Exists(Path);
    public string Status => Exists ? "Present on this PC" : "Not present at this location";
    public string Details => $"{Purpose}\n\nCreated or updated: {CreatedWhen}\n\nIf removed: {RemovalImpact}" + (Files.Length > 0 ? $"\n\nFiles / subfolders: {Files}" : "");
}
public sealed record StorageGroup(string Name, IReadOnlyList<StorageLocation> Locations);

/// <summary>A read-only map of built-in storage locations and configured targets. Never reads credentials or traverses drives.</summary>
public static class StorageTransparency
{
    public static IReadOnlyList<StorageGroup> Describe(AppSettings settings, IEnumerable<DownloadJob> downloads, IEnumerable<Game> games)
    {
        var local = CacheTempService.Root;
        var roaming = Steamy.Storage.AppDataPaths.Root;
        StorageLocation Local(string name, string leaf, string purpose, string when, string impact, string files = "") => new(name, Path.Combine(local, leaf), purpose, when, impact, files);
        var personal = new List<StorageLocation>
        {
            new("Storage folder locator", Steamy.Storage.AppDataPaths.Locations.LocatorPath, "Remembers the current data folder and a pending change. This small file always stays in Roaming.", "When a folder change is selected or completed.", "Steamy falls back to its default Roaming folder. Keep this file when using a custom data folder."),
            new("Preferences", Path.Combine(roaming, "settings.json"), "Appearance, configured folders, account ID and connection settings. Credential fields are stored separately.", "When preferences are saved, including autosave.", "Preferences and configured paths reset."),
            new("Encrypted credentials", Path.Combine(roaming, "credentials"), "API keys and tokens encrypted with Windows DPAPI for the current Windows user. Stored values are not read by this view or included in its report.", "When you connect or change a credential.", "You must reconnect affected services. Encryption protects storage; a key is still sent to its provider over HTTPS when that service is used."),
            new("Library and download queue", Path.Combine(roaming, "content-manager.db"), "Local library metadata, download queue and saved history in SQLite.", "When the library or queue is updated.", "Saved queue/library metadata can be lost. This is not a cache. Do not remove while Steamy is open.", "content-manager.db-wal / content-manager.db-shm may exist beside it."),
            Local("Per-game notes and startup options", "games", "Your notes, launch arguments and selected executable for each game.", "When you edit game notes or launch options.", "Your saved notes and launch settings are lost.", "<app-id>.json; short-lived .tmp files during atomic saves."),
            Local("Playtime and tracker configuration", PlayTimeFiles.TimeFile, "Locally collected game duration and current running state. Steam playtime is read separately and merged for display.", "While enabled games are tracked by PlayTimeTracker.exe.", "Locally tracked time is lost. Steam's own playtime is not deleted.", $"{PlayTimeFiles.ConfigurationFile}, collector-build.txt and temporary atomic-save files are in the same folder."),
            Local("Favorites and recent launches", "game-activity.json", "Favorite games and local launch timestamps.", "When you favorite or launch a game.", "Favorites and recorded recent launches reset."),
            Local("Recognized downloaded games", "installed-downloads.json", "Detected local/DepotDownloader installations, their folders and launch executable paths.", "When downloaded installations are discovered or updated.", "Saved detections must be discovered again; actual game files remain."),
            new("Search and share history", roaming, "Saved search queries and manifest-sharing history.", "When those features are used.", "Deleting these history files removes the corresponding history.", "search-history.json, share-history.json"),
            new("Crash diagnostics", Path.Combine(roaming, "crash.log"), "Error details used for troubleshooting. Logs may contain local paths and exception messages.", "When an application error is recorded.", "Past crash diagnostics are lost."),
            Local("Downloader output", "ddmod-output.log", "Output written by DepotDownloader progress parsing. Review logs before sharing; they can contain local paths and third-party tool output.", "As downloader output is received.", "Past downloader output is lost; this does not remove downloaded games.")
        };
        var cache = new List<StorageLocation>();
        void Cache(string name, string folder, string purpose, string when = "When the corresponding feature requests this data.") =>
            cache.Add(Local(name, folder, purpose, when, "Data may have to be downloaded or rebuilt. Do not remove files used by an active operation."));
        Cache("Game artwork", "artwork", "Cached Steam banners, covers and game logos.");
        Cache("Steam catalog", "steam-catalog", "Cached public game metadata and screenshot files.");
        Cache("Home catalog", "spotlight.json", "Cached public Home game feed.");
        Cache("Trailer previews", "trailer-previews", "Downloaded video-preview data.");
        Cache("HDR trailer browser", "TrailerBrowser", "WebView2 profile/cache used to render trailers; can include browser storage.");
        Cache("Download browser", "WebView2Cache", "WebView2 profile/cache used by browser downloads; can include cookies and browser storage.");
        Cache("Interface translations", "ui-translations.json", "Locally cached interface translations. Interface text is sent to the translation service when needed.");
        foreach (var folder in new[] { "fix-catalog", "ryuu-catalog", "hubcap-catalog", "source-indexes" }) Cache(folder, folder, "Cached catalog/index data for the corresponding source.");
        Cache("Manifest preparation", "manifest-workdir", "Extracted manifests and imported metadata for source/share operations.");
        Cache("Download preparation", "ryuu-workdir", "Per-game Lua, manifests, key files, prepared snapshots and downloader stop signals. These files can contain access data; do not share blindly.");
        Cache("Downloaded game archives", "ryuu-downloads", "Archives downloaded for the selected source.");
        Cache("Fix archives", "game-fix-downloads", "Downloaded fix packages before installation.");
        Cache("Tool release cache", "ToolCache", "Downloaded third-party tool releases.");
        Cache("DepotDownloaderMod releases", CacheTempService.ToolsFolderName, "Downloaded fallback DepotDownloaderMod binaries and staging releases.");
        foreach (var folder in new[] { "CreamInstallerCache", "DenuvoGeneratorCache", "CreamAPI", "SmokeAPI", "Koalageddon", "UplayR2Unlocker", "gbe_fork", "GreenLuma" })
            Cache(folder, folder, "Optional third-party tool files. These are executable tools or libraries, not ordinary game artwork. Some tool services create empty cache folders when initialized.");
        Cache("Optional tool preferences", local, "Local optional-tool selections and state.", "When optional tools are configured.");
        cache[^1] = cache[^1] with { Files = "goldberg_options.json, familyshare_games.json, greenluma_path.txt, greenluma_uninstalled", RemovalImpact = "Removing preference files resets tool selections and uninstall markers. Removing an uninstall marker can allow tool re-detection." };
        Cache("Installer staging", "installer", "Verified app ZIPs and Microsoft component installers staged by the download setup; per-run files are removed after installation or cancellation.", "When the download installer is used.");
        Cache("Update packages", "updates", "Downloaded Steamy update files. Installing an update also changes the application folder.");
        var targets = new List<StorageLocation>
        {
            new("Steamy installation", AppContext.BaseDirectory, "Steamy.exe, PlayTimeTracker.exe, runtime files and bundled Tools/Resources.", "When Steamy is extracted, built or updated.", "The application or bundled tools may stop working.", "PlayTimeTracker.exe runs locally when playtime tracking is enabled; it can continue after the main window closes. Disable Playtime tracking in General to stop collection."),
            new("Application tool working files", Steamy.Storage.AppDataPaths.ToolsRoot, "Working caches for optional tool operations. Read-only bundled tool binaries remain with the application. Some tool pages create empty working subfolders when initialized.", "When a tool page initializes or downloads/prepares its selected components.", "Removing tool files can break that tool or require downloading its components again.", "ColdLoader, ColdLoaderDebug, ColdLoaderProxy, SteamStubbed, Goldberg/current and other bundled tool directories.")
        };
        if (!string.IsNullOrWhiteSpace(settings.DownloadFolder))
            targets.Add(new("Configured download folder", settings.DownloadFolder, "Default destination selected in Downloads settings. Individual jobs can choose another destination.", "When you start a download targeting this folder.", "Game data or resumable partial downloads may be lost."));
        else targets.Add(new("Default download folder not configured", "", "Each job must supply its target; no download location is assumed here.", "After a destination is selected.", "See the actual job targets below."));
        var dumpBase = !string.IsNullOrWhiteSpace(settings.ManifestDumpFolder) ? settings.ManifestDumpFolder : !string.IsNullOrWhiteSpace(settings.DownloadFolder) ? settings.DownloadFolder : roaming;
        targets.Add(new("Depot dumps", Path.Combine(dumpBase, "DepotDumps"), "Saved manifest dumps used by dump/share features.", "When dumps are created.", "Saved dumps are lost and may need to be generated again."));
        if (!string.IsNullOrWhiteSpace(settings.WorkingDirectory)) targets.Add(new("Configured tool working directory", settings.WorkingDirectory, "Working folder supplied to configured tool processes. Third-party tools can create their own files here.", "When a tool runs with this working directory.", "Tool data or resumable work can be lost."));
        if (!string.IsNullOrWhiteSpace(settings.SteamLibraryPath))
            targets.Add(new("Configured Steam folder", settings.SteamLibraryPath, "Steam library discovery reads manifests and local playtime. Explicit Steam tool installation can also change files here; browsing game details does not install these tools.", "Only the selected tool operation writes its corresponding files.", "Deleting Steam files can break Steam or installed games.", "BetterSteamTools: config/stplug-in, depotcache, opensteamtool.toml; originals and files.json under config/steamy-backups/<id>. Other external tools can have different targets."));
        var paths = downloads.Select(job => (Name: job.GameName, Path: job.TargetFolder)).Concat(games.Select(game => (Name: game.Name, Path: game.InstallFolder)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Path)).DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Take(256);
        foreach (var item in paths) targets.Add(new(item.Name, item.Path, "Current download destination or detected installed-game folder. Fixes/tools target this folder only when you choose the corresponding operation.", "As that game's download or selected operation runs.", "Deleting files here can remove the game, saves/configuration stored in that folder, or download-resume data."));
        foreach (var item in new[] { ("Steamless target", settings.SteamlessTargetExePath), ("Installer target", settings.CreamInstallerTargetExePath), ("Steam API target", settings.CreamInstallerSteamApiPath) })
            if (!string.IsNullOrWhiteSpace(item.Item2)) targets.Add(new(item.Item1, item.Item2, "A file explicitly selected for the corresponding optional tool. That tool may modify or create files beside it.", "When you run the selected tool operation.", "Removing or replacing game binaries can stop the game from starting. Backup/restore behavior depends on that specific tool."));
        return [new("Your saved data", personal), new("Caches and working files", cache), new("Downloads, tools and changed targets", targets), new("Steamy temporary files", [
            new("Temporary work area", Steamy.Storage.AppDataPaths.TemporaryRoot, "Selected extraction, metadata, fix and tool operations stage files under the temp subfolder in your selected Steamy data folder.", "While the relevant operation is running. Normal finally-cleanup attempts to remove its owned work folder; a crash or locked file can leave leftovers.", "Removing active work can break an install or its rollback. Original Windows Temp leftovers from older versions are not automatically removed.", "SteamyFix-*, Steamy-metadata-*.zip, Steamy-bst-*, Steamy-bst-source-*, rt_gbe_interfaces_*, RT_Denuvo_<app-id>. Atomic saves can also create short-lived .tmp files beside their destination.")])];
    }

    public static string Report(IReadOnlyList<StorageGroup> groups)
    {
        var text = new StringBuilder("Steamy storage transparency report\n" + DateTimeOffset.Now.ToString("O") + "\n\nBuilt-in locations and current configured targets (up to 256 distinct game/job destinations). This is a snapshot, not a complete file-write audit. External tools may use additional locations. Contains local paths; no file contents or credential values are collected.\n");
        foreach (var group in groups)
        {
            text.AppendLine("\n" + group.Name);
            foreach (var entry in group.Locations) text.AppendLine($"\n{entry.Name}\n{entry.Path}\n{entry.Status}\n{entry.Details}");
        }
        return text.ToString();
    }
}
