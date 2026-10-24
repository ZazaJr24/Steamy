namespace Steamy;

/// <summary>User data stays per platform and outside the source or executable directory.</summary>
public static class PlatformPaths
{
    public static string DataRoot => GetDataRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Services.SteamInstallationLocator.CurrentPlatform, Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
        Environment.GetEnvironmentVariable("STEAMY_DATA_DIR"));

    public static string GetDataRoot(string home, Services.SteamPlatform platform, string? xdgDataHome = null, string? dataOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(dataOverride) && Path.IsPathFullyQualified(dataOverride)) return Path.GetFullPath(dataOverride);
        return platform switch
        {
            Services.SteamPlatform.Windows => Path.Combine(home, "AppData", "Roaming", "Steamy"),
            Services.SteamPlatform.MacOS => Path.Combine(home, "Library", "Application Support", "Steamy"),
            _ => Path.Combine(!string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathFullyQualified(xdgDataHome)
                ? xdgDataHome : Path.Combine(home, ".local", "share"), "Steamy")
        };
    }
}
