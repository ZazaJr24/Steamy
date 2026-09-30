namespace Steamy.Models;

public enum DownloadJobState
{
    Queued,
    Preparing,
    Downloading,
    Verifying,
    Completed,
    Failed,
    Cancelled,
    Paused
}

