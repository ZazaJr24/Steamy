using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Controls;
using Steamy.Pages;
using Steamy.Playtime;
using Steamy.Services;
using Steamy.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckGameDetails(IServiceProvider provider, AppDataStore store, FixtureArtwork artwork)
    {
        TracePhase("Game details fixtures");
        var arc = new Game { AppId = 1808500, Name = "ARC Raiders", ShortName = "ARC", CoverColor = "#262044", InstallState = GameInstallState.Installed, InstallFolder = @"C:\Games\ARC Raiders" };
        var local = new Game { AppId = -42, Name = "Local installation", ShortName = "LOCAL", IsLocalDownload = true, InstallationSource = "PC scan", InstallState = GameInstallState.Installed, InstallFolder = @"C:\Games\Local" };
        store.Games.Add(arc); store.Games.Add(local);
        AddScreenshotLibrary(store, artwork);
        TracePhase("Game details artwork");
        var art = artwork.LoadAsync(arc); PumpUntil(() => art.IsCompleted); art.GetAwaiter().GetResult();
        var model = provider.GetRequiredService<GameDetailsViewModel>();
        var fixture = (GameDetailsFixture)provider.GetRequiredService<IGameDetailsDataService>();
        var previous = Application.Current.MainWindow;
        TracePhase("Game details window construction");
        var window = new MainWindow { WindowState = WindowState.Normal, Width = 1280, Height = 800, ShowInTaskbar = false };
        Application.Current.MainWindow = window; window.Show();
        TracePhase("Game details window shown");
        PumpDispatcher(TimeSpan.FromMilliseconds(400));
        try
        {
            var dashboard = Descendants<DashboardPage>(window).Single();
            var dashboardModel = (DashboardViewModel)dashboard.DataContext;
            var tile = Descendants<Button>(dashboard).First(button => button.CommandParameter is Game game && game.AppId == arc.AppId && button.Command == dashboardModel.OpenGameCommand);
            Assert.True(tile.Command!.CanExecute(arc)); tile.Command.Execute(arc);
            TracePhase("Game details navigation");
            PumpUntil(() => Descendants<GameDetailsPage>(window).Any() && model.CanEditPreferences && model.AchievementsAvailable);
            var page = Descendants<GameDetailsPage>(window).Single();
            window.UpdateLayout();
            Assert.Equal("ARC Raiders", model.Name);
            Assert.Equal("2h 05m", model.PlayTimeLabel);
            Assert.Equal("2 / 3 unlocked", model.AchievementSummary);
            Assert.True(((FrameworkElement)window.FindName("PersistentNavigation")).IsVisible);
            Assert.InRange(page.TranslatePoint(new Point(), window).Y, 0, 1);
            PumpUntil(() => ReferenceEquals(model.HeroArtwork, page.AppliedHeroArtwork));
            Assert.True(((Image)page.FindName("HeroImage")).Source.IsFrozen);
            var heroScene = (FrameworkElement)page.FindName("HeroScene");
            var banner = (Image)page.FindName("HeroImage");
            Assert.True(heroScene.Height < ((FrameworkElement)page.FindName("HeroArtworkFade")).Height);
            Assert.InRange(Math.Abs(heroScene.ActualWidth / heroScene.ActualHeight - banner.Source.Width / banner.Source.Height), 0, .1);
            Assert.Equal(TextRenderingMode.ClearType, TextOptions.GetTextRenderingMode(page));
            Assert.Equal(0, fixture.Launches);
            var reduced = MotionPreferences.ReduceEffects;
            MotionPreferences.Configure(true); model.SelectedTab = 2; window.UpdateLayout();
            Assert.Equal(1, ((FrameworkElement)page.FindName("NotesPanel")).Opacity);
            MotionPreferences.Configure(reduced); model.SelectedTab = 0; window.UpdateLayout();
            Assert.True(((Button)page.FindName("PlayButton")).IsEnabled);
            Assert.True(((Button)page.FindName("AddToSteamButton")).IsVisible);
            SaveGameDetailsVisual(window, "overview");
            SaveGameDetailsDocument(page);
            CheckGameDetailsGlass(window, page, model);
            CheckBackgroundAmbientRendering(window, page, model);
            CheckSpatialGameAmbient(window, page, model);
            var cachedPalette = ArtworkPalette.FromImage(model.HeroArtwork);
            Assert.Same(cachedPalette, ArtworkPalette.FromImage(model.HeroArtwork));
            UiThemeService.ApplyGlassPolicy(true);
            Assert.Same(SystemColors.WindowBrush, ((Grid)page.FindName("DetailsScene")).Background);
            Assert.Same(Brushes.Transparent, ((Border)page.FindName("GameDetailsWarmGradient")).Background);
            UiThemeService.ApplyGlassPolicy(false);
            Assert.IsType<DrawingBrush>(((Border)page.FindName("GameDetailsWarmGradient")).Background);
            var scroll = (ScrollViewer)page.FindName("DetailsScroll");
            Assert.False(ScrollViewer.GetCanContentScroll(page));
            Assert.True(scroll.ScrollableHeight > 300);
            Assert.False(scroll.CanContentScroll);
            WheelOver((Image)page.FindName("HeroImage"), -120);
            PumpDispatcher(TimeSpan.FromMilliseconds(500));
            Assert.True(scroll.VerticalOffset > 0, $"Wheel offset={scroll.VerticalOffset}, enabled={SmoothScroll.GetEnabled(scroll)}, modifiers={System.Windows.Input.Keyboard.Modifiers}, lines={SystemParameters.WheelScrollLines}");
            Assert.NotEmpty(model.FullDescription); Assert.NotEmpty(model.SystemRequirements); Assert.NotEmpty(model.News);
            if (model.Screenshots.Count > 0)
            {
                Assert.Equal(model.Screenshots.Count, ((ItemsControl)page.FindName("ScreenshotGallery")).Items.Count);
                var gallery = (ItemsControl)page.FindName("ScreenshotGallery");
                gallery.BringIntoView(); window.UpdateLayout();
                try { PumpUntil(() => Descendants<Image>(gallery).Count(image => image.Source is not null) == model.Screenshots.Count); }
                catch (Exception error)
                {
                    SaveGameDetailsVisual(window, "gallery-failed");
                    throw new InvalidOperationException($"Preview loading failed: offset={scroll.VerticalOffset}, images={string.Join("; ", Descendants<Image>(gallery).Select(image => $"{image.ActualWidth}x{image.ActualHeight}, priority={ArtworkViewport.Priority(image)}, source={image.Source is not null}, url={AsyncArtwork.GetUrl(image)}"))}", error);
                }
                Assert.All(Descendants<Image>(gallery), image => Assert.True(image.ActualWidth > 40 && image.ActualHeight > 40,
                    "Screenshot thumbnails must have visible slots and load before opening the viewer."));
                // Repeated real scroll changes must not synchronously resample
                // the glass on the UI thread while movement is still ongoing.
                scroll.ScrollToTop(); window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(240));
                var sampleStamp = typeof(MainWindow).GetField("_lastCaptionRefresh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var beforeScroll = (long)sampleStamp.GetValue(window)!;
                for (var step = 1; step <= 16; step++)
                {
                    scroll.ScrollToVerticalOffset(step * 10); window.UpdateLayout();
                    PumpDispatcher(TimeSpan.FromMilliseconds(10));
                    Assert.Equal(beforeScroll, (long)sampleStamp.GetValue(window)!);
                }
                Assert.True(scroll.VerticalOffset > 0);
                PumpDispatcher(TimeSpan.FromMilliseconds(240));
                Assert.True((long)sampleStamp.GetValue(window)! > beforeScroll, "Glass contrast must update after scrolling settles.");
                gallery.BringIntoView(); window.UpdateLayout();
                SaveGameDetailsVisual(window, "gallery");
                CheckScreenshotViewer(window, page);
            }
            scroll.ScrollToEnd(); window.UpdateLayout();
            Assert.InRange(scroll.VerticalOffset, scroll.ScrollableHeight - 1, scroll.ScrollableHeight);
            SaveGameDetailsVisual(window, "information");
            scroll.ScrollToTop(); window.UpdateLayout();

            var launch = ((IAsyncRelayCommand)model.PlayCommand).ExecuteAsync(null); PumpUntil(() => launch.IsCompleted); launch.GetAwaiter().GetResult();
            Assert.Equal(1, fixture.Launches); Assert.Contains("requested", model.LaunchStatus);
            model.SelectedTab = 2; window.UpdateLayout();
            var notes = (TextBox)page.FindName("GameNotes");
            Assert.True(notes.IsVisible); Assert.True(notes.IsEnabled);
            notes.Text = "Remember the north extraction\nBring a shield";
            notes.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            model.SelectedTab = 3; window.UpdateLayout();
            Assert.True(((FrameworkElement)page.FindName("LaunchOptionsPanel")).IsVisible);
            model.LaunchArguments = "-windowed";
            var save = model.FlushAsync(); PumpUntil(() => save.IsCompleted); save.GetAwaiter().GetResult();
            Assert.Equal("-windowed", fixture.Preferences[arc.AppId].LaunchArguments);
            Assert.Contains("north extraction", fixture.Preferences[arc.AppId].Notes);
            model.SelectedTab = 1; window.UpdateLayout();
            Assert.Equal(3, ((ListBox)page.FindName("AchievementList")).Items.Count);
            SaveGameDetailsVisual(window, "achievements");

            foreach (var theme in new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme);
                foreach (var size in new[] { new Size(980, 620), new Size(1440, 900), new Size(1920, 1080) })
                {
                    window.Width = size.Width; window.Height = size.Height; model.SelectedTab = 0;
                    ((ScrollViewer)page.FindName("DetailsScroll")).ScrollToTop(); window.UpdateLayout();
                    PumpDispatcher(TimeSpan.FromMilliseconds(25)); window.UpdateLayout();
                    var actions = (FrameworkElement)page.FindName("GameActionBar");
                    Assert.True(actions.ActualWidth <= page.ActualWidth - 60);
                    var play = (FrameworkElement)page.FindName("PlayButton");
                    Assert.InRange(play.ActualWidth, 112, 136);
                    var steam = (Button)page.FindName("SteamStoreButton");
                    Assert.Equal(42, play.ActualHeight); Assert.Equal(play.ActualHeight, steam.ActualHeight);
                    Assert.Equal(new CornerRadius(21), Descendants<Border>(play).First().CornerRadius);
                    Assert.Equal(Descendants<Border>(play).First().CornerRadius, Descendants<Border>(steam).First().CornerRadius);
                    var time = (TextBlock)page.FindName("HeroPlayTimeText"); var lastPlayed = (TextBlock)page.FindName("HeroLastPlayedText");
                    Assert.Equal(model.PlayTimeLabel, time.Text); Assert.Equal(model.LastPlayedLabel, lastPlayed.Text);
                    Assert.True(time.IsVisible && lastPlayed.IsVisible);
                    var timeBounds = time.TransformToAncestor(page).TransformBounds(new Rect(time.RenderSize));
                    var lastBounds = lastPlayed.TransformToAncestor(page).TransformBounds(new Rect(lastPlayed.RenderSize));
                    Assert.True(timeBounds.Left > play.TransformToAncestor(page).TransformBounds(new Rect(play.RenderSize)).Right);
                    Assert.True(lastBounds.Left > timeBounds.Right);
                    Assert.True(lastBounds.Right <= page.ActualWidth);

                    Assert.Equal(Math.Clamp(page.ActualWidth * .29, 260, 440), ((FrameworkElement)page.FindName("GameHero")).ActualHeight, 0);
                    Assert.Equal(Visibility.Visible, ((FrameworkElement)page.FindName("HeroArtworkFade")).Visibility);
                    Assert.Equal(((FrameworkElement)page.FindName("GameHero")).ActualHeight + 260, ((FrameworkElement)page.FindName("HeroArtworkFade")).ActualHeight, 0);
                    Assert.Same(page.FindName("DetailsScene"), VisualTreeHelper.GetParent((FrameworkElement)page.FindName("HeroArtworkFade")));
                    Assert.Same(AppTypography.Text, page.FontFamily);
                    Assert.Same(AppTypography.Display, ((TextBlock)page.FindName("GameTitle")).FontFamily);
                    Assert.False(((Expander)page.FindName("GameToolsDisclosure")).IsExpanded);
                    var primaryBounds = play.TransformToAncestor(page).TransformBounds(new Rect(play.RenderSize));
                    Assert.True(primaryBounds.Right <= page.ActualWidth && primaryBounds.Bottom <= page.ActualHeight);
                    SaveGameDetailsVisual(window, $"{theme.ToLowerInvariant()}-{size.Width}");
                }
            }
            model.SelectedTab = 2; window.UpdateLayout();
            SaveGameDetailsVisual(window, "notes-light");
            model.BackCommand.Execute(null); PumpUntil(() => Descendants<DashboardPage>(window).Any());
            dashboardModel.OpenGameCommand.Execute(local);
            PumpUntil(() => Descendants<GameDetailsPage>(window).Any() && model.CanEditPreferences && !model.AchievementsBusy);
            Assert.Equal("Local installation", model.Name); Assert.Equal("17m", model.PlayTimeLabel);
            CheckAddToSteam(provider, window, model);
            Assert.Empty(model.Notes); Assert.Empty(model.Achievements); Assert.False(model.AchievementsAvailable);
            Assert.Null(model.LogoArtwork);
            model.SelectedTab = 3; window.UpdateLayout();
            Assert.Contains(Descendants<Button>(window), button => Equals(button.Content, "Choose executable") && button.IsVisible);
            model.Notes = "Local notes";
            model.SelectGame(arc); var activate = model.ActivateAsync(); PumpUntil(() => activate.IsCompleted); activate.GetAwaiter().GetResult();
            Assert.Contains("north extraction", model.Notes); Assert.Equal("Local notes", fixture.Preferences[local.AppId].Notes);
            var navigation = provider.GetRequiredService<INavigationService>();
            navigation.Navigate<LibraryPage>(); PumpUntil(() => Descendants<LibraryPage>(window).Any());
            var library = Descendants<LibraryPage>(window).Single();
            typeof(LibraryPage).GetMethod("GameCard_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(library, [new Button { Tag = local }, new RoutedEventArgs()]);
            PumpUntil(() => Descendants<GameDetailsPage>(window).Any() && model.CanEditPreferences);
            Assert.Same(local, model.Game);
            var currentPage = Descendants<GameDetailsPage>(window).Single();
            currentPage.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            PumpUntil(() => Descendants<LibraryPage>(window).Any());
            Assert.True(((FrameworkElement)window.FindName("PersistentNavigation")).IsVisible);
            navigation.Navigate<SettingsPage>(); PumpUntil(() => Descendants<SettingsPage>(window).Any());
            var settingsPage = Descendants<SettingsPage>(window).Single();
            var selector = (ListBox)settingsPage.FindName("CategorySelector");
            selector.SelectedItem = selector.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, "transparency"));
            window.UpdateLayout();
            var transparency = (StorageTransparencyView)settingsPage.FindName("TransparencyView");
            Assert.True(transparency.IsVisible); Assert.Equal(4, transparency.Groups.Count);
            Descendants<Expander>(transparency).First().IsExpanded = true;
            window.UpdateLayout();
            PumpUntil(() => Descendants<TextBox>(transparency).Any(box => box.IsReadOnly && box.Text.EndsWith("settings.json", StringComparison.Ordinal)));
            Assert.Contains(Descendants<TextBox>(transparency), box => box.IsReadOnly && box.Text.EndsWith("settings.json", StringComparison.Ordinal));
            SaveGameDetailsVisual(window, "transparency");
            var settingsScroll = (ScrollViewer)settingsPage.FindName("SettingsScrollViewer");
            settingsScroll.ScrollToEnd(); window.UpdateLayout();
            Assert.True(settingsScroll.VerticalOffset > 0);
            var deactivate = model.DeactivateAsync(); PumpUntil(() => deactivate.IsCompleted); deactivate.GetAwaiter().GetResult();
            fixture.SaveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            model.Notes = "Saved before closing";
            window.Close();
            Assert.True(window.IsVisible);
            fixture.SaveGate.SetResult();
            PumpUntil(() => !window.IsVisible);
            Assert.Equal("Saved before closing", fixture.Preferences[local.AppId].Notes);
        }
        finally { UiThemeService.Apply("Dark"); window.Close(); Application.Current.MainWindow = previous; }
    }
    private static void CheckScreenshotViewer(MainWindow window, GameDetailsPage page)
    {
        var previousLoader = GameScreenshotViewer.ImageLoader;
        var first = new ChatMedia([], BitmapSource.Create(16, 9, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 9 * 4], 64), false);
        var second = new ChatMedia([], BitmapSource.Create(16, 9, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 9 * 4], 64), false);
        var late = new TaskCompletionSource<ChatMedia>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ChatMedia>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; CancellationToken closedToken = default;
        GameScreenshotViewer.ImageLoader = (_, token) => ++calls switch
        {
            1 => Task.FromResult(first),
            2 => late.Task,
            3 => Task.FromResult(second),
            _ => WaitForClose(token)
        };
        Task<ChatMedia> WaitForClose(CancellationToken token) { closedToken = token; return pending.Task; }
        var scroll = (ScrollViewer)page.FindName("DetailsScroll"); var offset = scroll.VerticalOffset;
        var viewer = (GameScreenshotViewer)page.FindName("ScreenshotViewer");
        void Key(System.Windows.Input.Key key) => viewer.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        try
        {
            var tile = Descendants<Button>((ItemsControl)page.FindName("ScreenshotGallery")).First(button => button.Tag is GameScreenshot);
            tile.Focus(); tile.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); window.UpdateLayout();
            Assert.True(viewer.IsVisible); Assert.False(scroll.IsEnabled);
            Assert.Same(first.Poster, ((Image)viewer.FindName("PreviewImage")).Source);
            Assert.Single(Descendants<Button>(viewer));
            var frame = (Border)viewer.FindName("ViewerCard");
            Assert.Equal(16d / 9, frame.ActualWidth / frame.ActualHeight, 2);
            Key(System.Windows.Input.Key.Right); Key(System.Windows.Input.Key.Right);
            Assert.Same(second.Poster, ((Image)viewer.FindName("PreviewImage")).Source);
            late.SetResult(first); PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert.Same(second.Poster, ((Image)viewer.FindName("PreviewImage")).Source);
            // Use actual artwork for the screenshot; no network request is made by this fixture.
            var folder = Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK");
            if (folder is not null && File.Exists(Path.Combine(folder, "1808500_screenshot_3.jpg")))
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 1920; image.UriSource = new Uri(Path.Combine(folder, "1808500_screenshot_3.jpg")); image.EndInit(); image.Freeze();
                ((Image)viewer.FindName("PreviewImage")).Source = image;
            }
            SaveGameDetailsVisual(window, "image-viewer");
            Key(System.Windows.Input.Key.Right); Key(System.Windows.Input.Key.Escape); window.UpdateLayout();
            Assert.True(closedToken.IsCancellationRequested); Assert.False(viewer.IsVisible); Assert.True(scroll.IsEnabled);
            Assert.Equal(offset, scroll.VerticalOffset); Assert.Null(((Image)viewer.FindName("PreviewImage")).Source);
            pending.SetResult(first); PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert.Null(((Image)viewer.FindName("PreviewImage")).Source);
            Assert.Contains(Descendants<GameDetailsPage>(window), item => ReferenceEquals(item, page));
        }
        finally { viewer.Close(); scroll.IsEnabled = true; GameScreenshotViewer.ImageLoader = previousLoader; }
    }
    private static void CheckGameDetailsGlass(MainWindow window, GameDetailsPage page, GameDetailsViewModel model)
    {
        // A transparent-white endpoint used to turn black artwork gray halfway
        // through this shade. Render the actual page brush over black to catch it.
        var topShade = (Border)page.FindName("HeroTopShade");
        var shadeVisual = new DrawingVisual();
        using (var drawing = shadeVisual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 2, 84));
            drawing.DrawRectangle(topShade.Background, null, new Rect(0, 0, 2, 84));
        }
        var shadeBitmap = new RenderTargetBitmap(2, 84, 96, 96, PixelFormats.Pbgra32); shadeBitmap.Render(shadeVisual);
        var shadePixels = new byte[2 * 84 * 4]; shadeBitmap.CopyPixels(shadePixels, 8, 0);
        for (var i = 0; i < shadePixels.Length; i += 4)
            Assert.True(shadePixels[i] <= 1 && shadePixels[i + 1] <= 1 && shadePixels[i + 2] <= 1, "The header shade must not brighten black artwork.");
        var detailsScroll = (ScrollViewer)page.FindName("DetailsScroll"); detailsScroll.ApplyTemplate(); window.UpdateLayout();
        var scrollbar = (System.Windows.Controls.Primitives.ScrollBar)detailsScroll.Template.FindName("PART_VerticalScrollBar", detailsScroll);
        Assert.True(scrollbar.TranslatePoint(new Point(), window).Y >= window.WindowTitleBar.TranslatePoint(new Point(0, window.WindowTitleBar.ActualHeight), window).Y + 8,
            "The game-page scrollbar must start below the caption controls.");
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(window.WindowTitleBar.Background).Color);
        var saved = model.HeroArtwork;
        var hero = typeof(GameDetailsViewModel).GetProperty(nameof(model.HeroArtwork))!;
        var reduced = MotionPreferences.ReduceEffects;
        MotionPreferences.Configure(false);
        var pixels = new List<Color>();
        try
        {
            foreach (var (brush, expected) in new[] { (Brushes.White, Brushes.Black), (Brushes.Black, Brushes.White), (Brushes.RoyalBlue, Brushes.White) })
            {
                var art = new DrawingImage(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(0, 0, 1200, 600)))); art.Freeze();
                hero.SetValue(model, art); window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(400)); window.RefreshCaptionAppearance();
                PumpDispatcher(TimeSpan.FromMilliseconds(240)); // Includes the post-scroll contrast debounce.
                window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var navigation = (Grid)window.FindName("PersistentNavigation");
                Assert.Contains(Descendants<ShellBackdropView>(navigation), view => view.SamplePageSurface);
                foreach (var button in ((Grid)window.FindName("ShellPillItems")).Children.OfType<Button>())
                {
                    var point = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight - 4), content); var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
                    var background = ChromeLuminance(Color.FromRgb(pixel[2], pixel[1], pixel[0])); var foreground = ChromeLuminance(((SolidColorBrush)button.Foreground).Color);
                    var contrast = (Math.Max(background, foreground) + .05) / (Math.Min(background, foreground) + .05);
                    Assert.True(contrast >= 4.5, $"Game page {button.Content}: glass text contrast {contrast:0.00}:1. Source={brush}, Foreground={button.Foreground}, Background={pixel[2]},{pixel[1]},{pixel[0]}. Delay={((System.Windows.Threading.DispatcherTimer?)typeof(MainWindow).GetField("_captionRefreshDelay", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window))?.IsEnabled}, pending={((System.Windows.Threading.DispatcherOperation?)typeof(MainWindow).GetField("_pendingNavigationForeground", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window))?.Status}");
                }
                var sample = navigation.TranslatePoint(new Point(15, 12), content); var rgba = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)sample.X, (int)sample.Y, 1, 1), rgba, 4, 0); pixels.Add(Color.FromRgb(rgba[2], rgba[1], rgba[0]));
                Assert.Contains(window.WindowTitleBar.ButtonsForeground, new[] { Brushes.Black, Brushes.White });
                if (ReferenceEquals(brush, Brushes.RoyalBlue))
                {
                    window.RootNavigationView.IsPaneOpen = true; window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(300));
                    var drawer = (Grid)window.RootNavigationView.Template.FindName("PaneGrid", window.RootNavigationView);
                    Assert.Contains(Descendants<ShellBackdropView>(drawer), view => view.SamplePageSurface);
                    SaveGameDetailsVisual(window, "glass-drawer"); window.RootNavigationView.IsPaneOpen = false;
                }
            }
            Assert.True(pixels[0].R > pixels[1].R + 30, "The banner brightness must carry through the top navigation.");
            Assert.True(pixels[2].B > pixels[2].R + 30, "The blue artwork must tint the transparent navigation.");
        }
        finally { hero.SetValue(model, saved); MotionPreferences.Configure(reduced); window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(100)); }
    }

    private static void CheckBackgroundAmbientRendering(MainWindow window, GameDetailsPage page, GameDetailsViewModel model)
    {
        var saved = model.HeroArtwork;
        var property = typeof(GameDetailsViewModel).GetProperty(nameof(model.HeroArtwork))!;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var workerThread = 0;
        var blocked = ArtworkRenderWorker.RunAsync(() =>
        { workerThread = Environment.CurrentManagedThreadId; entered.Set(); return release.Wait(TimeSpan.FromSeconds(10)); }, CancellationToken.None);
        try
        {
            PumpUntil(() => entered.IsSet);
            var first = new DrawingImage(new GeometryDrawing(Brushes.Goldenrod, null, new RectangleGeometry(new Rect(0, 0, 1200, 600)))); first.Freeze();
            var latest = new DrawingImage(new GeometryDrawing(Brushes.Teal, null, new RectangleGeometry(new Rect(0, 0, 1200, 600)))); latest.Freeze();
            property.SetValue(model, first); property.SetValue(model, latest);
            var processedInput = false;
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => processedInput = true));
            PumpUntil(() => processedInput);
            Assert.False(blocked.IsCompleted); Assert.NotEqual(Environment.CurrentManagedThreadId, workerThread);
            Assert.False(ReferenceEquals(page.AppliedAmbientArtwork, first) || ReferenceEquals(page.AppliedAmbientArtwork, latest));
            release.Set(); PumpUntil(() => ReferenceEquals(page.AppliedAmbientArtwork, latest));
            Assert.IsType<DrawingBrush>(((Border)page.FindName("GameDetailsWarmGradient")).Background);
        }
        finally
        {
            release.Set(); property.SetValue(model, saved);
            if (saved is not null) PumpUntil(() => ReferenceEquals(page.AppliedAmbientArtwork, saved));
            PumpUntil(() => blocked.IsCompleted); Assert.True(blocked.GetAwaiter().GetResult());
        }
    }

    private static void CheckSpatialGameAmbient(MainWindow window, GameDetailsPage page, GameDetailsViewModel model)
    {
        var saved = model.HeroArtwork;
        var property = typeof(GameDetailsViewModel).GetProperty(nameof(model.HeroArtwork))!;
        try
        {
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(Brushes.LightGray, null, new RectangleGeometry(new Rect(0, 0, 600, 600))));
            drawing.Children.Add(new GeometryDrawing(Brushes.RoyalBlue, null, new RectangleGeometry(new Rect(600, 0, 600, 600))));
            drawing.Children.Add(new GeometryDrawing(Brushes.Crimson, null, new RectangleGeometry(new Rect(600, 300, 600, 300))));
            var art = new DrawingImage(drawing); art.Freeze();
            property.SetValue(model, art); window.UpdateLayout();
            PumpUntil(() => ReferenceEquals(page.AppliedAmbientArtwork, art));
            var brush = Assert.IsType<DrawingBrush>(((Border)page.FindName("GameDetailsWarmGradient")).Background);
            Assert.True(brush.IsFrozen);
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(Assert.IsType<ImageDrawing>(brush.Drawing).ImageSource);
            Assert.True(bitmap.IsFrozen); Assert.Equal(320, bitmap.PixelWidth); Assert.Equal(320, bitmap.PixelHeight);
            Color Pixel(double x, double y)
            {
                var bytes = new byte[4]; bitmap.CopyPixels(new Int32Rect((int)(319 * x), (int)(319 * y), 1, 1), bytes, 4, 0);
                return Color.FromRgb(bytes[2], bytes[1], bytes[0]);
            }
            foreach (var y in new[] { .2, .55, .9 })
            {
                var neutral = Pixel(.1, y); var colored = Pixel(.9, y);
                Assert.InRange(Math.Abs(neutral.R - neutral.B), 0, 3);
                if (y < .35) Assert.True(colored.B > colored.R + 30, "The upper blue banner region must remain blue.");
                if (y > .75) Assert.True(colored.R > colored.B + 30, "The lower red region must remain red; ambient retains vertical image structure.");
                Assert.InRange(UiThemeService.Luminance(neutral), .025, .11);
                Assert.InRange(UiThemeService.Luminance(colored), .01, .11);
            }
            Assert.Equal(((FrameworkElement)page.FindName("DetailsScene")).ActualHeight,
                ((FrameworkElement)page.FindName("GameDetailsWarmGradient")).ActualHeight, 0);
            var pageBitmap = new RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32); pageBitmap.Render(page);
            var bannerEnd = (int)((FrameworkElement)page.FindName("GameHero")).ActualHeight;
            var above = new byte[4]; var below = new byte[4]; var edgeX = (int)(page.ActualWidth * .94);
            pageBitmap.CopyPixels(new Int32Rect(edgeX, bannerEnd - 2, 1, 1), above, 4, 0);
            pageBitmap.CopyPixels(new Int32Rect(edgeX, bannerEnd + 2, 1, 1), below, 4, 0);
            for (var channel = 0; channel < 3; channel++) Assert.InRange(Math.Abs(above[channel] - below[channel]), 0, 8);
            var scroll = (ScrollViewer)page.FindName("DetailsScroll");
            scroll.ScrollToVerticalOffset(500); window.UpdateLayout();
            var after = Assert.IsType<ImageDrawing>(((DrawingBrush)((Border)page.FindName("GameDetailsWarmGradient")).Background).Drawing).ImageSource;
            Assert.Same(bitmap, after); // Scrolling repositions a brush, never recomputes blur.
            Assert.Null(((Border)page.FindName("GameDetailsWarmGradient")).Effect);
            Assert.Same(bitmap, GameDetailsAmbient.For(art, page.ActualWidth / ((FrameworkElement)page.FindName("GameHero")).ActualHeight));
            scroll.ScrollToTop();
        }
        finally { property.SetValue(model, saved); window.UpdateLayout(); }
    }
    private static void SaveGameDetailsDocument(GameDetailsPage page)
    {
        if (Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") is not { Length: > 0 } folder) return;
        var scene = (FrameworkElement)page.FindName("DetailsScene");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(scene.ActualWidth), (int)Math.Ceiling(scene.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(scene);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, "game-details-full-page.png")); encoder.Save(stream);
    }
    private static void SaveGameDetailsVisual(MainWindow window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        PumpDispatcher(TimeSpan.FromMilliseconds(380)); window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, "game-details-" + name + ".png")); encoder.Save(stream);
    }
    private sealed class GameDetailsFixture : IGameDetailsDataService, IGamePreferencesStore, IPlayTimeService, IGameLaunchService
    {
        public Dictionary<int, GamePreferences> Preferences { get; } = new();
        public int Launches { get; private set; }
        public TaskCompletionSource? SaveGate { get; set; }
        public Task<GameOverview> ReadOverviewAsync(int appId, CancellationToken token = default) => Task.FromResult(new GameOverview("ARC Raiders is a multiplayer extraction adventure, set in a lethal future earth, ravaged by a mysterious mechanized threat known as ARC.", "Embark Studios", "Embark Studios")
        {
            DetailedDescription = "Game description (test fixture)\n\nExplore the surface, prepare your loadout and return to the extraction point.\n\nThis section verifies that the complete game information remains reachable below the hero and overview.",
            ReleaseDate = "30 Oct, 2025 (test fixture)", Genres = "Action · Adventure (test fixture)",
            Languages = "English, German (test fixture)", Features = "Online co-op · Steam achievements (test fixture)",
            SystemRequirements = "Minimum (test fixture)\nOS: Windows 10\nMemory: 16 GB RAM\n\nRecommended (test fixture)\nOS: Windows 11\nMemory: 32 GB RAM",
            Reviews = new GameReviewSummary(945, 1000, "Very Positive (test fixture)"),
            Screenshots = FixtureScreenshots(),
            News = [new("Latest update (test fixture)", "Update cards are loaded automatically from the official Steam announcements feed. This is a layout fixture, not live news.", "8 Oct 2026", new Uri("https://store.steampowered.com/news/app/1808500"))]
        });
        private static IReadOnlyList<GameScreenshot> FixtureScreenshots()
        {
            var folder = Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK");
            if (string.IsNullOrWhiteSpace(folder)) return [];
            var screenshots = Directory.GetFiles(folder, "1808500_screenshot_*.jpg").Order().ToArray();
            if (screenshots.Length > 0) return screenshots.Select(file => new GameScreenshot(new Uri(file), new Uri(file))).ToArray();
            return new[] { "1808500_header.jpg", "1808500_hero.jpg", "1808500_portrait.jpg" }
                .Select(file => Path.GetFullPath(Path.Combine(folder, file))).Where(File.Exists)
                .Select(file => new GameScreenshot(new Uri(file), new Uri(file))).ToArray();
        }
        public Task<GameAchievementSnapshot> ReadAchievementsAsync(int appId, CancellationToken token = default) => Task.FromResult(appId <= 0
            ? GameAchievementSnapshot.Unavailable("Steam achievements are not available for this local game.")
            : new GameAchievementSnapshot([new("fixture-first", "First extraction (test fixture)", "Complete an extraction.", true, "", DateTimeOffset.UtcNow.AddDays(-3)), new("fixture-second", "A new loadout (test fixture)", "Prepare a loadout.", true, "", DateTimeOffset.UtcNow.AddDays(-2)), new("fixture-third", "Return to the surface (test fixture)", "Complete another run.", false, "", null)], true, "Synced with Steam (test fixture)"));
        public Task<GamePreferences> ReadAsync(int appId, CancellationToken token = default) => Task.FromResult(Preferences.GetValueOrDefault(appId) ?? new());
        public async Task SaveAsync(int appId, GamePreferences value, CancellationToken token = default) { if (SaveGate is not null) await SaveGate.Task; Preferences[appId] = value; }
        Task<PlayTimeSnapshot> IPlayTimeService.ReadAsync(CancellationToken token) => Task.FromResult(new PlayTimeSnapshot(1, DateTimeOffset.UtcNow, [new(1808500, 7500, false), new(-42, 1020, false)]));
        public Task<DateTimeOffset?> ReadLastPlayedAsync(int appId, CancellationToken token = default) => Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddDays(-1));
        public Task ConfigureAsync(AppSettings settings, IEnumerable<Game> games) => Task.CompletedTask;
        public Task<GameLaunchResult> LaunchAsync(Game game, CancellationToken token = default) { Launches++; return Task.FromResult(new GameLaunchResult(true, "Launch requested.")); }
    }
}
