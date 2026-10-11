using System.IO;

namespace Steamy.Services;

public sealed record GreenLumaDownloadCandidates(string? InjectorDirectory, string? ArchivePath);

/// <summary>Bounded filesystem discovery for the optional GreenLuma auto-installer.</summary>
public static class GreenLumaDownloadDiscovery
{
    public static GreenLumaDownloadCandidates Scan(string downloadsDirectory, int maximumDepth = 3)
    {
        if (string.IsNullOrWhiteSpace(downloadsDirectory) || maximumDepth < 0)
            return new GreenLumaDownloadCandidates(null, null);

        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((downloadsDirectory, 0));

        while (pending.TryPop(out var current))
        {
            if (!Directory.Exists(current.Directory)) continue;

            if (HasInjector(current.Directory))
                return new GreenLumaDownloadCandidates(current.Directory, null);

            if (current.Depth >= maximumDepth) continue;

            try
            {
                foreach (var subdirectory in Directory.EnumerateDirectories(current.Directory))
                {
                    try
                    {
                        if ((File.GetAttributes(subdirectory) & FileAttributes.ReparsePoint) != 0)
                            continue;
                        pending.Push((subdirectory, current.Depth + 1));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new GreenLumaDownloadCandidates(null, FindNewestArchive(downloadsDirectory));
    }

    private static bool HasInjector(string directory)
    {
        if (File.Exists(Path.Combine(directory, "DLLInjector.exe"))) return true;
        if (!File.Exists(Path.Combine(directory, "x64launcher.exe"))) return false;

        try
        {
            return Directory.EnumerateFiles(directory, "GreenLuma*.dll", SearchOption.TopDirectoryOnly).Any();
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string? FindNewestArchive(string downloadsDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(downloadsDirectory, "GreenLuma*.zip", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(downloadsDirectory, "GreenLuma*.7z", SearchOption.TopDirectoryOnly))
                .Concat(Directory.EnumerateFiles(downloadsDirectory, "GreenLuma*.rar", SearchOption.TopDirectoryOnly))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
