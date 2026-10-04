using System.Windows;
using System.Windows.Media.Animation;
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
    private const int DwmwaBorderColor = 34;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    public NavigationView RootNavigationView => RootNavigation;

    private readonly NavigationService _navigationService;

    public MainWindow()
    {
        InitializeComponent();
        App.Services.GetRequiredService<UiTranslationService>().Observe(this);
        VersionLabel.Text = $"Version {BuildStamp.Version} · .NET {Environment.Version.Major}";
        RootNavigation.SetPageProviderService(App.Services.GetRequiredService<INavigationViewPageProvider>());
        _navigationService = (NavigationService)App.Services.GetService(typeof(INavigationService))!;
        _navigationService.Attach(route => RootNavigation.Navigate(route));
        ContentRendered += (_, _) =>
        {
            // The custom WindowChrome can initially report restored bounds even when XAML
            // requested Maximized. Re-apply the ordinary Windows maximize state after the
            // HWND and chrome are live; this keeps the taskbar available (it is not fullscreen).
            if (WindowState != WindowState.Maximized)
                WindowState = WindowState.Maximized;
            ApplyFrameAppearance();
            // NavigationView's content presenter is created by its control template. The
            // window's Loaded event can fire before that template exists; ContentRendered
            // guarantees the navigation surface has completed its first layout pass.
            RootNavigation.ApplyTemplate();
            RootNavigation.UpdateLayout();
            RootNavigation.Navigate(typeof(Pages.DashboardPage));
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
        };
        Activated += (_, _) => ApplyFrameAppearance();
        StateChanged += (_, _) => ApplyFrameAppearance();

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
        ApplyFrameAppearance();
    }

    private void ApplyFrameAppearance()
    {
        // Remove the thin Windows DWM frame line around the maximized app surface.
        try
        {
            // Suppress the native DWM outline; matching the app's surface color still leaves
            // a visible one-pixel seam against the Windows taskbar on some accent/theme setups.
            var color = DwmColorNone;
            var cornerPreference = DwmwcpDoNotRound;
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref color, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    internal void RefreshFrameAppearance() => ApplyFrameAppearance();

    public void ShowOverlay(UIElement content)
    {
        OverlayHost.Content = content;
        // Keep the setup canvas tied to the selected palette so Light mode switches live too.
        if (content is FirstRunSetup)
            OverlayLayer.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "AppBackgroundBrush");
        else
            OverlayLayer.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "ScrimBrush");
        OverlayLayer.Visibility = Visibility.Visible;
        OverlayLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    public void HideOverlay()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            OverlayLayer.Visibility = Visibility.Collapsed;
            OverlayHost.Content = null;
        };
        OverlayLayer.BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        _navigationService.Detach();
        base.OnClosed(e);
    }
}
