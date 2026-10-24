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

