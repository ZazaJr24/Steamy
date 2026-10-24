using Microsoft.Win32;

namespace Steamy.Services;

public enum SteamPlatform { Windows, Linux, MacOS }
public enum SteamDistribution { Native, Flatpak }
public sealed record SteamInstallation(string Root, SteamPlatform Platform, SteamDistribution Distribution)
{
    public string Label => Distribution == SteamDistribution.Flatpak ? "Steam (Flatpak)" : "Steam";
}

/// <summary>Shared by the existing Windows app and the native desktop port. Does not modify Steam.</summary>
public static class SteamInstallationLocator
{
    public static SteamPlatform CurrentPlatform => OperatingSystem.IsWindows() ? SteamPlatform.Windows
        : OperatingSystem.IsMacOS() ? SteamPlatform.MacOS : SteamPlatform.Linux;
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static IReadOnlyList<SteamInstallation> FindAll() => FindAll(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), CurrentPlatform,
        Environment.GetEnvironmentVariable("XDG_DATA_HOME"));

    public static IReadOnlyList<SteamInstallation> FindAll(string home, SteamPlatform platform, string? xdgDataHome = null)
    {
        var result = new List<SteamInstallation>();
        var comparer = platform == SteamPlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var paths = new HashSet<string>(comparer);
        foreach (var installation in GetCandidates(home, platform, xdgDataHome))
        {
            try
            {
                var path = Path.GetFullPath(installation.Root);
                var info = new DirectoryInfo(path);
                if (!info.Exists) continue;
                if (info.LinkTarget is not null) path = info.ResolveLinkTarget(true)?.FullName ?? path;
                if (!IsSteamRoot(path, platform) || !paths.Add(path)) continue;
                result.Add(installation with { Root = path });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { /* One unavailable drive must not hide the other installations. */ }
        }
        return result;
    }

    public static SteamInstallation? FindDefault() => FindAll().FirstOrDefault();

    public static bool IsSteamRoot(string path, SteamPlatform platform) => Directory.Exists(path) && (platform switch
    {
        SteamPlatform.Windows => File.Exists(Path.Combine(path, "steam.exe")),
        SteamPlatform.MacOS => Directory.Exists(Path.Combine(path, "steamapps")) || File.Exists(Path.Combine(path, "steam_osx")),
        _ => Directory.Exists(Path.Combine(path, "steamapps")) || File.Exists(Path.Combine(path, "steam.sh"))
    });

    public static IEnumerable<SteamInstallation> GetCandidates(string home, SteamPlatform platform, string? xdgDataHome = null)
    {
        if (platform == SteamPlatform.Windows)
        {
            if (OperatingSystem.IsWindows())
                foreach (var path in ReadWindowsRegistry()) yield return new(path, platform, SteamDistribution.Native);
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrWhiteSpace(path)) yield return new(Path.Combine(path, "Steam"), platform, SteamDistribution.Native);
            }
            yield break;
        }
        if (platform == SteamPlatform.MacOS)
        {
            yield return new(Path.Combine(home, "Library", "Application Support", "Steam"), platform, SteamDistribution.Native);
            yield break;
        }
        if (!string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathFullyQualified(xdgDataHome))
            yield return new(Path.Combine(xdgDataHome, "Steam"), platform, SteamDistribution.Native);
        yield return new(Path.Combine(home, ".local", "share", "Steam"), platform, SteamDistribution.Native);
        yield return new(Path.Combine(home, ".steam", "steam"), platform, SteamDistribution.Native);
        yield return new(Path.Combine(home, ".steam", "root"), platform, SteamDistribution.Native);
        yield return new(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"), platform, SteamDistribution.Flatpak);
        yield return new(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".steam", "steam"), platform, SteamDistribution.Flatpak);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> ReadWindowsRegistry()
    {
        var paths = new List<string>();
        foreach (var (hive, name) in new[] {
            (Registry.CurrentUser, @"Software\Valve\Steam"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam") })
        {
            try
            {
                using var key = hive.OpenSubKey(name);
                if ((key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string) is { Length: > 0 } path)
                    paths.Add(path);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return paths;
    }
}
