// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

// Steamy fork addition, 2026-10-03.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DepotDownloader;

internal static class SteamyPaths
{
    public static void CheckExisting(string fullPath)
    {
        for (var path = Path.GetFullPath(fullPath); path != null; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new SteamyDownloadException("unsafe_path", "Download blocked: the target contains a symbolic link or junction.");
    }
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.StartsWith('/') || relative.StartsWith('\\'))
            throw new SteamyDownloadException("unsafe_path", "Download blocked: the manifest contains an unsafe file path.");
        var parts = relative.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => c < 32 || "<>:\"|?*".Contains(c))
                || new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem, StringComparer.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9')
                || part.Equals(".DepotDownloader", StringComparison.OrdinalIgnoreCase)
                || part.Equals(".steamy-rollback", StringComparison.OrdinalIgnoreCase))
                throw new SteamyDownloadException("unsafe_path", "Download blocked: the manifest contains an unsafe file path.");
        }
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(canonicalRoot, Path.Combine(parts)));
        if (!full.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new SteamyDownloadException("unsafe_path", "Download blocked: a manifest path leaves the target folder.");
        CheckExisting(full);
        return full;
    }
}

/// <summary>Same-volume replacement after a flushed write, keeping the previous checkpoint.</summary>
internal static class SteamyAtomicFile
{
    public static void Write(string path, Action<Stream> write, bool preserveBackup = true)
    {
        path = Path.GetFullPath(path);
        SteamyPaths.CheckExisting(path);
        var temporary = path + ".steamy-" + Guid.NewGuid().ToString("N") + ".tmp";
        var backupTemporary = temporary + ".backup";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            if (preserveBackup && File.Exists(path))
            {
                SteamyPaths.CheckExisting(path + ".bak");
                File.Copy(path, backupTemporary);
                using (var backup = new FileStream(backupTemporary, FileMode.Open, FileAccess.Write, FileShare.None)) backup.Flush(true);
                File.Move(backupTemporary, path + ".bak", overwrite: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
            TryDelete(backupTemporary);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>OS-held lock shared by standalone runs and every Steamy download process.</summary>
internal sealed class SteamyTargetLease : IDisposable
{
    private readonly FileStream stream;
    public SteamyTargetLease(string directory)
    {
        var root = Path.GetFullPath(directory);
        SteamyPaths.CheckExisting(root);
        var config = Path.Combine(root, ".DepotDownloader");
        SteamyPaths.CheckExisting(config);
        Directory.CreateDirectory(config);
        var path = Path.Combine(config, "steamy.target.lock");
        SteamyPaths.CheckExisting(path);
        try { stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new SteamyDownloadException("target_busy", "Download blocked: another process is using this target folder."); }
    }
    public void Dispose() => stream.Dispose(); // Keep the inode: deleting it introduces a lock race.
}
