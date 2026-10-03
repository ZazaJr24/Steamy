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
        var prioritized = store.Downloads.Single(job => job.Id != original.Id);
        prioritized.Priority = DownloadPriority.High; // Priority wins over the older job's enqueue order.
        var run = model.StartQueuedCommand.ExecuteAsync(null);
        Assert.Equal([prioritized.Id], fixture.Started);
        Assert.Equal(DownloadJobState.Queued, original.State);
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

        // Exercise real ComboBox bindings: collection refreshes used to discard pinned versions.
        var depotOne = new Depot { AppId = 42, DepotId = 101, Name = "Main files", Selected = true };
        var depotTwo = new Depot { AppId = 42, DepotId = 102, Name = "Audio" };
        store.Depots.Add(depotOne);
        store.Depots.Add(depotTwo);
        var branch = new Branch { Name = "beta" };
        store.Branches.Add(new Branch { Name = "public" });
        store.Branches.Add(branch);
        var version = new Manifest { AppId = 42, DepotId = 101, ManifestId = "18446744073709551615" };
        var otherVersion = new Manifest { AppId = 42, DepotId = 102, ManifestId = "200" };
        store.Manifests.Add(version);
        store.Manifests.Add(otherVersion);
        model.TargetFolder = @"C:\Games\Pinned";
        model.SelectedDepot = depotOne;
        model.SelectedManifest = version;
        model.SelectedBranch = branch;
        var page = new DepotDownloaderPage { DataContext = model, Width = 780, Height = 560 };
        page.Measure(new Size(780,560));
        page.Arrange(new Rect(0,0,780,560));
        page.UpdateLayout();
        model.OnNavigatedToAsync().GetAwaiter().GetResult();
        Assert.Same(depotOne, model.SelectedDepot);
        Assert.Same(version, model.SelectedManifest);
        Assert.Same(branch, model.SelectedBranch);
        model.SelectedDepot = depotTwo;
        Assert.Null(model.SelectedManifest); // An imported file is not silently pinned.
        model.SelectedDepot = depotOne;
        Assert.Same(version, model.SelectedManifest);
        model.SelectedManifest = otherVersion;
        ((IAsyncRelayCommand)model.AddToQueueCommand).ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Empty(store.Downloads);
        Assert.Contains("valid manifest", model.LastMessage);
        model.SelectedManifest = version;
        ((IAsyncRelayCommand)model.AddToQueueCommand).ExecuteAsync(null).GetAwaiter().GetResult();
        var pinned = Assert.Single(store.Downloads);
        Assert.Equal(version.ManifestId, pinned.ManifestId);
        Assert.Equal(101, pinned.DepotId);
        Assert.Equal("beta", pinned.Branch);
        store.Manifests.Remove(version);
        model.TargetFolder = @"C:\Games\MissingVersion";
        model.OnNavigatedToAsync().GetAwaiter().GetResult();
        Assert.Same(version, model.SelectedManifest); // A missing pin must not become Latest.
        ((IAsyncRelayCommand)model.AddToQueueCommand).ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Single(store.Downloads);
        Assert.Contains("valid manifest", model.LastMessage);
        model.UseLatestManifestCommand.Execute(null);
        model.OnNavigatedToAsync().GetAwaiter().GetResult();
        Assert.Null(model.SelectedManifest);
        Assert.Contains("Latest", model.ManifestSelectionLabel);
        model.UseAllDepotsCommand.Execute(null);
        Assert.Null(model.SelectedDepot);
        Assert.Null(model.SelectedManifest);
        Assert.Equal("All compatible depots", model.DepotSelectionLabel);
        var lateStore = new AppDataStore();
        var lateModel = new DepotDownloaderViewModel(lateStore, provider.GetRequiredService<INavigationService>(),
            provider.GetRequiredService<ILoggingService>(), provider.GetRequiredService<IDepotDownloaderService>(), fixture, settings, fixture);
        Assert.Null(lateModel.SelectedGame);
        var discovered = new Game { AppId = 1091500, Name = "Discovered after startup" };
        lateStore.Games.Add(discovered);
        lateModel.OnNavigatedToAsync().GetAwaiter().GetResult();
        Assert.Same(discovered, lateModel.SelectedGame);
        Assert.Contains("1091500", lateModel.SelectedHeroUrl!);
    }

    private static void CheckLibraryDialog(IServiceProvider provider, string theme)
    {
        _phase = "Create dialog test page " + theme;
        var page = new LibraryPage();
        var window = new Window { Content = page, Width = 980, Height = 760, ShowInTaskbar = false };
        window.Show();
        MoveCursorAway(0, 0);
        var model = (LibraryViewModel)page.DataContext;
        model.SelectedSourceFilter = "All sources";
        model.SearchText = string.Empty;
        PumpUntil(() => model.PagedCatalogItems.Count == 1);
        window.UpdateLayout();
        var card = Descendants<Button>(page).Single(button => button.Tag is SteamCatalogItem item && item.AppId == model.PagedCatalogItems[0].AppId);
        Assert.True(CardMotion.GetIsEnabled(card));
        var motionSurface = Assert.IsAssignableFrom<FrameworkElement>(card.Template.FindName("MotionSurface", card));
        var animatedTransforms = Assert.IsType<TransformGroup>(motionSurface.RenderTransform).Children.ToArray();
        var edge = card.TranslatePoint(new Point(card.ActualWidth / 2, card.ActualHeight - 1), window);
        card.Focus();
        PumpDispatcher(TimeSpan.FromMilliseconds(210));
        var hit = window.InputHitTest(edge) as DependencyObject;
        while (hit is not null && !ReferenceEquals(hit, card)) hit = VisualTreeHelper.GetParent(hit);
        Assert.Same(card, hit); // The hit area stays fixed while only the visual surface lifts/scales.
        _phase = "Open dialog " + theme;
        var originalPosition = card.TranslatePoint(new Point(0, 0), page);
        var originalWidth = card.ActualWidth;
        var backgroundGrid = (Grid)page.FindName("MainContentGrid");
        var marker = new Border { Width = 20, Height = 20, Background = Brushes.Magenta,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        backgroundGrid.Children.Add(marker);
        window.UpdateLayout();
        card.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); // The same action works for Enter/Space.
        window.UpdateLayout();
        Assert.Equal(originalPosition, card.TranslatePoint(new Point(0, 0), page));
        Assert.Equal(originalWidth, card.ActualWidth);
        var overlay = (Grid)page.FindName("OverlayGrid");
        var background = (Grid)page.FindName("MainContentGrid");
        var snapshot = (Image)page.FindName("BackdropImage");
        Assert.Equal(Visibility.Visible, overlay.Visibility);
        Assert.False(background.IsEnabled);
        Assert.False(background.IsHitTestVisible);
        if (MotionPreferences.BackdropBlurEnabled)
        {
            Assert.True(((BitmapSource)snapshot.Source).IsFrozen);
            var pixel = new byte[4];
            ((BitmapSource)snapshot.Source).CopyPixels(new Int32Rect(4, 4, 1, 1), pixel, 4, 0);
            Assert.Equal(new byte[] { 255, 0, 255, 255 }, pixel); // BGRA marker at local origin, without the page margin twice.
            backgroundGrid.Children.Remove(marker);
            Assert.Equal(Visibility.Visible, snapshot.Visibility);
            var previous = snapshot.Source;
            window.Width -= 20;
            PumpDispatcher(TimeSpan.FromMilliseconds(250));
            Assert.NotSame(previous, snapshot.Source);
            Assert.True(((BitmapSource)snapshot.Source).IsFrozen);
        }
        else
        {
            Assert.Null(snapshot.Source);
            Assert.Equal(Visibility.Collapsed, snapshot.Visibility);
        }
        backgroundGrid.Children.Remove(marker);
        Assert.True(overlay.IsKeyboardFocusWithin);
        Assert.Equal(0, page.DownloadWizardStep);
        Assert.True(((FrameworkElement)page.FindName("SourceStepPanel")).IsVisible);
        page.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page)!, 0, Key.Escape)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        PumpUntil(() => overlay.Visibility == Visibility.Collapsed);
        Assert.True(background.IsEnabled);
        Assert.True(background.IsHitTestVisible);
        Assert.Null(snapshot.Source);
        Assert.True(card.IsKeyboardFocused);

        _phase = "Open separate download setup " + theme;
        card.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        CheckWizardStage(page, 0);
        Assert.Equal(MotionPreferences.BackdropBlurEnabled ? Visibility.Visible : Visibility.Collapsed, snapshot.Visibility);
        AwaitWizardStep(page);
        CheckWizardStage(page, 1);
        Assert.NotEmpty(page.DownloadDepotChoices);
        AwaitWizardStep(page);
        CheckWizardStage(page, 2);
        var store = provider.GetRequiredService<IAppDataStore>();
        var settings = provider.GetRequiredService<ISettingsService>().Load();
        var standardJob = new DownloadJob { AppId = model.PagedCatalogItems[0].AppId, DownloadMode = "DepotDownloader", State = DownloadJobState.Paused,
            TargetFolder = Path.Combine(string.IsNullOrWhiteSpace(settings.DownloadFolder) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamGames") : settings.DownloadFolder, "An offline library game") };
        store.Downloads.Add(standardJob);
        AwaitWizardStep(page);
        Assert.Contains("Open Downloads", ((TextBlock)page.FindName("OverlayStatus")).Text);
        Assert.Contains(standardJob, store.Downloads);
        Assert.Equal(DownloadJobState.Paused, standardJob.State);
        store.Downloads.Remove(standardJob);
        _phase = "Close dialog " + theme;
        page.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page)!, 0, Key.Escape)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        PumpUntil(() => overlay.Visibility == Visibility.Collapsed);
        Assert.True(background.IsEnabled);
        Assert.True(background.IsHitTestVisible);
        Assert.Null(snapshot.Source);
        Assert.True(card.IsKeyboardFocused);
        try
        {
            MotionPreferences.Configure(true);
            card.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.Null(snapshot.Source); // Reduced effects never allocate a blurred gallery bitmap.
            Assert.Equal(Visibility.Collapsed, snapshot.Visibility);
            Assert.False(background.IsEnabled);
            Assert.False(background.IsHitTestVisible);
            page.CloseOverlay();
            PumpUntil(() => overlay.Visibility == Visibility.Collapsed);
            Assert.True(card.IsKeyboardFocused);
        }
        finally { MotionPreferences.Configure(false); }
        window.Close();
        Assert.NotNull(Application.Current);
        Assert.False(card.RenderTransform.HasAnimatedProperties); // Unloaded cards release their clocks.
        Assert.False(motionSurface.RenderTransform.HasAnimatedProperties);
        Assert.All(animatedTransforms, transform => Assert.False(transform.HasAnimatedProperties));
    }

    private static void AwaitWizardStep(LibraryPage page)
    {
        var transition = page.AdvanceDownloadWizardAsync();
        PumpUntil(() => transition.IsCompleted);
        transition.GetAwaiter().GetResult();
        PumpDispatcher(TimeSpan.FromMilliseconds(60));
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
