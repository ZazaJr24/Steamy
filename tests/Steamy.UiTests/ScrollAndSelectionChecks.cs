using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Steamy.Controls;
using Steamy.Pages;
using Steamy.Models;
using System.Windows.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Steamy.Services;
using Steamy.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckScrollReversal()
    {
        var viewer = new ScrollViewer { Width = 300, Height = 160, CanContentScroll = false,
            Content = new Border { Height = 2400 } };
        SmoothScroll.SetEnabled(viewer, true);
        var window = new Window { Content = viewer, Width = 340, Height = 210, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            // Exercise a real animation clock even when CI disables Windows motion.
            // Invoking the tween directly leaves the user's OS preference untouched.
            var stateProperty = (DependencyProperty)typeof(SmoothScroll)
                .GetField("StateProperty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            var state = viewer.GetValue(stateProperty);
            var animate = state.GetType().GetMethod("AnimateTo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            viewer.ScrollToVerticalOffset(300);
            window.UpdateLayout();
            PumpUntil(() => Math.Abs(viewer.VerticalOffset - 300) < 1);
            animate.Invoke(state, new object[] { 354d });
            PumpUntil(() => Math.Abs(viewer.VerticalOffset - 354) < 1);
            Assert.InRange(viewer.VerticalOffset, 353, 355);
            PumpDispatcher(TimeSpan.FromMilliseconds(240));
            Assert.InRange(viewer.VerticalOffset, 353, 355);
            animate.Invoke(state, new object[] { 408d });
            PumpUntil(() => Math.Abs(viewer.VerticalOffset - 408) < 1);
            Assert.InRange(viewer.VerticalOffset, 407, 409);
            var reduceEffects = MotionPreferences.ReduceEffects;
            try
            {
                MotionPreferences.Configure(false);
                animate.Invoke(state, new object[] { 650d });
                PumpDispatcher(TimeSpan.FromMilliseconds(60));
                Assert.InRange(viewer.VerticalOffset, 408, 650);
                MotionPreferences.Configure(true);
                PumpDispatcher(TimeSpan.FromMilliseconds(40));
                var stoppedOffset = viewer.VerticalOffset;
                Assert.True(stoppedOffset >= 408, "Stopping a tween must retain its current position.");
                PumpDispatcher(TimeSpan.FromMilliseconds(260));
                Assert.InRange(viewer.VerticalOffset, stoppedOffset - 1, stoppedOffset + 1);
            }
            finally { MotionPreferences.Configure(reduceEffects); }
            viewer.ScrollToVerticalOffset(300);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            static void Wheel(ScrollViewer target, int delta) => target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            Wheel(viewer, -120); Wheel(viewer, -120); Wheel(viewer, -120);
            var reversal = viewer.VerticalOffset;
            Wheel(viewer, 120);
            PumpDispatcher(TimeSpan.FromMilliseconds(240));
            Assert.True(viewer.VerticalOffset < reversal, "Reversing the wheel must reverse movement immediately, even during an unfinished animation.");
            viewer.ScrollToBottom();
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Wheel(viewer, -120);
            PumpDispatcher(TimeSpan.FromMilliseconds(200));
            Assert.InRange(viewer.VerticalOffset, 0, viewer.ScrollableHeight);
        }
        finally { window.Close(); SmoothScroll.SetEnabled(viewer, false); }
    }

    private static void CheckShellScrolling(IServiceProvider provider)
    {
        var previousCatalog = OfflineServiceProxy.ScreenshotCatalog;
        var model = provider.GetRequiredService<LibraryViewModel>();
        var previousSize = model.PageSize;
        var window = new MainWindow { Width = 1044, Height = 600, ShowInTaskbar = false };
        try
        {
            window.Show();
            Application.Current.MainWindow = window;
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            var dashboard = Descendants<DashboardPage>(window).Single();
            var homeScroll = (ScrollViewer)dashboard.FindName("DashboardScroll");
            Assert.False(SmoothScroll.GetEnabled(homeScroll));
            Assert.Same(homeScroll, Assert.Single(Descendants<ScrollViewer>(dashboard), viewer => viewer.IsVisible && viewer.ScrollableHeight > 0
                && !ReferenceEquals(viewer, dashboard.FindName("SpotlightPreviewScroll"))));
            Assert.NotNull(homeScroll.Template.FindName("PART_ScrollContentPresenter", homeScroll));
            Assert.False(ScrollViewer.GetCanContentScroll(dashboard));
            Assert.True(homeScroll.ViewportHeight > 0 && homeScroll.ScrollableHeight > 0,
                "The real navigation host must give Dashboard a finite scrolling viewport.");
            var image = (FrameworkElement)dashboard.FindName("SpotlightArtworkFrame");
            WheelOver(image, -120);
            PumpDispatcher(TimeSpan.FromMilliseconds(240));
            Assert.True(homeScroll.VerticalOffset > 0, "Wheel input over the Spotlight artwork must scroll Home.");
            var dashboardModel = (DashboardViewModel)dashboard.DataContext;
            var hero = (FrameworkElement)dashboard.FindName("DashboardHero");
            var originalHeroHeight = hero.ActualHeight;
            homeScroll.ScrollToVerticalOffset(Math.Min(240, homeScroll.ScrollableHeight));
            PumpDispatcher(TimeSpan.FromMilliseconds(60));
            var scrolled = homeScroll.VerticalOffset;
            for (var index = 0; index < 3; index++)
            {
                dashboardModel.NextFeaturedCommand.Execute(null);
                // A binding/focus update can raise this even with no navigation key pressed.
                image.BringIntoView();
                PumpDispatcher(TimeSpan.FromMilliseconds(240));
                Assert.InRange(homeScroll.VerticalOffset, scrolled - 1, scrolled + 1);
                Assert.Equal(originalHeroHeight, hero.ActualHeight);
            }


            // Keep the real five-second timer running through two rotations. The old
            // test only executed commands and could miss a later animation/layout reset.
            window.Activate();
            PumpDispatcher(TimeSpan.FromMilliseconds(80));
            Assert.True(window.IsActive, "The timed Spotlight regression requires an active window.");
            var timedRotations = 0;
            System.ComponentModel.PropertyChangedEventHandler rotated = (_, args) =>
            {
                if (args.PropertyName == nameof(DashboardViewModel.FeaturedGame)) timedRotations++;
            };
            dashboardModel.PropertyChanged += rotated;
            try
            {
                homeScroll.ScrollToVerticalOffset(Math.Min(240, homeScroll.ScrollableHeight));
                PumpDispatcher(TimeSpan.FromMilliseconds(60));
                var heldOffset = homeScroll.VerticalOffset;
                var heldViewport = homeScroll.ViewportHeight;
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                while (elapsed.Elapsed < TimeSpan.FromSeconds(11))
                {
                    PumpDispatcher(TimeSpan.FromMilliseconds(100));
                    Assert.InRange(homeScroll.VerticalOffset, heldOffset - 1, heldOffset + 1);
                    Assert.Equal(heldViewport, homeScroll.ViewportHeight);
                }
                Assert.True(timedRotations >= 2, "Exercise at least two automatic Spotlight changes while scrolled down.");
                dashboardModel.ToggleSpotlightCommand.Execute(null);
                Assert.True(dashboardModel.SpotlightPaused);
                var pausedId = dashboardModel.FeaturedGame!.Game.AppId;
                PumpDispatcher(TimeSpan.FromSeconds(5.5));
                Assert.Equal(pausedId, dashboardModel.FeaturedGame!.Game.AppId);
                Assert.InRange(homeScroll.VerticalOffset, heldOffset - 1, heldOffset + 1);
                dashboardModel.RefreshCountdowns();
                dashboardModel.ToggleSpotlightCommand.Execute(null);
                Assert.False(dashboardModel.SpotlightPaused);
                var rotationsBeforeResume = timedRotations;
                PumpDispatcher(TimeSpan.FromSeconds(5.5));
                Assert.True(timedRotations > rotationsBeforeResume);
                Assert.InRange(homeScroll.VerticalOffset, heldOffset - 1, heldOffset + 1);
                // Upward wheel input and scrollbar dragging must still be able to move.
                WheelOver(image, 120);
                PumpDispatcher(TimeSpan.FromMilliseconds(240));
                Assert.True(homeScroll.VerticalOffset < heldOffset);
                var bar = (ScrollBar)homeScroll.Template.FindName("PART_VerticalScrollBar", homeScroll);
                var thumbOffset = Math.Min(180, homeScroll.ScrollableHeight);
                // ScrollBar.ChangeValue sends this routed command to its template
                // parent during thumb tracking. ScrollEvent alone is only a notification.
                var thumbTarget = (IInputElement)bar.TemplatedParent;
                Assert.True(ScrollBar.ScrollToVerticalOffsetCommand.CanExecute(thumbOffset, thumbTarget));
                ScrollBar.ScrollToVerticalOffsetCommand.Execute(thumbOffset, thumbTarget);
                PumpDispatcher(TimeSpan.FromMilliseconds(240));
                Assert.InRange(homeScroll.VerticalOffset, thumbOffset - 1, thumbOffset + 1);
                var activity = dashboardModel.RefreshActivityAsync();
                PumpUntil(() => activity.IsCompleted);
                activity.GetAwaiter().GetResult();
                PumpDispatcher(TimeSpan.FromMilliseconds(200));
                Assert.InRange(homeScroll.VerticalOffset, thumbOffset - 1, thumbOffset + 1);
            }
            finally { dashboardModel.PropertyChanged -= rotated; }

            OfflineServiceProxy.ScreenshotCatalog = Enumerable.Range(1000, 56).Select(id =>
                new SteamCatalogItem { AppId = id, Name = $"Scroll fixture {id}", AppType = SteamCatalogAppType.Game }).ToArray();
            model.PageSize = 48;
            model.SelectedSourceFilter = "All sources";
            model.SelectedTypeFilter = "All games";
            model.SearchText = string.Empty;
            Assert.True(window.RootNavigationView.Navigate(typeof(LibraryPage)));
            var refresh = ((IAsyncRelayCommand)model.RefreshCatalogCommand).ExecuteAsync(null);
            PumpUntil(() => refresh.IsCompleted);
            refresh.GetAwaiter().GetResult();
            PumpUntil(() => model.PagedCatalogItems.Count == 48 && !model.IsCatalogLoading);
            window.UpdateLayout();
            var library = Descendants<LibraryPage>(window).Single();
            var gallery = (ScrollViewer)library.FindName("GalleryScroll");
            Assert.True(gallery.ScrollableHeight > gallery.ViewportHeight,
                "Several game rows must remain inside the Gallery scrolling viewport.");
            var card = Descendants<Button>(library).First(button => button.Tag is SteamCatalogItem);
            WheelOver(card, -120);
            PumpDispatcher(TimeSpan.FromMilliseconds(240));
            Assert.True(gallery.VerticalOffset > 0, "Wheel input over a game card must scroll the game grid.");
            var selector = new ComboBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0, Width = 180 };
            var content = Assert.IsType<StackPanel>(gallery.Content);
            content.Children.Add(selector);
            window.UpdateLayout();
            gallery.ScrollToBottom();
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            var before = gallery.VerticalOffset;
            var selected = selector.SelectedItem;
            WheelOver(selector, 120);
            PumpDispatcher(TimeSpan.FromMilliseconds(240));
            Assert.True(gallery.VerticalOffset < before, "A closed selector must not block page scrolling.");
            Assert.Same(selected, selector.SelectedItem);
            content.Children.Remove(selector);
            var keyboardCard = Descendants<Button>(library).Last(button => button.Tag is SteamCatalogItem);
            keyboardCard.Focus();
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            var keyboardBefore = gallery.VerticalOffset;
            keyboardCard.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(keyboardCard)!, Environment.TickCount, Key.PageUp)
                { RoutedEvent = Keyboard.KeyDownEvent });
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            Assert.True(gallery.VerticalOffset < keyboardBefore, "PageUp must work while a game card has focus.");
            var galleryOffset = gallery.VerticalOffset;
            var galleryPosition = gallery.TranslatePoint(new Point(0, 0), library);
            library.OpenDownloadSetup(model.PagedCatalogItems[0]);
            PumpDispatcher(TimeSpan.FromMilliseconds(220));
            Assert.Equal(galleryPosition, gallery.TranslatePoint(new Point(0, 0), library));
            Assert.InRange(gallery.VerticalOffset, galleryOffset - 1, galleryOffset + 1);
            library.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(library)!, 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            PumpDispatcher(TimeSpan.FromMilliseconds(220));
            Assert.InRange(gallery.VerticalOffset, galleryOffset - 1, galleryOffset + 1);

            Assert.True(window.RootNavigationView.Navigate(typeof(GameFixesPage)));
            PumpUntil(() => Descendants<GameFixesPage>(window).Any(page => page.IsVisible));
            var fixesModel = provider.GetRequiredService<GameFixesViewModel>();
            var fetch = ((IAsyncRelayCommand)fixesModel.FetchCommand).ExecuteAsync(null);
            PumpUntil(() => fetch.IsCompleted);
            fetch.GetAwaiter().GetResult();
            window.UpdateLayout();
            var fixesPage = Descendants<GameFixesPage>(window).Single();
            var fixCard = Descendants<Button>(fixesPage).First(button => button.Tag is GameFixGameCard);
            Assert.True(CardMotion.GetIsEnabled(fixCard));
            Assert.True(fixCard.ActualWidth >= 158);
            Assert.True(fixCard.ActualHeight >= fixCard.ActualWidth * 1.30 + 54,
                "Fix cards must fill their compact portrait-sized grid cell rather than float as small header thumbnails.");
            fixCard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpDispatcher(TimeSpan.FromMilliseconds(250));
            Assert.False(((Grid)fixesPage.FindName("MainContentGrid")).IsEnabled);
            Assert.Equal(Visibility.Visible, ((Grid)fixesPage.FindName("OverlayGrid")).Visibility);
            Assert.True(((Grid)fixesPage.FindName("OverlayGrid")).IsKeyboardFocusWithin);
            fixesPage.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(fixesPage)!, 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            PumpDispatcher(TimeSpan.FromMilliseconds(200));
            Assert.Equal(Visibility.Collapsed, ((Grid)fixesPage.FindName("OverlayGrid")).Visibility);
            Assert.True(((Grid)fixesPage.FindName("MainContentGrid")).IsEnabled);

            _phase = "SteamTools operation controls in the native shell";
            window.WindowState = WindowState.Normal;
            window.Width = 980; window.Height = 620;
            Assert.True(window.RootNavigationView.Navigate(typeof(BetterSteamToolsPage)));
            PumpDispatcher(TimeSpan.FromMilliseconds(150));
            window.UpdateLayout();
            var toolsPage = Descendants<BetterSteamToolsPage>(window).Single();
            Assert.False(((Expander)toolsPage.FindName("GameExpander")).IsExpanded);
            Assert.False(((Expander)toolsPage.FindName("SteamSetupExpander")).IsExpanded);
            ((Expander)toolsPage.FindName("GameExpander")).IsExpanded = true;
            ((Expander)toolsPage.FindName("SteamSetupExpander")).IsExpanded = true;
            window.UpdateLayout();
            var toolsScroll = (ScrollViewer)toolsPage.FindName("ToolScrollHost");
            var footer = (Grid)toolsPage.FindName("OperationFooter");
            Assert.False(ScrollViewer.GetCanContentScroll(toolsPage));
            Assert.Same(toolsScroll, Assert.Single(Descendants<ScrollViewer>(toolsPage), viewer => viewer.ScrollableHeight > 0));
            Assert.True(toolsPage.ActualHeight < window.ActualHeight, "The navigation host must constrain the page to the window.");
            var footerBounds = footer.TransformToAncestor(window).TransformBounds(new Rect(footer.RenderSize));
            Assert.True(footerBounds.Top >= 0 && footerBounds.Bottom <= window.ActualHeight, "Operation status must be inside the native window.");
            WheelOver((UIElement)toolsPage.FindName("GameInputBox"), -120);
            PumpDispatcher(TimeSpan.FromMilliseconds(120));
            Assert.True(toolsScroll.VerticalOffset > 0, "The real SteamTools viewport must handle mouse-wheel input.");
            toolsScroll.ScrollToBottom();
            PumpDispatcher(TimeSpan.FromMilliseconds(60));
            Assert.Equal(footerBounds, footer.TransformToAncestor(window).TransformBounds(new Rect(footer.RenderSize)));
            var toolsFixture = (BetterSteamToolsFixture)provider.GetRequiredService<IBetterSteamToolsService>();
            var toolsModel = provider.GetRequiredService<BetterSteamToolsViewModel>();
            toolsFixture.HoldOperation = true;
            toolsModel.GameInput = "480";
            var add = toolsModel.AddGameCommand.ExecuteAsync(null);
            try
            {
                PumpUntil(() => toolsModel.IsBusy);
                window.UpdateLayout();
                var cancel = (Button)toolsPage.FindName("CancelOperationButton");
                Assert.True(cancel.IsVisible && cancel.IsEnabled);
                var cancelBounds = cancel.TransformToAncestor(window).TransformBounds(new Rect(cancel.RenderSize));
                Assert.True(cancelBounds.Top >= 0 && cancelBounds.Bottom <= window.ActualHeight, "Cancel must stay inside the native window while scrolled.");
                SaveScreenshot(toolsPage, "Light-Native-Busy", new Size(toolsPage.ActualWidth, toolsPage.ActualHeight));
            }
            finally
            {
                toolsModel.CancelCommand.Execute(null);
                PumpUntil(() => add.IsCompleted); add.GetAwaiter().GetResult();
                toolsFixture.HoldOperation = false;
                toolsModel.GameInput = "";
            }

        }
        finally
        {
            OfflineServiceProxy.ScreenshotCatalog = previousCatalog;
            model.PageSize = previousSize;
            var restore = ((IAsyncRelayCommand)model.RefreshCatalogCommand).ExecuteAsync(null);
            PumpUntil(() => restore.IsCompleted);
            restore.GetAwaiter().GetResult();
            window.Close();
        }
    }

    private static void WheelOver(UIElement element, int delta)
    {
        // Real input tunnels first, then bubbles if the preview was not consumed.
        // Native ScrollViewer handles the bubbling phase; the custom tween handles preview.
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        element.RaiseEvent(args);
        if (args.Handled) return;
        args.RoutedEvent = UIElement.MouseWheelEvent;
        element.RaiseEvent(args);
    }

    private static void CheckDlcSelection(IServiceProvider provider)
    {
        var fixture = new DlcSelectionFixture();
        var model = new CreamApiViewModel(fixture, provider.GetRequiredService<ISettingsService>(),
            provider.GetRequiredService<IGameLocatorService>(), provider.GetRequiredService<ILoggingService>());
        PumpUntil(() => !model.IsLoadingGames);
        model.SelectedGame = new InstalledGameEntry(100, "First game", @"C:\Games\First");
        model.SelectedGame = new InstalledGameEntry(200, "Second game", @"C:\Games\Second");
        fixture.Requests[100].SetResult(new[] { new DlcEntry(101, "Stale DLC") }); // Some providers ignore cancellation.
        PumpDispatcher(TimeSpan.FromMilliseconds(20));
        Assert.True(model.IsFetching);
        Assert.Empty(model.DlcList);
        fixture.Requests[200].SetResult(new[] { new DlcEntry(201, "Current DLC") });
        PumpUntil(() => !model.IsFetching);
        Assert.Equal(201, Assert.Single(model.DlcList).AppId);
        model.DeselectAllCommand.Execute(null);
        Assert.False(Assert.Single(model.DlcList).IsSelected);
        model.SelectAllCommand.Execute(null);
        Assert.True(Assert.Single(model.DlcList).IsSelected);
        model.AppIdText = "300";
        Assert.Empty(model.DlcList);
        var pending = model.FetchDlcCommand.ExecuteAsync(null);
        Assert.True(model.IsFetching);
        model.AppIdText = "400";
        fixture.Requests[300].SetResult(new[] { new DlcEntry(301, "DLC for the previous App ID") });
        PumpUntil(() => pending.IsCompleted);
        pending.GetAwaiter().GetResult();
        Assert.False(model.IsFetching);
        Assert.Empty(model.DlcList);
    }

    private sealed class DlcSelectionFixture : ICreamApiService
    {
        public Dictionary<int, TaskCompletionSource<IReadOnlyList<DlcEntry>>> Requests { get; } = new();
        public bool HasCachedDlls(DlcUnlockerMode mode) => true;
        public string ProxyAddress { get; set; } = "";
        public Task EnsureDllsAvailableAsync(DlcUnlockerMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExtractDllsFromArchiveAsync(string archivePath, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<DlcEntry>> FetchDlcListAsync(int appId, CancellationToken ct = default)
        {
            var result = new TaskCompletionSource<IReadOnlyList<DlcEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(appId, result);
            return result.Task;
        }
        public CreamApiApplyResult ApplyToGameFolder(string gameFolder, int appId, IReadOnlyList<DlcEntry> dlcs,
            DlcUnlockerMode mode, string language, bool unlockAll, bool extraProtection, bool forceOffline) => throw new InvalidOperationException("Fixture never installs DLLs.");
        public CreamApiApplyResult RestoreOriginalDlls(string gameFolder) => throw new InvalidOperationException("Fixture never changes DLLs.");
    }
}
