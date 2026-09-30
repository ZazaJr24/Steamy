using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;
using Steamy.Controls;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckDepotQueue(IServiceProvider provider)
    {
        var store = new AppDataStore();
        store.Games.Add(new Game { AppId = 42, Name = "Queue fixture" });
        var settings = new MemorySettings();
        settings.Load().ParallelDownloads = 1;
        var fixture = new QueueFixture();
        var model = new DepotDownloaderViewModel(store, provider.GetRequiredService<INavigationService>(),
            provider.GetRequiredService<ILoggingService>(), provider.GetRequiredService<IDepotDownloaderService>(), fixture, settings, fixture)
        { TargetFolder = @"C:\Games\One", AuthorizationConfirmed = true };
        ((IAsyncRelayCommand)model.AddToQueueCommand).ExecuteAsync(null).GetAwaiter().GetResult();
        var original = Assert.Single(store.Downloads);
        Assert.Equal(original.Id, Assert.Single(fixture.Saved)); // Even an unstarted job survives a restart.
        model.TargetFolder = @"C:\Games\One\";
        ((IAsyncRelayCommand)model.AddToQueueCommand).ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Single(store.Downloads); // Canonical folder aliases do not add duplicates.
        model.TargetFolder = @"C:\Games\Two";
        ((IAsyncRelayCommand)model.AddToQueueCommand).ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Equal(2, store.Downloads.Count); // A genuinely separate folder is allowed.
        var run = model.StartQueuedCommand.ExecuteAsync(null);
        Assert.Single(fixture.Started);
        Assert.True(model.IsBusy);
        original.State = DownloadJobState.Cancelled; // Cancel the job while it is waiting for the slot.
        fixture.Completion.SetResult(true);
        PumpUntil(() => run.IsCompleted);
        run.GetAwaiter().GetResult();
        Assert.DoesNotContain(original.Id, fixture.Started);
        Assert.False(model.IsBusy);

        var completed = store.Downloads[0];
        model.RepairCommand.ExecuteAsync(completed).GetAwaiter().GetResult();
        Assert.Equal(2, fixture.Started.Count);
        Assert.Contains("Verify & repair", completed.ProcessLog);
        var remove = (IAsyncRelayCommand<DownloadJob>)model.RemoveCommand;
        completed.State = DownloadJobState.Paused;
        fixture.RefuseRemoval = true;
        remove.ExecuteAsync(completed).GetAwaiter().GetResult();
        Assert.Contains(completed, store.Downloads); // A still-finishing process keeps its queue row.
        Assert.Contains("finishing", model.LastMessage);
        fixture.RefuseRemoval = false;
        remove.ExecuteAsync(completed).GetAwaiter().GetResult();
        Assert.DoesNotContain(completed, store.Downloads);

        model.TargetFolder = "relative-path";
        ((IAsyncRelayCommand)model.AddToQueueCommand).ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Single(store.Downloads);
        model.TargetFolder = @"C:\Games\Three";
        fixture.FailSave = true;
        ((IAsyncRelayCommand)model.AddToQueueCommand).ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Single(store.Downloads);
        Assert.Contains("Could not save", model.LastMessage);
        fixture.FailSave = false;
        store.Downloads.Add(new DownloadJob { AppId = 42, TargetFolder = @"C:\Games\Three", DownloadMode = "DepotDownloaderMod (Sushi)", State = DownloadJobState.Paused });
        ((IAsyncRelayCommand)model.AddToQueueCommand).ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Equal(2, store.Downloads.Count); // Never replace a Mod resume with a standard-tool job.
        ((IAsyncRelayCommand)model.CheckToolCommand).ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Equal("Tool check failed", model.ToolStatus); // Strict offline service throws; the UI contains the error.
        Assert.False(model.IsBusy);
        store.Downloads.Clear();
        var changes = 0;
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.QueueSummary)) changes++; };
        original.State = DownloadJobState.Paused;
        Assert.Equal(0, changes); // Reset detaches the old job watchers.
    }

    private static void CheckLibraryDialog(IServiceProvider provider, string theme)
    {
        var page = new LibraryPage();
        var window = new Window { Content = page, Width = 980, Height = 760, ShowInTaskbar = false };
        window.Show();
        MoveCursorAway(0, 0);
        var model = (LibraryViewModel)page.DataContext;
        model.SelectedSourceFilter = "All sources";
        model.SearchText = string.Empty;
        PumpUntil(() => model.PagedCatalogItems.Count == 1);
        window.UpdateLayout();
        var card = Descendants<Button>(page).Single(button => button.Tag is SteamCatalogItem);
        Assert.True(CardMotion.GetIsEnabled(card));
        card.Focus();
        card.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); // The same action works for Enter/Space.
        var overlay = (Grid)page.FindName("OverlayGrid");
        var background = (Grid)page.FindName("MainContentGrid");
        var snapshot = (Image)page.FindName("BackdropImage");
        Assert.Equal(Visibility.Visible, overlay.Visibility);
        Assert.False(background.IsEnabled);
        Assert.False(background.IsHitTestVisible);
        if (!SystemParameters.HighContrast)
        {
            Assert.True(((BitmapSource)snapshot.Source).IsFrozen);
            Assert.Equal(Visibility.Visible, snapshot.Visibility);
        }
        Assert.True(overlay.IsKeyboardFocusWithin);
        var store = provider.GetRequiredService<IAppDataStore>();
        var settings = provider.GetRequiredService<ISettingsService>().Load();
        var standardJob = new DownloadJob { AppId = model.PagedCatalogItems[0].AppId, DownloadMode = "DepotDownloader", State = DownloadJobState.Paused,
            TargetFolder = Path.Combine(string.IsNullOrWhiteSpace(settings.DownloadFolder) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamGames") : settings.DownloadFolder, "An offline library game") };
        store.Downloads.Add(standardJob);
        ((Button)page.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Contains("Open Downloads", ((TextBlock)page.FindName("OverlayStatus")).Text);
        Assert.Contains(standardJob, store.Downloads);
        Assert.Equal(DownloadJobState.Paused, standardJob.State);
        store.Downloads.Remove(standardJob);
        page.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page)!, 0, Key.Escape)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        PumpUntil(() => overlay.Visibility == Visibility.Collapsed);
        Assert.True(background.IsEnabled);
        Assert.True(background.IsHitTestVisible);
        Assert.Null(snapshot.Source);
        Assert.True(card.IsKeyboardFocused);
        window.Close();
        Assert.NotNull(Application.Current);
        Assert.False(card.RenderTransform.HasAnimatedProperties); // Unloaded cards release their clocks.
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class QueueFixture : IDownloadManager, IDownloadQueueStore
    {
        public readonly List<Guid> Saved = new(), Started = new();
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool RefuseRemoval, FailSave;
        public async Task<bool> StartAsync(DownloadJob job, CancellationToken cancellationToken = default)
        {
            Started.Add(job.Id);
            job.State = DownloadJobState.Downloading;
            var result = await Completion.Task;
            job.State = DownloadJobState.Completed;
            return result;
        }
        public Task ForgetAsync(DownloadJob job, CancellationToken cancellationToken = default) => RefuseRemoval
            ? Task.FromException(new InvalidOperationException("The previous operation is still finishing.")) : Task.CompletedTask;
        public Task PauseAsync(DownloadJob job, CancellationToken cancellationToken = default) { job.State = DownloadJobState.Paused; return Task.CompletedTask; }
        public Task CancelAsync(DownloadJob job, CancellationToken cancellationToken = default) { job.State = DownloadJobState.Cancelled; return Task.CompletedTask; }
        public Task RetryAsync(DownloadJob job, CancellationToken cancellationToken = default) => StartAsync(job, cancellationToken);
        public Task<bool> VerifyAsync(DownloadJob job, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void RegisterJob(Guid id, CancellationTokenSource cancellation) { }
        public void UnregisterJob(Guid id) { }
        public bool IsPauseRequested(Guid id) => false;
        public Task RestoreAsync(ObservableCollection<DownloadJob> jobs, bool autoResume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default)
        {
            if (FailSave) throw new IOException("Fixture disk unavailable");
            Saved.Add(job.Id);
            return Task.CompletedTask;
        }
        public Task RemoveAsync(DownloadJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
