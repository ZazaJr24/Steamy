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
    public static readonly DependencyProperty RecentTileWidthProperty = DependencyProperty.Register(
        nameof(RecentTileWidth), typeof(double), typeof(DashboardPage), new PropertyMetadata(108d));
    public double RecentTileWidth { get => (double)GetValue(RecentTileWidthProperty); set => SetValue(RecentTileWidthProperty, value); }
    public static readonly DependencyProperty RecentCardHeightProperty = DependencyProperty.Register(
        nameof(RecentCardHeight), typeof(double), typeof(DashboardPage), new PropertyMetadata(120d));
    public double RecentCardHeight { get => (double)GetValue(RecentCardHeightProperty); set => SetValue(RecentCardHeightProperty, value); }
    public static readonly DependencyProperty DashboardNavigationFontSizeProperty = DependencyProperty.Register(
        nameof(DashboardNavigationFontSize), typeof(double), typeof(DashboardPage), new PropertyMetadata(13d));
    public double DashboardNavigationFontSize { get => (double)GetValue(DashboardNavigationFontSizeProperty); set => SetValue(DashboardNavigationFontSizeProperty, value); }
    private readonly System.Windows.Threading.DispatcherTimer _spotlightTimer = new(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly System.Windows.Threading.DispatcherTimer _countdownTimer = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _nextFeedCheck = DateTime.MinValue;
    private TimeSpan _slideElapsed;
    private long _lastSlideTick;
    private Window? _carouselWindow;
    private bool _loaderRunning;
    private System.Windows.IInputElement? _discoveryFocus;
    private IShellBackdropService? _shellBackdrop;
    private DashboardFeature? _observedFeature;
    public DashboardPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DashboardViewModel>();
        Loaded += (_, _) =>
        {
            var viewModel = (DashboardViewModel)DataContext;
            viewModel.RefreshSearchPreference();
            viewModel.ResumeSearch();
            viewModel.RefreshCountdowns();
            viewModel.PropertyChanged += SpotlightChanged;
            Controls.MotionPreferences.Changed += MotionChanged;
            UiThemeService.PaletteChanged += DashboardAmbientChanged;
            _carouselWindow = Window.GetWindow(this);
            DashboardNavigationPill.Visibility = _carouselWindow is MainWindow ? Visibility.Collapsed : Visibility.Visible;
            _shellBackdrop = _carouselWindow is MainWindow ? App.Services.GetRequiredService<IShellBackdropService>() : null;
            SpotlightArtworkFrame.Opacity = 0;
            UpdateShellArtwork();
            UpdateShellViewport();
            if (_carouselWindow is not null)
            {
                _carouselWindow.Activated += CarouselActivated;
                _carouselWindow.Deactivated += CarouselDeactivated;
                _carouselWindow.StateChanged += CarouselStateChanged;
            }
            _lastSlideTick = System.Diagnostics.Stopwatch.GetTimestamp();
            RefreshCarouselActivity();
            _ = viewModel.EnsureDiscoveryArtworkAsync();
            _ = viewModel.RefreshActivityAsync();
            _ = viewModel.RefreshAsync(force: false);
        };
        Unloaded += (_, _) =>
        {
            ClearDashboardPreview();
            var model = (DashboardViewModel)DataContext;
            if (_observedFeature is not null) _observedFeature.PropertyChanged -= FeatureArtworkChanged;
            _observedFeature = null;
            if (_shellBackdrop is not null)
                _shellBackdrop.SetViewport(_shellBackdrop.HeroHeight, _shellBackdrop.HeroTop, 0);
            _shellBackdrop = null;
            model.PropertyChanged -= SpotlightChanged;
            UiThemeService.PaletteChanged -= DashboardAmbientChanged;
            Controls.MotionPreferences.Changed -= MotionChanged;
            MotionChanged(this, EventArgs.Empty);
            model.StopLiveStats(); model.StopSearch();
            model.CloseInstalledLibraryCommand.Execute(null);
            DashboardScroll.IsEnabled = true;
            _spotlightTimer.Stop();
            if (_carouselWindow is not null)
            {
                _carouselWindow.Activated -= CarouselActivated;
                _carouselWindow.Deactivated -= CarouselDeactivated;
                _carouselWindow.StateChanged -= CarouselStateChanged;
                _carouselWindow = null;
            }
            HideSpotlightLoader();
            _countdownTimer.Stop();
            SpotlightArtworkFrame.BeginAnimation(OpacityProperty, null);
        };
        _countdownTimer.Tick += (_, _) => ((DashboardViewModel)DataContext).RefreshCountdowns();
        _spotlightTimer.Tick += CarouselTick;
        IsVisibleChanged += (_, _) => { if (IsLoaded) RefreshCarouselActivity(); };
        DashboardHero.SizeChanged += (_, _) => { UpdateShellViewport(); UpdateDashboardAmbient(); };
        RecentScroll.SizeChanged += (_, e) =>
        {
            RecentTileWidth = Math.Clamp(e.NewSize.Width / 12, 76, 84);
        };
        SizeChanged += (_, e) =>
        {
            ApplyResponsiveLayout(e.NewSize.Width);
            DiscoveryDetailsPanel.MaxHeight = Math.Max(0, e.NewSize.Height - 40);
            InstalledLibraryPanel.MaxHeight = Math.Max(0, e.NewSize.Height - 48);
            InstalledLibraryResults.Height = Math.Clamp(e.NewSize.Height - 330, 100, 400);
        };
    }

    private void UpdateShellArtwork()
    {
        var feature = ((DashboardViewModel)DataContext).FeaturedGame;
        if (!ReferenceEquals(feature, _observedFeature))
        {
            if (_observedFeature is not null) _observedFeature.PropertyChanged -= FeatureArtworkChanged;
            _observedFeature = feature;
            if (feature is not null) feature.PropertyChanged += FeatureArtworkChanged;
        }
        UpdateDashboardAmbient();
        _shellBackdrop?.SetArtwork(feature?.HeroArtwork);
    }

    private void DashboardAmbientChanged(object? sender, EventArgs args) => UpdateDashboardAmbient();

    private void UpdateDashboardAmbient()
    {
        if (!UiThemeService.AmbientEnabled) ClearDashboardPreview();
        UpdateDashboardBodyColors();
        DashboardBackdrop.Artwork = ((DashboardViewModel)DataContext).FeaturedGame?.HeroArtwork;
        DashboardBackdrop.InvalidateVisual();
        (Window.GetWindow(this) as MainWindow)?.RefreshCaptionAppearance();
    }

    private void FeatureArtworkChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DashboardFeature.HeroArtwork)) UpdateShellArtwork();
    }

    private void DashboardScroll_Changed(object sender, ScrollChangedEventArgs args)
    {
        if (!ReferenceEquals(args.OriginalSource, DashboardScroll)) return;
        UpdateShellViewport();
        if (DashboardScroll.VerticalOffset >= DashboardHero.ActualHeight) HideSpotlightLoader();
    }

    private void UpdateShellViewport()
    {
        if (_shellBackdrop is null || _carouselWindow is null) return;
        var top = DashboardHero.TranslatePoint(new Point(), _carouselWindow).Y + DashboardScroll.VerticalOffset;
        _shellBackdrop.SetViewport(DashboardHero.ActualHeight, top, DashboardScroll.VerticalOffset);
    }

    private void CarouselActivated(object? sender, EventArgs args)
        => RefreshCarouselActivity();
    private void CarouselStateChanged(object? sender, EventArgs args)
        => RefreshCarouselActivity();
    private void RefreshCarouselActivity()
    {
        if (!IsLoaded || !IsVisible || _carouselWindow is not { IsActive: true, WindowState: not WindowState.Minimized })
        { CarouselDeactivated(this, EventArgs.Empty); return; }
        var starting = !_spotlightTimer.IsEnabled;
        _lastSlideTick = System.Diagnostics.Stopwatch.GetTimestamp();
        _spotlightTimer.Start(); _countdownTimer.Start();
        if (starting) ((DashboardViewModel)DataContext).StartLiveStats();
    }

    private void CarouselDeactivated(object? sender, EventArgs args)
    {
        _spotlightTimer.Stop();
        _countdownTimer.Stop();
        ((DashboardViewModel)DataContext).StopLiveStats();
        HideSpotlightLoader();
    }

    private void CarouselTick(object? sender, EventArgs args)
    {
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastSlideTick);
        _lastSlideTick = System.Diagnostics.Stopwatch.GetTimestamp();
        var model = (DashboardViewModel)DataContext;
        if (!IsVisible || DashboardScroll.VerticalOffset >= DashboardHero.ActualHeight || _carouselWindow is not { IsActive: true, WindowState: not WindowState.Minimized } || model.SpotlightPaused || model.HasSearchQuery || model.HasSelectedDiscovery || model.IsInstalledLibraryOpen)
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
        if (_slideElapsed >= TimeSpan.FromSeconds(8))
        {
            _slideElapsed = TimeSpan.Zero;
            HideSpotlightLoader();
            model.NextFeaturedCommand.Execute(null);
        }
        else if (_slideElapsed >= TimeSpan.FromSeconds(7.4) && !_loaderRunning && Controls.MotionPreferences.AnimationsEnabled)
        {
            _loaderRunning = true;
            SpotlightLoader.Visibility = Visibility.Visible;
            LoaderRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(8) - _slideElapsed));
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
            SpotlightArtworkFrame.Opacity = 0;
        }
    }

    private void SpotlightChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DashboardViewModel.SelectedDiscovery) or nameof(DashboardViewModel.IsInstalledLibraryOpen))
        {
            var model = (DashboardViewModel)DataContext;
            var open = model.HasSelectedDiscovery || model.IsInstalledLibraryOpen;
            DashboardScroll.IsEnabled = !open;
            if (open)
            {
                _discoveryFocus = System.Windows.Input.Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
                { if (model.IsInstalledLibraryOpen) InstalledLibrarySearch.Focus(); else CloseDiscoveryButton.Focus(); }));
            }
            else { _discoveryFocus?.Focus(); _discoveryFocus = null; }
            HideSpotlightLoader();
        }
        if (args.PropertyName == nameof(DashboardViewModel.SpotlightPaused)) HideSpotlightLoader();
        if (args.PropertyName != nameof(DashboardViewModel.FeaturedGame)) return;
        UpdateShellArtwork();
        _slideElapsed = TimeSpan.Zero;
        _lastSlideTick = System.Diagnostics.Stopwatch.GetTimestamp();
        HideSpotlightLoader();
        ArtworkZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        ArtworkZoom.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
    }

    private void Discovery_Close(object sender, System.Windows.Input.MouseButtonEventArgs args) => ((DashboardViewModel)DataContext).CloseDiscoveryCommand.Execute(null);
    private void InstalledLibrary_Close(object sender, System.Windows.Input.MouseButtonEventArgs args) => ((DashboardViewModel)DataContext).CloseInstalledLibraryCommand.Execute(null);
    private void InstalledLibrary_KeyDown(object sender, System.Windows.Input.KeyEventArgs args)
    {
        if (args.Key != System.Windows.Input.Key.Escape) return;
        ((DashboardViewModel)DataContext).CloseInstalledLibraryCommand.Execute(null);
        args.Handled = true;
    }
    private void Discovery_KeyDown(object sender, System.Windows.Input.KeyEventArgs args)
    {
        if (args.Key == System.Windows.Input.Key.Escape)
        {
            ((DashboardViewModel)DataContext).CloseDiscoveryCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void Dashboard_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs args)
    {
        // Focus and refreshed bindings must not pull a manually scrolled page back to
        // Spotlight. Explicit keyboard navigation can still reveal the focused control.
        if (_carouselWindow?.IsActive != true || !IsKeyboardFocusWithin || !Controls.ScrollNavigation.IsNavigationKeyDown())
            args.Handled = true;
    }

    private void ApplyResponsiveLayout(double width)
    {
        var scale = Math.Clamp(width / 1280, 0.76, 1.5);
        DashboardSearch.Width = width < 640 ? 220 : width < 900 ? 280 : 340;
        DashboardScroll.Padding = new Thickness(0);
        DashboardNavigationFontSize = 12 * Math.Clamp(scale, 0.9, 1.15);
        DashboardNavigationChrome.Width = Math.Clamp(width * 0.42, 430, 660);
        DashboardNavigationChrome.CornerRadius = new CornerRadius(18 * scale);
        Controls.RoundedClip.SetRadius(DashboardNavigationPill, 18 * scale);
        RecentCardHeight = Math.Round(92 * Math.Clamp(scale, 0.9, 1.1));
        RecentSection.Margin = new Thickness(40 * scale, 4 * scale, 40 * scale, 24 * scale);
        RecentHeading.FontSize = UpcomingHeading.FontSize = 22 * scale;
        RecentHeading.Margin = new Thickness(0);
        UpcomingSection.Margin = new Thickness(40 * scale, 0, 40 * scale, 30);
        DashboardHero.Height = Math.Clamp(ActualHeight * 0.64, 360, 800);
        DashboardHero.Margin = new Thickness(0, 0, 0, 12 * scale);
        SpotlightArtworkFrame.Height = DashboardHero.Height;
        SpotlightTitle.FontSize = 36 * scale;
        SpotlightTitle.LineHeight = Math.Ceiling(SpotlightTitle.FontSize * 1.06);
        SpotlightTitle.MaxHeight = SpotlightTitle.LineHeight * 3;
        SpotlightLogo.Width = 320 * scale;
        SpotlightLogo.MaxHeight = 126 * scale;
        SpotlightEyebrow.FontSize = 10 * scale;
        SpotlightDescription.FontSize = 12 * scale;
        SpotlightDescription.Height = SpotlightDescription.LineHeight = 18 * scale;
        SpotlightViewButton.FontSize = 13 * scale;
        SpotlightViewButton.Padding = new Thickness(20 * scale, 9 * scale, 20 * scale, 9 * scale);
        SpotlightFooter.Margin = new Thickness(Math.Max(48, width * 0.058), 24 * scale, 48, 0);
        SpotlightDetails.MaxWidth = Math.Max(220, 300 * scale);
        SpotlightTitle.MaxWidth = SpotlightDetails.MaxWidth;
        SpotlightDots.HorizontalAlignment = HorizontalAlignment.Center;
        SpotlightDots.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
        SpotlightDots.Margin = new Thickness(0, 0, 0, -26 * scale);
        SpotlightDescription.MaxWidth = SpotlightDetails.MaxWidth;
    }

    private void DashboardHome_Click(object sender, RoutedEventArgs args) => DashboardScroll.ScrollToTop();
    private void DashboardTools_Click(object sender, RoutedEventArgs args) => (Window.GetWindow(this) as MainWindow)?.ToggleToolsMenu();
    private void DashboardFixes_Click(object sender, RoutedEventArgs args) => (Window.GetWindow(this) as MainWindow)?.ToggleToolsMenu(fixes: true);

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
        Loaded += (_, _) => { viewModel.StartLiveStats(); AttachDeckInput(); AttachDownloadAmbient(); };
        Unloaded += (_, _) => { viewModel.StopLiveStats(); DetachDeckInput(); DetachDownloadAmbient(); };
        SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width < 760;
            Tag = e.NewSize.Width < 1000 ? "Compact" : "Wide";
            DownloadArtworkColumn.Width = new GridLength(compact ? 112 : 152);
            DownloadHero.Height = compact ? 206 : 214;
            DownloadArtworkPane.Margin = new Thickness(0, 0, compact ? 12 : 18, 0);
            DownloadHeroTitle.FontSize = compact ? 16 : 18;
            DownloadNetworkRate.FontSize = compact ? 14 : 16;
            DeckContent.Margin = new Thickness(compact ? 18 : 24, 18, compact ? 18 : 24, 20);
            DeckAutomation.Visibility = e.NewSize.Width < 700 ? Visibility.Collapsed : Visibility.Visible;
            DownloadOptionsPanel.Width = Math.Min(520, Math.Max(280, e.NewSize.Width - 48));
            DownloadOptionsPanel.MaxHeight = Math.Max(240, e.NewSize.Height - 48);
            RefreshDownloadAmbientGeometry();
        };
        ApplyDeckAccessibility();
    }

    private void DownloadDetails_Click(object sender, RoutedEventArgs e)
    {
        var model = (DownloadsViewModel)DataContext;
        model.SelectedJob ??= model.HeroJob;
        SetDownloadOptions(true);
        DownloadDetails.IsExpanded = true;
    }

    private void QueueMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.IsOpen = true; }
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
