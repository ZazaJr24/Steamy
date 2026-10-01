using System.IO;
using System.Net.Http;

namespace Steamy.Services;

public sealed class DownloadQueuePersistenceException(string message, Exception innerException)
    : IOException(message, innerException);

public static class DownloadFailurePolicy
{
    public static bool CanRetry(string? output)
    {
        var detail = output ?? string.Empty;
        return !new[] { "not enough space", "no space left", "disk full", "access denied", "unauthorized",
            "invalid password", "invalid depot key", "does not own", "no subscription", "missing license",
            "invalid manifest", "manifest not found", "login denied", "account logon denied" }
            .Any(marker => detail.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public static string Describe(Exception exception)
    {
        if (exception is DownloadQueuePersistenceException)
            return "The download queue could not be saved — no new download was started. Check app storage permissions and try again.";
        if (exception is HttpRequestException or TimeoutException)
            return "Network connection interrupted — existing files were kept. Check your connection and retry to continue.";
        if (exception is UnauthorizedAccessException)
            return "The download folder cannot be written — check permissions or choose another folder. Existing files were kept.";
        if (exception is IOException io && (io.HResult & 0xFFFF) is 0x27 or 0x70)
            return "The drive is full — free some space and retry to continue. Existing files were kept.";
        if (exception is IOException)
            return "The download drive could not be written — check free space and reconnect the drive, then retry. Existing files were kept.";
        return $"Download failed: {exception.GetType().Name}. Existing files were kept; see the download details and retry.";
    }
}
