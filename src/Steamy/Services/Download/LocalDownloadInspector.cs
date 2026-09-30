using System.IO;

namespace Steamy.Services;

/// <summary>A read-only presence check, not a comparison against depot manifest hashes.</summary>
public sealed record FileVerificationResult(bool HasContent, long FileCount, long TotalBytes, string Message);

public static class LocalDownloadInspector
{
    public static FileVerificationResult Inspect(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
            return new(false, 0, 0, "Local check unavailable — no target folder is set.");
        if (!Directory.Exists(path))
            return new(false, 0, 0, "Local check unavailable — the target folder does not exist.");

        long fileCount = 0;
        long totalBytes = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        try
        {
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    // A junction can lead outside the target or form a cycle. Never follow it.
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        // Manifests and partially downloaded chunks are not completed game data.
                        if (!Path.GetFileName(entry).Equals(".DepotDownloader", StringComparison.OrdinalIgnoreCase))
                            pending.Push(entry);
                        continue;
                    }

                    totalBytes += new FileInfo(entry).Length;
                    fileCount++;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(false, fileCount, totalBytes, "Local check incomplete — some files could not be read. No integrity result is available.");
        }

        return totalBytes == 0
            ? new(false, fileCount, totalBytes, "Local check found no non-empty game files. Resume to verify and download missing data.")
            : new(true, fileCount, totalBytes,
                $"Local check: {fileCount:N0} file(s), {totalBytes:N0} bytes present. Integrity is checked by the downloader when resuming.");
    }
}
