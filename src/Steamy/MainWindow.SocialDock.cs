using System.Windows;
using Steamy.Pages;

namespace Steamy;

public partial class MainWindow
{
    private Type _socialReturnRoute = typeof(DashboardPage);
    // Kept for notification/controller callers; social now occupies the main workspace.
    internal bool IsSocialDockOpen => PageGlassSurface is FriendsPage;
    internal FriendsPage? SocialDockPage => _friendsPage;
    private void FriendsNavigation_Click(object sender, RoutedEventArgs args)
    { args.Handled = true; OpenSocialDock(); }
    internal void ToggleSocialDock() => OpenSocialDock();
    internal void OpenSocialDock()
    {
        if (!Services.DiscordIntegration.Enabled) return;
        CloseToolsMenu(restoreFocus:false); RootNavigation.IsPaneOpen = false;
        RootNavigation.Navigate(typeof(FriendsPage));
    }
    internal void CloseSocialDock()
    {
        if (!IsSocialDockOpen) return;
        while (_friendsPage?.CloseTransientPanels() == true) { }
        RootNavigation.Navigate(_socialReturnRoute);
    }
}
