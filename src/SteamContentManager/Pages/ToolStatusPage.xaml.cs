using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SteamContentManager.ViewModels;

namespace SteamContentManager.Pages;

public partial class ToolStatusPage : Page
{
    public ToolStatusPage(string title, string subtitle, string statusTitle, string statusMessage, string? infoText = null)
    {
        InitializeComponent();
        DataContext = new ToolStatusViewModel(title, subtitle, statusTitle, statusMessage, infoText);
    }
}

public sealed class DenuvoFixesPage : ToolStatusPage
{
    public DenuvoFixesPage()
        : base(
            "Denuvo Fixes",
            "Safe local compatibility and diagnostics only.",
            "No supported workflow configured",
            "Denuvo Fixes is reserved for authorized, reversible local compatibility workflows. No bypass or license-circumvention operation is available here.")
    {
    }
}

public sealed class HvFixesPage : ToolRunnerPage
{
    public HvFixesPage() : base(App.Services.GetRequiredService<HvFixesViewModel>()) { }
}

public sealed class UnsteamPage : ToolRunnerPage
{
    public UnsteamPage() : base(App.Services.GetRequiredService<UnsteamViewModel>()) { }
}

public sealed class ScreamApiPage : ToolRunnerPage
{
    public ScreamApiPage() : base(App.Services.GetRequiredService<ScreamApiViewModel>()) { }
}
