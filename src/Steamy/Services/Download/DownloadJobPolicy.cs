using Steamy.Models;

namespace Steamy.Services;

/// <summary>State decisions shared by queue actions without depending on the UI.</summary>
public static class DownloadJobPolicy
{
    public static bool UsesModDownloader(string? mode, int? depotId, string? targetFolder)
    {
        // The standard tool can download a whole app too; a missing depot is not a Mod marker.
        if (string.Equals(mode?.Trim(), "DepotDownloader", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var marker in new[] { "Ryuu", "Mod", "Hubcap", "Zaza", "DepotBox" })
            if (mode?.Contains(marker, StringComparison.OrdinalIgnoreCase) == true) return true;

        // Preserve the legacy fallback only for queue records that never stored a tool mode.
        return string.IsNullOrWhiteSpace(mode) && depotId is null && !string.IsNullOrWhiteSpace(targetFolder);
    }

    public static DownloadJobState AfterLocalCheck(DownloadJobState previous, bool hasContent) =>
        previous == DownloadJobState.Completed && !hasContent ? DownloadJobState.Failed : previous;

    public static DownloadJobState RestoreState(string? savedState, bool autoResume)
    {
        if (!Enum.TryParse<DownloadJobState>(savedState, out var state) || !Enum.IsDefined(state))
            return DownloadJobState.Paused;
        return state is DownloadJobState.Preparing or DownloadJobState.Downloading or DownloadJobState.Verifying
            ? autoResume ? DownloadJobState.Queued : DownloadJobState.Paused
            : state;
    }
}
