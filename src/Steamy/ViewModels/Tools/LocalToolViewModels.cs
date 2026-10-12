using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed class ScreamApiViewModel : ToolRunnerViewModel
{
    public ScreamApiViewModel(ILocalToolRunner runner, ISettingsService settings)
        : base(
            runner, settings,
            pageKey: "screamapi",
            title: "ScreamAPI",
            subtitle: "Epic DLC Unlocker — browse and configure manually.",
            expectedFileNameHint: "ScreamAPI.dll",
            infoText: "ScreamAPI replaces the Epic Online Services SDK DLL in a game folder to unlock DLC. GitHub API is rate-limited; browse to select the file manually.",
            downloadUrl: "https://github.com/acidicoala/ScreamAPI",
            readPath: s => s.ScreamApiPath,
            writePath: (s, v) => s.ScreamApiPath = v)
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

