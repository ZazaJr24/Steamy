namespace Steamy.Services;

public static class ToolDefinitions
{
    public static readonly GitHubToolDefinition CreamInstaller = new(
        "FroggMaster", "CreamInstaller", "CreamInstaller", "CreamInstaller.exe",
        name => name.Equals("CreamInstaller.exe", StringComparison.OrdinalIgnoreCase));

    public static readonly GitHubToolDefinition Steamless = new(
        "atom0s", "Steamless", "Steamless", "Steamless.CLI.exe",
        name => name.Contains("Steamless", StringComparison.OrdinalIgnoreCase)
             && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    // No working GitHub repo with releases — manual browse only.
    public static readonly GitHubToolDefinition? GreenLuma2024 = null;

    public static readonly GitHubToolDefinition XStoreUnlocker = new(
        "Zephkek", "XStoreUnlocker", "XStoreUnlocker", "XStoreUnlocker.exe",
        name => name.Contains("XGameRuntime", StringComparison.OrdinalIgnoreCase)
             && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    public static readonly GitHubToolDefinition ScreamApi = new(
        "acidicoala", "ScreamAPI", "ScreamAPI", "ScreamAPI.dll",
        name => name.Contains("ScreamAPI", StringComparison.OrdinalIgnoreCase)
             && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    // Repo does not exist (404) — manual browse only.
    public static readonly GitHubToolDefinition? Unsteam = null;

    public static readonly GitHubToolDefinition SteamTicketGenerator = new(
        "denuvosanctuary", "steam-ticket-generator", "DenuvoGenerator", "steam-ticket-generator.exe",
        name => name.Contains("windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    public static readonly GitHubToolDefinition BetterSteamTools = new(
        "madoiscool", "BetterSteamTools", "BetterSteamTools", "BetterSteamTools.exe",
        name => name.Contains("BetterSteamTools", StringComparison.OrdinalIgnoreCase)
             && (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
              || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));

    public static readonly GitHubToolDefinition SteamAchievementManager = new(
        "gibbed", "SteamAchievementManager", "SAM", "SAM.Picker.exe",
        name => name.StartsWith("SteamAchievementManager", StringComparison.OrdinalIgnoreCase)
             && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    public static readonly GitHubToolDefinition AutoSteamCracker = new(
        "BigBoiCJ", "SteamAutoCracker", "SteamAutoCracker", "SteamAutoCracker.exe",
        name => name.Contains("SteamAutoCracker", StringComparison.OrdinalIgnoreCase)
             && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    public static readonly GitHubToolDefinition SteamRE = new(
        "SteamRE", "SteamRE", "SteamRE", "SteamRE.exe",
        name => name.Contains("SteamRE", StringComparison.OrdinalIgnoreCase)
             && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
}
