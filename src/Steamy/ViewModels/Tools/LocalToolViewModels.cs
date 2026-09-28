using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed class XStoreUnlockerViewModel : ToolRunnerViewModel
{
    public XStoreUnlockerViewModel(ILocalToolRunner runner, ISettingsService settings, IGitHubToolDownloadService dl)
        : base(
            runner, settings,
            pageKey: "xstore-unlocker",
            title: "XStoreUnlocker",
            subtitle: "Microsoft Store & Xbox PC DLC Unlocker — auto-downloads from GitHub.",
            expectedFileNameHint: "XStoreUnlocker.exe",
            infoText: "XStoreUnlocker unlocks DLC for Microsoft Store and Xbox PC titles. The tool is downloaded automatically.",
            downloadUrl: "https://github.com/Zephkek/XStoreUnlocker",
            readPath: s => s.XStoreUnlockerPath,
            writePath: (s, v) => s.XStoreUnlockerPath = v,
            downloadService: dl,
            toolDefinition: ToolDefinitions.XStoreUnlocker)
    { }
}

public sealed class UnsteamViewModel : ToolRunnerViewModel
{
    public UnsteamViewModel(ILocalToolRunner runner, ISettingsService settings)
        : base(
            runner, settings,
            pageKey: "unsteam",
            title: "Unsteam",
            subtitle: "Steam multiplayer emulator — browse and configure manually.",
            expectedFileNameHint: "Unsteam.exe",
            infoText: "Unsteam is used for Steam multiplayer emulation. Launches with its own GUI window. Browse to select the executable manually.",
            downloadUrl: "https://github.com/Starter0110/UnammedSteamToolBox",
            showWindow: true,
            readPath: s => s.UnsteamPath,
            writePath: (s, v) => s.UnsteamPath = v)
    { }
}

public sealed class ScreamApiViewModel : ToolRunnerViewModel
{
    public ScreamApiViewModel(ILocalToolRunner runner, ISettingsService settings, IGitHubToolDownloadService dl)
        : base(
            runner, settings,
            pageKey: "screamapi",
            title: "ScreamAPI",
            subtitle: "Epic Games DLC Unlocker — auto-downloads from GitHub.",
            expectedFileNameHint: "ScreamAPI.dll",
            infoText: "ScreamAPI replaces the Epic Online Services SDK DLL in a game folder to unlock DLC for Epic Games titles. Downloads automatically from GitHub.",
            downloadUrl: "https://github.com/acidicoala/ScreamAPI",
            readPath: s => s.ScreamApiPath,
            writePath: (s, v) => s.ScreamApiPath = v,
            downloadService: dl,
            toolDefinition: ToolDefinitions.ScreamApi)
    { }
}

public sealed class HvFixesViewModel : ToolRunnerViewModel
{
    public HvFixesViewModel(ILocalToolRunner runner, ISettingsService settings)
        : base(
            runner, settings,
            pageKey: "hvfixes",
            title: "HV Fixes",
            subtitle: "Hardware virtualization fixes for Denuvo-protected games.",
            expectedFileNameHint: "HVFixes.exe",
            infoText: "HV Fixes applies compatibility patches for games with Hyper-V/VBS/WSL2 issues.",
            downloadUrl: "https://github.com",
            readPath: s => s.HvFixesPath,
            writePath: (s, v) => s.HvFixesPath = v)
    { }
}

public sealed class SteamAchievementManagerViewModel : ToolRunnerViewModel
{
    public SteamAchievementManagerViewModel(ILocalToolRunner runner, ISettingsService settings, IGitHubToolDownloadService dl)
        : base(
            runner, settings,
            pageKey: "sam",
            title: "SAM",
            subtitle: "Steam Achievement Manager — auto-downloads from GitHub.",
            expectedFileNameHint: "SAM.Picker.exe",
            infoText: "Steam Achievement Manager lets you lock and unlock achievements on your Steam account. Requires Steam to be running. Downloads automatically from GitHub.",
            downloadUrl: "https://github.com/gibbed/SteamAchievementManager",
            showWindow: true,
            readPath: s => s.SamPath,
            writePath: (s, v) => s.SamPath = v,
            downloadService: dl,
            toolDefinition: ToolDefinitions.SteamAchievementManager)
    { }
}

public sealed class AutoSteamCrackerViewModel : ToolRunnerViewModel
{
    public AutoSteamCrackerViewModel(ILocalToolRunner runner, ISettingsService settings, IGitHubToolDownloadService dl)
        : base(
            runner, settings,
            pageKey: "autosteamcracker",
            title: "SteamAutoCracker",
            subtitle: "Automatic Steam game cracker — auto-downloads from GitHub.",
            expectedFileNameHint: "SteamAutoCracker.exe",
            infoText: "SteamAutoCracker automatically applies Steamless + Goldberg emulator to crack Steam DRM in one click. Downloads automatically from GitHub.",
            downloadUrl: "https://github.com/BigBoiCJ/SteamAutoCracker",
            showWindow: true,
            readPath: s => s.AutoSteamCrackerPath,
            writePath: (s, v) => s.AutoSteamCrackerPath = v,
            downloadService: dl,
            toolDefinition: ToolDefinitions.AutoSteamCracker)
    { }
}
