namespace SteamContentManager.Services;

public static class ToolDefinitions
{
    public static readonly GitHubToolDefinition CreamInstaller = new(
        "FroggMaster", "CreamInstaller", "CreamInstaller", "CreamInstaller.exe",
        name => name.Equals("CreamInstaller.exe", StringComparison.OrdinalIgnoreCase));

    public static readonly GitHubToolDefinition Goldberg = new(
        "Detanup01", "gbe_fork", "Goldberg", "generate_interfaces_file.exe",
        name =>
        {
            var lower = name.ToLowerInvariant();
            return lower.Contains("win") && lower.Contains("release") && lower.EndsWith(".7z") && !lower.Contains("debug");
        });

    public static readonly GitHubToolDefinition SteamAutoCrack = new(
        "oureveryday", "Steam-auto-crack", "SteamAutoCrack", "SteamAutoCracker.exe",
        name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    // No working GitHub repo with releases — manual browse only.
    public static readonly GitHubToolDefinition? GreenLuma2024 = null;

    public static readonly GitHubToolDefinition XStoreUnlocker = new(
        "Zephkek", "XStoreUnlocker", "XStoreUnlocker", "XStoreUnlocker.exe",
        name => name.Contains("XGameRuntime", StringComparison.OrdinalIgnoreCase)
             && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    // GitHub API returns 403 (rate-limited) — manual browse only.
    public static readonly GitHubToolDefinition? ScreamApi = null;

    // Repo does not exist (404) — manual browse only.
    public static readonly GitHubToolDefinition? Unsteam = null;

    public static readonly GitHubToolDefinition SteamTicketGenerator = new(
        "denuvosanctuary", "steam-ticket-generator", "DenuvoGenerator", "steam-ticket-generator.exe",
        name => name.Contains("windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
}
