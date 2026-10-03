using System.Windows;
using System.Windows.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Services;
using Steamy.Views;
using Wpf.Ui.Controls;

namespace Steamy;

public partial class MainWindow : FluentWindow
{
    public NavigationView RootNavigationView => RootNavigation;

    private readonly NavigationService _navigationService;

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"Version {BuildStamp.Version} · .NET {Environment.Version.Major}";
        _navigationService = (NavigationService)App.Services.GetService(typeof(INavigationService))!;
        _navigationService.Attach(route => RootNavigation.Navigate(route));
        Loaded += (_, _) =>
        {
            RootNavigation.Navigate(typeof(Pages.DashboardPage));
            if (App.ShouldShowFirstRunSetup)
            {
                var setup = new FirstRunSetup(App.Services.GetRequiredService<ISettingsService>());
                setup.Completed += (_, _) =>
                {
                    App.MarkFirstRunSetupCompleted();
                    HideOverlay();
                };
                ShowOverlay(setup);
            }
        };

    }

    public void ShowOverlay(UIElement content)
    {
        OverlayHost.Content = content;
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
