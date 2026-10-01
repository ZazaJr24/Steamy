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
    private readonly System.Windows.Threading.DispatcherTimer _spotlightTimer = new(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromSeconds(5) };
    private DateTime _nextFeedCheck = DateTime.MinValue;
    public DashboardPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DashboardViewModel>();
        Loaded += (_, _) =>
        {
            var viewModel = (DashboardViewModel)DataContext;
            viewModel.StartLiveStats();
            viewModel.PropertyChanged += SpotlightChanged;
            _spotlightTimer.Start();
            _ = viewModel.EnsureDiscoveryArtworkAsync();
            _ = viewModel.RefreshActivityAsync();
            _ = viewModel.LoadSearchHistoryAsync();
            _ = viewModel.RefreshAsync(force: false);
        };
        Unloaded += (_, _) =>
        {
            var model = (DashboardViewModel)DataContext;
            model.PropertyChanged -= SpotlightChanged;
            model.StopLiveStats(); model.StopSearch();
            _spotlightTimer.Stop();
            SpotlightArtworkFrame.BeginAnimation(OpacityProperty, null);
        };
        _spotlightTimer.Tick += (_, _) =>
        {
            var model = (DashboardViewModel)DataContext;
            if (!IsVisible || Application.Current?.MainWindow?.IsActive != true) return;
            if (DateTime.UtcNow >= _nextFeedCheck)
            {
                _nextFeedCheck = DateTime.UtcNow.AddHours(6);
                _ = model.EnsureDiscoveryArtworkAsync();
            }
            if (!model.HasSearchQuery)
                model.NextFeaturedCommand.Execute(null);
        };
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void SpotlightChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DashboardViewModel.FeaturedGame) && Controls.MotionPreferences.AnimationsEnabled) Controls.EntranceMotion.Reveal(SpotlightArtworkFrame);
    }

    private void Dashboard_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs args)
    {
        // Focus and refreshed bindings must not pull a manually scrolled page back to
        // Spotlight. Explicit keyboard navigation can still reveal the focused control.
        if (!Controls.ScrollNavigation.IsNavigationKeyDown()) args.Handled = true;
    }

    private void ApplyResponsiveLayout(double width)
    {
        // Artwork owns the width. Stack only the compact controls on narrow windows.
        var compact = width < 720;
        DashboardSearch.Width = width < 640 ? 220 : width < 900 ? 280 : 340;
        DashboardScroll.Padding = width < 640 ? new Thickness(18, 18, 18, 26) : new Thickness(28, 22, 28, 30);
        DashboardHero.Height = width < 720 ? 320 : width < 1000 ? 300 : 340;
        SpotlightArtworkFrame.Height = DashboardHero.Height - 2;
        SpotlightTitle.FontSize = width < 640 ? 22 : 26;
        Grid.SetRow(SpotlightActions, compact ? 1 : 0);
        Grid.SetColumn(SpotlightActions, compact ? 0 : 1);
        Grid.SetColumnSpan(SpotlightActions, compact ? 2 : 1);
        Grid.SetColumnSpan(SpotlightDetails, compact ? 2 : 1);
        SpotlightActions.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        SpotlightDetails.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 18, 0);
        Grid.SetRow(CurrentDownloadCard, compact ? 1 : 0);
        Grid.SetColumn(CurrentDownloadCard, compact ? 0 : 2);
        Grid.SetColumnSpan(CurrentDownloadCard, compact ? 3 : 1);
        Grid.SetColumnSpan(ContinueCard, compact ? 3 : 1);
        CurrentDownloadCard.Margin = compact ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        QuickActionsGutter.Width = new GridLength(compact ? 0 : 14);
    }

    private void GameCover_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Models.Game game }) OpenInLibrary(game.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // Enter in the dashboard search box carries the query into the Games grid and switches to it.
    private void DashboardSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var model = (DashboardViewModel)DataContext;
        if (e.Key == System.Windows.Input.Key.Escape) { model.SearchText = string.Empty; model.IsSearchFocused = false; e.Handled = true; }
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

    private void DashboardSearch_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs args)
    {
        var model = (DashboardViewModel)DataContext;
        model.IsSearchFocused = true;
        _ = model.LoadSearchHistoryAsync();
    }

    private void DashboardSearch_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs args)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            var focused = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
            var inside = false;
            while (focused is not null)
            {
                if (ReferenceEquals(focused, DashboardSearch) || ReferenceEquals(focused, DashboardSearchPanel)) { inside = true; break; }
                focused = focused is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(focused) : LogicalTreeHelper.GetParent(focused);
            }
            ((DashboardViewModel)DataContext).IsSearchFocused = inside;
        }));
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
            var showNetwork = e.NewSize.Width >= 740 && e.NewSize.Height >= 560;
            NetworkColumn.Width = new GridLength(showNetwork ? 226 : 0);
            NetworkCard.Visibility = showNetwork ? Visibility.Visible : Visibility.Collapsed;
            FeaturedPanel.Height = e.NewSize.Height < 620 ? 190 : 236;
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
