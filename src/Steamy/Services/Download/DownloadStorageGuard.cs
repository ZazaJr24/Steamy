using System.IO;

namespace Steamy.Services;

public sealed record DownloadStorageCheck(bool CanDownload, string Message, long? AvailableBytes = null);

/// <summary>Checks storage without deleting or changing an existing download.</summary>
public static class DownloadStorageGuard
{
    public const long WorkingSpaceReserveBytes = 64 * 1024 * 1024;

    public static DownloadStorageCheck Evaluate(long availableBytes, long? remainingBytes = null)
    {
        var expected = Math.Max(0, remainingBytes ?? 0);
        var required = expected > long.MaxValue - WorkingSpaceReserveBytes
            ? long.MaxValue : expected + WorkingSpaceReserveBytes;
        return availableBytes >= required
            ? new(true, "Storage is available", availableBytes)
            : new(false, "Not enough free disk space — free space in this drive or choose another folder. Existing download files were kept.", Math.Max(0, availableBytes));
    }

    public static DownloadStorageCheck Check(string targetFolder, long? remainingBytes = null)
    {
        if (string.IsNullOrWhiteSpace(targetFolder) || !Path.IsPathFullyQualified(targetFolder))
            return new(false, "Choose a full, absolute download folder before starting.");
        try
        {
            var target = Path.GetFullPath(targetFolder);
            if (File.Exists(target)) return new(false, "The chosen folder is a file — choose a download folder instead.");
            var root = Path.GetPathRoot(target);
            if (string.IsNullOrWhiteSpace(root)) return new(false, "The download folder has no available drive.");
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return new(false, "The download drive is unavailable — reconnect it or choose another folder. Existing files were kept.");
            return Evaluate(drive.AvailableFreeSpace, remainingBytes);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return new(false, "The download drive could not be checked — check folder permissions and reconnect the drive before retrying.");
        }
    }
}
