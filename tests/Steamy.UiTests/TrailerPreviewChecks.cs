using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Pages;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckTrailerOffscreen(IServiceProvider provider)
    {
        var autoplay=MotionPreferences.TrailerPreviewsEnabled;
        var effects=MotionPreferences.ReduceEffects;
        MotionPreferences.Configure(false);
        MotionPreferences.ConfigureTrailers(true);
        var preview=new TrailerPreview { AppId=42, Width=480, Height=270 };
        var card=new Button { Content=preview, Width=480, Height=270 };
        // A hidden rendering host supplies WPF's media pipeline without showing a window.
        using var host=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Trailer offscreen QA")
        { Width=480, Height=270, WindowStyle=unchecked((int)0x80000000) });
        try
        {
            host.RootVisual=card;
            card.Measure(new Size(480,270)); card.Arrange(new Rect(0,0,480,270)); card.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(200));
            Assert.True(preview.IsLoaded);
            preview.BeginPreview();
            PumpDispatcher(TimeSpan.FromSeconds(7));
            Assert.NotNull(preview.Player);
            Assert.True(preview.Player.NaturalVideoWidth>0,"The native decoder must open the cached trailer.");
            Assert.True(preview.Player.Position.TotalSeconds>3,"Trailer playback must advance past its opening frame.");
            var sound = preview.Children.OfType<Button>().Single();
            Assert.Equal("Unmute", sound.Content);
            Assert.Equal(Visibility.Visible, sound.Visibility);
            Assert.True(preview.Player.IsMuted);
            sound.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, sound));
            Assert.False(preview.Player.IsMuted);
            PumpDispatcher(TimeSpan.FromMilliseconds(250));
            var audio = preview.Children.OfType<MediaElement>().Single(element => element != preview.Player);
            Assert.False(audio.IsMuted);
            Assert.Equal("Mute", sound.Content);
            sound.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, sound));
            Assert.True(preview.Player.IsMuted);
            Assert.True(audio.IsMuted);
            Assert.Equal("Unmute", sound.Content);
            Assert.Equal(480,preview.ActualWidth);
            Assert.Equal(270,preview.ActualHeight);
            preview.StopPreview();
            Assert.Null(preview.Player);
            Assert.Equal(Visibility.Collapsed, sound.Visibility);
        }
        finally
        {
            preview.StopPreview(); host.RootVisual=null;
            MotionPreferences.Configure(effects); MotionPreferences.ConfigureTrailers(autoplay);
        }
    }

    private sealed class FixtureTrailers : ITrailerPreviewService
    {
        public int Requests;
        public bool Missing;
        public TaskCompletionSource<Uri?>? Pending;
        public Task<Uri?> GetAudioPreviewAsync(int appId, CancellationToken cancellationToken = default) => GetPreviewAsync(appId, cancellationToken);
        public Task<Uri?> GetPreviewAsync(int appId, CancellationToken cancellationToken = default)
        {
            Requests++;
            if (Pending is { } pending) return pending.Task.WaitAsync(cancellationToken);
            var folder = Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK");
            var path = folder is null ? null : Path.Combine(folder, "trailer-preview.mp4");
            return Task.FromResult(!Missing && path is not null && File.Exists(path) ? new Uri(path) : null);
        }
    }

    private static void CheckTrailerPreviews(IServiceProvider provider)
    {
        var fixture = Assert.IsType<FixtureTrailers>(provider.GetRequiredService<ITrailerPreviewService>());
        var previous = Application.Current.MainWindow;
        var effects = MotionPreferences.ReduceEffects;
        var autoplay = MotionPreferences.TrailerPreviewsEnabled;
        DashboardPage? isolatedPage = null;
        MotionPreferences.ConfigureTrailers(true);
        var window = new MainWindow { Width = 1280, Height = 900, WindowState = WindowState.Normal, ShowInTaskbar = false };
        Application.Current.MainWindow = window;
        window.Show(); window.Activate();
        MotionPreferences.ConfigureTrailers(false);
        var caption = window.PointToScreen(new Point(200, 16));
        MoveCursorAway((int)caption.X, (int)caption.Y);
        PumpDispatcher(TimeSpan.FromMilliseconds(400));
        try
        {
            var page = Descendants<DashboardPage>(window).Single();
            isolatedPage = page;
            page.IsHitTestVisible = false; // Physical desktop input cannot start unrelated card requests.
            var scroll = (ScrollViewer)page.FindName("DashboardScroll");
            var upcoming = (ItemsControl)page.FindName("UpcomingGameGrid");
            ((FrameworkElement)upcoming.ItemContainerGenerator.ContainerFromIndex(0)).BringIntoView();
            window.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(120));
            foreach (var cardPreview in Descendants<TrailerPreview>(page)) cardPreview.StopPreview();
            MotionPreferences.ConfigureTrailers(true);
            var preview = Descendants<TrailerPreview>((DependencyObject)upcoming.ItemContainerGenerator.ContainerFromIndex(0)).Single();
            Assert.Equal(ImagePriority.Visible, ArtworkViewport.Priority(preview));
            if (Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK") is not null)
                Assert.NotNull(preview.Poster ?? preview.Children.OfType<Image>().Single().Source);
            var before = fixture.Requests;
            preview.BeginPreview();
            PumpDispatcher(TimeSpan.FromMilliseconds(60));
            preview.StopPreview();
            PumpDispatcher(TimeSpan.FromMilliseconds(400));
            Assert.Equal(before, fixture.Requests); // Passing across a card never starts a download.

            if (MotionPreferences.AnimationsEnabled)
            {
                // Cancellation and missing-media responses need a loaded visual,
                // not desktop activation. An isolated host prevents unrelated
                // foreground changes from cancelling these requests mid-assertion.
                using (var lifecycleHost = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Trailer request lifecycle QA")
                    { Width = 480, Height = 270, WindowStyle = unchecked((int)0x80000000) }))
                {
                    var lifecyclePreview = new TrailerPreview { AppId = preview.AppId, Width = 480, Height = 270 };
                    var lifecycleCard = new Button { Content = lifecyclePreview, Width = 480, Height = 270 };
                    lifecycleHost.RootVisual = lifecycleCard;
                    lifecycleCard.Measure(new Size(480, 270)); lifecycleCard.Arrange(new Rect(0, 0, 480, 270)); lifecycleCard.UpdateLayout();
                    PumpDispatcher(TimeSpan.FromMilliseconds(100));
                    fixture.Pending = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var pendingRequests = fixture.Requests;
                    lifecyclePreview.BeginPreview();
                    PumpUntil(() => fixture.Requests > pendingRequests);
                    lifecyclePreview.StopPreview();
                    fixture.Pending.SetResult(new Uri("https://video.akamai.steamstatic.com/late.mp4"));
                    PumpDispatcher(TimeSpan.FromMilliseconds(100));
                    Assert.Null(lifecyclePreview.Player); // Late responses cannot resurrect stopped media.
                    fixture.Pending = null;
                    fixture.Missing = true;
                    var missingRequests = fixture.Requests;
                    lifecyclePreview.BeginPreview();
                    PumpUntil(() => fixture.Requests > missingRequests);
                    Assert.Null(lifecyclePreview.Player);
                    lifecyclePreview.StopPreview(); fixture.Missing = false;
                    lifecycleHost.RootVisual = null;
                    PumpDispatcher(TimeSpan.FromMilliseconds(40));
                }

                var folder = Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK");
                if (folder is not null && File.Exists(Path.Combine(folder, "trailer-preview.mp4")))
                {
                    window.Activate();
                    preview.BeginPreview();
                    try { PumpUntil(() => preview.Player?.NaturalVideoWidth > 0, TimeSpan.FromSeconds(20)); }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException($"Trailer: active={window.IsActive}, loaded={preview.IsLoaded}, visible={preview.IsVisible}, motion={MotionPreferences.AnimationsEnabled}, enabled={MotionPreferences.TrailerPreviewsEnabled}, player={preview.Player is not null}, source={preview.Player?.Source}", error);
                    }
                    var player = preview.Player!;
                    Assert.True(player.IsMuted);
                    Assert.Equal(0, player.Volume);
                    Assert.Equal(1, player.SpeedRatio);
                    PumpDispatcher(TimeSpan.FromMilliseconds(600));
                    Assert.True(preview.IsPlaying);
                    Assert.InRange(Math.Abs(player.ActualWidth - preview.ActualWidth), 0, 1);
                    Assert.InRange(Math.Abs(player.ActualHeight - preview.ActualHeight), 0, 1);
                    var originalWidth = window.Width;
                    window.Width = 980; window.UpdateLayout();
                    PumpDispatcher(TimeSpan.FromMilliseconds(100));
                    Assert.InRange(Math.Abs(player.ActualWidth - preview.ActualWidth), 0, 1);
                    Assert.InRange(Math.Abs(player.ActualHeight - preview.ActualHeight), 0, 1);
                    window.Width = originalWidth; window.UpdateLayout();
                    SaveGlassVisual(window, "video-viewport");
                    var position = player.Position;
                    var elapsed = System.Diagnostics.Stopwatch.StartNew();
                    PumpDispatcher(TimeSpan.FromMilliseconds(1200));
                    Assert.True(player.Position > position, "The hover preview must advance actual decoded video frames.");
                    Assert.InRange((player.Position - position).TotalSeconds / elapsed.Elapsed.TotalSeconds, 0.65, 1.4);
                    DependencyObject owner = preview;
                    while (owner is not Button) owner = System.Windows.Media.VisualTreeHelper.GetParent(owner);
                    ((Button)owner).RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
                    Assert.Null(preview.Player);
                    window.Activate(); // Reopening media obeys the same active-window requirement as the initial hover.
                    preview.BeginPreview();
                    try { PumpUntil(() => preview.IsPlaying, TimeSpan.FromSeconds(20)); }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException($"Trailer reopen: active={window.IsActive}, loaded={preview.IsLoaded}, visible={preview.IsVisible}, motion={MotionPreferences.AnimationsEnabled}, enabled={MotionPreferences.TrailerPreviewsEnabled}, player={preview.Player is not null}, source={preview.Player?.Source}", error);
                    }
                    MotionPreferences.ConfigureTrailers(false);
                    Assert.Null(preview.Player); // Switching off restores the poster immediately.
                }
            }

            MotionPreferences.ConfigureTrailers(false);
            var disabledRequests = fixture.Requests;
            preview.BeginPreview();
            PumpDispatcher(TimeSpan.FromMilliseconds(500));
            Assert.Equal(disabledRequests, fixture.Requests);
            Assert.Null(preview.Player);
            Assert.True(window.RootNavigationView.Navigate(typeof(SettingsPage)));
            PumpUntil(() => Descendants<SettingsPage>(window).Any(page => page.IsLoaded));
            var settingsPage = Descendants<SettingsPage>(window).Single();
            var trackingToggle = (Wpf.Ui.Controls.ToggleSwitch)settingsPage.FindName("PlayTimeTrackingToggle");
            trackingToggle.IsChecked = false;
            Assert.False(provider.GetRequiredService<Steamy.ViewModels.SettingsViewModel>().Settings.TrackPlayTime);
            trackingToggle.IsChecked = true;
            Assert.True(provider.GetRequiredService<Steamy.ViewModels.SettingsViewModel>().Settings.TrackPlayTime);
            var toggle = (Wpf.Ui.Controls.ToggleSwitch)settingsPage.FindName("TrailerPreviewsToggle");
            toggle.IsChecked = false;
            Assert.False(provider.GetRequiredService<Steamy.ViewModels.SettingsViewModel>().Settings.AutoplayTrailers);
            Assert.False(MotionPreferences.TrailerPreviewsEnabled);
            toggle.IsChecked = true;
            Assert.True(MotionPreferences.TrailerPreviewsEnabled);

            MotionPreferences.Configure(true);
            var requests = fixture.Requests;
            preview.BeginPreview();
            PumpDispatcher(TimeSpan.FromMilliseconds(500));
            Assert.Null(preview.Player);
            Assert.Equal(requests, fixture.Requests);
            Assert.True(window.RootNavigationView.Navigate(typeof(LibraryPage)));
            Assert.Null(preview.Player);
        }
        finally
        {
            fixture.Pending = null;
            fixture.Missing = false;
            if (isolatedPage is not null) isolatedPage.IsHitTestVisible = true;
            MotionPreferences.Configure(effects);
            MotionPreferences.ConfigureTrailers(autoplay);
            window.Close();
            Application.Current.MainWindow = previous;
        }
    }
}
