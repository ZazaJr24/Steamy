using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Steamy.Playtime;

namespace Steamy.Services;

public sealed record GameScanProgress(int Folders, int Games, int Skipped, string CurrentFolder);
public sealed record GameScanResult(IReadOnlyList<DownloadedInstallation> Games, int Folders, int Skipped, bool Cancelled);

/// <summary>Read-only, cancellable traversal. Identifies games from manifests or engine data, never from an arbitrary EXE alone.</summary>
public static class PcGameScanner
{
    public static GameScanResult Scan(IEnumerable<string> roots, IProgress<GameScanProgress>? progress = null, CancellationToken token = default)
    {
        var found = new Dictionary<string, DownloadedInstallation>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase));
        var clock = Stopwatch.StartNew();
        long lastReport = -200;
        int folders = 0, skipped = 0;
        string current = "";
        try
        {
            while (pending.TryPop(out current!))
            {
                token.ThrowIfCancellationRequested();
                if (!visited.Add(Path.TrimEndingDirectorySeparator(current))) continue;
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    folders++;
                    // One directory enumeration supplies cached attributes. Avoid an extra
                    // filesystem query for every texture, archive and unrelated document.
                    foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                    {
                        token.ThrowIfCancellationRequested();
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(entry.FullName);
                            continue;
                        }
                        if (!IsIdentityCandidate(entry.Name)) continue;
                        try
                        {
                            var game = ReadIdentity(entry.FullName, token);
                            if (game is null || GameClassification.IsRuntime(game.AppId, game.Name)) continue;
                            var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.Folder));
                            // Explicit app identities take precedence over engine-only candidates.
                            if (!found.TryGetValue(key, out var existing) || existing.AppId < 0 && game.AppId > 0)
                                found[key] = game with { Folder = key };
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.ComponentModel.Win32Exception) { }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { skipped++; }
                if (clock.ElapsedMilliseconds - lastReport >= 150)
                {
                    progress?.Report(new(folders, found.Count, skipped, current));
                    lastReport = clock.ElapsedMilliseconds;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        // A nested binary's app marker must not create a second card inside the same installation.
        var ordered = found.Values.OrderBy(g => g.Folder.Length).ToArray();
        var result = new List<DownloadedInstallation>();
        foreach (var game in ordered)
            if (!result.Any(parent => (parent.AppId == game.AppId || game.AppId < 0)
                && game.Folder.StartsWith(parent.Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) result.Add(game);
        progress?.Report(new(folders, result.Count, skipped, current ?? ""));
        return new(result, folders, skipped, token.IsCancellationRequested);
    }

    private static bool IsIdentityCandidate(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".item", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".acf", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".info", StringComparison.OrdinalIgnoreCase)
        || name.Equals("steam_appid.txt", StringComparison.OrdinalIgnoreCase)
        || name.Equals("steam_emu.ini", StringComparison.OrdinalIgnoreCase)
        || name.Equals("steam_api.ini", StringComparison.OrdinalIgnoreCase)
        || name.Equals("steam_api64.ini", StringComparison.OrdinalIgnoreCase)
        || name.Equals("steam_settings.ini", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ColdClientLoader.ini", StringComparison.OrdinalIgnoreCase);

    private static DownloadedInstallation? ReadIdentity(string file, CancellationToken token)
    {
        var name = Path.GetFileName(file);
        var folder = Path.GetDirectoryName(file)!;
        if (name.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".acf", StringComparison.OrdinalIgnoreCase))
        {
            var state = VdfParser.Parse(ReadSmall(file))["AppState"];
            if (state is null || state.GetInt("appid") <= 0 || (state.GetInt("StateFlags") & 4) == 0) return null;
            var installName = state.GetString("installdir");
            if (string.IsNullOrWhiteSpace(installName) || Path.IsPathRooted(installName) || installName.Contains("..")) return null;
            var install = Path.Combine(folder, "common", installName);
            return Directory.Exists(install) ? new(state.GetInt("appid"), state.GetString("name") ?? installName, install, "Steam") : null;
        }
        if (name.Equals("steam_appid.txt", StringComparison.OrdinalIgnoreCase)
            || new[] { "steam_emu.ini", "steam_api.ini", "steam_api64.ini", "steam_settings.ini", "ColdClientLoader.ini" }.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            var text = ReadSmall(file);
            var value = name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? text.Trim()
                : Regex.Match(text, @"(?im)^\s*(?:AppId|SteamAppId)\s*=\s*(\d+)\s*(?:[;#].*)?$").Groups[1].Value;
            if (!int.TryParse(value, out var id) || id <= 0) return null;
            var root = GameRoot(folder);
            var exe = FindExecutable(root, token);
            return exe is null ? null : new(id, Path.GetFileName(root), root, "DepotDownloader", exe);
        }
        if (name.EndsWith(".item", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(ReadSmall(file));
            var data = doc.RootElement;
            if (!data.TryGetProperty("InstallLocation", out var location) || !data.TryGetProperty("DisplayName", out var title)) return null;
            var root = location.GetString();
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root)) return null;
            var exe = FindExecutable(root, token);
            return exe is null ? null : new(LocalId(root), title.GetString() ?? Path.GetFileName(root), root, "Epic", exe);
        }
        if (name.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".info", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(ReadSmall(file));
            var exe = FindExecutable(folder, token);
            return exe is null ? null : new(LocalId(folder), doc.RootElement.TryGetProperty("name", out var title) ? title.GetString() ?? Path.GetFileName(folder) : Path.GetFileName(folder), folder, "GOG", exe);
        }
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || new FileInfo(file).Length < 100_000
            || GameProcessMatcher.Match(file, [new(1, folder)]) is null) return null;
        var unity = File.Exists(Path.Combine(folder, "UnityPlayer.dll")) && Directory.Exists(Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + "_Data"));
        var unreal = name.Contains("-Shipping", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(folder).Equals("Win64", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(Path.Combine(GameRoot(folder), "Content"));
        var godot = File.Exists(Path.ChangeExtension(file, ".pck"));
        if (!unity && !unreal && !godot) return null;
        var gameRoot = unreal ? GameRoot(folder) : folder;
        string? product = null;
        try { product = FileVersionInfo.GetVersionInfo(file).ProductName; }
        catch (System.ComponentModel.Win32Exception) { }
        var gameName = string.IsNullOrWhiteSpace(product) || product.Equals("Unreal Engine", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(gameRoot) : product;
        return new(LocalId(gameRoot), ResolveName(gameName, gameRoot, file), gameRoot, "Local", file);
    }

    internal static string ResolveName(string name, string folder, string executable)
    {
        if (!string.IsNullOrWhiteSpace(name) && !new[] { "Content", "Binaries", "Win64", "Game", "Unreal Engine" }.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase))
            return name.Trim();
        // Xbox packages commonly install the game under <title>/Content.
        if (Path.GetFileName(folder).Equals("Content", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(Path.GetDirectoryName(folder)) ?? name;
        var title = Regex.Replace(Path.GetFileNameWithoutExtension(executable), @"(?i)-(?:Win64|Win32|Windows)(?:-Shipping)?$|-Shipping$", "");
        return string.IsNullOrWhiteSpace(title) ? Path.GetFileName(folder) : title;
    }

    private static string ReadSmall(string path) => new FileInfo(path).Length <= 1024 * 1024 ? File.ReadAllText(path) : "";
    private static string GameRoot(string folder)
    {
        if (new[] { "Win64", "Win32", "x64", "x86" }.Contains(Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase)) folder = Path.GetDirectoryName(folder)!;
        if (new[] { "Binaries", "Bin", "bin64" }.Contains(Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase)) folder = Path.GetDirectoryName(folder)!;
        return folder;
    }
    public static int LocalId(string folder)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)).ToUpperInvariant()));
        return -Math.Max(1, BitConverter.ToInt32(bytes) & int.MaxValue);
    }
    private static string? FindExecutable(string root, CancellationToken token)
    {
        var pending = new Queue<(string Folder, int Depth)>();
        pending.Enqueue((root, 0));
        string? best = null;
        long size = 0;
        var count = 0;
        while (pending.TryDequeue(out var next) && count++ < 1024)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(next.Folder) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var exe in Directory.EnumerateFiles(next.Folder, "*.exe"))
                    if ((File.GetAttributes(exe) & FileAttributes.ReparsePoint) == 0 && new FileInfo(exe).Length is var length && length >= 100_000 && length > size
                        && GameProcessMatcher.Match(exe, [new(1, root)]) is not null) { best = exe; size = length; }
                if (next.Depth < 4)
                    foreach (var child in Directory.EnumerateDirectories(next.Folder))
                        if (pending.Count < 1024 && !new[] { "_CommonRedist", "redist", "Support", "Tools", "Engine" }.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) pending.Enqueue((child, next.Depth + 1));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return best;
    }
}
