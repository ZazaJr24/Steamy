using System.IO;
using System.IO.Compression;

namespace Steamy.Services;

public static class ManifestArchiveReader
{
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;

    public static Task<(string? Lua, int Files)> ExtractAsync(string archive, string directory, CancellationToken token = default) => Task.Run(async () =>
    {
        using var zip = ZipFile.OpenRead(archive);
        var selected = zip.Entries.Where(entry => Path.GetExtension(entry.Name).ToLowerInvariant() is ".lua" or ".key" or ".manifest").ToArray();
        if (selected.Length > 4096 || selected.Any(entry => entry.Length > MaximumExpandedBytes)
            || selected.Sum(entry => entry.Length) > MaximumExpandedBytes
            || selected.Any(entry => entry.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) && entry.Length > 4 * 1024 * 1024))
            throw new InvalidDataException("Manifest archive exceeds the extraction limit.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in selected)
        {
            token.ThrowIfCancellationRequested();
            var normalized = entry.FullName.Replace('\\', '/');
            var segments = normalized.Split('/');
            if (segments.Any(part => part is ".." or "." || part.Contains(':')) || normalized.StartsWith('/')
                || entry.Name.IndexOfAny([':', '\\']) >= 0 || !names.Add(entry.Name))
                throw new InvalidDataException("Manifest archive has an unsafe or duplicate filename.");
        }
        Directory.CreateDirectory(directory);
        string? lua = null;
        long expanded = 0;
        var buffer = new byte[81920];
        foreach (var entry in selected)
        {
            token.ThrowIfCancellationRequested();
            await using (var source = entry.Open())
            await using (var destination = File.Create(Path.Combine(directory, entry.Name)))
            {
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    expanded += read;
                    if (expanded > MaximumExpandedBytes) throw new InvalidDataException("Manifest archive exceeds the extraction limit.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
            }
            if (Path.GetExtension(entry.Name).Equals(".lua", StringComparison.OrdinalIgnoreCase))
                lua ??= await File.ReadAllTextAsync(Path.Combine(directory, entry.Name), token).ConfigureAwait(false);
        }
        return (lua, selected.Length);
    }, token);
}
