namespace Steamy.Services;

public static class ToolDefinitions
{
    public static readonly GitHubToolDefinition UcOnline = new(
        "LukeWarmSodas", "uc-online2", "UcOnline", "steam_api64.dll",
        name => name.EndsWith("-release.zip", StringComparison.OrdinalIgnoreCase));
    public static readonly GitHubToolDefinition ColdLoader = new(
        "denuvosanctuary", "coldloader", "ColdLoader", "coldloader.dll",
        name => name.EndsWith("x64.zip", StringComparison.OrdinalIgnoreCase) && !name.Contains("debug", StringComparison.OrdinalIgnoreCase));
    public static readonly GitHubToolDefinition ColdLoaderProxy = new(
        "denuvosanctuary", "coldloader-proxy", "ColdLoaderProxy", "version.dll",
        name => name.EndsWith("x64.zip", StringComparison.OrdinalIgnoreCase) && !name.Contains("debug", StringComparison.OrdinalIgnoreCase));
    public static readonly GitHubToolDefinition Goldberg = new(
        "Detanup01", "gbe_fork", "Goldberg", "steamclient64.dll",
        name => name.Contains("emu-win-release", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));
    public static readonly GitHubToolDefinition BetterSteamTools = new(
        "madoiscool", "BetterSteamTools", "BetterSteamTools", "OpenSteamTool.dll",
        name => name.StartsWith("OpenSteamTool-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("-Release.zip", StringComparison.OrdinalIgnoreCase));
    public static readonly GitHubToolDefinition CreamInstaller = new(
        "FroggMaster", "CreamInstaller", "CreamInstaller", "CreamInstaller.exe",
        name => name.Equals("CreamInstaller.exe", StringComparison.OrdinalIgnoreCase));

    public static readonly GitHubToolDefinition Steamless = new(
        "atom0s", "Steamless", "Steamless", "Steamless.CLI.exe",
        name => name.Contains("Steamless", StringComparison.OrdinalIgnoreCase)
             && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    // No working GitHub repo with releases — manual browse only.
    public static readonly GitHubToolDefinition? GreenLuma2024 = null;

    public static readonly GitHubToolDefinition SteamTicketGenerator = new(
        "denuvosanctuary", "steam-ticket-generator", "DenuvoGenerator", "steam-ticket-generator.exe",
        name => name.Contains("windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
}
