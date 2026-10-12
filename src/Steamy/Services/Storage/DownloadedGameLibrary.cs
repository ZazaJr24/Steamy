using System.IO;
using Steamy.Models;
using Steamy.Playtime;

namespace Steamy.Services;

public interface IDownloadedGameLibrary
{
    event EventHandler? Changed;
    Task<IReadOnlyList<Game>> ScanAsync(CancellationToken token = default);
    Task RegisterAsync(int appId, string name, string folder, CancellationToken token = default);
    Task ImportAsync(IEnumerable<DownloadedInstallation> games, CancellationToken token = default);
}

public sealed class DownloadedGameLibrary(ILocalDatabase database, ISettingsService settings, IGameLocatorService locator, ILoggingService logging, string? catalogPath = null) : IDownloadedGameLibrary
{
    private readonly DownloadedInstallCatalog _catalog = new(catalogPath ?? Path.Combine(PlayTimeFiles.DefaultDirectory, "installed-downloads.json"));
    public event EventHandler? Changed;
    private readonly SemaphoreSlim _rootScanGate = new(1);
    private string? _scannedRoot;
    private DateTime _rootScanTime;
    private IReadOnlyList<DownloadedInstallation> _rootGames = [];

    private async Task<IReadOnlyList<DownloadedInstallation>> DiscoverDownloadRootAsync(string root, CancellationToken token)
    {
        if (!Directory.Exists(root)) return [];
        await _rootScanGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!string.Equals(root, _scannedRoot, StringComparison.OrdinalIgnoreCase) || DateTime.UtcNow - _rootScanTime >= TimeSpan.FromMinutes(2))
            {
                // Recognize external DepotDownloader markers/INI files and nested game
                // binaries too; the full-PC scan remains an explicit setup/library action.
                var result = await Task.Run(() => PcGameScanner.Scan([root], token: token), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                _rootGames = result.Games;
                _scannedRoot = root;
                _rootScanTime = DateTime.UtcNow;
            }
            return _rootGames;
        }
        finally { _rootScanGate.Release(); }
    }

    public async Task ImportAsync(IEnumerable<DownloadedInstallation> games, CancellationToken token = default)
    {
        await _catalog.DiscoverAsync(games, [], token).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RegisterAsync(int appId, string name, string folder, CancellationToken token = default)
    {
        try
        {
            await Task.Run(() => _catalog.DiscoverAsync([new(appId, name, folder)], [], token), token).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { logging.Add(LogLevel.Warning, "Library", $"The downloaded installation could not be registered: {e.Message}"); }
    }

    public async Task<IReadOnlyList<Game>> ScanAsync(CancellationToken token = default)
    {
        var rows = new List<DownloadedInstallation>();
        try
        {
            var jobs = await database.LoadDownloadJobsAsync(token).ConfigureAwait(false);
            rows.AddRange(jobs.Where(job => job.State == nameof(DownloadJobState.Completed))
                .Select(job => new DownloadedInstallation(job.AppId, job.GameName, job.TargetFolder)));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        { logging.Add(LogLevel.Debug, "Library", $"Download history unavailable: {e.GetType().Name}."); }
        var root = settings.Load().DownloadFolder;
        rows.AddRange(await DiscoverDownloadRootAsync(root, token).ConfigureAwait(false));
        var found = await _catalog.DiscoverAsync(rows, [], token).ConfigureAwait(false);
        return found.Select(installation =>
        {
            token.ThrowIfCancellationRequested();
            var name = string.IsNullOrWhiteSpace(installation.Name) ? Path.GetFileName(installation.Folder) : installation.Name;
            var launchPath = "";
            if (!string.IsNullOrWhiteSpace(installation.Executable) && File.Exists(installation.Executable)
                && Path.GetFullPath(installation.Executable).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.Folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                launchPath = installation.Executable;
            if (launchPath.Length == 0)
                launchPath = locator.FindMainExecutable(new(installation.AppId, name, installation.Folder))?.ExecutablePath ?? "";
            name = PcGameScanner.ResolveName(name, installation.Folder, launchPath);
            var (color, glyph) = GameFactory.CoverStyleFor(installation.AppId);
            return new Game
            {
                AppId = installation.AppId, Name = name, ShortName = GameFactory.ShortName(name),
                InstallFolder = installation.Folder, IsLocalDownload = installation.Source != "Steam", InstallationSource = installation.Source,
                LaunchExecutable = launchPath,
                InstallState = GameInstallState.Installed, Size = "Downloaded", CoverColor = color, CoverGlyph = glyph,
                DepotSummary = "DepotDownloader", ManifestSummary = "Local installation"
            };
        }).ToArray();
    }
}
