using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using SteamContentManager.Models;

namespace SteamContentManager.Services;

/// <summary>
/// A node of a Valve KeyValues (VDF/ACF) document. Only text is read: nothing is executed and
/// no file is written.
/// </summary>
public sealed class VdfNode
{
    private readonly Dictionary<string, VdfNode> _children = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode(string key, string? value = null)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }
    public string? Value { get; internal set; }
    public bool HasChildren => _children.Count > 0;
    public IReadOnlyCollection<VdfNode> Children => _children.Values;

    public VdfNode? this[string key] => _children.TryGetValue(key, out var node) ? node : null;

    public IEnumerable<VdfNode> Descendants()
    {
        foreach (var child in _children.Values)
        {
            yield return child;
            foreach (var descendant in child.Descendants()) yield return descendant;
        }
    }

    public string? GetString(string key)
    {
        var node = this[key];
        if (node is null) return null;
        if (node.Value is not null) return node.Value;
        // ACF files sometimes wrap the payload of a key in a block with a single child.
        return node.Children.Count == 1 ? node.Children.First().Value : null;
    }

    public long GetLong(string key, long fallback = 0)
    {
        var raw = GetString(key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    public int GetInt(string key, int fallback = 0)
    {
        var value = GetLong(key, fallback);
        return value is > int.MaxValue or < int.MinValue ? fallback : (int)value;
    }

    internal void Add(VdfNode child) => _children[child.Key] = child;
}

/// <summary>
/// Tolerant KeyValues parser. Malformed input never throws; unreadable parts are skipped so a
/// half-written ACF file cannot break the library scan.
/// </summary>
public static class VdfParser
{
    public static VdfNode Parse(string? text)
    {
        var root = new VdfNode("__root__");
        if (string.IsNullOrWhiteSpace(text)) return root;

        var stack = new Stack<VdfNode>();
        stack.Push(root);
        var tokens = Tokenize(text);
        var index = 0;

        while (index < tokens.Count)
        {
            var token = tokens[index];

            if (token == "}")
            {
                if (stack.Count > 1) stack.Pop();
                index++;
                continue;
            }

            if (token == "{")
            {
                index++;
                continue;
            }

            if (index + 1 >= tokens.Count)
            {
                stack.Peek().Add(new VdfNode(token, string.Empty));
                break;
            }

            var next = tokens[index + 1];
            if (next == "{")
            {
                var block = new VdfNode(token);
                stack.Peek().Add(block);
                stack.Push(block);
                index += 2;
                continue;
            }

            stack.Peek().Add(new VdfNode(token, next == "}" ? string.Empty : next));
            index += 2;
        }

        return root;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var index = 0;

        while (index < text.Length)
        {
            var character = text[index];

            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            // // and /* */ comments are part of the format but carry no data.
            if (character == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] is not ('\n' or '\r')) index++;
                continue;
            }

            if (character == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < text.Length && !(text[index] == '*' && text[index + 1] == '/')) index++;
                index = Math.Min(text.Length, index + 2);
                continue;
            }

            if (character is '{' or '}')
            {
                tokens.Add(character.ToString());
                index++;
                continue;
            }

            if (character == '"')
            {
                index++;
                var builder = new StringBuilder();
                while (index < text.Length && text[index] != '"')
                {
                    if (text[index] == '\\' && index + 1 < text.Length)
                    {
                        var escaped = text[index + 1];
                        builder.Append(escaped switch { 'n' => '\n', 't' => '\t', _ => escaped });
                        index += 2;
                        continue;
                    }

                    builder.Append(text[index]);
                    index++;
                }

                if (index < text.Length) index++; // closing quote
                tokens.Add(builder.ToString());
                continue;
            }

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not ('{' or '}' or '"')) index++;
            if (index > start) tokens.Add(text[start..index]);
            else index++;
        }

        return tokens;
    }
}

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

    public static string? FindSteamRoot()
    {
        var registryCandidates = new (RegistryKey Hive, string Path)[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam")
        };

        foreach (var (hive, path) in registryCandidates)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                var value = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
                if (string.IsNullOrWhiteSpace(value)) continue;
                var candidate = Path.GetFullPath(value);
                if (Directory.Exists(candidate)) return candidate;
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
            {
                // The next candidate is tried instead.
            }
        }

        var fallbacks = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
        };

        return fallbacks.FirstOrDefault(Directory.Exists);
    }

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
                    if (Directory.Exists(folder) && !result.Contains(folder, StringComparer.OrdinalIgnoreCase)) result.Add(folder);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The default folder below is still returned.
        }

        if (Directory.Exists(steamAppsFolder) && !result.Contains(steamAppsFolder, StringComparer.OrdinalIgnoreCase))
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
        var installDirectory = string.IsNullOrWhiteSpace(installDirectoryName)
            ? string.Empty
            : Path.Combine(steamAppsFolder, "common", installDirectoryName);

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
            LastUpdated = updatedAt
        };
    }
}

/// <summary>Maps Steam's AppState StateFlags onto the app's install state.</summary>
public static class SteamAppStateMapper
{
    public static GameInstallState Map(int stateFlags)
    {
        if ((stateFlags & 1024) != 0) return GameInstallState.Updating;      // update started
        if ((stateFlags & 2) != 0) return GameInstallState.Updating;         // update required
        if ((stateFlags & 4) != 0) return GameInstallState.Installed;
        return GameInstallState.NotInstalled;
    }
}

/// <summary>Human readable byte sizes for real file sizes.</summary>
public static class ByteSize
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Format(long bytes)
    {
        if (bytes <= 0) return "Unknown size";

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {Units[unit]}");
    }
}

/// <summary>Creates library entries from locally installed Steam apps.</summary>
public static class GameFactory
{
    private static readonly (string Color, string Glyph)[] CoverPalette =
    {
        ("#384F82", "◈"), ("#6A3438", "✦"), ("#7B3B2E", "△"), ("#5B4A32", "✧"),
        ("#513A64", "☽"), ("#376A59", "★"), ("#3B2D68", "◆"), ("#2F5D6B", "▲")
    };

    private static readonly string[] Glyphs = { "◆", "◈", "✦", "✧", "△", "☽", "★", "▲" };

    public static Game FromInstalledApp(InstalledSteamApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var (color, glyph) = CoverStyleFor(app.AppId);

        return new Game
        {
            AppId = app.AppId,
            Name = app.Name,
            ShortName = ShortName(app.Name),
            CoverColor = color,
            CoverGlyph = glyph,
            InstallFolder = app.InstallDirectory,
            Size = ByteSize.Format(app.SizeOnDisk),
            SizeOnDiskBytes = app.SizeOnDisk,
            InstallState = SteamAppStateMapper.Map(app.StateFlags),
            UpdateRequired = app.UpdateRequired,
            LastPlayed = app.LastUpdated is { } updatedAt
                ? $"Updated {updatedAt:dd.MM.yyyy HH:mm}"
                : "Update time unknown",
            DepotSummary = $"Local install · build {app.BuildId}".Trim(),
            ManifestSummary = "Not imported",
            AchievementPercent = 0
        };
    }

    public static (string Color, string Glyph) CoverStyleFor(int appId)
    {
        if (appId <= 0) return (CoverPalette[0].Color, Glyphs[0]);
        var index = Math.Abs(appId % Glyphs.Length);
        return (CoverPalette[index].Color, Glyphs[index]);
    }

    public static string ShortName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "APP";
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1)
        {
            var word = words[0];
            return word.Length <= 6 ? word.ToUpperInvariant() : word[..6].ToUpperInvariant();
        }

        return string.Concat(words.Take(4).Select(word => char.ToUpperInvariant(word[0])));
    }
}

public interface ILibrarySyncService
{
    SteamLibraryScanResult Refresh(CancellationToken cancellationToken = default);
}

/// <summary>
/// Fills the library with content that actually exists on this machine. When nothing is found
/// the library stays empty and the reason is written to the log instead of inventing entries.
/// </summary>
public sealed class LibrarySyncService : ILibrarySyncService
{
    private readonly IAppDataStore _store;
    private readonly ISteamLibraryService _steamLibrary;
    private readonly IArtworkService _artwork;
    private readonly ILoggingService _logging;
    private readonly ISettingsService _settings;

    public LibrarySyncService(
        IAppDataStore store,
        ISteamLibraryService steamLibrary,
        IArtworkService artwork,
        ILoggingService logging,
        ISettingsService settings)
    {
        _store = store;
        _steamLibrary = steamLibrary;
        _artwork = artwork;
        _logging = logging;
        _settings = settings;
    }

    public SteamLibraryScanResult Refresh(CancellationToken cancellationToken = default)
    {
        var result = _steamLibrary.Scan(_settings.Load().SteamLibraryPath);

        if (!result.Succeeded)
        {
            _logging.Add(LogLevel.Warning, "LibraryService", result.Message);
            return result;
        }

        Apply(result.Apps);
        _logging.Add(LogLevel.Info, "LibraryService", result.Message);
        _ = LoadArtworkAsync(cancellationToken);
        return result;
    }

    private void Apply(IReadOnlyList<InstalledSteamApp> apps)
    {
        var existing = _store.Games.ToDictionary(game => game.AppId);
        var incoming = apps.Select(app => app.AppId).ToHashSet();

        for (var index = _store.Games.Count - 1; index >= 0; index--)
        {
            var game = _store.Games[index];
            if (!incoming.Contains(game.AppId)) _store.Games.RemoveAt(index);
        }

        for (var index = 0; index < apps.Count; index++)
        {
            var app = apps[index];
            var game = GameFactory.FromInstalledApp(app);
            if (existing.TryGetValue(app.AppId, out var previous)) game.Selected = previous.Selected;

            var currentIndex = IndexOf(app.AppId);
            if (currentIndex >= 0) _store.Games[currentIndex] = game;
            else _store.Games.Insert(Math.Min(index, _store.Games.Count), game);
        }

        // Keep the display order stable and alphabetical.
        var ordered = _store.Games.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _store.Games.Clear();
        foreach (var game in ordered) _store.Games.Add(game);
    }

    private int IndexOf(int appId)
    {
        for (var index = 0; index < _store.Games.Count; index++)
        {
            if (_store.Games[index].AppId == appId) return index;
        }

        return -1;
    }

    private async Task LoadArtworkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _artwork.LoadManyAsync(_store.Games.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logging.Add(LogLevel.Debug, "Artwork", $"Optional artwork unavailable: {exception.GetType().Name}.");
        }
    }
}

/// <summary>Real free space of the drive that stores the downloads.</summary>
public sealed class SystemDiskSpaceService : IDiskSpaceService
{
    public Task<string> GetAvailableAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var probe = string.IsNullOrWhiteSpace(path) ? Path.GetTempPath() : path;
            var root = Path.GetPathRoot(Path.GetFullPath(probe));
            if (string.IsNullOrWhiteSpace(root)) return Task.FromResult("Unknown");

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return Task.FromResult($"Drive {root} is not ready");
            return Task.FromResult($"{ByteSize.Format(drive.AvailableFreeSpace)} free on {root.TrimEnd('\\')}");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return Task.FromResult("Unknown");
        }
    }
}
