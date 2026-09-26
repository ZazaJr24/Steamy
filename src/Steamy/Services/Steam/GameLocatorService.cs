using System.IO;

namespace Steamy.Services;

/// <summary>An installed title that can be worked on, with its real folder on disk.</summary>
public sealed record InstalledGameEntry(int AppId, string Name, string InstallPath)
{
    public string Label => AppId > 0 ? $"{Name} · App {AppId}" : Name;
}

/// <summary>The executable the locator picked, plus why, plus what else was found.</summary>
public sealed record GameExecutableMatch(
    string ExecutablePath,
    string Reason,
    IReadOnlyList<string> Alternatives)
{
    public long SizeBytes { get; init; }
}

public interface IGameLocatorService
{
    /// <summary>Every installed title the application can see, newest list first.</summary>
    IReadOnlyList<InstalledGameEntry> ListInstalledGames();

    /// <summary>
    /// Finds the one executable that starts a game. Returns null when nothing plausible exists;
    /// guessing a wrong file would be worse than saying so.
    /// </summary>
    GameExecutableMatch? FindMainExecutable(InstalledGameEntry game);
}

/// <summary>
/// Resolves "which .exe starts this game" from the local installation. Read-only: it lists folders
/// and reads file sizes, nothing is started, moved or modified. Files that are obviously part of an
/// installer or a runtime (redistributables, uninstallers, crash handlers) never win.
/// </summary>
public sealed class GameLocatorService : IGameLocatorService
{
    private const int MaxDepth = 4;
    private const int MaxCandidates = 40;

    /// <summary>Folder names that only ever contain runtimes, documentation or installers.</summary>
    private static readonly string[] SkippedFolders =
    {
        "_CommonRedist", "CommonRedist", "DirectX", "DirectXRedist", "DotNet", "dotnet",
        "redist", "Redist", "redistributable", "Redistributable", "Support", "SupportFiles",
        "docs", "documentation", "Documentation", "install", "Installers", "tools", "Tools",
        "CrashReports", "logs", "Logs", "cache", "Cache", "shadercache", "ShaderCache"
    };

    /// <summary>File name fragments that mark a helper rather than the game itself.</summary>
    private static readonly string[] SkippedNames =
    {
        "unins", "setup", "install", "vcredist", "dxsetup", "dxwebsetup", "directx",
        "crashpad", "crashreport", "crashhandler", "unitycrashhandler", "createdump",
        "pbsetup", "pbweb", "eac", "easyanticheat", "battleye", "report", "errorreport",
        "updater", "updatehelper", "steamless", "depotdownloader", "manifest", "cmd", "console"
    };

    private readonly ISteamLibraryService _steamLibrary;
    private readonly IAppDataStore _store;

    public GameLocatorService(ISteamLibraryService steamLibrary, IAppDataStore store)
    {
        _steamLibrary = steamLibrary;
        _store = store;
    }

    public IReadOnlyList<InstalledGameEntry> ListInstalledGames()
    {
        var entries = new Dictionary<string, InstalledGameEntry>(StringComparer.OrdinalIgnoreCase);

        // The Steam installation first: it has real folders and app ids for every installed title.
        try
        {
            var scan = _steamLibrary.Scan();
            foreach (var app in scan.Apps)
            {
                var path = Path.Combine(app.LibraryPath, "steamapps", "common", app.InstallDirectory);
                if (!Directory.Exists(path)) continue;

                entries[path] = new InstalledGameEntry(app.AppId, app.Name, path);
            }
        }
        catch (Exception)
        {
            // A missing or unreadable Steam installation is not an error: the local store below may
            // still know about games.
        }

        // Titles the library sync already discovered locally, in case Steam could not be read.
        foreach (var game in _store.Games)
        {
            if (string.IsNullOrWhiteSpace(game.InstallFolder) || !Directory.Exists(game.InstallFolder)) continue;
            entries.TryAdd(game.InstallFolder, new InstalledGameEntry(game.AppId, game.Name, game.InstallFolder));
        }

        return entries.Values
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public GameExecutableMatch? FindMainExecutable(InstalledGameEntry game)
    {
        ArgumentNullException.ThrowIfNull(game);

        if (string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath)) return null;

        var candidates = new List<(string Path, long Size)>();
        Collect(game.InstallPath, 0, candidates);

        if (candidates.Count == 0) return null;

        var folderName = Path.GetFileName(game.InstallPath.TrimEnd(Path.DirectorySeparatorChar));

        // Ranking: name match beats size. A file called like the game (or its folder) is almost
        // always the real entry point; otherwise the largest executable is the best guess.
        var ranked = candidates
            .Select(candidate => (candidate.Path, candidate.Size, Rank: Rank(candidate.Path, candidate.Size, candidates, game.Name, folderName)))
            .OrderByDescending(entry => entry.Rank)
            .ThenByDescending(entry => entry.Size)
            .ToArray();

        var best = ranked[0];
        var alternatives = ranked
            .Skip(1)
            .Take(5)
            .Select(entry => entry.Path)
            .ToArray();

        return new GameExecutableMatch(best.Path, Describe(best.Path, game, folderName), alternatives)
        {
            SizeBytes = best.Size
        };
    }

    private static int Rank(string path, long size, List<(string Path, long Size)> all, string gameName, string folderName)
    {
        var file = Path.GetFileNameWithoutExtension(path);
        var score = 0;

        if (Equals(file, folderName)) score += 100;
        if (Equals(file, gameName)) score += 100;
        else if (Contains(file, gameName)) score += 60;
        else if (Contains(file, folderName)) score += 40;

        // Sitting next to the game data rather than in a sub-folder of it is a small plus.
        if (string.Equals(Path.GetDirectoryName(path), all[0].Path is null ? null : Path.GetDirectoryName(all[0].Path), StringComparison.OrdinalIgnoreCase)) score += 5;

        // A file that is a compiled launcher/engine marker but has no data next to it is suspect.
        if (!Contains(file, "launcher")) score += 5;

        return score + (int)Math.Min(20, size / 5_000_000);
    }

    private static string Describe(string path, InstalledGameEntry game, string folderName)
    {
        var file = Path.GetFileNameWithoutExtension(path);
        if (Equals(file, folderName) || Equals(file, game.Name))
            return $"“{Path.GetFileName(path)}” matches the game name.";

        return $"Largest executable in the installation folder: {Path.GetFileName(path)}.";
    }

    private static bool Equals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string value, string fragment)
        => !string.IsNullOrWhiteSpace(fragment) && value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static void Collect(string directory, int depth, List<(string Path, long Size)> candidates)
    {
        if (depth > MaxDepth || candidates.Count >= MaxCandidates) return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var file in files)
        {
            if (candidates.Count >= MaxCandidates) return;

            var name = Path.GetFileNameWithoutExtension(file);
            if (SkippedNames.Any(skip => name.Contains(skip, StringComparison.OrdinalIgnoreCase))) continue;

            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception)
            {
                continue;
            }

            // Tiny executables (helpers, stubs) are never the game.
            if (size < 100_000) continue;

            candidates.Add((file, size));
        }

        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(directory);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var folder in folders)
        {
            var folderName = Path.GetFileName(folder);
            if (SkippedFolders.Contains(folderName, StringComparer.OrdinalIgnoreCase)) continue;
            Collect(folder, depth + 1, candidates);
        }
    }
}
