using System.IO;
using System.Reflection;

namespace SteamContentManager;

/// <summary>
/// Identifies the binary that is actually running. When an error dialog appears, this text makes
/// it obvious whether the user is looking at a current build or at an older copy that is still
/// lying around somewhere on disk.
/// </summary>
public static class BuildStamp
{
    public static string Describe()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "unknown";

        string location;
        try
        {
            location = assembly.Location;
        }
        catch (NotSupportedException)
        {
            return $"Build {version} (single-file)";
        }

        var built = File.Exists(location)
            ? File.GetLastWriteTime(location).ToString("dd.MM.yyyy HH:mm:ss")
            : "unknown";

        return $"Build {version} — compiled {built}{Environment.NewLine}{location}";
    }
}
