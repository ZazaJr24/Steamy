using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;

namespace Steamy.Services;

/// <summary>One app that is registered in a local Steam library.</summary>
public sealed record InstalledSteamApp
{
    public int AppId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string InstallDirectory { get; init; } = string.Empty;
    public string LibraryPath { get; init; } = string.Empty;
    public long SizeOnDisk { get; init; }
    public int StateFlags { get; init; }
    public string BuildId { get; init; } = string.Empty;
    public DateTime? LastUpdated { get; init; }
    public long BytesDownloaded { get; init; }
    public long BytesToDownload { get; init; }
    public double? DownloadProgressPercent => BytesToDownload > 0
        ? Math.Clamp((double)BytesDownloaded / BytesToDownload * 100, 0, 100) : null;

    public bool IsFullyInstalled => (StateFlags & 4) != 0 && (StateFlags & 2) == 0;
    public bool UpdateRequired => (StateFlags & 2) != 0;
    public bool UpdateInProgress => (StateFlags & 1024) != 0;
}

public sealed record SteamLibraryScanResult(
    bool Succeeded,
    string Message,
    string SteamRoot,
    IReadOnlyList<string> LibraryFolders,
    IReadOnlyList<InstalledSteamApp> Apps)
{
    public static SteamLibraryScanResult Failure(string message) =>
        new(false, message, string.Empty, Array.Empty<string>(), Array.Empty<InstalledSteamApp>());
}

public interface ISteamLibraryService
{
    SteamLibraryScanResult Scan(string? steamRootOverride = null);
}

/// <summary>
/// Reads the local Steam installation: the Steam path from the registry, every registered
/// library folder from libraryfolders.vdf and the installed apps from appmanifest_*.acf.
/// This is read-only metadata of the user's own library.
/// </summary>
public sealed class SteamLibraryService : ISteamLibraryService
{
    public SteamLibraryScanResult Scan(string? steamRootOverride = null)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(steamRootOverride) ? FindSteamRoot() : steamRootOverride!.Trim();
            if (string.IsNullOrWhiteSpace(root))
                return SteamLibraryScanResult.Failure("No Steam installation was found. Select your Steam folder in Settings to read the local library.");

            if (!Directory.Exists(root))
                return SteamLibraryScanResult.Failure($"The configured Steam folder does not exist: {root}");

            var folders = ReadLibraryFolders(Path.Combine(root, "steamapps"));
            var apps = new List<InstalledSteamApp>();
            foreach (var folder in folders) apps.AddRange(ReadApps(folder));

            var distinct = apps
                .GroupBy(app => app.AppId)
                .Select(group => group.First())
                .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var message = distinct.Count == 0
                ? "Steam was found, but no installed apps are registered in the local library."
                : $"{distinct.Count} installed apps read from {folders.Count} local Steam library folder(s).";

            return new SteamLibraryScanResult(true, message, root, folders, distinct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return SteamLibraryScanResult.Failure($"The local Steam library could not be read ({exception.GetType().Name}).");
        }
    }

    public static string? FindSteamRoot() => SteamInstallationLocator.FindDefault()?.Root;

    /// <summary>Returns the steamapps folders of every registered library, newest format included.</summary>
    public static IReadOnlyList<string> ReadLibraryFolders(string steamAppsFolder)
    {
        var result = new List<string>();

        try
        {
            var file = Path.Combine(steamAppsFolder, "libraryfolders.vdf");
            if (File.Exists(file))
            {
                var root = VdfParser.Parse(File.ReadAllText(file));
                var libraryFolders = root["libraryfolders"] ?? root;

                foreach (var node in libraryFolders.Children)
                {
                    var path = node["path"]?.Value;
                    if (string.IsNullOrWhiteSpace(path) && !node.HasChildren) path = node.Value;
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    var folder = Path.Combine(path.Trim(), "steamapps");
                    if (Directory.Exists(folder) && !result.Contains(folder, SteamInstallationLocator.PathComparer)) result.Add(folder);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The default folder below is still returned.
        }

        if (Directory.Exists(steamAppsFolder) && !result.Contains(steamAppsFolder, SteamInstallationLocator.PathComparer))
            result.Insert(0, steamAppsFolder);

        return result;
    }

    public static IEnumerable<InstalledSteamApp> ReadApps(string steamAppsFolder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(steamAppsFolder, "appmanifest_*.acf", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            InstalledSteamApp? app = null;
            try
            {
                app = ReadApp(file, steamAppsFolder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A single unreadable manifest must not break the whole scan.
            }

            if (app is not null) yield return app;
        }
    }

    public static InstalledSteamApp? ReadApp(string manifestPath, string steamAppsFolder)
    {
        var root = VdfParser.Parse(File.ReadAllText(manifestPath));
        var state = root["AppState"] ?? root;

        var appId = state.GetInt("appid");
        if (appId <= 0) return null;

        var installDirectoryName = state.GetString("installdir");
        var installDirectory = string.Empty;
        if (!string.IsNullOrWhiteSpace(installDirectoryName))
        {
            var common = Path.GetFullPath(Path.Combine(steamAppsFolder, "common"));
            installDirectory = Path.GetFullPath(Path.Combine(common, installDirectoryName));
            var prefix = Path.TrimEndingDirectorySeparator(common) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!installDirectory.StartsWith(prefix, comparison)) return null;
        }

        var lastUpdated = state.GetLong("LastUpdated");
        DateTime? updatedAt = lastUpdated > 0
            ? DateTimeOffset.FromUnixTimeSeconds(lastUpdated).LocalDateTime
            : null;

        return new InstalledSteamApp
        {
            AppId = appId,
            Name = state.GetString("name") ?? $"App {appId}",
            InstallDirectory = installDirectory,
            LibraryPath = steamAppsFolder,
            SizeOnDisk = state.GetLong("SizeOnDisk"),
            StateFlags = state.GetInt("StateFlags"),
            BuildId = state.GetString("buildid") ?? string.Empty,
            LastUpdated = updatedAt,
            BytesDownloaded = Math.Max(0, state.GetLong("BytesDownloaded")),
            BytesToDownload = Math.Max(0, state.GetLong("BytesToDownload"))
        };
    }
}

