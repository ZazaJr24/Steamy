using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Abstractions;
using Steamy.Models;
using Steamy.Services;
using Steamy.Views;
using Wpf.Ui.Controls;

namespace Steamy;

public partial class MainWindow : FluentWindow
{
    protected override void SetWindowChrome()
    {
        // FluentWindow reapplies chrome on backdrop changes; keep our drag and resize areas.
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 48,
            ResizeBorderThickness = new Thickness(8),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });
    }

    private const int DwmwaBorderColor = 34;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmwcpRound = 2;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    public NavigationView RootNavigationView => RootNavigation;

    private readonly NavigationService _navigationService;
    private readonly IShellBackdropService? _shellBackdrop;
    private readonly ViewModels.ShellDownloadViewModel _shellDownloads;
    private bool _maximizeOnFirstRender;
    private System.Windows.Threading.DispatcherOperation? _pendingCaptionRefresh;
    private System.Windows.Threading.DispatcherTimer? _captionRefreshDelay;
    private long _lastCaptionRefresh;
    private System.Windows.Threading.DispatcherOperation? _pendingNavigationForeground;
    private Controls.ChromeSurfaceSample? _pageChromeSample;
    private bool? _drawerPaletteDashboard;
    private IInputElement? _toolsPreviousFocus;
    private int _toolsPreviousSelection;
    internal bool IsToolsMenuOpen=>ToolsMenuLayer.Visibility==Visibility.Visible;
    private bool _closingAfterNotes, _waitingForNotes;
    internal Controls.DashboardBackdropView? DashboardSurface { get; private set; }
    internal FrameworkElement? PageGlassSurface { get; private set; }
    internal event EventHandler? PageSurfaceChanged;
    private Pages.DashboardPage? _dashboardPage;
    private readonly System.ComponentModel.DependencyPropertyDescriptor _paneDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(NavigationView.IsPaneOpenProperty, typeof(NavigationView));

    public MainWindow() : this(false) { }
    private Controls.GamepadNavigation? _gamepad;
    internal void EnableControllerNavigation() => _gamepad ??= new(this, App.Services.GetRequiredService<IControllerBackend>(), App.Services.GetRequiredService<ISettingsService>());
    internal void RefreshControllerNavigation() => _gamepad?.RefreshEnabled();
    internal MainWindow(bool openSocialOnStartup, bool openControllersOnStartup = false)
    {
        StartupTimings.Mark("Before window XAML");
        InitializeComponent();
        AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
            new System.Windows.Controls.ScrollChangedEventHandler(WindowScrolled), true);
        StartupTimings.Mark("After window XAML");
        Width = Math.Min(Width, Math.Max(MinWidth, SystemParameters.WorkArea.Width * .92));
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height * .92));
        InitializeNotificationWorkspace();
        if (DiscordIntegration.Enabled) ShellFriendsButton.DataContext = App.Services.GetRequiredService<ViewModels.DiscordFriendsViewModel>();
        OverlayLayer.SizeChanged += (_, _) => ConstrainOverlay();
        _paneDescriptor.AddValueChanged(RootNavigation, PaneAppearanceChanged);
        _shellDownloads = new ViewModels.ShellDownloadViewModel(App.Services.GetRequiredService<IAppDataStore>(), App.Services.GetRequiredService<IArtworkService>());
        ShellDownloadsButton.DataContext = _shellDownloads;
        _shellBackdrop = App.Services.GetRequiredService<IShellBackdropService>();
        _shellBackdrop.Changed += ShellBackdropChanged;
        App.Services.GetRequiredService<UiTranslationService>().Observe(this);
        VersionLabel.Text = $"Version {BuildStamp.Version}";
        var pageProvider = App.Services.GetRequiredService<INavigationViewPageProvider>();
        RootNavigation.SetPageProviderService(pageProvider is NavigationViewPageProvider provider ? provider.CreateWindowScope(RootNavigation.ReleasePage) : pageProvider);
        RootNavigation.Navigated += (_, args) =>
        {
            _routeRevision++;
            CloseToolsMenu(restoreFocus:false);
            RootNavigation.Tag = args.Page is Pages.DashboardPage or Pages.GameDetailsPage ? "Home" : "Page";
            DashboardSurface = args.Page is Pages.DashboardPage home ? (Controls.DashboardBackdropView)home.FindName("DashboardBackdrop") : null;
            PageGlassSurface = args.Page as FrameworkElement;
            if(args.Page is Pages.BetterSteamToolsPage or Pages.SteamlessPage or Pages.DenuvoActivationPage or Pages.CreamApiPage or Pages.FamilySharePage or Pages.GoldbergPage or Pages.UcOnlinePage)
            {
                Controls.SurfaceMotion.SetDuration(PageGlassSurface!,160);
                Controls.SurfaceMotion.SetStartScale(PageGlassSurface!,1);
                Controls.SurfaceMotion.SetLift(PageGlassSurface!,0);
                Controls.SurfaceMotion.SetRevealOnShow(PageGlassSurface!,true);
                if(PageGlassSurface!.IsLoaded) Controls.SurfaceMotion.Reveal(PageGlassSurface);
            }
            _friendsPage = args.Page as Pages.FriendsPage;
            if (_friendsPage is null) _socialReturnRoute = args.Page.GetType();
            PageSurfaceChanged?.Invoke(this, EventArgs.Empty);
            _dashboardPage = args.Page as Pages.DashboardPage;
            if (args.Page is not (Pages.DashboardPage or Pages.GameDetailsPage)) _shellBackdrop?.Clear();
            PersistentNavigation.Visibility = Visibility.Visible;
            PersistentNavigation.Tag = args.Page is Pages.DashboardPage dashboard ? dashboard.DataContext : null;
            WorkspaceBackdrop.IsPageBackdrop = args.Page is not Pages.DashboardPage;
            QueueCaptionAppearance();
            Controls.PillMotion.SetSelectedIndex(ShellPillItems, args.Page is Pages.DashboardPage ? 0 : args.Page is Pages.LibraryPage or Pages.GameDetailsPage ? 1
                : args.Page is Pages.FriendsHubPage or Pages.FriendsPage ? 2 : args.Page is Pages.DownloadsPage ? 7 : args.Page is Pages.FriendsPage or Pages.CommunityPage ? 5 : args.Page is Pages.SettingsPage ? 6
                : args.Page is Pages.GameFixesPage ? 4 : 3);
            RootNavigation.IsPaneOpen = false;
        };
        PreviewKeyDown += (_, args) =>
        {
            if(IsToolsMenuOpen && args.Key==Key.Escape) {CloseToolsMenu(); args.Handled=true; return;}
            if (IsSocialDockOpen && args.Key == Key.Escape && _friendsPage?.IsKeyboardFocusWithin == true)
            { if (_friendsPage?.CloseTransientPanels() == true) { args.Handled = true; return; } }
            if (RootNavigation.IsPaneOpen && args.Key == System.Windows.Input.Key.Escape)
            {
                RootNavigation.IsPaneOpen = false;
                args.Handled = true;
            }
        };
        _navigationService = (NavigationService)App.Services.GetService(typeof(INavigationService))!;
        PreviewMouseDown+=(_,_)=>
        {if(IsToolsMenuOpen && !ToolsDropdown.IsMouseOver && !ShellToolsButton.IsMouseOver && !ShellFixesButton.IsMouseOver) CloseToolsMenu(restoreFocus:false);};
        Deactivated+=(_,_)=>CloseToolsMenu(restoreFocus:false);
        _navigationService.Attach(route =>
        {
            if (DiscordIntegration.Enabled || !DiscordIntegration.IsSocialPage(route))
                RootNavigation.Navigate(route == typeof(Pages.FriendsHubPage) ? typeof(Pages.FriendsPage) : route);
        });
        ContentRendered += (_, _) =>
        {
            StartupTimings.Mark("ContentRendered");
            // The custom WindowChrome can initially report restored bounds even when XAML
            // requested Maximized. Re-apply the ordinary Windows maximize state after the
            // HWND and chrome are live; this keeps the taskbar available (it is not fullscreen).
            if (_maximizeOnFirstRender && WindowState != WindowState.Maximized)
                WindowState = WindowState.Maximized;
            ApplyFrameAppearance();
            // NavigationView's content presenter is created by its control template. The
            // window's Loaded event can fire before that template exists; ContentRendered
            // guarantees the navigation surface has completed its first layout pass.
            RootNavigation.ApplyTemplate();
            RootNavigation.UpdateLayout();
            RootNavigation.Navigate(openControllersOnStartup ? typeof(Pages.ControllersPage) : typeof(Pages.DashboardPage));
            if (openSocialOnStartup) OpenSocialDock();
            void ContinueStartup()
            {
            if (App.ShouldShowFirstRunSetup)
            {
                var setup = new FirstRunSetup(
                    App.Services.GetRequiredService<ISettingsService>(),
                    App.Services.GetRequiredService<IRyuuGameDownloadService>(),
                    App.Services.GetRequiredService<ISecureCredentialService>());
                setup.Completed += (_, _) =>
                {
                    App.MarkFirstRunSetupCompleted();
                    HideOverlay();
                    _ = ApplySelectedLanguageAsync();
                };
                ShowOverlay(setup);
            }
            }
            if (App.IsLaunched && App.ShouldShowTransparencyNotice)
            {
                var notice = new TransparencyNotice(App.Services.GetRequiredService<ISettingsService>());
                notice.Completed += (_, _) => { App.MarkTransparencyCompleted(); HideOverlay(); ContinueStartup(); };
                ShowOverlay(notice);
            }
            else ContinueStartup();
        };
        Activated += (_, _) => ApplyFrameAppearance();
        StateChanged += (_, _) => ApplyFrameAppearance();
        SizeChanged += (_, _) => { PersistentNavigation.Width = DiscordIntegration.Enabled ? Math.Clamp(ActualWidth - 500, 440, 540) : Math.Clamp(ActualWidth - 500, 400, 456); QueueCaptionAppearance(); };
        Closing += SaveNotesBeforeClose;

    }

    private async void SaveNotesBeforeClose(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        if (_closingAfterNotes) return;
        if (_waitingForNotes) { args.Cancel = true; return; }
        var save = App.Services.GetRequiredService<ViewModels.GameDetailsViewModel>().FlushAsync();
        if (save.IsCompletedSuccessfully) return;
        args.Cancel = true; _waitingForNotes = true;
        try { await save; }
        finally { _waitingForNotes = false; _closingAfterNotes = true; Close(); }
    }

    private void ShellRoute_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: Type route })
        {
            CloseToolsMenu(restoreFocus:false);
            NavigateWithFeedback(route);
        }
    }
    private int _routeRevision;
    internal void NavigateWithFeedback(Type route)
    {
        if (route == typeof(Pages.FriendsHubPage)) route = typeof(Pages.FriendsPage);
        if (!DiscordIntegration.Enabled && DiscordIntegration.IsSocialPage(route)) return;
        var revision = ++_routeRevision;
        Controls.PillMotion.SetSelectedIndex(ShellPillItems, route == typeof(Pages.DashboardPage) ? 0 : route == typeof(Pages.LibraryPage) ? 1
            : route == typeof(Pages.FriendsPage) ? 2 : route == typeof(Pages.DownloadsPage) ? 7 : route == typeof(Pages.FriendsPage) || route == typeof(Pages.CommunityPage) ? 5 : route == typeof(Pages.SettingsPage) ? 6 : route == typeof(Pages.GameFixesPage) ? 4 : 3);
        if (PageGlassSurface?.GetType() == route) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        { if (revision == _routeRevision && IsLoaded) RootNavigation.Navigate(route); }));
    }

    private void ShellTools_Click(object sender,RoutedEventArgs args)=>ToggleToolsMenu();
    private void ShellFixes_Click(object sender,RoutedEventArgs args)=>ToggleToolsMenu(fixes:true);
    internal void ToggleToolsMenu(bool fixes=false)
    {
        if(IsToolsMenuOpen && (FixesMenuSection.Visibility==Visibility.Visible)==fixes) {CloseToolsMenu(); return;}
        if(IsToolsMenuOpen) CloseToolsMenu(restoreFocus:false);
        _toolsPreviousFocus=Keyboard.FocusedElement;
        _toolsPreviousSelection=Controls.PillMotion.GetSelectedIndex(ShellPillItems);
        FixesMenuSection.Visibility=fixes ? Visibility.Visible : Visibility.Collapsed;
        ToolsMenuSection.Visibility=fixes ? Visibility.Collapsed : Visibility.Visible;
        RootNavigation.IsPaneOpen=false;
        Controls.PillMotion.SetSelectedIndex(ShellPillItems,fixes ? 4 : 3);
        ToolsMenuLayer.Visibility=Visibility.Visible;
        (fixes ? GameFixesMenuButton : SteamToolsMenuButton).Focus();
    }
    internal void CloseToolsMenu(bool restoreFocus=true)
    {
        if(!IsToolsMenuOpen) return;
        ToolsMenuLayer.Visibility=Visibility.Collapsed;
        Controls.PillMotion.SetSelectedIndex(ShellPillItems,_toolsPreviousSelection);
        if(restoreFocus && _toolsPreviousFocus is UIElement {IsVisible:true,IsEnabled:true} element) element.Focus();
        _toolsPreviousFocus=null;
    }

    private async Task ApplySelectedLanguageAsync()
    {
        // Let the setup overlay close and the first dashboard frame render before walking the
        // visual tree. Online translation can take seconds on a first run; it must never hold up
        // the Save & finish action or the window's input queue.
        try
        {
            await Task.Delay(220);
            var language = App.Services.GetRequiredService<ISettingsService>().Load().Language;
            var translations = App.Services.GetRequiredService<UiTranslationService>();
            await translations.ApplyToAsync(RootNavigation, language);
        }
        catch (Exception exception)
        {
            App.Services.GetRequiredService<ILoggingService>()
                .Add(LogLevel.Debug, "Translation", $"Initial language update skipped: {exception.GetType().Name}.");
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _maximizeOnFirstRender = WindowState == WindowState.Maximized;
        ApplyFrameAppearance();
    }

    private void ApplyFrameAppearance()
    {
        UpdateCaptionForeground();
        // Remove the thin Windows DWM frame line around the maximized app surface.
        try
        {
            // Suppress the native DWM outline; matching the app's surface color still leaves
            // a visible one-pixel seam against the Windows taskbar on some accent/theme setups.
            var color = DwmColorNone;
            var cornerPreference = WindowState == WindowState.Maximized ? DwmwcpDoNotRound : DwmwcpRound;
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref color, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    internal void RefreshFrameAppearance() => ApplyFrameAppearance();
    internal void RefreshCaptionAppearance(bool paletteChanged = false)
    {
        if (paletteChanged) { _drawerPaletteDashboard = null; UpdateCaptionForeground(); }
        else QueueCaptionAppearance();
    }

    internal void QueueCaptionAppearance()
    {
        if (_pendingCaptionRefresh?.Status == System.Windows.Threading.DispatcherOperationStatus.Pending || Dispatcher.HasShutdownStarted) return;
        if (_captionRefreshDelay?.IsEnabled == true) return;
        var elapsed = _lastCaptionRefresh == 0 ? 80 : System.Diagnostics.Stopwatch.GetElapsedTime(_lastCaptionRefresh).TotalMilliseconds;
        if (elapsed < 80)
        {
            EnsureCaptionRefreshDelay();
            // Contrast sampling performs synchronous bitmap rendering. Keep the
            // latest scroll position, but do not repeat that work every frame.
            _captionRefreshDelay!.Interval = TimeSpan.FromMilliseconds(Math.Max(1, 80 - elapsed));
            _captionRefreshDelay.Start();
            return;
        }
        _pendingCaptionRefresh = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        { _pendingCaptionRefresh = null; if (IsLoaded) UpdateCaptionForeground(); }));
    }
    private void EnsureCaptionRefreshDelay()
    {
        if (_captionRefreshDelay is not null) return;
        _captionRefreshDelay = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher);
        _captionRefreshDelay.Tick += (_, _) => { _captionRefreshDelay.Stop(); QueueCaptionAppearance(); };
    }
    private void WindowScrolled(object sender, System.Windows.Controls.ScrollChangedEventArgs args)
    {
        if (!IsLoaded || !IsVisible || Dispatcher.HasShutdownStarted ||
            (args.VerticalChange == 0 && args.HorizontalChange == 0) ||
            args.ViewportHeightChange != 0 || args.ViewportWidthChange != 0 ||
            args.ExtentHeightChange != 0 || args.ExtentWidthChange != 0) return;
        // Bitmap readback blocks the UI thread. Keep the existing contrast while
        // moving and sample the final position once scrolling has settled.
        _pendingCaptionRefresh?.Abort(); _pendingCaptionRefresh = null;
        _pendingNavigationForeground?.Abort(); _pendingNavigationForeground = null;
        EnsureCaptionRefreshDelay();
        _captionRefreshDelay!.Stop();
        _captionRefreshDelay.Interval = TimeSpan.FromMilliseconds(140);
        _captionRefreshDelay.Start();
    }
    private void ShellBackdropChanged(object? sender, EventArgs args)
    {
        if (_captionRefreshDelay?.IsEnabled == true) return;
        QueueCaptionAppearance();
    }
    private void QueueNavigationForeground()
    {
        if (_pendingNavigationForeground?.Status == System.Windows.Threading.DispatcherOperationStatus.Pending || Dispatcher.HasShutdownStarted) return;
        // Sample after the changed glass brushes have rendered, rather than sampling
        // the previous frame from inside the brush-update callback.
        _pendingNavigationForeground = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        { _pendingNavigationForeground = null; if (IsLoaded && !UiThemeService.IsHighContrastActive) UpdateNavigationForeground(); }));
    }
    private void PaneAppearanceChanged(object? sender, EventArgs args) => UpdateCaptionForeground();

    private void UpdateCaptionForeground()
    {
        if (WindowTitleBar is null) return;
        _lastCaptionRefresh = System.Diagnostics.Stopwatch.GetTimestamp();
        Controls.ChromeSurfaceSample? detailSample = null;
        if (PageGlassSurface is Pages.GameDetailsPage { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } details
            && Controls.MotionPreferences.BackdropBlurEnabled && !UiThemeService.IsHighContrastActive)
        {
            if (_pageChromeSample is null) _pageChromeSample = new Controls.ChromeSurfaceSample(details);
            else _pageChromeSample.Capture(details);
            detailSample = _pageChromeSample;
        }
        double LuminanceBehind(FrameworkElement element)
        {
            if (detailSample is not null && PageGlassSurface is { } page)
                return detailSample.Luminance(new Rect(element.TranslatePoint(new Point(), page), new Size(Math.Max(1, element.ActualWidth), Math.Max(1, element.ActualHeight))));
            if (DashboardSurface is not { IsLoaded: true, ActualWidth: > 0 } surface) return (!UiThemeService.AmbientEnabled ? UiThemeService.StaticBackgroundIsLight : UiThemeService.IsLight) ? 1 : 0;
            var origin = element.TranslatePoint(new Point(), surface);
            var region = new Rect(origin, new Size(Math.Max(1, element.ActualWidth), Math.Max(1, element.ActualHeight)));
            return (ReferenceEquals(element, PersistentNavigation) || ShellPillItems.IsAncestorOf(element)) && _dashboardPage is not null
                ? _dashboardPage.ToolbarRegionLuminance(region) : surface.RegionLuminance(region);
        }
        bool LightBehind(FrameworkElement element) => LuminanceBehind(element) > .179;
        var launcher = RootNavigation.Template?.FindName("MenuLauncher", RootNavigation) as FrameworkElement ?? RootNavigation;
        var launcherLight = UiThemeService.IsLight;
        NavigationSheen.Visibility = UiThemeService.IsHighContrastActive ? Visibility.Collapsed : Visibility.Visible;
        var launcherBackground = UiThemeService.IsHighContrastActive ? SystemColors.WindowBrush : launcherLight ? FrostedLightChrome : FrostedDarkChrome;
        var launcherForeground = UiThemeService.IsHighContrastActive ? SystemColors.WindowTextBrush
            : launcherLight ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
        if (!ReferenceEquals(Application.Current.Resources["LauncherBackgroundBrush"], launcherBackground))
            Application.Current.Resources["LauncherBackgroundBrush"] = launcherBackground;
        if (!ReferenceEquals(Application.Current.Resources["LauncherForegroundBrush"], launcherForeground))
            Application.Current.Resources["LauncherForegroundBrush"] = launcherForeground;
        if (!UiThemeService.IsHighContrastActive)
        {
            NavigationHighlight.Background = launcherLight ? LightSelection : DarkSelection;
            QueueNavigationForeground();
            WindowTitleBar.Foreground = LightBehind(WindowTitleBar) ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
            ShellFriendsButton.Foreground = LightBehind(ShellFriendsButton) ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
        }
        else
        {
            foreach (var button in ShellPillItems.Children.OfType<System.Windows.Controls.Button>()) button.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            NavigationHighlight.Background = SystemColors.HighlightBrush;
            WindowTitleBar.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
            ShellFriendsButton.Foreground = SystemColors.WindowTextBrush;
        }
        // The dashboard body is black in both themes. WPF-UI's themed item styles
        // otherwise keep dark text when the glass drawer sits over this surface.
        var dashboardDrawer = DashboardSurface is not null && !UiThemeService.IsLight && !UiThemeService.IsHighContrastActive;
        if (_drawerPaletteDashboard == dashboardDrawer) return;
        _drawerPaletteDashboard = dashboardDrawer;
        RootNavigation.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        void UpdateDrawerItems(System.Collections.IEnumerable items)
        {
            foreach (var item in items.OfType<NavigationViewItem>())
            {
                if (dashboardDrawer) item.Foreground = System.Windows.Media.Brushes.White;
                else item.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
                if (item.Icon is { } icon)
                {
                    if (dashboardDrawer) icon.Foreground = System.Windows.Media.Brushes.White;
                    else icon.SetResourceReference(Wpf.Ui.Controls.IconElement.ForegroundProperty, "TextPrimaryBrush");
                }
                UpdateDrawerItems(item.MenuItems);
            }
        }
        UpdateDrawerItems(RootNavigation.MenuItems); UpdateDrawerItems(RootNavigation.FooterMenuItems);
        if (RootNavigation.PaneHeader is FrameworkElement paneHeader)
        {
            if (dashboardDrawer)
            {
                if (!ReferenceEquals(paneHeader.Resources["TextPrimaryBrush"], System.Windows.Media.Brushes.White)) paneHeader.Resources["TextPrimaryBrush"] = System.Windows.Media.Brushes.White;
                if (!ReferenceEquals(paneHeader.Resources["TextTertiaryBrush"], System.Windows.Media.Brushes.LightGray)) paneHeader.Resources["TextTertiaryBrush"] = System.Windows.Media.Brushes.LightGray;
                VersionLabel.Foreground = System.Windows.Media.Brushes.LightGray;
            }
            else
            {
                if (paneHeader.Resources.Contains("TextPrimaryBrush")) paneHeader.Resources.Remove("TextPrimaryBrush");
                if (paneHeader.Resources.Contains("TextTertiaryBrush")) paneHeader.Resources.Remove("TextTertiaryBrush");
                VersionLabel.SetResourceReference(ForegroundProperty, "TextTertiaryBrush");
            }
        }
    }

    private static readonly System.Windows.Media.Brush FrostedLightChrome = ChromeBrush(0xD0, System.Windows.Media.Colors.White);
    private static readonly System.Windows.Media.Brush FrostedDarkChrome = ChromeBrush(0xB8, System.Windows.Media.Color.FromRgb(24, 24, 27));
    private void UpdateNavigationForeground()
    {
        // Both themes now own a stable frosted material. Labels do not need a
        // synchronous GPU readback after each scroll or palette update.
        foreach (var button in ShellPillItems.Children.OfType<System.Windows.Controls.Button>())
            button.Foreground = UiThemeService.IsHighContrastActive ? SystemColors.WindowTextBrush
                : UiThemeService.IsLight ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
    }
    private static readonly System.Windows.Media.Brush LightSelection = ChromeBrush(0x18, System.Windows.Media.Colors.Black);
    private static readonly System.Windows.Media.Brush DarkSelection = ChromeBrush(0x30, System.Windows.Media.Colors.White);
    private static System.Windows.Media.Brush ChromeBrush(byte opacity, System.Windows.Media.Color color)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(opacity, color.R, color.G, color.B));
        brush.Freeze(); return brush;
    }

    private int _overlayRevision;
    public void ShowOverlay(UIElement content)
    {
        CloseToolsMenu(restoreFocus:false);
        ++_overlayRevision;
        OverlayHost.Content = content;
        SetupBackdrop.Visibility = content is FirstRunSetup or TransparencyNotice ? Visibility.Visible : Visibility.Collapsed;
        ConstrainOverlay();
        // Keep onboarding readable with a stable theme surface.
        if (content is FirstRunSetup or TransparencyNotice)
            OverlayLayer.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "WorkspaceBackgroundBrush");
        else
            OverlayLayer.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "ScrimBrush");
        OverlayLayer.Visibility = Visibility.Visible;
        OverlayLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void ConstrainOverlay()
    {
        if (OverlayHost.Content is not FrameworkElement panel || panel is not (FirstRunSetup or TransparencyNotice)) return;
        var root = Content as FrameworkElement;
        var width = OverlayLayer.ActualWidth > 0 ? OverlayLayer.ActualWidth : root?.ActualWidth ?? ActualWidth;
        var height = OverlayLayer.ActualHeight > 0 ? OverlayLayer.ActualHeight : Math.Max(0, (root?.ActualHeight ?? ActualHeight) - 48);
        panel.MaxWidth = Math.Max(0, width - 24);
        panel.MaxHeight = Math.Max(0, height - 24);
    }

    public void HideOverlay()
    {
        var revision = ++_overlayRevision;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            if (revision != _overlayRevision) return;
            OverlayLayer.Visibility = Visibility.Collapsed;
            OverlayHost.Content = null;
        };
        OverlayLayer.BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        _captionRefreshDelay?.Stop();
        _pendingCaptionRefresh?.Abort(); _pendingCaptionRefresh = null;
        _pendingNavigationForeground?.Abort(); _pendingNavigationForeground = null;
        _pageChromeSample = null;
        _paneDescriptor.RemoveValueChanged(RootNavigation, PaneAppearanceChanged);
        _shellDownloads.Dispose();
        _gamepad?.Dispose();
        if (_shellBackdrop is not null) _shellBackdrop.Changed -= ShellBackdropChanged;
        _navigationService.Detach();
        base.OnClosed(e);
    }
}
