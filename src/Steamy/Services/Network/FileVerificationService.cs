using System.IO;
using System.Security.Cryptography;
using Steamy.Models;

namespace Steamy.Services;

/// <summary>Hashes a local file. Used to check that an imported manifest did not change.</summary>
public static class FileHashing
{
    public static async Task<string> Sha1Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>What was actually found in a target folder. Nothing is repaired or deleted.</summary>
public sealed record FileVerificationResult(bool HasContent, long FileCount, long TotalBytes, string Message);

public interface IFileVerificationService
{
    Task<FileVerificationResult> VerifyAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Local, read-only check of a download target: it counts the files that exist and sums their
/// size. It never claims a cryptographic verification that did not happen.
/// </summary>
public sealed class LocalFileVerificationService : IFileVerificationService
{
    public async Task<FileVerificationResult> VerifyAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new FileVerificationResult(false, 0, 0, "Verification unavailable — no target folder is set.");

        if (!Directory.Exists(path))
            return new FileVerificationResult(false, 0, 0, $"Verification unavailable — the folder does not exist: {path}");

        return await Task.Run(() => Inspect(path, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static FileVerificationResult Inspect(string path, CancellationToken cancellationToken)
    {
        long fileCount = 0;
        long totalBytes = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length <= 0) continue;
                    fileCount++;
                    totalBytes += info.Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A locked or unreadable file is skipped instead of failing the whole check.
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new FileVerificationResult(false, fileCount, totalBytes, "Verification failed — the target folder could not be read.");
        }

        return fileCount == 0
            ? new FileVerificationResult(false, 0, 0, "Verification found no files in the target folder.")
            : new FileVerificationResult(true, fileCount, totalBytes,
                $"Local check passed: {fileCount} file(s), {ByteSize.Format(totalBytes)}");
    }
}

/// <summary>
/// Checks a locally imported manifest file. It only reads the file: the hash is recomputed and
/// compared with the recorded one, and the manifest itself is never modified.
/// </summary>
public sealed class LocalManifestService : IManifestService
{
    public async Task<bool> ValidateAsync(Manifest manifest, CancellationToken cancellationToken = default)
    {
        if (manifest is null) return false;

        if (string.IsNullOrWhiteSpace(manifest.Path) || !File.Exists(manifest.Path))
        {
            manifest.ValidationStatus = "File missing";
            return false;
        }

        try
        {
            var info = new FileInfo(manifest.Path);
            if (info.Length <= 0)
            {
                manifest.ValidationStatus = "Empty file";
                return false;
            }

            if (string.IsNullOrWhiteSpace(manifest.Hash))
            {
                manifest.ValidationStatus = "Readable, no hash recorded";
                return true;
            }

            var hash = await FileHashing.Sha1Async(manifest.Path, cancellationToken).ConfigureAwait(false);
            var matches = string.Equals(hash, manifest.Hash, StringComparison.OrdinalIgnoreCase);
            manifest.ValidationStatus = matches ? "Locally verified (SHA-1)" : "Hash changed since import";
            return matches;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            manifest.ValidationStatus = $"Could not be read ({exception.GetType().Name})";
            return false;
        }
    }
}
