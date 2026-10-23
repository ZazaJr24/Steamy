using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckChromeVisuals(IServiceProvider services, AppDataStore store, FixtureArtwork artwork)
    {
        AddScreenshotLibrary(store, artwork);
        OfflineServiceProxy.ScreenshotCatalog = store.Games.Select(game => new SteamCatalogItem
        { AppId = game.AppId, Name = game.Name, AppType = SteamCatalogAppType.Game, ArtworkImage = game.ArtworkImage, HeaderImage = game.HeaderImage }).ToArray();
        Application.Current.Resources["ArtworkImage"] = new ScreenshotArtworkConverter();
        var settings = services.GetRequiredService<ISettingsService>().Load();
        settings.AutoplayTrailers = false; settings.Appearance = "Dark";
        var window = new MainWindow { Width = 1440, Height = 900, ShowInTaskbar = false, WindowState = WindowState.Normal };
        Application.Current.MainWindow = window;
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(250));
            UiThemeService.Apply("Dark");
            window.NavigateWithFeedback(typeof(LibraryPage));
            PumpUntil(() => window.PageGlassSurface is LibraryPage);
            var model = services.GetRequiredService<LibraryViewModel>();
            var refresh = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)model.RefreshCatalogCommand).ExecuteAsync(null);
            PumpUntil(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
            var baseline = Environment.GetEnvironmentVariable("STEAMY_UI_CHROME_BASELINE") == "1";
            foreach (var theme in baseline ? new[] { "Dark" } : new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme);
                foreach (var width in baseline ? new[] { 1440 } : new[] { 980, 1440, 1920 })
                {
                    TracePhase($"Chrome {theme} {width}");
                    window.Width = width; window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(250));
                    var rail = (FrameworkElement)window.FindName("PersistentNavigation");
                    var launcher = (FrameworkElement)window.RootNavigationView.Template.FindName("MenuLauncher", window.RootNavigationView);
                    var caption = (FrameworkElement)window.FindName("WindowTitleBar");
                    var bell = (FrameworkElement)window.FindName("NotificationBell");
                    Rect Bounds(FrameworkElement view) => view.TransformToAncestor(window).TransformBounds(new Rect(view.RenderSize));
                    Assert.InRange(rail.ActualHeight - launcher.ActualHeight, -.5, .5);
                    Assert.InRange(Bounds(rail).Y - Bounds(launcher).Y, -.5, .5);
                    Assert.True(Bounds(launcher).Right + 12 < Bounds(rail).Left);
                    Assert.True(Bounds(rail).Right + 12 < Bounds(bell).Left);
                    Assert.True(Bounds(bell).Right <= Bounds(caption).Left);
                    CaptureRework(window, $"chrome-{theme.ToLowerInvariant()}-{width}", 1);
                    if (!baseline && width == 1440) CaptureRework(window, $"chrome-{theme.ToLowerInvariant()}-{width}-2x", 2);
                }
            }
            if (!baseline)
            {
                UiThemeService.Apply("Dark"); window.Width = 1440;
                var items = (Grid)window.FindName("ShellPillItems");
                var buttons = items.Children.OfType<Button>().ToArray();
                var highlight = (Border)window.FindName("NavigationHighlight");
                Assert.False(PillMotion.GetFollowPointer(items));
                PumpDispatcher(TimeSpan.FromMilliseconds(180));
                var selectedX = ((TranslateTransform)highlight.RenderTransform).X;
                buttons[3].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
                buttons[3].Focus(); PumpDispatcher(TimeSpan.FromMilliseconds(180));
                Assert.Equal(selectedX, ((TranslateTransform)highlight.RenderTransform).X);
                Assert.True(ShellNavigation.GetIsSelected(buttons[1]));
                Assert.False(ShellNavigation.GetIsSelected(buttons[3]));
                buttons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpUntil(() => window.PageGlassSurface is DashboardPage); PumpDispatcher(TimeSpan.FromMilliseconds(400));
                Assert.True(ShellNavigation.GetIsSelected(buttons[0]));
                CaptureRework(window, "chrome-home-dark-1440", 1);
                buttons[3].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); PumpDispatcher(TimeSpan.FromMilliseconds(220));
                Assert.True(window.IsToolsMenuOpen);
                Assert.True(ShellNavigation.GetIsSelected(buttons[3]));
                Assert.True(window.PageGlassSurface!.IsEnabled);
                CaptureRework(window, "chrome-tools-dark-1440", 1);
                window.CloseToolsMenu();
                Assert.True(ShellNavigation.GetIsSelected(buttons[0]));
                var toggle = (Button)window.RootNavigationView.Template.FindName("PART_ToggleButton", window.RootNavigationView);
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(window.RootNavigationView.IsPaneOpen);
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.False(window.RootNavigationView.IsPaneOpen);
                UiThemeService.ApplyGlassPolicy(true); window.RefreshFrameAppearance();
                Assert.Same(SystemColors.WindowTextBrush, Application.Current.Resources["ShellChromeBorderBrush"]);
                UiThemeService.ApplyGlassPolicy(false); window.RefreshFrameAppearance();
                Assert.NotSame(SystemColors.WindowTextBrush, Application.Current.Resources["ShellChromeBorderBrush"]);
            }
        }
        finally { window.Close(); OfflineServiceProxy.ScreenshotCatalog = null; }
    }
}
