using System.Diagnostics;
using System.Globalization;

namespace Steamy.Services;

public enum SteamAction { Install, Run, Downloads, Library, Workshop }

/// <summary>Uses the selected Steam client; no shell interpolation or simulated download progress.</summary>
public static class SteamActions
{
    public static string GetUri(SteamAction action, int? appId = null)
    {
        if (action is SteamAction.Install or SteamAction.Run or SteamAction.Workshop && appId is not > 0)
            throw new ArgumentOutOfRangeException(nameof(appId), "A positive Steam App ID is required.");
        var id = appId?.ToString(CultureInfo.InvariantCulture);
        return action switch
        {
            SteamAction.Install => "steam://install/" + id,
            SteamAction.Run => "steam://run/" + id,
            SteamAction.Downloads => "steam://nav/downloads",
            SteamAction.Library => "steam://nav/games",
            SteamAction.Workshop => "steam://url/SteamWorkshopPage/" + id,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    public static ProcessStartInfo BuildStartInfo(SteamInstallation installation, SteamAction action, int? appId = null)
    {
        var uri = GetUri(action, appId);
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        switch (installation.Platform)
        {
            case SteamPlatform.Windows:
                start.FileName = Path.Combine(installation.Root, "steam.exe");
                break;
            case SteamPlatform.MacOS:
                start.FileName = "/usr/bin/open";
                start.ArgumentList.Add("-a"); start.ArgumentList.Add("Steam");
                break;
            default:
                start.FileName = installation.Distribution == SteamDistribution.Flatpak ? "flatpak" : "steam";
                if (installation.Distribution == SteamDistribution.Flatpak)
                { start.ArgumentList.Add("run"); start.ArgumentList.Add("com.valvesoftware.Steam"); }
                break;
        }
        start.ArgumentList.Add(uri);
        return start;
    }

    public static void Open(SteamInstallation installation, SteamAction action, int? appId = null)
    {
        using var process = Process.Start(BuildStartInfo(installation, action, appId))
            ?? throw new IOException("Steam could not be started.");
    }
}
