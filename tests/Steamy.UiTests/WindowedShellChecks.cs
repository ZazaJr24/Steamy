using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private sealed class NoWindowsControllers : IWindowsControllers
    {
        public IReadOnlyList<Steamy.ControllerProtocol.ControllerDevice> Read() => [];
        public Task RumbleAsync(int slot, CancellationToken token) => throw new InvalidOperationException("No hardware in layout tests.");
    }
    private static void CheckWindowedShell(IServiceProvider services, AppDataStore store, FixtureArtwork artwork)
    {
        AddScreenshotLibrary(store, artwork);
        Application.Current.Resources["ArtworkImage"] = new ScreenshotArtworkConverter();
        store.Downloads.Clear();
        var job = new DownloadJob { AppId = 1091500, GameName = "Cyberpunk 2077", State = DownloadJobState.Downloading,
            Progress = 42.5, HasWholeGameProgress = true, TransferredBytes = 36_700_000_000, TransferTotalBytes = 86_300_000_000,
            BytesPerSecond = 12.4 * 1024 * 1024, EtaSeconds = 4080, DiskSpeed = "28.1 MiB/s" };
        store.Downloads.Add(job);
        store.Downloads.Add(new DownloadJob { AppId = 1245620, GameName = "ELDEN RING", State = DownloadJobState.Paused, Progress = 61, Downloaded = "37.3 GB", TotalSize = "61.1 GB" });
        store.Downloads.Add(new DownloadJob { AppId = 2358720, GameName = "Black Myth: Wukong", State = DownloadJobState.Completed, Progress = 100 });
        var dashboardModel = services.GetRequiredService<DashboardViewModel>();
        var settings = services.GetRequiredService<ISettingsService>().Load();
        var searchEnabled = settings.DashboardSearch;
        var previousSearch = dashboardModel.SearchText;
        var previous = Application.Current.MainWindow;
        var window = new MainWindow { Width = 1180, Height = 780, WindowState = WindowState.Normal, ShowInTaskbar = false };
        Application.Current.MainWindow = window;
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(150));
            foreach (var theme in new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme);
                window.NavigateWithFeedback(typeof(DownloadsPage)); PumpDispatcher(TimeSpan.FromMilliseconds(120));
                var downloads = Descendants<DownloadsPage>(window).Single();
                PumpUntil(() => Descendants<Image>((Border)downloads.FindName("DownloadHeroArtwork")).Single().Source is not null);
                foreach (var size in new[] { new Size(980, 620), new Size(1180, 780), new Size(1920, 1080) })
                {
                    _phase = $"Windowed {theme} {size.Width}";
                    window.Width = size.Width; window.Height = size.Height; window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(120));
                    window.CloseSocialDock(); PumpDispatcher(TimeSpan.FromMilliseconds(40));
                    if (Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") is { } outputDirectory)
                    {
                        System.IO.Directory.CreateDirectory(outputDirectory);
                        var caption = (FrameworkElement)window.FindName("WindowTitleBar");
                        System.IO.File.WriteAllText(System.IO.Path.Combine(outputDirectory, "caption-layout.txt"), string.Join("\n", Descendants<FrameworkElement>(caption).Where(view => view is ButtonBase).Select(view => $"{view.GetType().Name} {view.Name} {view.ActualWidth}x{view.ActualHeight} {view.TranslatePoint(new Point(), window)}")));
                    }
                    CheckDownloadsLayout(downloads);
                    var sampledToolbar = (FrameworkElement)window.FindName("PersistentNavigation");
                    var toolbarGlass = Descendants<ShellBackdropView>(sampledToolbar).Single();
                    var crop = new Rect(toolbarGlass.TranslatePoint(new Point(), downloads), toolbarGlass.RenderSize);
                    Assert.False(crop.IntersectsWith(new Rect(downloads.RenderSize)));
                    var toolbarDrawings = new[] { (Visual)toolbarGlass }.Concat(Descendants<Visual>(toolbarGlass))
                        .Select(VisualTreeHelper.GetDrawing).Where(drawing => drawing is not null).ToArray();
                    Assert.NotEmpty(toolbarDrawings);
                    Assert.All(toolbarDrawings, drawing => Assert.False(HasLiveVisualSample(drawing!),
                        "A toolbar above the page must not retain a live brush of that page."));
                    const System.Reflection.BindingFlags sampleFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    typeof(MainWindow).GetMethod("UpdateNavigationForeground", sampleFlags)!.Invoke(window, null);
                    var contrastSample = Assert.IsType<DrawingVisual>(typeof(MainWindow).GetField("_navigationSampleVisual", sampleFlags)!.GetValue(window));
                    Assert.False(HasLiveVisualSample(VisualTreeHelper.GetDrawing(contrastSample)),
                        "A completed contrast capture must release its live toolbar brush while retaining only the reusable bitmap.");
                    CaptureRework(window, $"{theme.ToLowerInvariant()}-downloads-{size.Width}", 1);
                    // The friends entry opens a persistent right dock without changing the current route.
                    window.NavigateWithFeedback(typeof(FriendsPage));
                    PumpUntil(() => Descendants<FriendsPage>(window).Any(page => page.IsVisible));
                    var friends = Descendants<FriendsPage>(window).Single();
                    Assert.False(friends.IsDocked);
                    Assert.Null(window.FindName("SocialDockHost"));
                    Assert.Equal(new Thickness(0), window.RootNavigationView.Margin);
                    Assert.True(friends.ActualWidth >= window.ActualWidth - 2);
                    var contacts = (Border)friends.FindName("ContactListSurface");
                    var chat = (Border)friends.FindName("ChatSurface");
                    Assert.True(contacts.IsVisible && chat.IsVisible);
                    Assert.InRange(contacts.ActualWidth, 240, 300);
                    Assert.True(chat.ActualWidth > contacts.ActualWidth);
                    Assert.True(contacts.TranslatePoint(new Point(contacts.ActualWidth, 0), friends).X <= chat.TranslatePoint(new Point(), friends).X);
                    Assert.Equal(Visibility.Collapsed, ((Border)friends.FindName("PartyPane")).Visibility);
                    var model = services.GetRequiredService<DiscordFriendsViewModel>();
                    model.SelectedFilter = "Friends"; model.SearchText = "";
                    model.SelectedFriend = model.Friends.First(friend => friend.IsFriend);
                    PumpDispatcher(TimeSpan.FromMilliseconds(240));
                    var composer = (TextBox)friends.FindName("MessageComposer");
                    Assert.True(composer.IsVisible);
                    var composerBounds = composer.TransformToAncestor(friends).TransformBounds(new Rect(composer.RenderSize));
                    Assert.True(composerBounds.Bottom <= friends.ActualHeight && composerBounds.Width > 200);
                    Assert.All(Descendants<ScrollViewer>(friends), viewer => Assert.True(viewer.ScrollableWidth < 1));
                    CaptureRework(window, $"{theme.ToLowerInvariant()}-friends-main-{size.Width}", 1);
                    model.MessageDraft = "Keep this draft";
                    window.NavigateWithFeedback(typeof(ControllersPage));
                    PumpUntil(() => Descendants<ControllersPage>(window).Any(page => page.IsVisible));
                    Assert.False(window.IsSocialDockOpen); Assert.False(friends.IsVisible);
                    Assert.Equal("Keep this draft", model.MessageDraft);
                    ((ButtonBase)window.FindName("ShellFriendsHubButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    PumpUntil(() => Descendants<FriendsPage>(window).Any(page => page.IsVisible));
                    friends = Descendants<FriendsPage>(window).Single();
                    Assert.Equal("Keep this draft", ((TextBox)friends.FindName("MessageComposer")).Text);
                    ((ButtonBase)window.FindName("ShellFriendsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    PumpDispatcher(TimeSpan.FromMilliseconds(90));
                    Assert.True(friends.IsVisible); Assert.Single(Descendants<FriendsPage>(window));
                    model.MessageDraft = "";
                    ((ButtonBase)window.FindName("ShellDownloadsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    PumpUntil(() => Descendants<DownloadsPage>(window).Any());
                    downloads = Descendants<DownloadsPage>(window).Single();
                }
            }
            window.CloseSocialDock();
            window.NavigateWithFeedback(typeof(DashboardPage)); PumpDispatcher(TimeSpan.FromMilliseconds(180));
            var oldHome = Descendants<DashboardPage>(window).Single();
            var homeScroll = (ScrollViewer)oldHome.FindName("DashboardScroll");
            homeScroll.ScrollToVerticalOffset(300); window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(40));
            var offset = homeScroll.VerticalOffset;
            settings.DashboardSearch = true;
            dashboardModel.RefreshSearchPreference();
            dashboardModel.SearchText = "Cyberpunk";
            Assert.True(dashboardModel.IsSearchBusy);
            // Leave before the debounce completes. The query is retained independently
            // of the page, and must restart when this Home visual has been evicted.
            Assert.True(window.RootNavigationView.Navigate(typeof(SettingsPage)));
            PumpDispatcher(TimeSpan.FromMilliseconds(60));
            Assert.False(dashboardModel.IsSearchBusy);
            foreach (var route in new[] { typeof(SettingsPage), typeof(LibraryPage), typeof(DownloadsPage), typeof(ControllersPage), typeof(FriendsHubPage) })
            {
                window.NavigateWithFeedback(route); PumpDispatcher(TimeSpan.FromMilliseconds(100));
                Assert.InRange(((BoundedNavigationView)window.FindName("RootNavigation")).AssociatedPageCount, 1, 3);
            }
            window.NavigateWithFeedback(typeof(SettingsPage)); PumpDispatcher(TimeSpan.FromMilliseconds(120));
            var oldSettings = Descendants<SettingsPage>(window).Single();
            ((TextBox)oldSettings.FindName("SettingsSearch")).Text = "downloads";
            ((ListBox)oldSettings.FindName("CategorySelector")).SelectedIndex = 2;
            foreach (var route in new[] { typeof(DownloadsPage), typeof(ControllersPage), typeof(FriendsHubPage) })
            { window.NavigateWithFeedback(route); PumpDispatcher(TimeSpan.FromMilliseconds(70)); }
            window.NavigateWithFeedback(typeof(SettingsPage)); PumpDispatcher(TimeSpan.FromMilliseconds(120));
            var restoredSettings = Descendants<SettingsPage>(window).Single();
            Assert.Same(oldSettings, restoredSettings);
            Assert.Equal("downloads", ((TextBox)restoredSettings.FindName("SettingsSearch")).Text);
            Assert.Equal(2, ((ListBox)restoredSettings.FindName("CategorySelector")).SelectedIndex);
            var detachedSettings = CreateDetachedSettingsSnapshot(restoredSettings.FindResource("SettingsCard"));
            PumpDispatcher(TimeSpan.FromMilliseconds(250));
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.False(detachedSettings.IsAlive, "Shared settings styles must not retain a discarded page.");
            window.NavigateWithFeedback(typeof(DashboardPage)); PumpDispatcher(TimeSpan.FromMilliseconds(180));
            var restored = Descendants<DashboardPage>(window).Single();
            Assert.NotSame(oldHome, restored);
            Assert.Equal("Cyberpunk", dashboardModel.SearchText);
            PumpUntil(() => !dashboardModel.IsSearchBusy && dashboardModel.SearchMatches.Any(match => match.Item.AppId == 1091500));
            Assert.DoesNotContain("Searching", dashboardModel.SearchStatus, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(((ScrollViewer)restored.FindName("DashboardScroll")).VerticalOffset, Math.Max(0, offset - 1), offset + 1);
            Assert.InRange(((BoundedNavigationView)window.FindName("RootNavigation")).AssociatedPageCount, 1, 3);
        }
        finally
        {
            settings.DashboardSearch = searchEnabled;
            dashboardModel.RefreshSearchPreference();
            if (searchEnabled) dashboardModel.SearchText = previousSearch;
            window.Close(); Application.Current.MainWindow = previous;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateDetachedSettingsSnapshot(object existingStyle)
    {
        var page = new SettingsPage();
        Assert.Equal("downloads", ((TextBox)page.FindName("SettingsSearch")).Text);
        Assert.Equal(2, ((ListBox)page.FindName("CategorySelector")).SelectedIndex);
        Assert.Same(existingStyle, page.FindResource("SettingsCard"));
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        return new WeakReference(page);
    }

    private static bool HasLiveVisualSample(Drawing? drawing) => drawing switch
    {
        GeometryDrawing { Brush: VisualBrush } => true,
        DrawingGroup group => group.Children.Any(HasLiveVisualSample),
        _ => false
    };
}
