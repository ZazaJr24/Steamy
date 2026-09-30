using System.Globalization;
using System.IO;
using Steamy.Models;

namespace Steamy.Services;

public interface IManifestRefetchService
{
    /// <summary>
    /// Restores missing source metadata for legacy jobs. Existing manifests stay pinned so
    /// resuming a paused download cannot silently switch the game version.
    /// </summary>
    Task<bool> RefreshBeforeResumeAsync(DownloadJob job, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, ManifestSource? source = null);
}

/// <summary>
/// Existing manifests define the version the user chose. Resume preserves them, including the
/// per-target snapshot, and only contacts a source when all local metadata is missing.
/// </summary>
public sealed class ManifestRefetchService : IManifestRefetchService
{
    private readonly IManifestSourceService _sources;
    private readonly ILoggingService _logging;

    public ManifestRefetchService(IManifestSourceService sources, ILoggingService logging)
    {
        _sources = sources;
        _logging = logging;
    }

    public async Task<bool> RefreshBeforeResumeAsync(DownloadJob job, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, ManifestSource? source = null)
    {
        if (job.AppId <= 0) return true;

        // DepotDownloader handles plain jobs itself; only the per-depot Mod runs need local manifests.
        var modeUsesLocalManifests = !string.IsNullOrWhiteSpace(job.DownloadMode)
            && job.DownloadMode.Contains("Mod", StringComparison.OrdinalIgnoreCase);
        if (!modeUsesLocalManifests) return true;

        var app = job.AppId.ToString(CultureInfo.InvariantCulture);
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy");
        var ryuuRoot = Path.Combine(appData, "ryuu-workdir");
        if (!string.IsNullOrWhiteSpace(job.TargetFolder))
        {
            var session = DepotResumeStateStore.SessionDirectory(ryuuRoot, job.AppId, job.TargetFolder);
            if (DepotResumeStateStore.Read(session, job.AppId, job.TargetFolder) is not null)
            {
                progress?.Report("Using the saved manifest version — checking existing files before continuing.");
                return true;
            }
        }

        var directories = new List<string> { Path.Combine(ryuuRoot, app) };
        var manifestRoot = Path.Combine(appData, "manifest-workdir");
        if (Directory.Exists(manifestRoot))
        {
            directories.Add(Path.Combine(manifestRoot, app));
            directories.AddRange(Directory.EnumerateDirectories(manifestRoot).Select(folder => Path.Combine(folder, app)));
        }
        if (directories.Any(folder => Directory.Exists(folder)
            && (Directory.EnumerateFiles(folder, "*.lua").Any()
                || Directory.EnumerateFiles(folder, "*.manifest").Any())))
        {
            progress?.Report("Using cached manifests — existing downloaded chunks will be checked.");
            return true;
        }

        // No usable local cache exists (for example a migrated job on a new machine).
        var manifestSource = source ?? ReadSourceFromMode(job.DownloadMode) ?? ManifestSource.Zaza;

        progress?.Report("Restoring missing manifest metadata…");
        try
        {
            var result = await _sources.DownloadManifestsAsync(manifestSource, job.AppId, progress, cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                progress?.Report("Manifest metadata restored — continuing.");
                _logging.Add(LogLevel.Info, "Resume", $"Refreshed manifests for App {job.AppId} from {manifestSource} before resume.", job.AppId, job.Id);
                return true;
            }

            // A source that says no must not block the resume: the cached set is still tried.
            progress?.Report($"Refresh skipped ({result.Message}) — continuing with cached manifests.");
            _logging.Add(LogLevel.Warning, "Resume", $"Manifest refresh failed for App {job.AppId}: {result.Message}", job.AppId, job.Id);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            progress?.Report($"Refresh failed ({exception.Message}) — continuing with cached manifests.");
            _logging.Add(LogLevel.Warning, "Resume", $"Manifest refresh error for App {job.AppId}: {exception.Message}", job.AppId, job.Id);
            return true;
        }
    }

    private static ManifestSource? ReadSourceFromMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return null;
        foreach (var name in new[] { "Ryuu", "Zaza", "Hubcap", "DepotBox" })
        {
            if (mode.Contains(name, StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<ManifestSource>(name, out var source))
                return source;
        }

        return null;
    }
}
