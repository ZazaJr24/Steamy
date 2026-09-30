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
        Unloaded += (_, _) => { var model = (DashboardViewModel)DataContext; model.StopLiveStats(); model.StopSearch(); };
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        DashboardSearch.Width = width < 900 ? 260 : 340;
        HomeHeading.FontSize = width < 1000 ? 36 : 42;
        HomeHeading.LineHeight = HomeHeading.FontSize * 1.15;
        var stacked = width < 720;
        Grid.SetRow(DashboardHero, stacked ? 1 : 0);
        Grid.SetColumn(DashboardHero, stacked ? 0 : 2);
        Grid.SetColumnSpan(DashboardHero, stacked ? 3 : 1);
        Grid.SetColumnSpan(HeroCopy, stacked ? 3 : 1);
        DashboardHero.Margin = stacked ? new Thickness(0, 18, 0, 0) : new Thickness(0);
        SpotlightGutter.Width = new GridLength(stacked ? 0 : 28);
    }

    private void GameCover_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Models.Game game }) OpenInLibrary(game.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // Enter in the dashboard search box carries the query into the Games grid and switches to it.
    private void DashboardSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var model = (DashboardViewModel)DataContext;
        if (e.Key == System.Windows.Input.Key.Escape) { model.SearchText = string.Empty; e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Enter) { model.OpenSearchCommand.Execute(null); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Down && model.SearchResults.Count > 0)
        {
            SearchMatches.UpdateLayout();
            if (SearchMatches.ItemContainerGenerator.ContainerFromIndex(0) is DependencyObject container)
            {
                var button = FindSearchButton(container);
                if (button is not null) { button.Focus(); e.Handled = true; }
            }
        }
    }

    private static System.Windows.Controls.Primitives.ButtonBase? FindSearchButton(DependencyObject element)
    {
        if (element is System.Windows.Controls.Primitives.ButtonBase button) return button;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindSearchButton(System.Windows.Media.VisualTreeHelper.GetChild(element, index)) is { } child) return child;
        return null;
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
            var showNetwork = e.NewSize.Width >= 920 && e.NewSize.Height >= 620;
            NetworkColumn.Width = new GridLength(showNetwork ? 226 : 0);
            NetworkCard.Visibility = showNetwork ? Visibility.Visible : Visibility.Collapsed;
            FeaturedPanel.Height = 190;
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

