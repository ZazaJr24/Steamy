using System.Windows;
using System.Windows.Controls;
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
    private static void CheckDownloadAmbient(IServiceProvider provider, AppDataStore store, FixtureArtwork artwork)
    {
        var previous = Application.Current.MainWindow; var originalJobs = store.Downloads.ToArray();
        store.Downloads.Clear();
        var active = new DownloadJob { AppId = 1091500, GameName = "Cyberpunk 2077", State = DownloadJobState.Downloading,
            Progress = 41, HasWholeGameProgress = true, TransferredBytes = 967_100_000, TransferTotalBytes = 1_800_000_000,
            BytesPerSecond = 11_475_000, EtaSeconds = 29 * 60 + 36, Status = "Downloading selected depot files" };
        var queued = new DownloadJob { AppId = 1245620, GameName = "ELDEN RING", State = DownloadJobState.Queued, QueuePosition = 2, TotalSize = "64 GB" };
        store.Downloads.Add(active); store.Downloads.Add(queued); UiThemeService.Apply("Dark");
        var window = new MainWindow { Width = 1280, Height = 800, WindowState = WindowState.Normal, ShowInTaskbar = false };
        Application.Current.MainWindow = window;
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(150));
            provider.GetRequiredService<INavigationService>().Navigate<DownloadsPage>();
            PumpUntil(() => Descendants<DownloadsPage>(window).Any());
            var page = Descendants<DownloadsPage>(window).Single(); var model = (DownloadsViewModel)page.DataContext;
            PumpDispatcher(TimeSpan.FromMilliseconds(120)); CheckDownloadsLayout(page);
            var a = (Border)page.FindName("DownloadPageAmbientA"); var b = (Border)page.FindName("DownloadPageAmbientB");
            Assert.Equal(0, a.Opacity); Assert.Equal(0, b.Opacity);
            Assert.True(Assert.IsType<DrawingBrush>(page.Resources["DeckBackground"]).IsFrozen);
            Assert.Same(page.Resources["DeckBackground"], window.Resources["WorkspaceBackgroundBrush"]);
            var rootPaint = window.TryFindResource("WorkspaceBackgroundBrush");
            var row = Descendants<Border>(page).Single(border => ReferenceEquals(border.DataContext, queued) && ReferenceEquals(border.Style, page.Resources["DeckRow"]));
            var before = artwork.HeroRequests.GetValueOrDefault(queued.AppId);
            row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            PumpDispatcher(TimeSpan.FromMilliseconds(80));
            Assert.Same(rootPaint, window.TryFindResource("WorkspaceBackgroundBrush"));
            Assert.Equal(before, artwork.HeroRequests.GetValueOrDefault(queued.AppId)); Assert.Same(active, model.HeroJob);
            var preview = page.PreviewDownloadAmbientAsync(queued); PumpUntil(() => preview.IsCompleted); preview.GetAwaiter().GetResult();
            Assert.Equal(before, artwork.HeroRequests.GetValueOrDefault(queued.AppId));
            Assert.Equal(0, a.Opacity); Assert.Equal(0, b.Opacity);
            foreach (var theme in new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme); PumpDispatcher(TimeSpan.FromMilliseconds(60));
                foreach (var size in new[] { new Size(980, 620), new Size(1440, 900) })
                { window.Width = size.Width; window.Height = size.Height; window.UpdateLayout(); CheckDownloadsLayout(page); }
            }
            UiThemeService.ApplyGlassPolicy(true); Assert.Same(SystemColors.WindowBrush, page.Resources["DeckBackground"]);
            UiThemeService.ApplyGlassPolicy(false);
            SaveGameDetailsVisual(window, "downloads-compact-rows");
            provider.GetRequiredService<INavigationService>().Navigate<SettingsPage>(); PumpUntil(() => !page.IsLoaded);
            Assert.False(window.Resources.Contains("WorkspaceBackgroundBrush"));
        }
        finally { window.Close(); Application.Current.MainWindow = previous; store.Downloads.Clear(); foreach (var job in originalJobs) store.Downloads.Add(job); UiThemeService.ApplyGlassPolicy(false); }
    }
}
