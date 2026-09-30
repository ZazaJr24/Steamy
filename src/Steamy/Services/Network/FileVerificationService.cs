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
    public Task<FileVerificationResult> VerifyAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => LocalDownloadInspector.Inspect(path, cancellationToken), cancellationToken);
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
