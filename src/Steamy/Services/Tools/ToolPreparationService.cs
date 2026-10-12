using System.IO;
using System.Text.Json;

namespace Steamy.Services;

public sealed record ToolPreparationStatus(string Phase, int Completed, int Total, bool Done = false, string? Error = null);

/// <summary>Installer-only preparation. Downloads files; never executes a tool or changes Steam/games.</summary>
public static class ToolPreparationService
{
    public static async Task PrepareAsync(string statusPath, CancellationToken token = default,
        IGitHubToolDownloadService? downloader = null, IDenuvoGeneratorDownloadService? generator = null, string? dataRoot = null, string? bundledRoot = null)
    {
        var root = dataRoot ?? Steamy.Storage.AppDataPaths.Root;
        var definitions = new[] { ToolDefinitions.BetterSteamTools, ToolDefinitions.Steamless, ToolDefinitions.CreamInstaller,
            ToolDefinitions.XStoreUnlocker, ToolDefinitions.UcOnline, ToolDefinitions.ColdLoader, ToolDefinitions.ColdLoaderProxy, ToolDefinitions.Goldberg };
        var total = definitions.Length + 1; var completed = 0;
        void Report(string phase, bool done = false, string? error = null)
        {
            var full = Path.GetFullPath(statusPath); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full + ".tmp", JsonSerializer.Serialize(new ToolPreparationStatus(phase, completed, total, done, error)));
            File.Move(full + ".tmp", full, true);
        }
        try
        {
            using var ownedDownloader = downloader is null ? new GitHubToolDownloadService(baseCacheDir: Path.Combine(root, "ToolCache")) : null;
            var downloads = downloader ?? ownedDownloader!;
            foreach (var tool in definitions)
            {
                Report("Preparing " + tool.RepoName + "…"); token.ThrowIfCancellationRequested();
                string? cached = downloads.GetCachedPath(tool);
                if (tool == ToolDefinitions.Steamless && File.Exists(Path.Combine(bundledRoot ?? AppContext.BaseDirectory, "Tools", "Steamless", "Steamless.CLI.exe")))
                { completed++; continue; }
                if (cached is null || tool == ToolDefinitions.Goldberg && FullSteamClient.Find(Path.GetDirectoryName(cached)!) is null)
                {
                    var result = await downloads.DownloadLatestAsync(tool, token);
                    if (!result.Succeeded || result.CachedPath is null) throw new IOException(tool.RepoName + ": " + result.Message);
                    cached = result.CachedPath;
                }
                if (tool == ToolDefinitions.ColdLoader || tool == ToolDefinitions.ColdLoaderProxy)
                    CopyTree(Path.GetDirectoryName(cached)!, Path.Combine(root, "Tools", tool.CacheSubfolder));
                if (tool == ToolDefinitions.Goldberg)
                {
                    var cache = Path.Combine(root, "ToolCache", tool.CacheSubfolder);
                    if (FullSteamClient.Find(cache) is null) throw new InvalidDataException("Goldberg did not include a full x64 steamclient64.dll.");
                    CopyTree(cache, Path.Combine(root, "gbe_fork", "current"));
                }
                completed++;
            }
            Report("Preparing activation generator…");
            using var ownedGenerator = generator is null ? new DenuvoGeneratorDownloadService(cacheDirectory: Path.Combine(root, "DenuvoGeneratorCache")) : null;
            var activationGenerator = generator ?? ownedGenerator!;
            if (!activationGenerator.HasCachedExecutable)
            {
                var result = await activationGenerator.DownloadLatestAsync(token);
                if (!result.Succeeded) throw new IOException(result.Message);
            }
            completed++; Report("Tools ready", true);
        }
        catch (Exception e) { Report("Tool preparation failed", true, e.Message); throw; }
    }
    private static void CopyTree(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.GetFullPath(Path.Combine(destination, Path.GetRelativePath(source, file)));
            if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe tool cache path.");
            for (var path = target; path is not null; path = Path.GetDirectoryName(path))
                if ((Directory.Exists(path) || File.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Tool folder contains a symbolic link.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
        }
    }
}
