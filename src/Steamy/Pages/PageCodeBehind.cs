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
        { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly System.Windows.Threading.DispatcherTimer _countdownTimer = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _nextFeedCheck = DateTime.MinValue;
    private TimeSpan _slideElapsed;
    private long _lastSlideTick;
    private Window? _carouselWindow;
    private bool _loaderRunning;
    public DashboardPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DashboardViewModel>();
        Loaded += (_, _) =>
        {
            var viewModel = (DashboardViewModel)DataContext;
            viewModel.RefreshSearchPreference();
            viewModel.StartLiveStats();
            viewModel.PropertyChanged += SpotlightChanged;
            Controls.MotionPreferences.Changed += MotionChanged;
            _carouselWindow = Window.GetWindow(this);
            if (_carouselWindow is not null)
            {
                _carouselWindow.Activated += CarouselActivated;
                _carouselWindow.Deactivated += CarouselDeactivated;
            }
            _lastSlideTick = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_carouselWindow?.IsActive == true) _spotlightTimer.Start();
            _countdownTimer.Start();
            _ = viewModel.EnsureDiscoveryArtworkAsync();
            _ = viewModel.RefreshActivityAsync();
            _ = viewModel.RefreshAsync(force: false);
        };
        Unloaded += (_, _) =>
        {
            var model = (DashboardViewModel)DataContext;
            model.PropertyChanged -= SpotlightChanged;
            Controls.MotionPreferences.Changed -= MotionChanged;
            MotionChanged(this, EventArgs.Empty);
            model.StopLiveStats(); model.StopSearch();
            _spotlightTimer.Stop();
            if (_carouselWindow is not null)
            {
                _carouselWindow.Activated -= CarouselActivated;
                _carouselWindow.Deactivated -= CarouselDeactivated;
                _carouselWindow = null;
            }
            HideSpotlightLoader();
            _countdownTimer.Stop();
            SpotlightArtworkFrame.BeginAnimation(OpacityProperty, null);
        };
        _countdownTimer.Tick += (_, _) => ((DashboardViewModel)DataContext).RefreshCountdowns();
        _spotlightTimer.Tick += CarouselTick;
        DashboardHero.SizeChanged += (_, _) => DashboardHero.Clip = new System.Windows.Media.RectangleGeometry(new Rect(DashboardHero.RenderSize), 16, 16);
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void CarouselActivated(object? sender, EventArgs args)
    {
        _lastSlideTick = System.Diagnostics.Stopwatch.GetTimestamp();
        if (IsLoaded) _spotlightTimer.Start();
    }

    private void CarouselDeactivated(object? sender, EventArgs args)
    {
        _spotlightTimer.Stop();
        HideSpotlightLoader();
    }

    private void CarouselTick(object? sender, EventArgs args)
    {
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastSlideTick);
        _lastSlideTick = System.Diagnostics.Stopwatch.GetTimestamp();
        var model = (DashboardViewModel)DataContext;
        if (!IsVisible || _carouselWindow?.IsActive != true || model.SpotlightPaused || model.HasSearchQuery)
        {
            HideSpotlightLoader();
            return;
        }
        if (DateTime.UtcNow >= _nextFeedCheck)
        {
            _nextFeedCheck = DateTime.UtcNow.AddHours(6);
            _ = model.EnsureDiscoveryArtworkAsync();
        }
        _slideElapsed += elapsed > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : elapsed;
        if (_slideElapsed >= TimeSpan.FromSeconds(5))
        {
            _slideElapsed = TimeSpan.Zero;
            HideSpotlightLoader();
            model.NextFeaturedCommand.Execute(null);
        }
        else if (_slideElapsed >= TimeSpan.FromSeconds(4.4) && !_loaderRunning && Controls.MotionPreferences.AnimationsEnabled)
        {
            _loaderRunning = true;
            SpotlightLoader.Visibility = Visibility.Visible;
            LoaderRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(5) - _slideElapsed));
        }
    }

    private void HideSpotlightLoader()
    {
        _loaderRunning = false;
        SpotlightLoader.Visibility = Visibility.Collapsed;
        LoaderRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
    }

    private void RecommendationArtwork_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is FrameworkElement artwork)
            artwork.Clip = new System.Windows.Media.RectangleGeometry(new Rect(artwork.RenderSize), 12, 12);
    }

    private void MotionChanged(object? sender, EventArgs args)
    {
        if (!IsLoaded || !Controls.MotionPreferences.AnimationsEnabled)
        {
            HideSpotlightLoader();
            ArtworkZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            ArtworkZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            SpotlightArtworkFrame.BeginAnimation(OpacityProperty, null);
        }
    }

    private void SpotlightChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DashboardViewModel.SpotlightPaused)) HideSpotlightLoader();
        if (args.PropertyName != nameof(DashboardViewModel.FeaturedGame)) return;
        _slideElapsed = TimeSpan.Zero;
        _lastSlideTick = System.Diagnostics.Stopwatch.GetTimestamp();
        HideSpotlightLoader();
        var model = (DashboardViewModel)DataContext;
        var index = model.SpotlightPreviews.ToList().FindIndex(item => item.IsSelected);
        if (index >= 0)
        {
            var top = index * 48d;
            if (top < SpotlightPreviewScroll.VerticalOffset) SpotlightPreviewScroll.ScrollToVerticalOffset(top);
            else if (top + 48 > SpotlightPreviewScroll.VerticalOffset + SpotlightPreviewScroll.ViewportHeight)
                SpotlightPreviewScroll.ScrollToVerticalOffset(top + 48 - SpotlightPreviewScroll.ViewportHeight);
        }
        ArtworkZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        ArtworkZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        if (!Controls.MotionPreferences.AnimationsEnabled) return;
        Controls.EntranceMotion.Reveal(SpotlightArtworkFrame);
        var zoom = new System.Windows.Media.Animation.DoubleAnimation(1, 1.015, TimeSpan.FromSeconds(5));
        ArtworkZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, zoom);
        ArtworkZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, zoom);
    }

    private void Dashboard_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs args)
    {
        // Focus and refreshed bindings must not pull a manually scrolled page back to
        // Spotlight. Explicit keyboard navigation can still reveal the focused control.
        if (!Controls.ScrollNavigation.IsNavigationKeyDown()) args.Handled = true;
    }

    private void ApplyResponsiveLayout(double width)
    {
        var compact = width < 700;
        DashboardSearch.Width = width < 640 ? 220 : width < 900 ? 280 : 340;
        DashboardScroll.Padding = width < 640 ? new Thickness(18, 18, 18, 26) : new Thickness(28, 22, 28, 30);
        DashboardHero.Height = compact ? 340 : width < 1100 ? 360 : 380;
        SpotlightArtworkFrame.Height = DashboardHero.Height;
        SpotlightTitle.FontSize = compact ? 24 : width < 1100 ? 28 : 30;
        SpotlightPreviews.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SpotlightPreviewScroll.Visibility = SpotlightPreviews.Visibility;
        SpotlightPreviewScroll.MaxHeight = DashboardHero.Height - 100;
        SpotlightPaging.SetValue(Grid.RowProperty, 1);
        SpotlightPaging.SetValue(Grid.ColumnProperty, 1);
        SpotlightPaging.Margin = new Thickness(12,0,0,0);
        SpotlightDetails.MaxWidth = compact ? Math.Max(220, Math.Min(460, width - 100)) : Math.Max(220, Math.Min(500, width - 245));
        SpotlightDescription.MaxWidth = Math.Min(360, SpotlightDetails.MaxWidth * 0.8);
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
