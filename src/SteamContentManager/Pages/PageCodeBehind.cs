using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SteamContentManager.Services;
using SteamContentManager.ViewModels;
using Wpf.Ui.Appearance;

namespace SteamContentManager.Pages;

public partial class DashboardPage : Page
{
    public DashboardPage() { InitializeComponent(); DataContext = App.Services.GetRequiredService<DashboardViewModel>(); }

    // Enter in the dashboard search box carries the query into the Games grid and switches to it.
    private void DashboardSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        var query = DashboardSearch.Text?.Trim() ?? string.Empty;
        App.Services.GetRequiredService<LibraryViewModel>().SearchText = query;
        App.Services.GetRequiredService<INavigationService>().Navigate<LibraryPage>();
    }
}

public partial class DownloadsPage : Page
{
    public DownloadsPage() { InitializeComponent(); DataContext = App.Services.GetRequiredService<DownloadsViewModel>(); }
}

public partial class DepotsPage : Page
{
    public DepotsPage() { InitializeComponent(); DataContext = App.Services.GetRequiredService<DepotsViewModel>(); }
}

public partial class BranchesPage : Page
{
    public BranchesPage() { InitializeComponent(); DataContext = App.Services.GetRequiredService<BranchesViewModel>(); }
}

public partial class AchievementsPage : Page
{
    public AchievementsPage() { InitializeComponent(); DataContext = App.Services.GetRequiredService<AchievementsViewModel>(); }
}


public partial class ModFixesPage : Page
{
    public ModFixesPage() { InitializeComponent(); DataContext = App.Services.GetRequiredService<ModFixesViewModel>(); }
}


public sealed partial class DenuvoGenerationPage : Page
{
    public DenuvoGenerationPage() { InitializeComponent(); DataContext = App.Services.GetRequiredService<DenuvoGenerationViewModel>(); }

    private DenuvoGenerationViewModel ViewModel => (DenuvoGenerationViewModel)DataContext;

    /// <summary>The page owns the file dialog; the view model only writes the file.</summary>
    private async void ExportPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export the local preview",
                Filter = "JSON files (*.json)|*.json|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = "local-preview.json",
                AddExtension = true,
                DefaultExt = ".json",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            await ViewModel.ExportAsync(dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"The preview could not be exported.{Environment.NewLine}{exception.Message}",
                "Steam Content Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}

public partial class LogsPage : Page
{
    public LogsPage() { InitializeComponent(); DataContext = App.Services.GetRequiredService<LogsViewModel>(); }
}


