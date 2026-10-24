using System.Globalization;
using System.IO;
using Steamy.Models;

namespace Steamy.Services;

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
            LastUpdated = app.LastUpdated,
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
    Task<SteamLibraryScanResult> RefreshAsync(CancellationToken cancellationToken = default);
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
    private readonly IDownloadedGameLibrary? _downloadedLibrary;

    public LibrarySyncService(
        IAppDataStore store,
        ISteamLibraryService steamLibrary,
        IArtworkService artwork,
        ILoggingService logging,
        ISettingsService settings,
        IDownloadedGameLibrary? downloadedLibrary = null)
    {
        _store = store;
        _steamLibrary = steamLibrary;
        _artwork = artwork;
        _logging = logging;
        _settings = settings;
        _downloadedLibrary = downloadedLibrary;
    }

    // Scan off-thread, then publish collection changes on the caller's UI context.
    public async Task<SteamLibraryScanResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _scanGeneration);
        var steamRoot = _settings.Load().SteamLibraryPath;
        var result = await Task.Run(() => _steamLibrary.Scan(steamRoot), cancellationToken);
        var downloaded = await ReadDownloadsAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (generation == Volatile.Read(ref _scanGeneration)) CompleteRefresh(result, downloaded, cancellationToken);
        return result;
    }

    private int _scanGeneration;

    public SteamLibraryScanResult Refresh(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _scanGeneration);
        var result = _steamLibrary.Scan(_settings.Load().SteamLibraryPath);
        var downloaded = ReadDownloadsAsync(cancellationToken).GetAwaiter().GetResult();
        CompleteRefresh(result, downloaded, cancellationToken);
        return result;
    }

    private async Task<IReadOnlyList<Game>?> ReadDownloadsAsync(CancellationToken token)
    {
        if (_downloadedLibrary is null) return Array.Empty<Game>();
        try { return await Task.Run(() => _downloadedLibrary.ScanAsync(token), token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { _logging.Add(LogLevel.Warning, "LibraryService", $"Local installations could not be read: {e.GetType().Name}."); return null; }
    }

    private void CompleteRefresh(SteamLibraryScanResult result, IReadOnlyList<Game>? downloaded, CancellationToken cancellationToken)
    {
        if (!result.Succeeded)
        {
            _logging.Add(LogLevel.Warning, "LibraryService", result.Message);
        }
        var steam = result.Succeeded ? result.Apps.Select(GameFactory.FromInstalledApp) : _store.Games.Where(game => !game.IsLocalDownload);
        var local = downloaded ?? _store.Games.Where(game => game.IsLocalDownload).ToArray();
        Apply(steam.Concat(local).Where(game => !GameClassification.IsRuntime(game.AppId, game.Name)).DistinctBy(game => game.AppId).ToArray());
        _logging.Add(LogLevel.Info, "LibraryService", result.Message);
        _ = LoadArtworkAsync(cancellationToken);
    }

    private void Apply(IReadOnlyList<Game> apps)
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
            var game = apps[index];
            if (existing.TryGetValue(game.AppId, out var previous))
            {
                game.Selected = previous.Selected;
                game.HeaderImage = previous.HeaderImage;
                game.ArtworkImage = previous.ArtworkImage;
            }

            var currentIndex = IndexOf(game.AppId);
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
            await _artwork.PrefetchAsync(_store.Games.Take(24).ToArray(), cancellationToken).ConfigureAwait(false);
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
