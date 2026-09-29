using System.Globalization;
using System.IO;
using Steamy.Models;

namespace Steamy.Services;

public interface IManifestRefetchService
{
    /// <summary>
    /// Makes sure the cached manifests of a paused download are current before it continues.
    /// True when fresh data is in place (or nothing needed doing), false when it failed.
    /// </summary>
    Task<bool> RefreshBeforeResumeAsync(DownloadJob job, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, ManifestSource? source = null);
}

/// <summary>
/// Resume support: a download that was paused can sit for hours or days. Steam publishes new
/// manifests in that time and continuing with the old ones makes the tool validate content it
/// cannot match. So every resume first re-asks the source the job came from for a fresh Lua and
/// manifest set. Steam itself may still ask for a 2FA/Steam Guard confirmation in its own console
/// when a depot is licensed but not cached — the app never sees or stores that code.
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

        // The source the download started with is refreshed; Zaza is only the fallback when the
        // mode does not name a source (older jobs) — it needs no key, so it is the safest guess.
        var manifestSource = source ?? ReadSourceFromMode(job.DownloadMode) ?? ManifestSource.Zaza;

        progress?.Report("Refreshing manifests before resume…");
        try
        {
            var result = await _sources.DownloadManifestsAsync(manifestSource, job.AppId, progress, cancellationToken)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                progress?.Report("Manifests are up to date — continuing.");
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
