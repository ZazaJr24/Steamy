using System.IO;

namespace Steamy.Services;

/// <summary>
/// Finds the SteamID64 of the account that was last logged into the local Steam client, read from
/// <c>loginusers.vdf</c> (the entry marked MostRecent). Never a secret: the id only names the
/// account for public profile lookups such as the account game list on the Share page.
/// </summary>
public static class SteamIdLookup
{
    /// <summary>Finds the Steam installation the same way the library scan does.</summary>
    private static string? FindSteamRoot()
    {
        if (OperatingSystem.IsWindows())
        foreach (var (hive, path) in new[]
                 {
                     (Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam"),
                     (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Valve\Steam")
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                var value = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
                if (string.IsNullOrWhiteSpace(value)) continue;
                var candidate = Path.GetFullPath(value);
                if (Directory.Exists(candidate)) return candidate;
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
            {
                // The next candidate is tried instead.
            }
        }

        var fallbacks = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
        };
        return fallbacks.FirstOrDefault(Directory.Exists);
    }

    public static ulong? FindLocalSteamId(string? steamRootOverride = null)
    {
        var root = string.IsNullOrWhiteSpace(steamRootOverride)
            ? FindSteamRoot()
            : steamRootOverride.Trim();
        if (string.IsNullOrWhiteSpace(root)) return null;

        var file = Path.Combine(root, "config", "loginusers.vdf");
        if (!File.Exists(file)) return null;

        try
        {
            var parsed = VdfParser.Parse(File.ReadAllText(file));
            VdfNode? newest = null;
            var newestStamp = -1L;
            foreach (var user in parsed.Children)
            {
                if (user.GetLong("MostRecent") == 1)
                    return ulong.TryParse(user.Key, out var recent) && recent > 0 ? recent : null;

                var stamp = user.GetLong("Timestamp", -1);
                if (stamp > newestStamp && ulong.TryParse(user.Key, out var id) && id > 0)
                {
                    newest = user;
                    newestStamp = stamp;
                }
            }

            if (newest is null) return null;
            return ulong.TryParse(newest.Key, out var best) && best > 0 ? best : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
