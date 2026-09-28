// Auto-generated — do not edit by hand. Regenerate from Strings.resx.
namespace Steamy.Resources;

using System.Globalization;
using System.Resources;

public static class Strings
{
    private static readonly ResourceManager _rm = new("Steamy.Resources.Strings", typeof(Strings).Assembly);

    private static string Get(string name) => _rm.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    // Navigation
    public static string Nav_Dashboard => Get(nameof(Nav_Dashboard));
    public static string Nav_Games => Get(nameof(Nav_Games));
    public static string Nav_Tools => Get(nameof(Nav_Tools));
    public static string Nav_Fixes => Get(nameof(Nav_Fixes));
    public static string Nav_Utilities => Get(nameof(Nav_Utilities));
    public static string Nav_Downloads => Get(nameof(Nav_Downloads));
    public static string Nav_Settings => Get(nameof(Nav_Settings));

    // Tool names
    public static string Tool_Steamless => Get(nameof(Tool_Steamless));
    public static string Tool_DenuvoActivation => Get(nameof(Tool_DenuvoActivation));
    public static string Tool_DlcUnlocker => Get(nameof(Tool_DlcUnlocker));
    public static string Tool_GreenLuma => Get(nameof(Tool_GreenLuma));
    public static string Tool_Goldberg => Get(nameof(Tool_Goldberg));
    public static string Tool_BetterSteamTools => Get(nameof(Tool_BetterSteamTools));
    public static string Tool_ScreamApi => Get(nameof(Tool_ScreamApi));
    public static string Tool_XStoreUnlocker => Get(nameof(Tool_XStoreUnlocker));
    public static string Tool_AutoSteamCracker => Get(nameof(Tool_AutoSteamCracker));
    public static string Tool_AchievementManager => Get(nameof(Tool_AchievementManager));
    public static string Tool_GameFixes => Get(nameof(Tool_GameFixes));

    // ToolRunner Page
    public static string ToolRunner_Status => Get(nameof(ToolRunner_Status));
    public static string ToolRunner_NotConfigured => Get(nameof(ToolRunner_NotConfigured));
    public static string ToolRunner_Ready => Get(nameof(ToolRunner_Ready));
    public static string ToolRunner_Running => Get(nameof(ToolRunner_Running));
    public static string ToolRunner_Download => Get(nameof(ToolRunner_Download));
    public static string ToolRunner_Downloading => Get(nameof(ToolRunner_Downloading));
    public static string ToolRunner_Browse => Get(nameof(ToolRunner_Browse));
    public static string ToolRunner_Run => Get(nameof(ToolRunner_Run));
    public static string ToolRunner_Cancel => Get(nameof(ToolRunner_Cancel));
    public static string ToolRunner_Arguments => Get(nameof(ToolRunner_Arguments));
    public static string ToolRunner_Executable => Get(nameof(ToolRunner_Executable));
    public static string ToolRunner_WorkingDir => Get(nameof(ToolRunner_WorkingDir));
    public static string ToolRunner_Version => Get(nameof(ToolRunner_Version));
    public static string ToolRunner_Output => Get(nameof(ToolRunner_Output));
    public static string ToolRunner_Done => Get(nameof(ToolRunner_Done));
    public static string ToolRunner_Failed => Get(nameof(ToolRunner_Failed));
    public static string ToolRunner_Info => Get(nameof(ToolRunner_Info));
    public static string ToolRunner_AutoDownloaded => Get(nameof(ToolRunner_AutoDownloaded));
    public static string ToolRunner_SelectExecutable => Get(nameof(ToolRunner_SelectExecutable));

    // Settings Page
    public static string Settings_Title => Get(nameof(Settings_Title));
    public static string Settings_Appearance => Get(nameof(Settings_Appearance));
    public static string Settings_Theme => Get(nameof(Settings_Theme));
    public static string Settings_ThemeDesc => Get(nameof(Settings_ThemeDesc));
    public static string Settings_WindowBackdrop => Get(nameof(Settings_WindowBackdrop));
    public static string Settings_WindowBackdropDesc => Get(nameof(Settings_WindowBackdropDesc));
    public static string Settings_Language => Get(nameof(Settings_Language));
    public static string Settings_LanguageDesc => Get(nameof(Settings_LanguageDesc));
    public static string Settings_SteamConnection => Get(nameof(Settings_SteamConnection));
    public static string Settings_SteamApiKey => Get(nameof(Settings_SteamApiKey));
    public static string Settings_Save => Get(nameof(Settings_Save));
    public static string Settings_Reset => Get(nameof(Settings_Reset));
    public static string Settings_Export => Get(nameof(Settings_Export));
    public static string Settings_Import => Get(nameof(Settings_Import));
    public static string Settings_DownloadSettings => Get(nameof(Settings_DownloadSettings));
    public static string Settings_ParallelDownloads => Get(nameof(Settings_ParallelDownloads));
    public static string Settings_RetryCount => Get(nameof(Settings_RetryCount));
    public static string Settings_Timeout => Get(nameof(Settings_Timeout));
    public static string Settings_Connections => Get(nameof(Settings_Connections));
    public static string Settings_VerifyAfterDownload => Get(nameof(Settings_VerifyAfterDownload));
    public static string Settings_KeepHistory => Get(nameof(Settings_KeepHistory));
    public static string Settings_AutoResume => Get(nameof(Settings_AutoResume));
    public static string Settings_DebugLogging => Get(nameof(Settings_DebugLogging));
    public static string Settings_AutoUpdate => Get(nameof(Settings_AutoUpdate));
    public static string Settings_Notifications => Get(nameof(Settings_Notifications));
    public static string Settings_DnsSettings => Get(nameof(Settings_DnsSettings));

    // Dashboard
    public static string Dashboard_Title => Get(nameof(Dashboard_Title));
    public static string Dashboard_Welcome => Get(nameof(Dashboard_Welcome));
    public static string Dashboard_Subtitle => Get(nameof(Dashboard_Subtitle));
    public static string Dashboard_QuickActions => Get(nameof(Dashboard_QuickActions));
    public static string Dashboard_RecentDownloads => Get(nameof(Dashboard_RecentDownloads));
    public static string Dashboard_ToolStatus => Get(nameof(Dashboard_ToolStatus));
    public static string Dashboard_SystemHealth => Get(nameof(Dashboard_SystemHealth));
    public static string Dashboard_InstalledGames => Get(nameof(Dashboard_InstalledGames));
    public static string Dashboard_ActiveDownloads => Get(nameof(Dashboard_ActiveDownloads));
    public static string Dashboard_AvailableTools => Get(nameof(Dashboard_AvailableTools));

    // Common
    public static string Common_OK => Get(nameof(Common_OK));
    public static string Common_Cancel => Get(nameof(Common_Cancel));
    public static string Common_Save => Get(nameof(Common_Save));
    public static string Common_Close => Get(nameof(Common_Close));
    public static string Common_Error => Get(nameof(Common_Error));
    public static string Common_Warning => Get(nameof(Common_Warning));
    public static string Common_Success => Get(nameof(Common_Success));
    public static string Common_Loading => Get(nameof(Common_Loading));
    public static string Common_Search => Get(nameof(Common_Search));
    public static string Common_Refresh => Get(nameof(Common_Refresh));
    public static string Common_Delete => Get(nameof(Common_Delete));
    public static string Common_Copy => Get(nameof(Common_Copy));
    public static string Common_Paste => Get(nameof(Common_Paste));
    public static string Common_Yes => Get(nameof(Common_Yes));
    public static string Common_No => Get(nameof(Common_No));
    public static string Common_Available => Get(nameof(Common_Available));
    public static string Common_NotAvailable => Get(nameof(Common_NotAvailable));
    public static string Common_Installed => Get(nameof(Common_Installed));
    public static string Common_NotInstalled => Get(nameof(Common_NotInstalled));
    public static string Common_StartDownload => Get(nameof(Common_StartDownload));
    public static string Common_SelectFolder => Get(nameof(Common_SelectFolder));

    // Downloads
    public static string Downloads_Title => Get(nameof(Downloads_Title));
    public static string Downloads_NoActive => Get(nameof(Downloads_NoActive));
    public static string Downloads_Completed => Get(nameof(Downloads_Completed));
    public static string Downloads_Failed => Get(nameof(Downloads_Failed));
    public static string Downloads_Cancelled => Get(nameof(Downloads_Cancelled));
    public static string Downloads_Paused => Get(nameof(Downloads_Paused));
    public static string Downloads_Preparing => Get(nameof(Downloads_Preparing));
    public static string Downloads_ClearHistory => Get(nameof(Downloads_ClearHistory));

    // Library
    public static string Library_Title => Get(nameof(Library_Title));
    public static string Library_SearchGames => Get(nameof(Library_SearchGames));
    public static string Library_ChooseSource => Get(nameof(Library_ChooseSource));
    public static string Library_CheckingAvailability => Get(nameof(Library_CheckingAvailability));
    public static string Library_SelectDownloadFolder => Get(nameof(Library_SelectDownloadFolder));
}
