using System.IO;
using System.Security;

namespace Steamy.Services;

/// <summary>Result of a maintenance pass: how much was freed and what stayed.</summary>
public sealed record CleanupResult(long FreedBytes, int DeletedFiles, IReadOnlyList<string> Notes);

/// <summary>
/// One place that knows where Steamy writes temporary data. Every cache, manifest work folder and
/// download residue has a single well-defined location, and this service can measure and clean it.
/// Deleting is explicit — nothing is removed silently in the background.
/// <para>
/// Locations (all inside the user profile, nothing machine-wide):
/// <list type="bullet">
/// <item><c>%LOCALAPPDATA%\Steamy\artwork</c> — cached store art</item>
/// <item><c>%LOCALAPPDATA%\Steamy\ryuu-workdir</c> — Lua + depot manifests per app</item>
/// <item><c>%LOCALAPPDATA%\Steamy\ryuu-downloads</c> — cached Ryuu archives</item>
/// <item><c>%LOCALAPPDATA%\Steamy\tools</c> — downloaded DepotDownloaderMod</item>
/// <item><c>%LOCALAPPDATA%\Steamy\updates</c> — downloaded update packages</item>
/// </list>
/// </para>
/// </summary>
public sealed class CacheTempService
{
    /// <summary>The tool download is expensive to fetch again, so it is only cleared on request.</summary>
    public const string ToolsFolderName = "tools";

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy");

    public static string ArtworkCache => Path.Combine(Root, "artwork");
    public static string ManifestWorkDir => Path.Combine(Root, "ryuu-workdir");
    public static string RyuuArchiveCache => Path.Combine(Root, "ryuu-downloads");
    public static string ToolsCache => Path.Combine(Root, ToolsFolderName);
    public static string UpdateCache => Path.Combine(Root, "updates");

    public static string DumpRoot(ISettingsService settings)
    {
        var configured = settings.Load();
        var root = !string.IsNullOrWhiteSpace(configured.ManifestDumpFolder) ? configured.ManifestDumpFolder
            : !string.IsNullOrWhiteSpace(configured.DownloadFolder) ? configured.DownloadFolder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy");
        return Path.Combine(root, "DepotDumps");
    }

    /// <summary>Total bytes the selectable folders currently occupy.</summary>
    public static long Measure(ISettingsService settings)
    {
        return Size(ArtworkCache)
            + Size(ManifestWorkDir)
            + Size(RyuuArchiveCache)
            + Size(UpdateCache)
            + Size(DumpRoot(settings));
    }

    /// <summary>
    /// Deletes the selected caches. The tools folder is only removed when
    /// <paramref name="includeTools"/> is set, and the dump folder only when the user asks for it —
    /// both can hold data that took real time to build.
    /// </summary>
    public static CleanupResult Clean(ISettingsService settings, bool artwork, bool manifests, bool archives,
        bool updates, bool dumps, bool includeTools, IProgress<string>? progress = null)
    {
        long freed = 0;
        var files = 0;
        var notes = new List<string>();

        if (artwork) (freed, files) = CleanFolder(ArtworkCache, freed, files, "Artwork cache", notes, progress);
        if (manifests) (freed, files) = CleanFolder(ManifestWorkDir, freed, files, "Manifest work folder", notes, progress);
        if (archives) (freed, files) = CleanFolder(RyuuArchiveCache, freed, files, "Ryuu archives", notes, progress);
        if (updates) (freed, files) = CleanFolder(UpdateCache, freed, files, "Update packages", notes, progress);
        if (dumps) (freed, files) = CleanFolder(DumpRoot(settings), freed, files, "Depot dumps", notes, progress);
        if (includeTools) (freed, files) = CleanFolder(ToolsCache, freed, files, "DepotDownloaderMod download", notes, progress);

        return new CleanupResult(freed, files, notes);
    }

    private static (long, int) CleanFolder(string folder, long freedBefore, int filesBefore,
        string label, List<string> notes, IProgress<string>? progress)
    {
        if (!Directory.Exists(folder)) return (freedBefore, filesBefore);

        long freed = 0;
        var files = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    freed += info.Length;
                    info.Delete();
                    files++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
                {
                    // A locked file stays; the folder is still usable afterwards.
                }
            }

            if (files > 0) notes.Add($"{label}: {files} files, {DownloadFormat.Bytes(freed)}");
            progress?.Report($"{label}: {files} files ({DownloadFormat.Bytes(freed)})");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            notes.Add($"{label}: could not be read ({exception.GetType().Name})");
        }

        return (freedBefore + freed, filesBefore + files);
    }

    private static long Size(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(file =>
            {
                try { return new FileInfo(file).Length; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return 0L; }
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
