using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;
using Steamy.Views;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckDiscordRemoved(IServiceProvider provider)
    {
        DiscordIntegration.TestOverride = null;
        Assert.False(DiscordIntegration.Enabled);
        Assert.Null(provider.GetService<IDiscordSocialService>());
        Assert.Null(provider.GetService<DiscordFriendsViewModel>());
        Assert.Null(provider.GetService<DiscordVoiceViewModel>());
        var previous = Application.Current.MainWindow;
        var window = new MainWindow(openSocialOnStartup: true) { Width = 980, Height = 700, WindowState = WindowState.Normal, ShowInTaskbar = false };
        Application.Current.MainWindow = window;
        try
        {
            window.Show(); PumpUntil(() => Descendants<DashboardPage>(window).Any(page => page.IsVisible));
            Assert.False(((Button)window.FindName("ShellFriendsButton")).IsVisible);
            Assert.False(((Button)window.FindName("ShellFriendsHubButton")).IsVisible);
            Assert.Empty(Descendants<VoiceCallBar>(window));
            window.OpenSocialDock(); window.NavigateWithFeedback(typeof(FriendsPage));
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            Assert.Empty(Descendants<FriendsPage>(window));
            var factory = provider.GetRequiredService<Wpf.Ui.Abstractions.INavigationViewPageProvider>();
            Assert.Null(factory.GetPage(typeof(FriendsPage))); Assert.Null(factory.GetPage(typeof(CommunityPage)));
            var center = provider.GetRequiredService<NotificationCenterViewModel>();
            center.Publish(new("removal-download", SteamyNotificationKind.Download, "Download completed", "Ready to play"));
            center.OpenCommand.Execute(center.Items.Single(item => item.Content.Key == "removal-download"));
            PumpUntil(() => Descendants<DownloadsPage>(window).Any(page => page.IsVisible));
            Assert.True(((FrameworkElement)window.FindName("NotificationBell")).IsVisible);
            foreach (var theme in new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme);
                foreach (var size in new[] { new Size(980, 700), new Size(1440, 900) })
                {
                    window.Width = size.Width; window.Height = size.Height;
                    window.RootNavigationView.Navigate(typeof(SettingsPage)); window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(180));
                    var page = Descendants<SettingsPage>(window).Single();
                    ((TextBox)page.FindName("SettingsSearch")).Text = "Discord";
                    PumpDispatcher(TimeSpan.FromMilliseconds(260));
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)page.FindName("DiscordSettingsSection")).Visibility);
                    Assert.Null(page.FindName("SettingsDiscordConnection")); // Disabled section stays unconstructed.
                    CaptureRework(window, $"{theme.ToLowerInvariant()}-discord-removed-{size.Width}", 1);
                }
            }
            var setup = new FirstRunSetup(new FirstRunMemorySettings(), downloaderSetup: new ReadyDownloaderFixture(), gameScanner: new ReadyScannerFixture());
            setup.Measure(new Size(800, 700)); setup.Arrange(new Rect(0, 0, 800, 700)); setup.UpdateLayout();
            ClickButton(setup, "NextButton"); ClickButton(setup, "NextButton"); ClickButton(setup, "NextButton");
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)setup.FindName("DiscordStep")).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)setup.FindName("ControllerStep")).Visibility);
            Assert.Equal("04  /  05", ((TextBlock)setup.FindName("StepCounter")).Text);
            ClickButton(setup, "BackButton"); Assert.Equal(Visibility.Visible, ((FrameworkElement)setup.FindName("FoldersStep")).Visibility);
            ClickButton(setup, "NextButton"); ClickButton(setup, "NextButton");
            PumpUntil(() => ((FrameworkElement)setup.FindName("ReadyStep")).Visibility == Visibility.Visible);
            Assert.Equal("05  /  05", ((TextBlock)setup.FindName("StepCounter")).Text);
            Assert.Null(((DiscordConnectionCard)setup.FindName("SetupDiscordConnection")).Model);
            CaptureRework(window, "discord-removed-shell", 1);
        }
        finally { window.Close(); Application.Current.MainWindow = previous; }
    }
}
