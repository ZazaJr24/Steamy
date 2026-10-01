using System.IO;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Steamy.Services;

/// <summary>Outcome of <see cref="ArchiveExtractor.Extract"/>.</summary>
public sealed record ArchiveExtractResult(bool Succeeded, string Message, int FilesWritten = 0, long BytesWritten = 0, bool UsedPassword = false);

/// <summary>Progress of an extraction: files written so far out of all files in the archive.</summary>
public readonly record struct ArchiveExtractProgress(int Done, int Total, string CurrentFile);

/// <summary>
/// Extracts ZIP, 7z and RAR archives (including RAR5, solid and multi-volume sets such as
/// <c>.part1.rar</c> / <c>.7z.001</c>) plus tar-based formats, detected by content rather than by
/// file extension. Every archive is read in one sequential pass, which is what keeps solid 7z/RAR
/// archives fast, and no entry can escape the target folder.
/// </summary>
public static class ArchiveExtractor
{
    /// <summary>File-dialog filter for every format the extractor reads.</summary>
    public const string DialogFilter =
        "Archives (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar;*.001;*.tar;*.gz;*.tgz;*.xz;*.bz2|ZIP|*.zip|7-Zip|*.7z;*.001|RAR|*.rar|All files|*.*";

    /// <summary>
    /// Extracts <paramref name="archivePath"/> into <paramref name="targetFolder"/>. When the archive
    /// is password protected, the given passwords are tried in order.
    /// </summary>
    public static ArchiveExtractResult Extract(string archivePath, string targetFolder, IEnumerable<string>? passwords = null,
        IProgress<ArchiveExtractProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath)) return new ArchiveExtractResult(false, "The archive was not found.");

        var attempts = new List<string?> { null };
        attempts.AddRange((passwords ?? Array.Empty<string>()).Where(password => !string.IsNullOrEmpty(password)).Distinct());

        Exception? lastError = null;
        var encrypted = false;
        foreach (var password in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ExtractOnce(archivePath, targetFolder, password, progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (EncryptedArchiveException exception)
            {
                encrypted = true;
                lastError = exception;
            }
            catch (Exception exception) when (password is not null || LooksLikePasswordProblem(exception))
            {
                // A wrong password shows up as a data/CRC error, so any failure with a password set
                // just means "try the next one".
                encrypted = true;
                lastError = exception;
            }
            catch (Exception exception)
            {
                return new ArchiveExtractResult(false, Describe(exception));
            }
        }

        return new ArchiveExtractResult(false, encrypted
            ? "The archive is password protected and none of the known passwords fit."
            : Describe(lastError));
    }

    private static ArchiveExtractResult ExtractOnce(string archivePath, string targetFolder, string? password,
        IProgress<ArchiveExtractProgress>? progress, CancellationToken cancellationToken)
    {
        var options = new ReaderOptions { Password = password };
        using var archive = Open(archivePath, options);

        var files = archive.Entries.Where(entry => !entry.IsDirectory).ToList();
        if (files.Count == 0) return new ArchiveExtractResult(false, "The archive is empty.");
        // Only entries with content count: SharpCompress reports empty 7z files (which have no
        // coder folder) as encrypted, and gbe_fork's archive contains a few of those.
        if (password is null && files.Any(entry => entry.IsEncrypted && entry.Size > 0))
            throw new EncryptedArchiveException();

        var root = Path.GetFullPath(targetFolder);
        Directory.CreateDirectory(root);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        var written = 0;
        long bytes = 0;
        void Write(string key, long size, Action<Stream> copy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = SafeDestination(rootWithSeparator, key);
            if (destination is null) return;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var cancellable = new CancellationCheckingWriteStream(output, cancellationToken))
                copy(cancellable);

            cancellationToken.ThrowIfCancellationRequested();
            written++;
            bytes += size;
            progress?.Report(new ArchiveExtractProgress(written, files.Count, key));
        }

        // Solid archives (and 7z, which always needs its folder streams in order) are decoded in one
        // sequential pass; per-entry access would restart decompression for every file.
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key)) continue;
                Write(entry.Key, entry.Size, reader.WriteEntryTo);
            }
        }
        else
        {
            foreach (var entry in files)
            {
                if (string.IsNullOrWhiteSpace(entry.Key)) continue;
                Write(entry.Key, entry.Size, output =>
                {
                    using var input = entry.OpenEntryStream();
                    input.CopyTo(output, 1 << 16);
                });
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ArchiveExtractResult(true, $"Extracted {written} file(s).", written, bytes, password is not null);
    }

    /// <summary>Opens single archives and multi-volume sets (part1.rar, .7z.001, .r00 …).</summary>
    private static IArchive Open(string archivePath, ReaderOptions options)
    {
        var parts = ArchiveFactory.GetFileParts(archivePath).Where(File.Exists).ToList();
        return parts.Count > 1
            ? ArchiveFactory.OpenArchive(parts.Select(part => new FileInfo(part)).ToList(), options)
            : ArchiveFactory.OpenArchive(archivePath, options);
    }

    /// <summary>
    /// The full path an entry is written to, or null when its name would leave the target folder
    /// ("../", absolute paths, drive letters).
    /// </summary>
    public static string? SafeDestination(string rootWithSeparator, string entryKey)
    {
        var relative = entryKey.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':')) return null;

        var full = Path.GetFullPath(Path.Combine(rootWithSeparator, relative));
        return full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static bool LooksLikePasswordProblem(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SharpCompress.Common.CryptographicException) return true;
            var message = current.Message;
            if (message.Contains("password", StringComparison.OrdinalIgnoreCase)
                || message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string Describe(Exception? exception) => exception switch
    {
        null => "The archive could not be extracted.",
        InvalidOperationException or SharpCompress.Common.SharpCompressException =>
            $"This is not a supported archive or it is damaged ({exception.Message}).",
        IOException or UnauthorizedAccessException => $"The files could not be written: {exception.Message}",
        _ => $"The archive could not be extracted: {exception.Message}"
    };

    private sealed class EncryptedArchiveException : Exception
    {
        public EncryptedArchiveException() : base("The archive is password protected.") { }
    }

    /// <summary>
    /// SharpCompress's solid-archive reader writes synchronously to a supplied stream.
    /// Checking every write also interrupts a single large entry, while the caller keeps
    /// ownership of the destination and closes it even when extraction is cancelled.
    /// </summary>
    internal sealed class CancellationCheckingWriteStream(Stream destination, CancellationToken cancellationToken) : Stream
    {
        private bool _disposed;
        public override bool CanRead => false;
        public override bool CanSeek => !_disposed && destination.CanSeek;
        public override bool CanWrite => !_disposed && destination.CanWrite;
        public override long Length { get { Check(); return destination.Length; } }
        public override long Position
        {
            get { Check(); return destination.Position; }
            set { Check(); destination.Position = value; }
        }

        private void Check()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check();
            destination.Write(buffer, offset, count);
            Check();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check();
            destination.Write(buffer);
            Check();
        }

        public override void WriteByte(byte value)
        {
            Check();
            destination.WriteByte(value);
            Check();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken callerCancellation) =>
            WriteAsync(buffer.AsMemory(offset, count), callerCancellation).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken callerCancellation = default)
        {
            Check();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, callerCancellation);
            await destination.WriteAsync(buffer, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            Check();
        }

        public override void Flush()
        {
            Check();
            destination.Flush();
            Check();
        }

        public override async Task FlushAsync(CancellationToken callerCancellation)
        {
            Check();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, callerCancellation);
            await destination.FlushAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            Check();
        }

        public override long Seek(long offset, SeekOrigin origin) { Check(); return destination.Seek(offset, origin); }
        public override void SetLength(long value) { Check(); destination.SetLength(value); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
