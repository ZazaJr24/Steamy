using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckReworkShell(IServiceProvider services, AppDataStore store, FixtureArtwork artwork)
    {
        AddScreenshotLibrary(store, artwork);
        Application.Current.Resources["ArtworkImage"] = new ScreenshotArtworkConverter();
        OfflineServiceProxy.ScreenshotCatalog = store.Games.Select(game => new Steamy.Models.SteamCatalogItem { AppId = game.AppId, Name = game.Name, AppType = Steamy.Models.SteamCatalogAppType.Game, ArtworkImage = game.ArtworkImage, HeaderImage = game.HeaderImage }).ToArray();
        store.Downloads.Clear();
        store.Downloads.Add(new Steamy.Models.DownloadJob { AppId = 1091500, GameName = "Cyberpunk 2077", State = Steamy.Models.DownloadJobState.Downloading, HasWholeGameProgress = true, Progress = 42.5, TransferredBytes = 36_700_000_000, TransferTotalBytes = 86_300_000_000, BytesPerSecond = 12.4 * 1024 * 1024, DiskSpeed = "28.1 MiB/s", EtaSeconds = 4080, Downloaded = "34.18 GiB", TotalSize = "80.37 GiB", Status = "Downloading depot files" });
        store.Downloads.Add(new Steamy.Models.DownloadJob { AppId = 1245620, GameName = "ELDEN RING", State = Steamy.Models.DownloadJobState.Paused, Progress = 61, Downloaded = "37.3 GB", TotalSize = "61.1 GB" });
        store.Downloads.Add(new Steamy.Models.DownloadJob { AppId = 2358720, GameName = "Black Myth: Wukong", State = Steamy.Models.DownloadJobState.Completed, Progress = 100 });
        var previous = Application.Current.MainWindow;
        var window = new MainWindow { Width = 1440, Height = 900, WindowState = WindowState.Normal, ShowInTaskbar = false };
        Application.Current.MainWindow = window;
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(200));
            var routes = new[] { typeof(DashboardPage), typeof(LibraryPage), typeof(DownloadsPage), typeof(FriendsPage), typeof(CommunityPage), typeof(ControllersPage), typeof(SettingsPage) };
            foreach (var theme in new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme); UiThemeService.ApplyGlassPolicy(false);
                foreach (var route in routes)
                {
                    _phase = "Rework " + theme + " " + route.Name;
                    window.RootNavigationView.Navigate(route);
                    PumpUntil(() => Descendants<Page>(window).Any(page => page.GetType() == route));
                    if (route == typeof(LibraryPage))
                    {
                        var library = services.GetRequiredService<LibraryViewModel>(); library.SelectedMode = "Browse all";
                        var refresh = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)library.RefreshCatalogCommand).ExecuteAsync(null); PumpUntil(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
                    }
                    if (route == typeof(FriendsPage))
                    {
                        var social = services.GetRequiredService<DiscordFriendsViewModel>();
                        social.SelectedFriend ??= social.Friends.FirstOrDefault(friend => friend.IsFriend);
                    }
                    PumpDispatcher(TimeSpan.FromMilliseconds(220));
                    foreach (var size in new[] { new Size(980, 620), new Size(1440, 900), new Size(1920, 1080) })
                    {
                        window.Width = size.Width; window.Height = size.Height; window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(80));
                        var visiblePages = Descendants<Page>(window).ToArray();
                        var page = visiblePages.FirstOrDefault(view => view.GetType() == route);
                        if (page is null)
                        {
                            CaptureRework(window, $"missing-{theme.ToLowerInvariant()}-{route.Name}-{(int)size.Width}", 1);
                            Assert.Fail($"Route {route.Name} disappeared at {theme} {size.Width}x{size.Height}; visible pages: {string.Join(", ", visiblePages.Select(view => view.GetType().Name))}; surface: {window.PageGlassSurface?.GetType().Name ?? "none"}.");
                        }
                        Assert.True(page.ActualWidth > 500, "Every routed page must receive a usable viewport.");
                        Assert.True(page.ActualWidth >= window.ActualWidth - 2, "The established shell keeps pages full width.");
                        Assert.True(((Button)window.FindName("ShellFriendsButton")).IsVisible);
                        var bell = (FrameworkElement)window.FindName("NotificationBell");
                        var caption = (FrameworkElement)window.FindName("WindowTitleBar");
                        Assert.True(bell.TranslatePoint(new Point(bell.ActualWidth, 0), window).X + 5 <= caption.TranslatePoint(new Point(), window).X, "Notifications must never overlap the Windows caption controls.");
                        var bounds = page.TransformToAncestor(window).TransformBounds(new Rect(page.RenderSize));
                        Assert.True(bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1);
                        if (route == typeof(DownloadsPage)) Assert.InRange(((Border)page.FindName("DownloadHero")).ActualHeight, 200, 220);
                        if (route == typeof(LibraryPage)) Assert.Contains(Descendants<VirtualizingGamePanel>(page), panel => panel.Children.Count >= 4);
                        if (route == typeof(ControllersPage))
                        {
                            var controllerModel = services.GetRequiredService<ControllersViewModel>();
                            var diagram = (ControllerDiagram)page.FindName("MappingDiagram");
                            var triangle = Descendants<System.Windows.Controls.Primitives.ToggleButton>(diagram).Single(button => Equals(button.Tag, "Triangle"));
                            triangle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); PumpDispatcher(TimeSpan.FromMilliseconds(20));
                            Assert.Equal("Triangle", controllerModel.MappingSource);
                            var output = (ComboBox)page.FindName("SelectedMappingOutput"); output.SelectedItem = "LB";
                            Assert.Equal("LB", controllerModel.Mappings.Single(row => row.Source == "Triangle").Output);
                            Assert.True(triangle.IsChecked); controllerModel.SelectedMapping!.Output = "Y";
                            Assert.IsType<ControllerDriverSetup>(((Expander)page.FindName("ControllerSetupExpander")).Content);
                        }
                        CaptureRework(window, theme.ToLowerInvariant() + "-" + route.Name + "-" + (int)size.Width, 1);
                        if (route == typeof(ControllersPage) && size.Width == 1440)
                        {
                            var setup = Descendants<Expander>(page).Single(expander => expander.Content is ControllerDriverSetup); setup.IsExpanded = true;
                            window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(80));
                            CaptureRework(window, theme.ToLowerInvariant() + "-controllers-setup", 1);
                            setup.IsExpanded = false;
                            window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(80));
                            CaptureRework(window, theme.ToLowerInvariant() + "-controllers-mapping", 1);
                            window.UpdateLayout();
                        }
                        if (size.Width == 1440) CaptureRework(window, theme.ToLowerInvariant() + "-" + route.Name + "-150pct", 1.5);
                    }
                }
            }
        }
        finally { window.Close(); Application.Current.MainWindow = previous; }
    }
    private static void CaptureRework(Window window, string name, double scale)
    {
        if (Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png")); png.Save(file);
    }
}
