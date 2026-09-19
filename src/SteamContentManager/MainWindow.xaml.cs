using System.Windows;
using SteamContentManager.Services;
using Wpf.Ui.Controls;

namespace SteamContentManager;

public partial class MainWindow : FluentWindow
{
    public NavigationView RootNavigationView => RootNavigation;

    private readonly NavigationService _navigationService;

    public MainWindow()
    {
        InitializeComponent();
        _navigationService = (NavigationService)App.Services.GetService(typeof(INavigationService))!;
        _navigationService.Attach(route => RootNavigation.Navigate(route));
        Loaded += (_, _) => RootNavigation.Navigate(typeof(Pages.DashboardPage));

    }

    protected override void OnClosed(EventArgs e)
    {
        _navigationService.Detach();
        base.OnClosed(e);
    }
}
