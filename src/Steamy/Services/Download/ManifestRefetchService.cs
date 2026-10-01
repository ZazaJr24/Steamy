using System.IO;
using Steamy.Models;

namespace Steamy.Services;

public interface IManifestRefetchService
{
    /// <summary>Checks a Mod job's saved selection without requesting a newer source version.</summary>
    Task<bool> RefreshBeforeResumeAsync(DownloadJob job, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, ManifestSource? source = null);
}

/// <summary>Resume uses only the manifest selection saved for this app and target.</summary>
public sealed class ManifestRefetchService : IManifestRefetchService
{
    private readonly ILoggingService _logging;

    public ManifestRefetchService(IManifestSourceService sources, ILoggingService logging) => _logging = logging;

    public Task<bool> RefreshBeforeResumeAsync(DownloadJob job, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, ManifestSource? source = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (job.AppId <= 0 || string.IsNullOrWhiteSpace(job.DownloadMode)
            || !job.DownloadMode.Contains("Mod", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(true);

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy", "ryuu-workdir");
        if (!string.IsNullOrWhiteSpace(job.TargetFolder) && Path.IsPathFullyQualified(job.TargetFolder))
        {
            var session = DepotResumeStateStore.SessionDirectory(root, job.AppId, job.TargetFolder);
            if (DepotResumeStateStore.Read(session, job.AppId, job.TargetFolder) is not null)
            {
                progress?.Report("Using the saved manifest selection — checking existing files before continuing.");
                return Task.FromResult(true);
            }
        }

        const string message = "The saved depot selection is missing or invalid. Existing files were kept; start a new download to select its source and manifest versions.";
        progress?.Report(message);
        _logging.Add(LogLevel.Warning, "Resume", message, job.AppId, job.Id);
        return Task.FromResult(false);
    }
}
