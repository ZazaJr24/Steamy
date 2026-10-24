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
using Wpf.Ui.Controls;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr FrameHitMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "IsIconic")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool FrameIsIconic(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "IsZoomed")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool FrameIsZoomed(IntPtr hwnd);

    private static int FrameHit(Window window, Point point)
    {
        var screen = window.PointToScreen(point);
        var packed = ((int)screen.Y << 16) | ((int)screen.X & 0xffff);
        return (int)FrameHitMessage(new System.Windows.Interop.WindowInteropHelper(window).Handle,
            0x84, IntPtr.Zero, new IntPtr(packed));
    }

    private static void CheckGlassShell(IServiceProvider provider)
    {
        UiThemeService.Apply("Dark");
        var app = Application.Current;
        var previousWindow = app.MainWindow;
        var model = provider.GetRequiredService<DashboardViewModel>();
        var backdrop = provider.GetRequiredService<IShellBackdropService>();
        var effects = MotionPreferences.ReduceEffects;
        var search = provider.GetRequiredService<ISettingsService>().Load();
        var previousSearch = search.DashboardSearch;
        var originalFeature = model.FeaturedGame;
        var spotlightPaused = model.SpotlightPaused;
        if (!spotlightPaused) model.ToggleSpotlightCommand.Execute(null);
        var window = new MainWindow { WindowState = WindowState.Normal, Width = 1280, Height = 800, ShowInTaskbar = false };
        app.MainWindow = window;
        window.Show();
        PumpDispatcher(TimeSpan.FromMilliseconds(400));
        try
        {
            CheckMinimizedDashboard(window, model);
            Assert.Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(window));
            Assert.Equal(TextRenderingMode.ClearType, TextOptions.GetTextRenderingMode(window));
            Assert.Equal(TextHintingMode.Fixed, TextOptions.GetTextHintingMode(window));
            Assert.Equal(ClearTypeHint.Enabled, RenderOptions.GetClearTypeHint(window));
            Assert.True(window.UseLayoutRounding);
            Assert.True(window.SnapsToDevicePixels);
            Assert.IsType<DrawingImage>(window.FindResource("SteamyBrandImage"));
            Assert.Equal(17, FrameHit(window, new Point(window.ActualWidth - 3, window.ActualHeight - 3)));
            Assert.Equal(2, FrameHit(window, new Point(250, 20)));
            Assert.Equal(20, FrameHit(window, new Point(window.ActualWidth - 22, 17))); // HTCLOSE
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            PumpUntil(() => !FrameIsIconic(handle));
            TracePhase($"Chrome before maximize: state={window.WindowState}, iconic={FrameIsIconic(handle)}, zoomed={FrameIsZoomed(handle)}, active={window.IsActive}");
            FrameHitMessage(handle, 0x112, new IntPtr(0xF030), IntPtr.Zero); // SC_MAXIMIZE
            PumpUntil(() => window.WindowState == WindowState.Maximized && FrameIsZoomed(handle));
            Assert.Equal(WindowState.Maximized, window.WindowState);
            FrameHitMessage(handle, 0x112, new IntPtr(0xF120), IntPtr.Zero); // SC_RESTORE
            PumpUntil(() => window.WindowState == WindowState.Normal && !FrameIsZoomed(handle) && !FrameIsIconic(handle));
            Assert.Equal(WindowState.Normal, window.WindowState);
            FrameHitMessage(handle, 0x112, new IntPtr(0xF020), IntPtr.Zero); // SC_MINIMIZE
            PumpUntil(() => window.WindowState == WindowState.Minimized && FrameIsIconic(handle));
            Assert.Equal(WindowState.Minimized, window.WindowState);
            FrameHitMessage(handle, 0x112, new IntPtr(0xF120), IntPtr.Zero);
            PumpUntil(() => window.WindowState == WindowState.Normal && !FrameIsIconic(handle));
            window.Activate();
            var face = new Typeface(window.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Assert.True(face.TryGetGlyphTypeface(out var glyph));
            Assert.Contains(glyph.FamilyNames.Values, name => name is "Google Sans" or "SF Pro Text");
            var nav = window.RootNavigationView;
            var pane = Assert.IsType<Grid>(nav.Template.FindName("PaneGrid", nav));
            var toggle = Assert.IsAssignableFrom<System.Windows.Controls.Primitives.ButtonBase>(nav.Template.FindName("PART_ToggleButton", nav));
            Assert.False(nav.IsPaneOpen);
            Assert.Equal(Visibility.Collapsed, pane.Visibility);
            SaveGlassVisual(window, "sidebar-hidden");
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            window.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert.True(nav.IsPaneOpen);
            Assert.Equal(new Thickness(12), pane.Margin);
            Assert.Equal(24, RoundedClip.GetRadius(pane));
            Assert.InRange(pane.ActualWidth, 231, 233);
            Assert.All(nav.MenuItems.OfType<NavigationViewItem>(), item => Assert.True(item.ActualHeight >= 40));
            Assert.Equal(new[] { "Downloads", "Settings" }, nav.FooterMenuItems.OfType<NavigationViewItem>().Select(item => item.Content));

            var page = Descendants<DashboardPage>(window).Single();
            Assert.Equal("Home", nav.Tag);
            Assert.InRange(page.TranslatePoint(new Point(), window).Y, 0, 1);
            var feature = model.FeaturedGame!;
            var artwork = feature.HeroArtwork;
            // A deterministic source also exercises late artwork arrival when no cache is supplied.
            var testImage = new DrawingImage(new GeometryDrawing(Brushes.CornflowerBlue, null, new RectangleGeometry(new Rect(0, 0, 1200, 600))));
            testImage.Freeze();
            feature.HeroArtwork = testImage;
            Assert.Same(testImage, backdrop.Artwork);
            var homeAmbient = (DashboardBackdropView)page.FindName("DashboardBackdrop");
            PumpUntil(() => homeAmbient.AmbientBitmap is not null);
            var blueAmbient = Assert.IsAssignableFrom<BitmapSource>(homeAmbient.AmbientBitmap);
            Assert.True(blueAmbient.IsFrozen);
            Assert.Equal(320, blueAmbient.PixelWidth);
            Assert.InRange(blueAmbient.PixelHeight, 1, 1280);
            Assert.Null(homeAmbient.Effect);
            PumpUntil(() => backdrop.BlurredArtwork is not null);
            var blurred = Assert.IsAssignableFrom<BitmapSource>(backdrop.BlurredArtwork);
            Assert.True(blurred.IsFrozen);
            var color = new byte[4];
            blurred.CopyPixels(new Int32Rect(blurred.PixelWidth / 2, blurred.PixelHeight / 2, 1, 1), color, 4, 0);
            Assert.True(color[0] > color[2], "The cached glass background must contain the source image, not a black texture.");
            feature.HeroArtwork = null;
            Assert.Null(backdrop.Artwork);
            Assert.Null(homeAmbient.AmbientBitmap);
            feature.HeroArtwork = testImage;
            Assert.Same(blueAmbient, homeAmbient.AmbientBitmap);
            Assert.Same(blurred, backdrop.BlurredArtwork); // Scroll/resize/revisit never recompute blur.
            feature.HeroArtwork = artwork;
            var paneBeforeAdaptiveChrome = nav.IsPaneOpen;
            nav.IsPaneOpen = false;
            var savedLogo = feature.LogoArtwork;
            feature.LogoArtwork = null;
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, ((System.Windows.Controls.TextBlock)page.FindName("SpotlightTitle")).Visibility);
            feature.LogoArtwork = testImage;
            foreach (var (brush, expected) in new[] { (Brushes.White, Brushes.Black), (Brushes.Black, Brushes.White) })
            {
                var solidArtwork = new DrawingImage(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(0, 0, 1200, 600))));
                solidArtwork.Freeze(); feature.HeroArtwork = solidArtwork;
                window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(380));
                window.RefreshCaptionAppearance();
                window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
                var navigation = (Grid)window.FindName("ShellPillItems");
                var chrome = (FrameworkElement)window.Content;
                var chromeBitmap = new RenderTargetBitmap((int)chrome.ActualWidth, (int)chrome.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                chromeBitmap.Render(chrome);
                Assert.All(navigation.Children.OfType<System.Windows.Controls.Button>(), button =>
                {
                    // The Home scrim and selection tint can put adjacent buttons on
                    // different backgrounds. Check the actual composed pixels.
                    var point = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight - 4), chrome);
                    var pixel = new byte[4];
                    chromeBitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
                    var background = ChromeLuminance(Color.FromRgb(pixel[2], pixel[1], pixel[0]));
                    var foreground = ChromeLuminance(Assert.IsType<SolidColorBrush>(button.Foreground).Color);
                    var contrast = (Math.Max(background, foreground) + .05) / (Math.Min(background, foreground) + .05);
                    Assert.True(contrast >= 4.5, $"{button.Content}: composed glass text contrast was {contrast:0.00}:1.");
                });
                Assert.Same(expected, window.WindowTitleBar.ButtonsForeground);
                // Each navigation button is checked against its composed glass
                // pixels above; its selection tint can differ from the title bar.
                var snapshot = homeAmbient.ChromeSnapshot;
                var testScroll = (ScrollViewer)page.FindName("DashboardScroll");
                testScroll.ScrollToVerticalOffset(180); window.UpdateLayout(); window.RefreshCaptionAppearance();
                Assert.Same(snapshot, homeAmbient.ChromeSnapshot);
                testScroll.ScrollToTop(); window.UpdateLayout();
                SaveGlassVisual(window, ReferenceEquals(brush, Brushes.White) ? "adaptive-bright-chrome" : "adaptive-dark-chrome");
            }
            feature.HeroArtwork = artwork;
            var spatialSource = new DrawingGroup();
            nav.IsPaneOpen = paneBeforeAdaptiveChrome;
            spatialSource.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 600, 600))));
            spatialSource.Children.Add(new GeometryDrawing(Brushes.Crimson, null, new RectangleGeometry(new Rect(600, 0, 600, 600))));
            var spatialImage = new DrawingImage(spatialSource); spatialImage.Freeze();
            feature.HeroArtwork = spatialImage;
            var paneBeforeBannerScreenshot = nav.IsPaneOpen;
            nav.IsPaneOpen = false;
            window.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(320));
            Assert.Equal(Visibility.Collapsed, ((System.Windows.Controls.TextBlock)page.FindName("SpotlightTitle")).Visibility);
            SaveGlassVisual(window, "readable-white-banner");
            nav.IsPaneOpen = paneBeforeBannerScreenshot;
            var spatialGradient = homeAmbient.AmbientBitmap;
            // Check the actual composed backdrop at the old hero/body seam, without text over it.
            var renderedAmbient = new RenderTargetBitmap((int)homeAmbient.ActualWidth, (int)homeAmbient.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            renderedAmbient.Render(homeAmbient);
            foreach (var fraction in new[] { .15, .5, .9 })
            {
                var above = new byte[4]; var below = new byte[4];
                var x = (int)(homeAmbient.ActualWidth * fraction);
                renderedAmbient.CopyPixels(new Int32Rect(x, (int)homeAmbient.HeroHeight - 2, 1, 1), above, 4, 0);
                renderedAmbient.CopyPixels(new Int32Rect(x, (int)homeAmbient.HeroHeight + 2, 1, 1), below, 4, 0);
                for (var channel = 0; channel < 3; channel++) Assert.InRange(Math.Abs(above[channel] - below[channel]), 0, 6);
            }
            var bannerBody = new byte[4];
            renderedAmbient.CopyPixels(new Int32Rect((int)(homeAmbient.ActualWidth * .9), (int)(homeAmbient.HeroHeight + 45), 1, 1), bannerBody, 4, 0);
            Assert.True(bannerBody[2] > bannerBody[0] + 20, $"The original red banner ambient must remain visible directly below the hero: B={bannerBody[0]} G={bannerBody[1]} R={bannerBody[2]} A={bannerBody[3]}");
            var spatialScroll = (ScrollViewer)page.FindName("DashboardScroll");
            spatialScroll.ScrollToVerticalOffset(270); window.UpdateLayout();
            Assert.Same(spatialGradient, homeAmbient.AmbientBitmap);
            SaveGlassVisual(window, "spatial-white-red-ambient");
            spatialScroll.ScrollToTop();
            feature.HeroArtwork = artwork;
            feature.LogoArtwork = savedLogo;
            MoveCursorAway(0, 0);
            ((System.Windows.Controls.Button)page.FindName("ShowAllGamesButton")).Focus();
            var upcoming = (ItemsControl)page.FindName("UpcomingGameGrid");
            var ambientFeature = model.NewGames.Last(item => !ReferenceEquals(item, feature));
            var ambientCard = Descendants<System.Windows.Controls.Button>(upcoming)
                .First(button => ReferenceEquals(button.DataContext, ambientFeature) && ReferenceEquals(button.CommandParameter, ambientFeature));
            ambientCard.BringIntoView();
            window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(60));
            var originalPreviewArt = ambientFeature.HeroArtwork;
            byte[] BodyPixel(double xFraction = .5, double yFraction = .76)
            {
                var surface = new RenderTargetBitmap((int)homeAmbient.ActualWidth, (int)homeAmbient.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                // This assertion compares the logical background at offscreen
                // coordinates too. OnRender now deliberately records only the
                // viewport; sample the complete scene independently of culling.
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                    typeof(DashboardBackdropView).GetMethod("DrawFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(homeAmbient, [dc, true, null]);
                surface.Render(visual);
                var pixel = new byte[4];
                surface.CopyPixels(new Int32Rect((int)(homeAmbient.ActualWidth * xFraction), (int)(homeAmbient.ActualHeight * yFraction), 1, 1), pixel, 4, 0);
                return pixel;
            }
            void PreviewEvent(RoutedEvent routedEvent) => ambientCard.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = routedEvent });
            byte[] PreviewPixel(double x = .5) => BodyPixel(x,
                (ambientCard.TranslatePoint(new Point(0, ambientCard.ActualHeight * .5), homeAmbient).Y) / homeAmbient.ActualHeight);
            try
            {
                PumpDispatcher(TimeSpan.FromMilliseconds(410));
                var idle = BodyPixel();
                Assert.True(idle.Take(3).All(channel => channel == 0), "The lower dashboard must be true black without a game preview.");
                var installedCard = Descendants<System.Windows.Controls.Button>((ItemsControl)page.FindName("RecentlyOpenedStrip")).First();
                installedCard.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
                PumpDispatcher(TimeSpan.FromMilliseconds(410));
                Assert.Equal(idle, BodyPixel());
                installedCard.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
                foreach (var menuItem in nav.MenuItems.OfType<NavigationViewItem>())
                    Assert.Same(Brushes.White, menuItem.Foreground);
                spatialScroll.ScrollToEnd(); window.UpdateLayout();
                var installedY = (homeAmbient.HeroHeight + 45) / homeAmbient.ActualHeight;
                var installedBaseline = BodyPixel(.9, installedY);
                SaveGlassVisual(window, "black-body");
                ambientFeature.HeroArtwork = new DrawingImage(new GeometryDrawing(Brushes.Red, null, new RectangleGeometry(new Rect(0, 0, 1200, 600))));
                PreviewEvent(UIElement.MouseEnterEvent);
                PumpUntil(() => { var r = PreviewPixel(); return r[2] > r[0] + 15; }, TimeSpan.FromSeconds(2));
                var red = PreviewPixel();
                Assert.True(red[2] > red[0] + 15, "Hover must use the hovered game's red artwork, not the featured banner palette.");
                Assert.Equal(installedBaseline, BodyPixel(.9, installedY));
                SaveGlassVisual(window, "red-preview");
                ambientFeature.HeroArtwork = new DrawingImage(new GeometryDrawing(Brushes.Blue, null, new RectangleGeometry(new Rect(0, 0, 1200, 600))));
                PumpUntil(() => { var b = PreviewPixel(); return b[0] > b[2] + 15; }, TimeSpan.FromSeconds(2));
                var blue = PreviewPixel();
                Assert.True(blue[0] > blue[2] + 15, "A changed hovered artwork must replace the previous ambient colors.");
                SaveGlassVisual(window, "blue-preview");
                var regionalArt = new DrawingGroup();
                regionalArt.Children.Add(new GeometryDrawing(Brushes.Red, null, new RectangleGeometry(new Rect(0, 0, 600, 600))));
                regionalArt.Children.Add(new GeometryDrawing(Brushes.Blue, null, new RectangleGeometry(new Rect(600, 0, 600, 600))));
                ambientFeature.HeroArtwork = new DrawingImage(regionalArt);
                PumpUntil(() => { var l = PreviewPixel(.15); var r = PreviewPixel(.85); return l[2] > l[0] + 15 && r[0] > r[2] + 15; }, TimeSpan.FromSeconds(2));
                var regionalLeft = PreviewPixel(.15); var regionalRight = PreviewPixel(.85);
                Assert.True(regionalLeft[2] > regionalLeft[0] + 15 && regionalRight[0] > regionalRight[2] + 15,
                    "Upcoming ambient must preserve the artwork's separate left and right colors.");
                SaveGlassVisual(window, "regional-preview");
                ambientFeature.HeroArtwork = new DrawingImage(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 1200, 600))));
                PumpDispatcher(TimeSpan.FromMilliseconds(410));
                var neutral = PreviewPixel();
                var neutralLuminance = ChromeLuminance(Color.FromRgb(neutral[2], neutral[1], neutral[0]));
                var captionLuminance = ChromeLuminance(Assert.IsType<SolidColorBrush>(page.Resources["DashboardBodyTertiary"]).Color);
                Assert.True((captionLuminance + .05) / (neutralLuminance + .05) >= 4.5,
                    $"Even white artwork must leave release dates readable: background={neutralLuminance:F3}, caption={captionLuminance:F3}.");
                ambientFeature.HeroArtwork = originalPreviewArt;
                PumpDispatcher(TimeSpan.FromMilliseconds(410));
                spatialScroll.ScrollToVerticalOffset(ambientCard.TranslatePoint(new Point(), (Grid)page.FindName("DashboardContent")).Y - 100);
                window.UpdateLayout(); SaveGlassVisual(window, "natural-preview");
                PreviewEvent(UIElement.MouseLeaveEvent);
                PumpDispatcher(TimeSpan.FromMilliseconds(410));
                Assert.True(BodyPixel().Take(3).All(channel => channel == 0), "Leaving the game must restore the black background.");
            }
            finally { PreviewEvent(UIElement.MouseLeaveEvent); ambientFeature.HeroArtwork = originalPreviewArt; spatialScroll.ScrollToTop(); }
            var toolbarBackdrop = Descendants<ShellBackdropView>((Grid)window.FindName("PersistentNavigation")).Single();
            Assert.False(toolbarBackdrop.IncludePageContent);
            Assert.InRange(toolbarBackdrop.FrostRadius, 1, 12);
            Assert.IsType<System.Windows.Media.Effects.BlurEffect>(toolbarBackdrop.Effect);
            var haloFeature = model.NewGames.FirstOrDefault(item => item.Game?.Name.Contains("Clockwork", StringComparison.OrdinalIgnoreCase) == true) ?? ambientFeature;
            var haloCard = Descendants<System.Windows.Controls.Button>(upcoming)
                .Single(button => ReferenceEquals(button.CommandParameter, haloFeature));
            haloCard.BringIntoView();
            window.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(60));
            var paneBeforeHalo = nav.IsPaneOpen;
            nav.IsPaneOpen = false;
            PumpDispatcher(TimeSpan.FromMilliseconds(400));
            window.UpdateLayout();
            spatialScroll.ScrollToVerticalOffset(haloCard.TranslatePoint(new Point(), (Grid)page.FindName("DashboardContent")).Y - 180);
            window.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(160));
            window.UpdateLayout();
            Assert.Equal(ImagePriority.Visible, ArtworkViewport.Priority(haloCard));
            var halo = Descendants<ArtworkHalo>(haloCard).Single();
            var haloSource = halo.Source;
            var haloSourceBinding = System.Windows.Data.BindingOperations.GetBindingBase(halo, ArtworkHalo.SourceProperty);
            var haloActiveBinding = System.Windows.Data.BindingOperations.GetBindingBase(halo, ArtworkHalo.IsActiveProperty);
            var haloWasActive = halo.IsActive;
            // This probe measures light outside the real card, independently of
            // asynchronous fixture artwork/pointer updates. Restore both bindings.
            halo.SetValue(ArtworkHalo.SourceProperty, testImage);
            halo.SetValue(ArtworkHalo.IsActiveProperty, false);
            byte[] ExteriorPixel()
            {
                var position = halo.TranslatePoint(new Point(halo.ActualWidth * .5, -12), window);
                var capture = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                capture.Render(window);
                var pixel = new byte[4];
                capture.CopyPixels(new Int32Rect((int)position.X, Math.Max(0, (int)position.Y), 1, 1), pixel, 4, 0);
                return pixel;
            }
            try
            {
                PumpDispatcher(TimeSpan.FromMilliseconds(320));
                var beforeHalo = ExteriorPixel();
                window.Activate();
                halo.SetValue(ArtworkHalo.IsActiveProperty, true);
                PumpUntil(() => halo.Opacity > 0.5);
                halo.BeginAnimation(UIElement.OpacityProperty, null);
                halo.Opacity = 1;
                window.UpdateLayout();
                PumpDispatcher(TimeSpan.FromMilliseconds(100));
                Assert.Same(testImage, halo.Source);
                Assert.Equal(Colors.CornflowerBlue, ArtworkColorSample.ColorAt(testImage, .5, .5, halo.ActualWidth / halo.ActualHeight));
                SaveGlassVisual(window, "local-artwork-halo");
                var clipDetails = new List<string>();
                for (DependencyObject? ancestor = halo; ancestor is Visual visual; ancestor = VisualTreeHelper.GetParent(ancestor))
                    if (VisualTreeHelper.GetClip(visual) is { } clip) clipDetails.Add($"{ancestor.GetType().Name}:{clip.Bounds}");
                Assert.True(ExteriorPixel().Take(3).Sum(channel => (int)channel) > beforeHalo.Take(3).Sum(channel => (int)channel) + 8,
                    $"The artwork light must be visible outside the hovered card: before={string.Join(',', beforeHalo)}, after={string.Join(',', ExteriorPixel())}, opacity={halo.Opacity}, source={halo.Source?.GetType().Name}, size={halo.RenderSize}, origin={halo.TranslatePoint(new Point(), window)}, clips={string.Join(';', clipDetails)}.");
            }
            finally
            {
                if (haloSourceBinding is not null) System.Windows.Data.BindingOperations.SetBinding(halo, ArtworkHalo.SourceProperty, haloSourceBinding);
                else halo.SetValue(ArtworkHalo.SourceProperty, haloSource);
                if (haloActiveBinding is not null) System.Windows.Data.BindingOperations.SetBinding(halo, ArtworkHalo.IsActiveProperty, haloActiveBinding);
                else halo.SetValue(ArtworkHalo.IsActiveProperty, haloWasActive);
            }
            PumpDispatcher(TimeSpan.FromMilliseconds(320));
            nav.IsPaneOpen = paneBeforeHalo;
            var content = (Grid)page.FindName("DashboardContent");
            spatialScroll.ScrollToEnd(); window.UpdateLayout();
            byte[] ToolbarPixel()
            {
                var pixels = new RenderTargetBitmap((int)toolbarBackdrop.ActualWidth, (int)toolbarBackdrop.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                pixels.Render(toolbarBackdrop);
                var pixel = new byte[4];
                pixels.CopyPixels(new Int32Rect(pixels.PixelWidth / 2, pixels.PixelHeight / 2, 1, 1), pixel, 4, 0);
                return pixel;
            }
            PumpDispatcher(TimeSpan.FromMilliseconds(80));
            var toolbarBaseline = ToolbarPixel();
            var clearGlassProbe = new Canvas { Background = Brushes.Cyan, IsHitTestVisible = false };
            var glassStrip = new Border { Background = Brushes.Red, Width = content.ActualWidth, Height = toolbarBackdrop.ActualHeight + 8 };
            Canvas.SetTop(glassStrip, toolbarBackdrop.TranslatePoint(new Point(), content).Y - 4);
            clearGlassProbe.Children.Add(glassStrip);
            content.Children.Add(clearGlassProbe);
            try
            {
                window.UpdateLayout(); toolbarBackdrop.InvalidateVisual();
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                Assert.Equal(toolbarBaseline, ToolbarPixel()); // Matching launcher glass does not recapture page controls.
            }
            finally { content.Children.Remove(clearGlassProbe); spatialScroll.ScrollToTop(); toolbarBackdrop.InvalidateVisual(); }
            var backgroundChoice = new Steamy.Models.AppSettings { AmbientEnabled = false, BackgroundColor = "#314A72" };
            try
            {
                UiThemeService.ConfigureBackground(backgroundChoice);
                PumpDispatcher(TimeSpan.FromMilliseconds(410));
                Assert.Equal(new byte[] { 0x72, 0x4A, 0x31, 255 }, BodyPixel());
                PreviewEvent(UIElement.MouseEnterEvent); PumpDispatcher(TimeSpan.FromMilliseconds(410));
                Assert.Equal(new byte[] { 0x72, 0x4A, 0x31, 255 }, BodyPixel());
                SaveGlassVisual(window, "static-background");
                backgroundChoice.BackgroundColor = "#FFFFFF";
                UiThemeService.ConfigureBackground(backgroundChoice);
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                Assert.Equal(new byte[] { 255, 255, 255, 255 }, BodyPixel());
                Assert.Same(Brushes.Black, page.Resources["DashboardBodyForeground"]);
            }
            finally { PreviewEvent(UIElement.MouseLeaveEvent); UiThemeService.ConfigureBackground(new Steamy.Models.AppSettings()); }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_DASHBOARD_AMBIENT_ONLY") == "1") return;
            var glassLayer = Descendants<ShellBackdropView>(pane).Single();
            Assert.Equal(18, Assert.IsType<System.Windows.Media.Effects.BlurEffect>(glassLayer.Effect).Radius);

            search.DashboardSearch = true;
            model.RefreshSearchPreference();
            window.UpdateLayout();
            Assert.True(((FrameworkElement)page.FindName("DashboardSearch")).IsVisible);
            search.DashboardSearch = false;
            model.RefreshSearchPreference();

            var pill = (Grid)window.FindName("PersistentNavigation");
            var fixedNavigationScroll = (System.Windows.Controls.ScrollViewer)page.FindName("DashboardScroll");
            window.UpdateLayout();
            var navigationTop = pill.TranslatePoint(new Point(), window).Y;
            fixedNavigationScroll.ScrollToVerticalOffset(200);
            window.UpdateLayout();
            Assert.Equal(navigationTop, pill.TranslatePoint(new Point(), window).Y);
            Assert.True(pill.IsVisible);
            fixedNavigationScroll.ScrollToTop();
            window.UpdateLayout();
            var launcher = (FrameworkElement)nav.Template.FindName("MenuLauncher", nav);
            PumpDispatcher(TimeSpan.FromMilliseconds(350)); // Measure the drawer after its reveal transform settles.
            var drawerHeader = Assert.IsAssignableFrom<FrameworkElement>(nav.PaneHeader);
            var drawerHeaderCenter = drawerHeader.TranslatePoint(new Point(0, drawerHeader.ActualHeight / 2), window).Y;
            var launcherCenter = launcher.TranslatePoint(new Point(0, launcher.ActualHeight / 2), window).Y;
            Assert.InRange(launcherCenter - drawerHeaderCenter, -1, 1);
            var headerBounds = drawerHeader.TransformToAncestor(window).TransformBounds(new Rect(drawerHeader.RenderSize));
            var launcherBounds = launcher.TransformToAncestor(window).TransformBounds(new Rect(launcher.RenderSize));
            Assert.True(headerBounds.Right + 8 <= launcherBounds.Left);
            var windowContent = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            Assert.InRange(pill.TranslatePoint(new Point(pill.ActualWidth / 2, 0), windowContent).X - windowContent.ActualWidth / 2, -1, 1);
            Assert.InRange(launcher.ActualHeight - pill.ActualHeight, -1, 1);
            var highlight = (Border)window.FindName("NavigationHighlight");
            var pillItems = (Grid)highlight.Parent;
            var pillButtons = pillItems.Children.OfType<System.Windows.Controls.Button>().ToArray();
            Assert.True(PillMotion.GetIsEnabled(pillItems));
            Assert.False(PillMotion.GetFollowPointer(pillItems));
            var homeBounds = pillButtons[0].TranslatePoint(new Point(), pillItems);
            pillButtons[2].RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
            PumpDispatcher(TimeSpan.FromMilliseconds(340));
            Assert.InRange(((TranslateTransform)highlight.RenderTransform).X, -1, 1);
            Assert.Equal(homeBounds, pillButtons[0].TranslatePoint(new Point(), pillItems));
            pillItems.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
            PumpDispatcher(TimeSpan.FromMilliseconds(340));
            Assert.InRange(((TranslateTransform)highlight.RenderTransform).X, -1, 1);
            Assert.Equal(new Thickness(0), ((Border)page.FindName("DashboardNavigationChrome")).BorderThickness);
            var options = pill.ContextMenu!;
            options.PlacementTarget = pill;
            options.IsOpen = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            try
            {
                Assert.Same(model, options.DataContext);
                var playback = Assert.IsType<System.Windows.Controls.MenuItem>(options.Items[0]);
                Assert.Equal(model.SpotlightPlaybackLabel, playback.Header);
                Assert.NotNull(playback.Command);
                var paused = model.SpotlightPaused;
                playback.Command.Execute(null);
                Assert.NotEqual(paused, model.SpotlightPaused);
                playback.Command.Execute(null);
                Assert.Equal(paused, model.SpotlightPaused);
                Assert.NotNull(Assert.IsType<System.Windows.Controls.MenuItem>(options.Items[1]).Command);
            }
            finally { options.IsOpen = false; }

            var paging = (Grid)page.FindName("SpotlightPaging");
            Assert.Equal(2, Descendants<System.Windows.Controls.Button>(paging).Count());
            foreach (var arrow in Descendants<System.Windows.Controls.Button>(paging))
            {
                var center = arrow.TranslatePoint(new Point(arrow.ActualWidth / 2, arrow.ActualHeight / 2), (UIElement)page.FindName("DashboardHero"));
                Assert.InRange(center.Y, backdrop.HeroHeight / 2 - 1, backdrop.HeroHeight / 2 + 1);
            }
            Assert.InRange(((System.Windows.Controls.TextBlock)page.FindName("SpotlightTitle")).FontSize, 27, 36);

            var scroll = (ScrollViewer)page.FindName("DashboardScroll");
            scroll.ScrollToVerticalOffset(120);
            window.UpdateLayout();
            Assert.InRange(backdrop.ScrollOffset, scroll.VerticalOffset - 1, scroll.VerticalOffset + 1);
            scroll.ScrollToTop();

            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            window.UpdateLayout();
            Assert.False(nav.IsPaneOpen);
            Assert.Equal(Visibility.Collapsed, pane.Visibility);
            Assert.False(drawerHeader.IsVisible);
            Assert.InRange(launcher.TranslatePoint(new Point(), window).Y - pill.TranslatePoint(new Point(), window).Y, -1, 1);
            SaveGlassVisual(window, "sidebar-hidden-after-toggle");
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            window.UpdateLayout();
            Assert.True(nav.IsPaneOpen);

            var dismiss = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(nav.Template.FindName("PaneDismiss", nav));
            dismiss.IsChecked = false;
            window.UpdateLayout();
            Assert.False(nav.IsPaneOpen);
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyUpEvent });
            Assert.False(nav.IsPaneOpen);

            foreach (var theme in new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme);
                foreach (var item in nav.MenuItems.OfType<NavigationViewItem>().Where(item => item.MenuItems.Count > 0))
                    item.IsExpanded = true;
                foreach (var size in new[] { new Size(980, 620), new Size(1280, 800) })
                {
                    window.Width = size.Width; window.Height = size.Height;
                    nav.IsPaneOpen = true;
                    window.UpdateLayout();
                    Assert.InRange(pane.ActualWidth, 231, 233);
                    Assert.InRange(launcher.TranslatePoint(new Point(0, launcher.ActualHeight / 2), window).Y
                        - drawerHeader.TranslatePoint(new Point(0, drawerHeader.ActualHeight / 2), window).Y, -1, 1);
                    Assert.True(((FrameworkElement)nav.Template.FindName("PART_NavigationViewContentPresenter", nav)).ActualHeight > 0);
                    var footer = (FrameworkElement)nav.Template.FindName("PART_FooterMenuItemsItemsControl", nav);
                    var footerBounds = footer.TransformToAncestor(window).TransformBounds(new Rect(footer.RenderSize));
                    Assert.True(footerBounds.Top >= 0 && footerBounds.Bottom <= window.ActualHeight);
                    SaveGlassVisual(window, $"{theme}-drawer-{size.Width}x{size.Height}");
                    nav.IsPaneOpen = false;
                    window.UpdateLayout();
                    var pagePresenter = (FrameworkElement)nav.Template.FindName("PART_NavigationViewContentPresenter", nav);
                    Assert.InRange(pagePresenter.ActualWidth, window.ActualWidth - 24, window.ActualWidth);
                    if (model.RecentlyOpenedGames.Count == 8)
                    {
                        var currentHome = Descendants<DashboardPage>(window).Single();
                        var timeRefresh = model.RefreshPlayTimeAsync();
                        PumpUntil(() => timeRefresh.IsCompleted);
                        timeRefresh.GetAwaiter().GetResult();
                        var elden = Assert.Single(model.InstalledGames, entry => entry.Game.AppId == 1245620);
                        Assert.Equal("5h 20m", elden.PlayTimeLabel);
                        Assert.False(elden.IsPlaying);
                        Assert.InRange(currentHome.RecentCardHeight, 82, 102);
                        var strip = (FrameworkElement)currentHome.FindName("RecentlyOpenedStrip");
                        Assert.Contains(Descendants<System.Windows.Controls.TextBlock>(strip), text => text.Text == "5h 20m");
                        var recentScroll = (ScrollViewer)currentHome.FindName("RecentScroll");
                        // Compact square tiles may all fit; overflowing rows still reach their last tile.
                        recentScroll.ScrollToRightEnd();
                        window.UpdateLayout();
                        Assert.InRange(recentScroll.HorizontalOffset, recentScroll.ScrollableWidth - 1, recentScroll.ScrollableWidth + 1);
                        recentScroll.ScrollToLeftEnd();
                        window.UpdateLayout();
                    }
                    foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
                        SaveGlassVisual(window, $"{theme}-dashboard-{size.Width}x{size.Height}-{scale:0.0}x", scale);
                }
                window.WindowState = WindowState.Maximized;
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                foreach (var scale in new[] { 1d, 1.25, 1.5, 2d }) SaveGlassVisual(window, $"{theme}-dashboard-maximized-{scale:0.##}x", scale);
                window.WindowState = WindowState.Normal;
                window.Width = 1280; window.Height = 800;
                foreach (var route in new[] { typeof(LibraryPage), typeof(DownloadsPage), typeof(SettingsPage), typeof(BetterSteamToolsPage), typeof(SteamlessPage), typeof(DenuvoActivationPage), typeof(CreamApiPage), typeof(FamilySharePage), typeof(GoldbergPage), typeof(GameFixesPage), typeof(DepotDownloaderPage) })
                {
                    Assert.True(nav.Navigate(route));
                    Assert.False(nav.IsPaneOpen);
                    PumpDispatcher(TimeSpan.FromMilliseconds(30));
                    window.UpdateLayout();
                    Assert.Equal(0, backdrop.ScrollOffset);
                    Assert.True(((ShellBackdropView)window.FindName("WorkspaceBackdrop")).IsPageBackdrop);
                    Assert.True(((FrameworkElement)window.FindName("PersistentNavigation")).IsVisible);
                    SaveGlassVisual(window, $"{theme}-{route.Name}");
                }
                Assert.True(nav.Navigate(typeof(DashboardPage)));
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
            }

            // Capture the requested live-feed titles, with the carousel held still.
            var wasPaused = model.SpotlightPaused;
            if (!wasPaused) model.ToggleSpotlightCommand.Execute(null);
            try
            {
                Assert.NotEmpty(model.DiscoverGames);
                foreach (var selectedFeature in model.DiscoverGames.Take(4).ToArray())
                {
                    var id = selectedFeature.Game.AppId;
                    model.SelectFeaturedCommand.Execute(selectedFeature);
                    if (FixtureArtwork.Read(id, "hero") is not null) PumpUntil(() => selectedFeature.HeroArtwork is not null);
                    if (FixtureArtwork.Read(id, "logo") is not null)
                    {
                        PumpUntil(() => selectedFeature.LogoArtwork is not null);
                        var home = Descendants<DashboardPage>(window).Single();
                        Assert.Equal(Visibility.Collapsed, ((System.Windows.Controls.TextBlock)home.FindName("SpotlightTitle")).Visibility);
                        Assert.Same(selectedFeature.LogoArtwork, ((System.Windows.Controls.Image)home.FindName("SpotlightLogo")).Source);
                        Assert.True(((System.Windows.Controls.Image)home.FindName("SpotlightLogo")).IsVisible);
                        Assert.Equal(selectedFeature.Metadata.ComingSoon ? "Release details" : "View game", selectedFeature.FeatureActionLabel);
                        if (selectedFeature.Metadata.ComingSoon)
                            Assert.Equal(Visibility.Collapsed, ((System.Windows.Controls.Button)home.FindName("SpotlightViewButton")).Visibility);
                    }
                    Assert.DoesNotContain(model.NewGames, feature => !feature.Metadata.ComingSoon);
                    foreach (var theme in new[] { "Dark", "Light" })
                    {
                        UiThemeService.Apply(theme);
                        window.UpdateLayout();
                        if (selectedFeature.HeroArtwork is not null) Assert.True(ReferenceEquals(Brushes.White, window.WindowTitleBar.ButtonsForeground) || ReferenceEquals(Brushes.Black, window.WindowTitleBar.ButtonsForeground));
                        SaveGlassVisual(window, $"featured-{id}-{theme}");
                        if (id == 3751950 && theme == "Dark")
                        {
                            nav.IsPaneOpen = true;
                            window.UpdateLayout();
                            SaveGlassVisual(window, "featured-drawer-Black-Flag");
                            nav.IsPaneOpen = false;
                            var home = Descendants<DashboardPage>(window).Single();
                            var homeScroll = (ScrollViewer)home.FindName("DashboardScroll");
                            homeScroll.ScrollToVerticalOffset(470);
                            window.UpdateLayout();
                            Assert.NotNull(homeAmbient.AmbientBitmap);
                            SaveGlassVisual(window, "upcoming-releases");
                            homeScroll.ScrollToTop();
                            window.UpdateLayout();
                            var showAll = (System.Windows.Controls.Button)home.FindName("ShowAllGamesButton");
                            Assert.NotNull(showAll.Command);
                            showAll.Command.Execute(null);
                            PumpDispatcher(TimeSpan.FromMilliseconds(340));
                            window.UpdateLayout();
                            Assert.True(model.IsInstalledLibraryOpen);
                            Assert.False(homeScroll.IsEnabled);
                            Assert.True(((FrameworkElement)home.FindName("InstalledLibraryOverlay")).IsVisible);
                            var panel = (FrameworkElement)home.FindName("InstalledLibraryPanel");
                            var bounds = panel.TranslatePoint(new Point(), home);
                            Assert.InRange(bounds.Y, 0, home.ActualHeight - panel.ActualHeight + 1);
                            Assert.Equal(model.AllInstalledGames.Count, ((ItemsControl)home.FindName("AllInstalledGamesGrid")).Items.Count);
                            SaveGlassVisual(window, "installed-library");
                            var store = provider.GetRequiredService<IAppDataStore>();
                            var installedBefore = model.AllInstalledGames.Select(entry => entry.Game.AppId).ToHashSet();
                            var extraGames = Enumerable.Range(800_000, 1000).Select(appId => new Steamy.Models.Game
                            {
                                AppId = appId, Name = $"Virtualization fixture {appId}", InstallState = Steamy.Models.GameInstallState.Installed
                            }).ToArray();
                            try
                            {
                                foreach (var game in extraGames) store.Games.Add(game);
                                PumpUntil(() => model.FilteredInstalledGames.Count == installedBefore.Count + extraGames.Length);
                                window.UpdateLayout();
                                var grid = (ItemsControl)home.FindName("AllInstalledGamesGrid");
                                var virtualPanel = Descendants<VirtualizingGamePanel>(grid).Single();
                                Assert.InRange(virtualPanel.RealizedCount, 1, 40);
                                Assert.NotNull(virtualPanel.ScrollOwner);
                                virtualPanel.ScrollOwner.ScrollToBottom();
                                window.UpdateLayout();
                                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                                Assert.True(virtualPanel.VerticalOffset > 0);
                                Assert.InRange(virtualPanel.RealizedCount, 1, 40);
                                Assert.NotNull(grid.ItemContainerGenerator.ContainerFromIndex(grid.Items.Count - 1));
                                // A title created by this test, independent of optional artwork fixtures.
                                model.InstalledSearchText = extraGames[521].Name;
                                window.UpdateLayout();
                                Assert.Single(model.FilteredInstalledGames);
                                Assert.InRange(virtualPanel.RealizedCount, 1, 3);
                                model.InstalledSearchText = "missing installed title";
                                window.UpdateLayout();
                                Assert.Equal(0, virtualPanel.RealizedCount);
                            }
                            finally
                            {
                                model.InstalledSearchText = "";
                                foreach (var game in extraGames) store.Games.Remove(game);
                                PumpUntil(() => installedBefore.SetEquals(model.AllInstalledGames.Select(entry => entry.Game.AppId)));
                            }
                            ((UIElement)home.FindName("InstalledLibraryOverlay")).RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                                PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Escape)
                                { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
                            Assert.False(model.IsInstalledLibraryOpen);
                            Assert.True(homeScroll.IsEnabled);
                        }
                    }
                }
            }
            finally { if (!wasPaused) model.ToggleSpotlightCommand.Execute(null); }

            var tilePage = Descendants<DashboardPage>(window).Single();
            MoveCursorAway(0, 0);
            ((System.Windows.Controls.Button)tilePage.FindName("ShowAllGamesButton")).Focus();
            PumpDispatcher(TimeSpan.FromMilliseconds(560));
            var tile = Descendants<System.Windows.Controls.Button>((ItemsControl)tilePage.FindName("RecentlyOpenedStrip")).First();
            var neighbor = Descendants<System.Windows.Controls.Button>((ItemsControl)tilePage.FindName("RecentlyOpenedStrip")).Skip(1).First();
            var neighborBefore = neighbor.TranslatePoint(new Point(), tilePage).X;
            var details = Assert.IsAssignableFrom<FrameworkElement>(tile.Template.FindName("HoverDetails", tile));
            var square = Assert.IsAssignableFrom<FrameworkElement>(tile.Template.FindName("MotionSurface", tile));
            Assert.InRange(square.ActualWidth, square.ActualHeight - 1, square.ActualHeight + 1);
            window.Activate();
            MoveCursorAway(0, 0);
            MotionPreferences.Configure(true);
            Assert.True(tile.Focus());
            window.UpdateLayout();
            Assert.Equal(1, details.Opacity); // Reduced motion still exposes playtime to keyboard users.
            Assert.InRange(tile.ActualWidth, 247, 249);
            Assert.True(neighbor.TranslatePoint(new Point(), tilePage).X > neighborBefore + 140);
            ((System.Windows.Controls.Button)tilePage.FindName("ShowAllGamesButton")).Focus();
            Assert.Equal(0, details.Opacity);
            window.UpdateLayout();
            Assert.InRange(neighbor.TranslatePoint(new Point(), tilePage).X, neighborBefore - 1, neighborBefore + 1);
            MoveCursorAway(0, 0);
            MotionPreferences.Configure(false);
            tile.Focus();
            var intermediateSampled = false;
            var sampleLimit = System.Diagnostics.Stopwatch.StartNew();
            while (sampleLimit.ElapsedMilliseconds < 800)
            {
                PumpDispatcher(TimeSpan.FromMilliseconds(10));
                window.UpdateLayout();
                if (tile.ActualWidth > square.ActualHeight + 1 && tile.ActualWidth < 247)
                {
                    intermediateSampled = true;
                    break;
                }
                if (tile.ActualWidth >= 247) break;
            }
            Assert.True(intermediateSampled || (tile.ActualWidth > square.ActualHeight + 1 && tile.ActualWidth < 247),
                $"The installed tile must visibly pass through intermediate widths instead of snapping open. Current width={tile.ActualWidth}, square height={square.ActualHeight}.");
            var openingWidth = tile.ActualWidth;
            ((System.Windows.Controls.Button)tilePage.FindName("ShowAllGamesButton")).Focus();
            window.UpdateLayout();
            Assert.InRange(tile.ActualWidth, openingWidth - 2, openingWidth + 2);
            PumpUntil(() => Math.Abs(tile.ActualWidth - square.ActualHeight) <= 1.5, TimeSpan.FromSeconds(1));
            window.UpdateLayout();
            Assert.InRange(tile.ActualWidth, square.ActualHeight - 1.5, square.ActualHeight + 1.5);
            tile.Focus();
            PumpDispatcher(TimeSpan.FromMilliseconds(560));
            Assert.Equal(1, details.Opacity);
            window.UpdateLayout();
            Assert.InRange(tile.ActualWidth, 247, 249); // Final width must survive removal of animation clocks.
            SaveGlassVisual(window, "installed-tile-hover");
            ((System.Windows.Controls.Button)tilePage.FindName("ShowAllGamesButton")).Focus();

            window.ToggleToolsMenu();
            window.UpdateLayout();
            Assert.True(window.IsToolsMenuOpen);
            Assert.True(((FrameworkElement)window.FindName("WorkspaceLayer")).IsEnabled);
            Assert.Null(((FrameworkElement)window.FindName("WorkspaceLayer")).Effect);
            Assert.NotNull(window.FindName("ToolsMenuLayer"));
            SaveGlassVisual(window, "tools-direct-selection");
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!,0,System.Windows.Input.Key.Escape) {RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent});
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!,0,System.Windows.Input.Key.Escape) {RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyUpEvent});
            Assert.False(window.IsToolsMenuOpen);
            Assert.True(nav.Navigate(typeof(LibraryPage)));
            window.UpdateLayout();
            Assert.True(((FrameworkElement)window.FindName("PersistentNavigation")).IsVisible);
            ((System.Windows.Controls.Button)window.FindName("ShellSettingsButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => Descendants<SettingsPage>(window).Any(p => p.IsVisible));
            Assert.Equal(6, PillMotion.GetSelectedIndex((Grid)window.FindName("ShellPillItems")));
            Assert.True(nav.Navigate(typeof(LibraryPage)));
            PumpUntil(() => Descendants<LibraryPage>(window).Any(p => p.IsVisible));
            window.ToggleToolsMenu(fixes:true);
            ((System.Windows.Controls.Button)window.FindName("GameFixesMenuButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => Descendants<GameFixesPage>(window).Any(p => p.IsVisible));
            Assert.True(nav.Navigate(typeof(DashboardPage)));
            PumpUntil(() => Descendants<DashboardPage>(window).Any(p => p.IsVisible));
            window.UpdateLayout();

            nav.IsPaneOpen = true;
            window.UpdateLayout();
            MotionPreferences.Configure(true);
            Assert.Null(glassLayer.Effect);
            Assert.Equal(255, Assert.IsType<SolidColorBrush>(app.Resources["SidebarBackgroundBrush"]).Color.A);
            Assert.Equal(255, Assert.IsType<SolidColorBrush>(app.Resources["GlassSurfaceBrush"]).Color.A);
            SaveGlassVisual(window, "reduced-effects");
            MotionPreferences.Configure(false);
            UiThemeService.ApplyGlassPolicy(highContrast: true);
            window.UpdateLayout();
            Assert.Null(glassLayer.Effect);
            Assert.Same(SystemColors.WindowTextBrush, ((System.Windows.Controls.TextBlock)Descendants<DashboardPage>(window).Single().FindName("SpotlightTitle")).Foreground);
            Assert.Same(SystemColors.WindowTextBrush, window.WindowTitleBar.Foreground);
            Assert.Same(SystemColors.WindowTextBrush, window.WindowTitleBar.ButtonsForeground);
            Assert.Same(SystemColors.WindowBrush, app.Resources["SidebarBackgroundBrush"]);
            Assert.Same(SystemColors.HighlightTextBrush, app.Resources["SidebarSelectedForegroundBrush"]);
            SaveGlassVisual(window, "high-contrast");
            UiThemeService.ApplyGlassPolicy(highContrast: false);
            Assert.IsType<LinearGradientBrush>(app.Resources["SidebarBackgroundBrush"]);
            Assert.IsType<DrawingBrush>(app.Resources["WorkspaceBackgroundBrush"]);
        }
        finally
        {
            if (originalFeature is not null) model.SelectFeaturedCommand.Execute(originalFeature);
            if (model.SpotlightPaused != spotlightPaused) model.ToggleSpotlightCommand.Execute(null);
            search.DashboardSearch = previousSearch;
            model.RefreshSearchPreference();
            MotionPreferences.Configure(effects);
            UiThemeService.Apply("Dark");
            window.Close();
            app.MainWindow = previousWindow;
        }
    }

    private static void CheckMinimizedDashboard(MainWindow window, DashboardViewModel model)
    {
        var page = Descendants<Steamy.Pages.DashboardPage>(window).Single();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var paused = model.SpotlightPaused;
        using var gamepad = new Steamy.Controls.GamepadNavigation(window,
            App.Services.GetRequiredService<IControllerBackend>(), App.Services.GetRequiredService<ISettingsService>(), pollHardware: false);
        try
        {
            if (paused) model.ToggleSpotlightCommand.Execute(null);
            var feature = model.FeaturedGame;
            window.WindowState = WindowState.Minimized; PumpDispatcher(TimeSpan.FromMilliseconds(60));
            // WPF can still report IsActive while minimized. A delayed activation
            // callback must never restart timers, advance the scene or poll input.
            page.GetType().GetMethod("CarouselActivated", flags)!.Invoke(page, new object?[] { window, EventArgs.Empty });
            typeof(Steamy.Controls.GamepadNavigation).GetMethod("Activated", flags)!.Invoke(gamepad, new object?[] { window, EventArgs.Empty });
            foreach (var name in new[] { "_spotlightTimer", "_countdownTimer" })
                Assert.False(((System.Windows.Threading.DispatcherTimer)page.GetType().GetField(name, flags)!.GetValue(page)!).IsEnabled);
            Assert.False(((System.Windows.Threading.DispatcherTimer)model.GetType().GetField("_statsTimer", flags)!.GetValue(model)!).IsEnabled);
            Assert.False((bool)typeof(Steamy.Controls.GamepadNavigation).GetField("_active", flags)!.GetValue(gamepad)!);
            page.GetType().GetField("_slideElapsed", flags)!.SetValue(page, TimeSpan.FromSeconds(9));
            page.GetType().GetMethod("CarouselTick", flags)!.Invoke(page, new object?[] { window, EventArgs.Empty });
            Assert.Same(feature, model.FeaturedGame);
        }
        finally
        {
            if (model.SpotlightPaused != paused) model.ToggleSpotlightCommand.Execute(null);
            page.GetType().GetField("_slideElapsed", flags)!.SetValue(page, TimeSpan.Zero);
            window.WindowState = WindowState.Normal;
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            PumpUntil(() => window.WindowState == WindowState.Normal && !FrameIsIconic(handle));
        }
    }

    private static double ChromeLuminance(Color color)
    {
        static double Linear(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    private static void SaveGlassVisual(MainWindow window, string name, double scale = 1)
    {
        var folder = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        // Route layout is asserted above; exporting the same large page twice adds no coverage.
        if (name.EndsWith("Page", StringComparison.Ordinal) && !name.EndsWith("SettingsPage", StringComparison.Ordinal)) return;
        // Fast follow-up captures keep all interaction/layout assertions, while reusing
        // the full DPI matrix already rendered by the default capture mode.
        if (Environment.GetEnvironmentVariable("STEAMY_UI_QUICK_CAPTURE") == "1"
            && (scale != 1 || name.Contains("maximized") || (name.Contains("dashboard") && !name.Contains("1280x800"))
                || name.StartsWith("featured-", StringComparison.Ordinal))) return;
        if (MotionPreferences.AnimationsEnabled) PumpDispatcher(TimeSpan.FromMilliseconds(380));
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale), (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(content);
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(folder, "glass-" + name + ".png"));
        encoder.Save(output);
    }
}
