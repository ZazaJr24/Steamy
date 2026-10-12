using Microsoft.Win32;
using Steamy.Playtime;
using Steamy.Services;

namespace Steamy.Tracking;

internal static class SteamGameReader
{
    public static IReadOnlyList<TrackedGame> Read(TrackerConfiguration config)
    {
        var games = new List<TrackedGame>((config.Games ?? []).Where(g => g.AppId != 0 && Directory.Exists(g.InstallDirectory)).Take(5000));
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var root = !string.IsNullOrWhiteSpace(config.SteamRoot) ? config.SteamRoot : key?.GetValue("SteamPath") as string;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return games;
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
            var folders = VdfParser.Parse(ReadSmall(Path.Combine(root, "steamapps", "libraryfolders.vdf")))["libraryfolders"];
            foreach (var folder in folders?.Children ?? [])
            {
                var path = folder.GetString("path") ?? folder.Value;
                if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)) libraries.Add(path);
            }
            foreach (var library in libraries.Take(100))
            {
                var steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) continue;
                foreach (var file in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf").Take(5000))
                {
                    var state = VdfParser.Parse(ReadSmall(file))["AppState"];
                    if (state is null || (state.GetInt("StateFlags") & 4) == 0 || state.GetInt("appid") <= 0) continue;
                    var relative = state.GetString("installdir");
                    if (string.IsNullOrWhiteSpace(relative)) continue;
                    var common = Path.GetFullPath(Path.Combine(steamapps, "common")) + Path.DirectorySeparatorChar;
                    var install = Path.GetFullPath(Path.Combine(common, relative));
                    if (install.StartsWith(common, StringComparison.OrdinalIgnoreCase) && Directory.Exists(install))
                        games.Add(new(state.GetInt("appid"), install));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        return games.DistinctBy(g => (g.AppId, g.InstallDirectory.ToUpperInvariant())).ToArray();
    }
    private static string ReadSmall(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length < 2 * 1024 * 1024 ? File.ReadAllText(path) : ""; }
        catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; }
    }
}
