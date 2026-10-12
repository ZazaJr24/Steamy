using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckDownloadAmbient(IServiceProvider provider, AppDataStore store, FixtureArtwork artwork)
    {
        var previous = Application.Current.MainWindow;
        var originalJobs = store.Downloads.ToArray();
        store.Downloads.Clear();
        var active = new DownloadJob { AppId = 1808500, GameName = "ARC Raiders", State = DownloadJobState.Downloading,
            Progress = 41, HasWholeGameProgress = true, TransferredBytes = 967_100_000, TransferTotalBytes = 1_800_000_000,
            BytesPerSecond = 11_475_000, EtaSeconds = 29 * 60 + 36, Status = "Downloading selected depot files" };
        var queued = new DownloadJob { AppId = 1091500, GameName = "Cyberpunk 2077", State = DownloadJobState.Queued, QueuePosition = 2, TotalSize = "64 GB" };
        store.Downloads.Add(active); store.Downloads.Add(queued);
        var now = Environment.TickCount64 / 1000d;
        for (var i = 0; i < 50; i++) active.RecordTransferRate((.7 + Math.Sin(i / 7d) * .2) * 11_475_000, now - 50 + i);
        UiThemeService.Apply("Dark");
        var window = new MainWindow { Width = 1280, Height = 800, WindowState = WindowState.Normal, ShowInTaskbar = false };
        Application.Current.MainWindow = window;
        window.Show(); MoveCursorAway(0, 0); PumpDispatcher(TimeSpan.FromMilliseconds(250));
        try
        {
            provider.GetRequiredService<INavigationService>().Navigate<DownloadsPage>();
            PumpUntil(() => Descendants<DownloadsPage>(window).Any());
            var page = Descendants<DownloadsPage>(window).Single();
            var model = (DownloadsViewModel)page.DataContext;
            PumpUntil(() => model.HeroArtwork is not null);
            Keyboard.ClearFocus(); PumpDispatcher(TimeSpan.FromMilliseconds(300));
            var a = (Border)page.FindName("DownloadAmbientA"); var b = (Border)page.FindName("DownloadAmbientB");
            Assert.Equal(Color.FromRgb(16,19,25), ((SolidColorBrush)page.Resources["DeckBackground"]).Color);
            PumpUntil(() => a.Opacity > .99 || b.Opacity > .99);
            var activeBrush = a.Opacity > .99 ? a.Background : b.Background;
            Assert.IsType<DrawingBrush>(activeBrush);
            Assert.IsType<DrawingBrush>(window.FindResource("WorkspaceBackgroundBrush"));
            Assert.Equal(Visibility.Collapsed, ((Border)page.FindName("DeckFooter")).Visibility);
            CheckDownloadsLayout(page);
            SaveGameDetailsVisual(window, "downloads-persistent-ambient");
            var row = Descendants<Border>(page).Single(border => ReferenceEquals(border.DataContext, queued) && ReferenceEquals(border.Style, page.Resources["DeckRow"]));
            var before = artwork.HeroRequests.GetValueOrDefault(queued.AppId);
            row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            PumpDispatcher(TimeSpan.FromMilliseconds(300));
            Assert.Same(activeBrush, a.Opacity > .99 ? a.Background : b.Background);
            Assert.Equal(before, artwork.HeroRequests.GetValueOrDefault(queued.AppId));
            Assert.Same(active, model.HeroJob);
            row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            Keyboard.ClearFocus(); PumpDispatcher(TimeSpan.FromMilliseconds(300));
            Assert.Same(activeBrush, a.Opacity > .99 ? a.Background : b.Background);
            Assert.True(((Button)page.FindName("DownloadOptionsButton")).Focus());
            PumpDispatcher(TimeSpan.FromMilliseconds(300));
            Assert.Same(activeBrush, a.Opacity > .99 ? a.Background : b.Background);
            var oldMotion = MotionPreferences.ReduceEffects;
            MotionPreferences.Configure(true);
            var preview = page.PreviewDownloadAmbientAsync(queued); PumpUntil(() => preview.IsCompleted); preview.GetAwaiter().GetResult();
            Assert.True(a.Opacity == 1 || b.Opacity == 1);
            UiThemeService.ApplyGlassPolicy(true);
            Assert.Equal(0, a.Opacity); Assert.Equal(0, b.Opacity);
            Assert.Same(SystemColors.WindowBrush, page.Resources["DeckBackground"]);
            UiThemeService.ApplyGlassPolicy(false);
            Assert.Equal(Color.FromRgb(16,19,25), ((SolidColorBrush)page.Resources["DeckBackground"]).Color);
            Assert.True(a.Opacity == 1 || b.Opacity == 1);
            page.PreviewDownloadAmbientAsync(null).GetAwaiter().GetResult();
            MotionPreferences.Configure(oldMotion);

            var late = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken staleToken = default;
            artwork.HeroOverride = (id, token) => { staleToken = token; return late.Task; };
            var stalePreview = page.PreviewDownloadAmbientAsync(new DownloadJob { AppId = 999000, GameName = "Slow artwork" });
            var current = page.PreviewDownloadAmbientAsync(queued); current.GetAwaiter().GetResult();
            Assert.True(staleToken.IsCancellationRequested);
            PumpUntil(() => a.Opacity > .99 || b.Opacity > .99);
            var currentBrush = a.Opacity > .99 ? a.Background : b.Background;
            late.SetResult(FixtureArtwork.Read(active.AppId, "hero"));
            PumpUntil(() => stalePreview.IsCompleted); stalePreview.GetAwaiter().GetResult();
            Assert.Same(currentBrush, a.Opacity > .99 ? a.Background : b.Background);
            artwork.HeroOverride = (_, _) => Task.FromResult<BitmapImage?>(null);
            var missing = page.PreviewDownloadAmbientAsync(new DownloadJob { AppId = 999001, GameName = "No art" });
            PumpUntil(() => missing.IsCompleted); missing.GetAwaiter().GetResult();
            PumpUntil(() => a.Opacity < .01 && b.Opacity < .01);
            artwork.HeroOverride = null;

            foreach (var size in new[] { new Size(980, 620), new Size(660, 520) })
            { window.Width = size.Width; window.Height = size.Height; window.UpdateLayout(); CheckDownloadsLayout(page); }
            SaveGameDetailsVisual(window, "downloads-black-small");
            provider.GetRequiredService<INavigationService>().Navigate<LibraryPage>();
            PumpUntil(() => !page.IsLoaded);
            Assert.False(window.Resources.Contains("WorkspaceBackgroundBrush"));
            Assert.Equal(0, a.Opacity); Assert.Equal(0, b.Opacity);
        }
        finally
        {
            artwork.HeroOverride = null;
            window.Close(); Application.Current.MainWindow = previous;
            store.Downloads.Clear(); foreach (var job in originalJobs) store.Downloads.Add(job);
            UiThemeService.ApplyGlassPolicy(false);
        }
    }
}
