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
    private IInputElement? _toolsPreviousFocus;
    private Effect? _toolsPreviousEffect;
    private bool _toolsWorkspaceEnabled;
    private int _toolsPreviousSelection;
    private bool _closingAfterNotes, _waitingForNotes;
    internal bool IsToolsMenuOpen => ToolsMenuLayer.Visibility == Visibility.Visible;
    internal Controls.DashboardBackdropView? DashboardSurface { get; private set; }
    private Pages.DashboardPage? _dashboardPage;
    private readonly System.ComponentModel.DependencyPropertyDescriptor _paneDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(NavigationView.IsPaneOpenProperty, typeof(NavigationView));

    public MainWindow()
    {
        InitializeComponent();
        OverlayLayer.SizeChanged += (_, _) => ConstrainOverlay();
        _paneDescriptor.AddValueChanged(RootNavigation, PaneAppearanceChanged);
        _shellDownloads = new ViewModels.ShellDownloadViewModel(App.Services.GetRequiredService<IAppDataStore>(), App.Services.GetRequiredService<IArtworkService>());
        ShellQuickDownloadsButton.DataContext = _shellDownloads;
        _shellBackdrop = App.Services.GetRequiredService<IShellBackdropService>();
        _shellBackdrop.Changed += ShellBackdropChanged;
        App.Services.GetRequiredService<UiTranslationService>().Observe(this);
        VersionLabel.Text = $"Version {BuildStamp.Version}";
        RootNavigation.SetPageProviderService(App.Services.GetRequiredService<INavigationViewPageProvider>());
        RootNavigation.Navigated += (_, args) =>
        {
            CloseToolsMenu(restoreFocus: false);
            RootNavigation.Tag = args.Page is Pages.DashboardPage or Pages.GameDetailsPage ? "Home" : "Page";
            DashboardSurface = args.Page is Pages.DashboardPage home ? (Controls.DashboardBackdropView)home.FindName("DashboardBackdrop") : null;
            _dashboardPage = args.Page as Pages.DashboardPage;
            PersistentNavigation.Visibility = Visibility.Visible;
            PersistentNavigation.Tag = args.Page is Pages.DashboardPage dashboard ? dashboard.DataContext : null;
            WorkspaceBackdrop.IsPageBackdrop = args.Page is not Pages.DashboardPage;
            UpdateCaptionForeground();
            Controls.PillMotion.SetSelectedIndex(ShellPillItems, args.Page is Pages.DashboardPage ? 0 : args.Page is Pages.LibraryPage or Pages.GameDetailsPage ? 1
                : args.Page is Pages.DownloadsPage ? 2 : args.Page is Pages.SettingsPage ? 6
                : args.Page is Pages.GameFixesPage or Pages.HypervisorFixesPage ? 4 : 3);
            RootNavigation.IsPaneOpen = false;
            if (args.Page is FrameworkElement page)
                Dispatcher.BeginInvoke(() => Controls.SurfaceMotion.Reveal(page));
        };
        PreviewKeyDown += (_, args) =>
        {
            if (IsToolsMenuOpen && args.Key == Key.Escape)
            {
                CloseToolsMenu();
                args.Handled = true;
                return;
            }
            if (RootNavigation.IsPaneOpen && args.Key == System.Windows.Input.Key.Escape)
            {
                RootNavigation.IsPaneOpen = false;
                args.Handled = true;
            }
        };
        _navigationService = (NavigationService)App.Services.GetService(typeof(INavigationService))!;
        _navigationService.Attach(route => RootNavigation.Navigate(route));
        ContentRendered += (_, _) =>
        {
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
            RootNavigation.Navigate(typeof(Pages.DashboardPage));
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
        SizeChanged += (_, _) => { PersistentNavigation.Width = Math.Clamp(ActualWidth * 0.40 + 36, 536, 680); Dispatcher.BeginInvoke(new Action(UpdateCaptionForeground)); };
        Deactivated += (_, _) => CloseToolsMenu();
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
            CloseToolsMenu(restoreFocus: false);
            RootNavigation.Navigate(route);
        }
    }

    private void ShellTools_Click(object sender, RoutedEventArgs args) => ToggleToolsMenu();
    private void ShellFixes_Click(object sender, RoutedEventArgs args) => ToggleToolsMenu(fixes: true);
    private void ToolsDismiss_Click(object sender, MouseButtonEventArgs args) { CloseToolsMenu(); args.Handled = true; }

    internal void ToggleToolsMenu(bool fixes = false)
    {
        if (IsToolsMenuOpen && (FixesMenuSection.Visibility == Visibility.Visible) == fixes) { CloseToolsMenu(); return; }
        if (IsToolsMenuOpen) CloseToolsMenu(restoreFocus: false);
        FixesMenuSection.Visibility = fixes ? Visibility.Visible : Visibility.Collapsed;
        ToolsMenuSection.Visibility = fixes ? Visibility.Collapsed : Visibility.Visible;
        RootNavigation.IsPaneOpen = false;
        _toolsPreviousFocus = Keyboard.FocusedElement;
        _toolsPreviousEffect = WorkspaceLayer.Effect;
        _toolsWorkspaceEnabled = WorkspaceLayer.IsEnabled;
        _toolsPreviousSelection = Controls.PillMotion.GetSelectedIndex(ShellPillItems);
        Controls.PillMotion.SetSelectedIndex(ShellPillItems, fixes ? 4 : 3);
        if (Controls.MotionPreferences.BackdropBlurEnabled)
        {
            var blur = new BlurEffect { Radius = 12, RenderingBias = RenderingBias.Performance };
            WorkspaceLayer.Effect = blur;
            if (Controls.MotionPreferences.AnimationsEnabled)
                blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, 12, TimeSpan.FromMilliseconds(180))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
        WorkspaceLayer.IsEnabled = false;
        ToolsMenuLayer.Visibility = Visibility.Visible;
        (fixes ? GameFixesMenuButton : SteamToolsMenuButton).Focus();
    }

    internal void CloseToolsMenu(bool restoreFocus = true)
    {
        if (!IsToolsMenuOpen) return;
        ToolsMenuLayer.Visibility = Visibility.Collapsed;
        WorkspaceLayer.Effect = _toolsPreviousEffect;
        WorkspaceLayer.IsEnabled = _toolsWorkspaceEnabled;
        Controls.PillMotion.SetSelectedIndex(ShellPillItems, _toolsPreviousSelection);
        if (restoreFocus && _toolsPreviousFocus is not null) Keyboard.Focus(_toolsPreviousFocus);
        _toolsPreviousFocus = null;
        _toolsPreviousEffect = null;
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
    internal void RefreshCaptionAppearance() => UpdateCaptionForeground();

    private void ShellBackdropChanged(object? sender, EventArgs args) => UpdateCaptionForeground();
    private void PaneAppearanceChanged(object? sender, EventArgs args) => UpdateCaptionForeground();

    private void UpdateCaptionForeground()
    {
        if (WindowTitleBar is null) return;
        double LuminanceBehind(FrameworkElement element)
        {
            if (DashboardSurface is not { IsLoaded: true, ActualWidth: > 0 } surface) return (!UiThemeService.AmbientEnabled ? UiThemeService.StaticBackgroundIsLight : UiThemeService.IsLight) ? 1 : 0;
            var origin = element.TranslatePoint(new Point(), surface);
            var region = new Rect(origin, new Size(Math.Max(1, element.ActualWidth), Math.Max(1, element.ActualHeight)));
            return (ReferenceEquals(element, PersistentNavigation) || ShellPillItems.IsAncestorOf(element)) && _dashboardPage is not null
                ? _dashboardPage.ToolbarRegionLuminance(region) : surface.RegionLuminance(region);
        }
        bool LightBehind(FrameworkElement element) => LuminanceBehind(element) > .179;
        var launcher = RootNavigation.Template?.FindName("MenuLauncher", RootNavigation) as FrameworkElement ?? RootNavigation;
        var launcherLight = RootNavigation.IsPaneOpen ? UiThemeService.IsLight : LightBehind(launcher);
        var navigationLight = LightBehind(PersistentNavigation);
        NavigationSheen.Visibility = UiThemeService.IsHighContrastActive ? Visibility.Collapsed : Visibility.Visible;
        var launcherBackground = UiThemeService.IsHighContrastActive ? SystemColors.WindowBrush : launcherLight ? LightChrome : DarkChrome;
        var launcherForeground = UiThemeService.IsHighContrastActive ? SystemColors.WindowTextBrush
            : launcherLight ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
        if (!ReferenceEquals(Application.Current.Resources["LauncherBackgroundBrush"], launcherBackground))
            Application.Current.Resources["LauncherBackgroundBrush"] = launcherBackground;
        if (!ReferenceEquals(Application.Current.Resources["LauncherForegroundBrush"], launcherForeground))
            Application.Current.Resources["LauncherForegroundBrush"] = launcherForeground;
        if (!UiThemeService.IsHighContrastActive)
        {
            foreach (var button in ShellPillItems.Children.OfType<System.Windows.Controls.Button>())
                button.Foreground = FrostedLuminance(LuminanceBehind(button), navigationLight) > .179 ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
            NavigationSurface.Background = navigationLight ? FrostedLightChrome : FrostedDarkChrome;
            NavigationHighlight.Background = navigationLight ? LightSelection : DarkSelection;
            WindowTitleBar.Foreground = LightBehind(WindowTitleBar) ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
            ShellQuickDownloadsButton.Foreground = LightBehind(ShellQuickDownloadsButton) ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
        }
        else
        {
            foreach (var button in ShellPillItems.Children.OfType<System.Windows.Controls.Button>()) button.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            NavigationSurface.Background = SystemColors.WindowBrush;
            NavigationHighlight.Background = SystemColors.HighlightBrush;
            WindowTitleBar.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
            ShellQuickDownloadsButton.Foreground = SystemColors.WindowTextBrush;
        }
        RootNavigation.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        // The dashboard body is black in both themes. WPF-UI's themed item styles
        // otherwise keep dark text when the glass drawer sits over this surface.
        var dashboardDrawer = DashboardSurface is not null && !UiThemeService.IsHighContrastActive;
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

    private static readonly System.Windows.Media.Brush LightChrome = ChromeBrush(0x30, 0x18, System.Windows.Media.Colors.White);
    private static readonly System.Windows.Media.Brush FrostedLightChrome = ChromeBrush(0x60, 0x40, System.Windows.Media.Colors.White);
    private static readonly System.Windows.Media.Brush FrostedDarkChrome = ChromeBrush(0x80, 0x60, System.Windows.Media.Color.FromRgb(26, 30, 36));
    private static double FrostedLuminance(double source, bool light)
    {
        // Include the frosted tint when choosing text contrast; source artwork alone
        // can be dark even when the white glass coating makes the button area light.
        var encoded = source <= .0031308 ? source * 12.92 : 1.055 * Math.Pow(source, 1 / 2.4) - .055;
        var alpha = light ? (0x60 + 0x40) / 510d : (0x80 + 0x60) / 510d;
        var tint = light ? 1 : (.2126 * 26 + .7152 * 30 + .0722 * 36) / 255;
        var result = encoded * (1 - alpha) + tint * alpha;
        return result <= .04045 ? result / 12.92 : Math.Pow((result + .055) / 1.055, 2.4);
    }
    private static readonly System.Windows.Media.Brush DarkChrome = ChromeBrush(0x48, 0x24, System.Windows.Media.Colors.Black);
    private static readonly System.Windows.Media.Brush LightSelection = ChromeBrush(0x24, 0x14, System.Windows.Media.Colors.Black);
    private static readonly System.Windows.Media.Brush DarkSelection = ChromeBrush(0x48, 0x20, System.Windows.Media.Colors.White);
    private static System.Windows.Media.Brush ChromeBrush(byte top, byte bottom, System.Windows.Media.Color color)
    {
        var brush = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromArgb(top, color.R, color.G, color.B), System.Windows.Media.Color.FromArgb(bottom, color.R, color.G, color.B), 90);
        brush.Freeze(); return brush;
    }

    private int _overlayRevision;
    public void ShowOverlay(UIElement content)
    {
        ++_overlayRevision;
        CloseToolsMenu(restoreFocus: false);
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
        _paneDescriptor.RemoveValueChanged(RootNavigation, PaneAppearanceChanged);
        _shellDownloads.Dispose();
        if (_shellBackdrop is not null) _shellBackdrop.Changed -= ShellBackdropChanged;
        _navigationService.Detach();
        base.OnClosed(e);
    }
}
