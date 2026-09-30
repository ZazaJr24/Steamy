using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Steamy.Services;
using Steamy.ViewModels;
using Wpf.Ui.Appearance;

namespace Steamy.Pages;

public partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DashboardViewModel>();
        Loaded += (_, _) =>
        {
            var viewModel = (DashboardViewModel)DataContext;
            viewModel.StartLiveStats();
            _ = viewModel.EnsureDiscoveryArtworkAsync();
            _ = viewModel.RefreshAsync(force: false);
        };
        Unloaded += (_, _) => ((DashboardViewModel)DataContext).StopLiveStats();
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        DashboardSearch.Width = width < 900 ? 230 : 340;
        DashboardHero.Height = Math.Clamp((width - 56) * 0.37, 340, 490);
        HeroCopy.MaxWidth = width < 900 ? 370 : 480;
        HeroTitle.FontSize = width < 900 ? 34 : 46;
    }

    private void GameCover_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Models.Game game }) OpenInLibrary(game.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // Enter in the dashboard search box carries the query into the Games grid and switches to it.
    private void DashboardSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        OpenInLibrary(DashboardSearch.Text?.Trim() ?? string.Empty);
    }

    private static void OpenInLibrary(string query)
    {
        var library = App.Services.GetRequiredService<LibraryViewModel>();
        library.SelectedSourceFilter = "All sources";
        library.SelectedTypeFilter = "All games";
        library.SearchText = query;
        App.Services.GetRequiredService<INavigationService>().Navigate<LibraryPage>();
    }
}

public partial class DownloadsPage : Page
{
    public DownloadsPage()
    {
        InitializeComponent();
        var viewModel = App.Services.GetRequiredService<DownloadsViewModel>();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.StartLiveStats();
        Unloaded += (_, _) => viewModel.StopLiveStats();
        SizeChanged += (_, e) =>
        {
            var showNetwork = e.NewSize.Width >= 1080 && e.NewSize.Height >= 700;
            NetworkColumn.Width = new GridLength(showNetwork ? 280 : 0);
            NetworkCard.Visibility = showNetwork ? Visibility.Visible : Visibility.Collapsed;
            FeaturedPanel.Height = e.NewSize.Height < 700 ? 210 : 238;
        };
    }

    // Clicking Start/Pause swaps the buttons and WPF would scroll the card into view; only
    // keyboard navigation should move the page.
    private void JobList_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (System.Windows.Input.InputManager.Current.MostRecentInputDevice is System.Windows.Input.MouseDevice)
            e.Handled = true;
    }
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
                "Steamy",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}

public partial class LogsPage : Page
{
    public LogsPage() { InitializeComponent(); DataContext = App.Services.GetRequiredService<LogsViewModel>(); }
}

