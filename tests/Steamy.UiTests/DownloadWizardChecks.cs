using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckDownloadWizard(IServiceProvider provider, WizardDownloadFixture downloads, WizardQueueFixture queue)
    {
        var page = new LibraryPage();
        var window = new Window { Content = page, Width = 980, Height = 760, ShowInTaskbar = false };
        var store = provider.GetRequiredService<IAppDataStore>();
        var settings = provider.GetRequiredService<ISettingsService>().Load();
        var previousFolder = settings.DownloadFolder;
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-wizard-smoke-" + Guid.NewGuid().ToString("N"));
        settings.DownloadFolder = directory;
        var originalIds = store.Downloads.Select(job => job.Id).ToHashSet();
        TaskCompletionSource? pendingSaveCompletion = null;
        try
        {
            window.Show();
            MoveCursorAway(0, 0);
            var model = (LibraryViewModel)page.DataContext;
            model.SelectedSourceFilter = "All sources";
            model.SearchText = string.Empty;
            PumpUntil(() => model.PagedCatalogItems.Count == 1);
            var game = model.PagedCatalogItems[0];
            page.OpenGameDetails(game);
            Assert.Equal(Visibility.Collapsed, ((Button)page.FindName("PlayButton")).Visibility);
            var activity = provider.GetRequiredService<IGameActivityService>();
            var favoriteButton = (Button)page.FindName("FavoriteButton");
            favoriteButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => favoriteButton.Content?.ToString() == "Favorited" && favoriteButton.IsEnabled);
            Assert.Contains(game.AppId, activity.GetAsync().GetAwaiter().GetResult().FavoriteAppIds);
            favoriteButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => favoriteButton.Content?.ToString() == "Favorite" && favoriteButton.IsEnabled);
            Assert.DoesNotContain(game.AppId, activity.GetAsync().GetAwaiter().GetResult().FavoriteAppIds);
            Assert.Equal(0, page.DownloadWizardStep); // A game opens the original compact source picker.
            CheckWizardStage(page, 0);
            page.SelectDownloadSource(ManifestSource.Zaza);
            var beforePreparation = downloads.PreparationCalls.Count;
            AwaitWizardStep(page);
            Assert.Equal(1, page.DownloadWizardStep);
            Assert.Equal(beforePreparation + 1, downloads.PreparationCalls.Count);
            Assert.Equal((game.AppId, ManifestSource.Zaza), downloads.PreparationCalls.Last());
            var firstPlan = downloads.Plans.Last();
            Assert.Equal(2, page.DownloadDepotChoices.Count);
            CheckWizardStage(page, 1);
            CheckDepotPageChoices(page);
            foreach (var choice in page.DownloadDepotChoices) choice.IsSelected = false;
            var beforeDownload = downloads.Downloads.Count;
            AwaitWizardStep(page);
            Assert.Equal(1, page.DownloadWizardStep); // No empty depot selection may reach Location.
            Assert.Equal(beforeDownload, downloads.Downloads.Count);
            var firstCheckbox = Descendants<CheckBox>((FrameworkElement)page.FindName("DepotStepPanel"))
                .Single(checkbox => ReferenceEquals(checkbox.DataContext, page.DownloadDepotChoices[0]));
            firstCheckbox.IsChecked = true;
            Assert.True(page.DownloadDepotChoices[0].IsSelected); // The right-hand control updates the actual selection.
            page.DownloadDepotChoices[0].SelectedVersion = firstPlan.Depots[0].Versions[1];
            AwaitWizardStep(page);
            Assert.Equal(2, page.DownloadWizardStep);
            CheckWizardStage(page, 2);
            ((Button)page.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, page.DownloadWizardStep);
            Assert.Equal("101", page.DownloadDepotChoices[0].SelectedVersion!.ManifestId);
            Assert.False(page.DownloadDepotChoices[1].IsSelected);
            ((Button)page.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(0, page.DownloadWizardStep);
            CheckWizardStage(page, 0);
            page.SelectDownloadSource(ManifestSource.Sushi);
            Assert.Empty(page.DownloadDepotChoices); // A source switch invalidates the previous snapshot.
            Assert.Contains(firstPlan.Id, downloads.DiscardedPlans);
            AwaitWizardStep(page);
            var plan = downloads.Plans.Last();
            Assert.Equal(ManifestSource.Sushi, plan.Source);
            Assert.All(page.DownloadDepotChoices, choice => Assert.True(choice.IsSelected));
            page.DownloadDepotChoices[0].SelectedVersion = plan.Depots[0].Versions[1];
            page.DownloadDepotChoices[1].IsSelected = false;
            AwaitWizardStep(page);
            Assert.Equal(2, page.DownloadWizardStep);
            page.ConfigureDownloadLocation("relative download folder");
            AwaitWizardStep(page);
            Assert.Equal(2, page.DownloadWizardStep);
            Assert.Equal(beforeDownload, downloads.Downloads.Count);
            Assert.Contains("valid download folder", ((TextBlock)page.FindName("OverlayStatus")).Text, StringComparison.OrdinalIgnoreCase);
            page.ConfigureDownloadLocation(directory);
            var target = Path.Combine(directory, game.Name);
            var running = new DownloadJob { AppId = game.AppId, GameName = game.Name,
                TargetFolder = target, State = DownloadJobState.Downloading, DownloadMode = "DepotDownloaderMod (Sushi)" };
            store.Downloads.Add(running);
            AwaitWizardStep(page);
            Assert.Equal(beforeDownload, downloads.Downloads.Count);
            Assert.Equal(DownloadJobState.Downloading, running.State);
            Assert.Contains("already", ((TextBlock)page.FindName("OverlayStatus")).Text, StringComparison.OrdinalIgnoreCase);
            store.Downloads.Remove(running);

            queue.RefuseRegistration = true;
            var beforeSaved = queue.Saved.Count;
            AwaitWizardStep(page);
            Assert.Equal(beforeSaved, queue.Saved.Count);
            Assert.Equal(originalIds.Count, store.Downloads.Count);
            Assert.Contains("finishing", ((TextBlock)page.FindName("OverlayStatus")).Text, StringComparison.OrdinalIgnoreCase);
            queue.RefuseRegistration = false;

            queue.SaveOverride = (_, _) => Task.FromException(new IOException("Offline fixture cannot save queue."));
            var beforeFailedSavePreparations = downloads.PreparationCalls.Count;
            AwaitWizardStep(page);
            Assert.Equal(beforeSaved, queue.Saved.Count);
            Assert.Equal(originalIds.Count, store.Downloads.Count);
            Assert.Equal(beforeDownload, downloads.Downloads.Count);
            Assert.Equal(0, queue.RegisteredCount);
            Assert.Equal(beforeFailedSavePreparations, downloads.PreparationCalls.Count);
            Assert.DoesNotContain(plan.Id, downloads.DiscardedPlans);
            Assert.Contains("could not be saved", ((TextBlock)page.FindName("OverlayStatus")).Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(((Button)page.FindName("StartButton")).IsEnabled);
            queue.SaveOverride = null;

            downloads.DownloadOverride = async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new RyuuGameDownloadResult(true, "Must be cancelled by Pause");
            };
            var download = page.AdvanceDownloadWizardAsync();
            PumpUntil(() => downloads.Downloads.Count == beforeDownload + 1);
            var call = downloads.Downloads.Last();
            Assert.Equal(plan.Id, call.Plan.Id);
            var selection = Assert.Single(call.Selections);
            Assert.Equal(plan.Depots[0].DepotId, selection.DepotId);
            Assert.Equal("101", selection.ManifestId);
            Assert.Equal(target, call.TargetFolder);
            var added = Assert.Single(store.Downloads, job => !originalIds.Contains(job.Id));
            Assert.Equal("Sushi", added.SourceLabel);
            Assert.Contains(added.Id, queue.Saved); // The selected download is saved before it can be paused.
            AwaitWizardStep(page); // Double-clicking cannot start another process over the same files.
            Assert.Equal(beforeDownload + 1, downloads.Downloads.Count);
            ((Button)page.FindName("PauseButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => download.IsCompleted);
            download.GetAwaiter().GetResult();
            Assert.Equal(DownloadJobState.Paused, added.State);
            Assert.Equal(0, queue.RegisteredCount);

            page.CloseOverlay();
            PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);
            page.OpenDownloadSetup(game);
            Assert.Equal(2, page.DownloadWizardStep);
            Assert.False(((Button)page.FindName("SourceSushi")).IsEnabled);
            page.SelectDownloadSource(ManifestSource.Zaza);
            Assert.Contains("original source", ((TextBlock)page.FindName("OverlayStatus")).Text, StringComparison.OrdinalIgnoreCase);
            ((Button)page.FindName("NewSelectionButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(0, page.DownloadWizardStep);
            Assert.True(((Button)page.FindName("SourceSushi")).IsEnabled);
            Assert.Contains(added, store.Downloads);
            Assert.Equal(DownloadJobState.Paused, added.State);
            Assert.Equal("Sushi", added.SourceLabel);
            page.CloseOverlay();
            PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);
            page.OpenDownloadSetup(game);
            Assert.Equal(2, page.DownloadWizardStep);
            var preparingBeforeResume = downloads.PreparationCalls.Count;
            downloads.DownloadOverride = null;
            var previousResumeStatus = added.Status;
            var previousResumeFinished = added.Finished;
            var resumesBeforeSaveFailure = downloads.Resumes.Count;
            queue.SaveOverride = (_, _) => Task.FromException(new IOException("Offline fixture cannot save resume."));
            AwaitWizardStep(page);
            Assert.Equal(DownloadJobState.Paused, added.State);
            Assert.Equal(previousResumeStatus, added.Status);
            Assert.Equal(previousResumeFinished, added.Finished);
            Assert.Equal(resumesBeforeSaveFailure, downloads.Resumes.Count);
            Assert.Equal(0, queue.RegisteredCount);
            Assert.Single(store.Downloads, job => !originalIds.Contains(job.Id));
            queue.SaveOverride = null;
            AwaitWizardStep(page);
            Assert.Equal(preparingBeforeResume, downloads.PreparationCalls.Count); // Resume uses its saved snapshot.
            Assert.Equal((game.AppId, target), downloads.Resumes.Last());
            Assert.Equal(beforeDownload + 1, downloads.Downloads.Count);
            Assert.Equal(DownloadJobState.Completed, added.State);
            Assert.Equal("Sushi", added.SourceLabel);
            Assert.Single(store.Downloads, job => !originalIds.Contains(job.Id));
            page.CloseOverlay();
            PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);
            store.Downloads.Remove(added);

            var emptyPlan = new PreparedGameDownload(Guid.NewGuid(), game.AppId, ManifestSource.Sushi, []);
            downloads.PrepareOverride = (_, _, _) => Task.FromResult(new GameDownloadPreparation(true, "No downloadable depots in this source snapshot.", emptyPlan));
            page.OpenDownloadSetup(game);
            AwaitWizardStep(page);
            Assert.Equal(0, page.DownloadWizardStep);
            Assert.Empty(page.DownloadDepotChoices);
            Assert.Contains("No downloadable depots", ((TextBlock)page.FindName("OverlayStatus")).Text);
            Assert.Contains(emptyPlan.Id, downloads.DiscardedPlans);
            Assert.Equal(beforeDownload + 1, downloads.Downloads.Count);
            page.CloseOverlay();
            PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);

            var changedSource = new TaskCompletionSource<GameDownloadPreparation>(TaskCreationOptions.RunContinuationsAsynchronously);
            downloads.PrepareOverride = (appId, source, _) => source == ManifestSource.Sushi
                ? changedSource.Task : Task.FromResult(downloads.CreatePreparation(appId, source));
            page.OpenDownloadSetup(game);
            var beforeChangingSource = downloads.PreparationCalls.Count;
            var superseded = page.AdvanceDownloadWizardAsync();
            PumpUntil(() => downloads.PreparationCalls.Count == beforeChangingSource + 1);
            var supersededToken = downloads.PreparationTokens.Last();
            page.SelectDownloadSource(ManifestSource.Zaza);
            Assert.True(supersededToken.IsCancellationRequested);
            AwaitWizardStep(page);
            var sourceChangePlan = downloads.Plans.Last();
            var supersededPlan = downloads.CreatePreparation(game.AppId, ManifestSource.Sushi);
            changedSource.SetResult(supersededPlan);
            PumpUntil(() => superseded.IsCompleted);
            superseded.GetAwaiter().GetResult();
            Assert.Equal(1, page.DownloadWizardStep);
            Assert.Equal(sourceChangePlan.Depots[0].Name, page.DownloadDepotChoices[0].Name);
            Assert.Contains(supersededPlan.Plan!.Id, downloads.DiscardedPlans);
            page.CloseOverlay();
            PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);

            var stale = new TaskCompletionSource<GameDownloadPreparation>(TaskCreationOptions.RunContinuationsAsynchronously);
            downloads.PrepareOverride = (appId, source, token) => source == ManifestSource.Sushi
                ? stale.Task : Task.FromResult(downloads.CreatePreparation(appId, source));
            page.OpenDownloadSetup(game);
            page.SelectDownloadSource(ManifestSource.Sushi);
            var beforeStalePreparation = downloads.PreparationCalls.Count;
            var preparation = page.AdvanceDownloadWizardAsync();
            PumpUntil(() => downloads.PreparationCalls.Count == beforeStalePreparation + 1);
            var preparationToken = downloads.PreparationTokens.Last();
            page.CloseOverlay();
            PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);
            Assert.True(preparationToken.IsCancellationRequested);
            page.OpenDownloadSetup(game);
            page.SelectDownloadSource(ManifestSource.Zaza);
            AwaitWizardStep(page);
            var currentPlan = downloads.Plans.Last();
            Assert.Equal(ManifestSource.Zaza, currentPlan.Source);
            var stalePlan = downloads.CreatePreparation(game.AppId, ManifestSource.Sushi);
            stale.SetResult(stalePlan);
            PumpUntil(() => preparation.IsCompleted);
            preparation.GetAwaiter().GetResult();
            Assert.Equal(1, page.DownloadWizardStep);
            Assert.Equal(currentPlan.Depots[0].Name, page.DownloadDepotChoices[0].Name);
            Assert.Equal(currentPlan.Depots[0].DefaultManifestId, page.DownloadDepotChoices[0].SelectedVersion!.ManifestId);
            Assert.Contains(stalePlan.Plan!.Id, downloads.DiscardedPlans); // Late source responses cannot replace the reopened dialog.

            page.CloseOverlay();
            PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);
            downloads.PrepareOverride = null;
            var firstCompletion = new TaskCompletionSource<RyuuGameDownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            downloads.DownloadOverride = token => firstCompletion.Task.WaitAsync(token);
            page.OpenDownloadSetup(game);
            AwaitWizardStep(page);
            AwaitWizardStep(page);
            page.ConfigureDownloadLocation(directory);
            var beforeDetachedStart = downloads.Downloads.Count;
            var detached = page.AdvanceDownloadWizardAsync();
            PumpUntil(() => downloads.Downloads.Count == beforeDetachedStart + 1);
            var detachedToken = downloads.DownloadTokens.Last();
            var detachedJob = Assert.Single(store.Downloads, job => !originalIds.Contains(job.Id));
            page.CloseOverlay();
            PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);
            Assert.False(detached.IsCompleted);
            Assert.False(detachedToken.IsCancellationRequested);
            Assert.Equal(1, queue.RegisteredCount);

            var secondGame = new SteamCatalogItem { AppId = 20, Name = "A second offline game", AppType = SteamCatalogAppType.Game };
            page.OpenDownloadSetup(secondGame);
            page.SelectDownloadSource(ManifestSource.Zaza);
            AwaitWizardStep(page);
            Assert.Equal(1, page.DownloadWizardStep);
            var secondChoices = page.DownloadDepotChoices.ToArray();
            var secondStatus = ((TextBlock)page.FindName("OverlayStatus")).Text;
            firstCompletion.SetResult(new(true, "The first background download completed."));
            PumpUntil(() => detached.IsCompleted);
            detached.GetAwaiter().GetResult();
            Assert.Equal(DownloadJobState.Completed, detachedJob.State);
            Assert.Equal(0, queue.RegisteredCount);
            Assert.Equal(Visibility.Visible, ((Grid)page.FindName("OverlayGrid")).Visibility);
            Assert.Equal(secondGame.Name, ((TextBlock)page.FindName("OverlayTitle")).Text);
            Assert.Equal(1, page.DownloadWizardStep);
            Assert.Equal(secondChoices, page.DownloadDepotChoices);
            Assert.Equal(secondStatus, ((TextBlock)page.FindName("OverlayStatus")).Text);
            Assert.True(((Button)page.FindName("StartButton")).IsEnabled);
            Assert.True(((Button)page.FindName("BackButton")).IsEnabled);

            page.CloseOverlay();
            PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);
            store.Downloads.Remove(detachedJob);
            downloads.DownloadOverride = null;
            page.OpenDownloadSetup(game);
            AwaitWizardStep(page);
            var savingPlan = downloads.Plans.Last();
            AwaitWizardStep(page);
            page.ConfigureDownloadLocation(directory);
            var waitingSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingSaveCompletion = waitingSave;
            queue.SaveOverride = (_, _) => waitingSave.Task;
            var beforePendingSave = queue.SaveAttempts.Count;
            var beforePendingSaved = queue.Saved.Count;
            var beforePendingDownload = downloads.Downloads.Count;
            var saving = page.AdvanceDownloadWizardAsync();
            PumpUntil(() => queue.SaveAttempts.Count == beforePendingSave + 1);
            Assert.False(saving.IsCompleted);
            Assert.Equal(beforePendingDownload, downloads.Downloads.Count);
            Assert.Equal(originalIds.Count, store.Downloads.Count); // No row or process before the first save succeeds.
            Assert.Equal(1, queue.RegisteredCount);
            page.CloseOverlay();
            PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);
            Assert.DoesNotContain(savingPlan.Id, downloads.DiscardedPlans);
            page.OpenDownloadSetup(game);
            Assert.Equal(0, page.DownloadWizardStep);
            page.SelectDownloadSource(ManifestSource.Zaza);
            AwaitWizardStep(page);
            var replacementPlan = downloads.Plans.Last();
            AwaitWizardStep(page);
            page.ConfigureDownloadLocation(Path.Combine(directory, ".")); // The same target through a canonical folder alias.
            AwaitWizardStep(page);
            Assert.Equal(beforePendingSave + 1, queue.SaveAttempts.Count);
            Assert.Equal(beforePendingDownload, downloads.Downloads.Count);
            Assert.Contains("already starting", ((TextBlock)page.FindName("OverlayStatus")).Text, StringComparison.OrdinalIgnoreCase);
            var replacementStatus = ((TextBlock)page.FindName("OverlayStatus")).Text;
            var replacementChoices = page.DownloadDepotChoices.ToArray();
            waitingSave.SetException(new IOException("The initial save failed after the dialog closed."));
            PumpUntil(() => saving.IsCompleted);
            saving.GetAwaiter().GetResult();
            Assert.Equal(0, queue.RegisteredCount);
            Assert.Equal(beforePendingSaved, queue.Saved.Count);
            Assert.Equal(beforePendingDownload, downloads.Downloads.Count);
            Assert.Equal(originalIds.Count, store.Downloads.Count);
            Assert.Contains(savingPlan.Id, downloads.DiscardedPlans);
            Assert.DoesNotContain(replacementPlan.Id, downloads.DiscardedPlans);
            Assert.Equal(2, page.DownloadWizardStep);
            Assert.Equal(replacementChoices, page.DownloadDepotChoices);
            Assert.Equal(replacementStatus, ((TextBlock)page.FindName("OverlayStatus")).Text);
            Assert.True(((Button)page.FindName("StartButton")).IsEnabled);
            queue.SaveOverride = null;
            AwaitWizardStep(page);
            Assert.Equal(beforePendingDownload + 1, downloads.Downloads.Count); // A failed save releases the target so retry can start.
            Assert.Equal(replacementPlan.Id, downloads.Downloads.Last().Plan.Id);
            Assert.Equal(DownloadJobState.Completed, Assert.Single(store.Downloads, job => !originalIds.Contains(job.Id)).State);
            CheckDownloadStartHandoff(provider, page, downloads, queue, directory);
        }
        finally
        {
            downloads.DownloadOverride = null;
            downloads.PrepareOverride = null;
            queue.RefuseRegistration = false;
            queue.SaveOverride = null;
            pendingSaveCompletion?.TrySetCanceled();
            queue.CancelPendingOperations();
            PumpUntil(() => queue.RegisteredCount == 0);
            page.CloseOverlay();
            window.Close();
            settings.DownloadFolder = previousFolder;
            foreach (var job in store.Downloads.Where(job => !originalIds.Contains(job.Id)).ToArray()) store.Downloads.Remove(job);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void CheckDownloadStartHandoff(IServiceProvider provider, LibraryPage page,
        WizardDownloadFixture downloads, WizardQueueFixture queue, string directory)
    {
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<RyuuGameDownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigation = Assert.IsType<NavigationService>(provider.GetRequiredService<INavigationService>());
        Type? destination = null;
        navigation.Attach(route => destination = route);
        try
        {
            page.CloseOverlay();
            PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);
            var item = new SteamCatalogItem { AppId = 30, Name = "A click-start fixture", AppType = SteamCatalogAppType.Game };
            page.OpenDownloadSetup(item);
            AwaitWizardStep(page);
            AwaitWizardStep(page);
            page.ConfigureDownloadLocation(directory);
            var beforeAttempts = queue.SaveAttempts.Count;
            var beforeDownloads = downloads.Downloads.Count;
            queue.SaveOverride = (_, _) => saved.Task;
            downloads.DownloadOverride = token => finished.Task.WaitAsync(token);

            ((Button)page.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => queue.SaveAttempts.Count == beforeAttempts + 1);
            Assert.Null(destination);
            Assert.Equal(beforeDownloads, downloads.Downloads.Count);
            Assert.Equal(1, queue.RegisteredCount);

            saved.SetResult();
            PumpUntil(() => destination == typeof(DownloadsPage) && downloads.Downloads.Count == beforeDownloads + 1);
            PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);
            Assert.Equal(1, queue.RegisteredCount); // Navigation keeps the registered background operation alive.
            Assert.False(downloads.DownloadTokens.Last().IsCancellationRequested);
            var job = Assert.Single(provider.GetRequiredService<IAppDataStore>().Downloads, entry => entry.AppId == item.AppId);
            finished.SetResult(new(true, "The click-start download completed in the background."));
            PumpUntil(() => queue.RegisteredCount == 0);
            Assert.Equal(DownloadJobState.Completed, job.State);
        }
        finally
        {
            saved.TrySetResult();
            finished.TrySetResult(new(true, "Clean up click-start fixture"));
            queue.SaveOverride = null;
            downloads.DownloadOverride = null;
            navigation.Detach();
        }
    }

    private static void CheckWizardStage(LibraryPage page, int selectedStage)
    {
        Assert.Equal(selectedStage, page.DownloadWizardStep);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)page.FindName("SetupHeader")).Visibility);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)page.FindName("DetailHeader")).Visibility);
        var backdrop = (Image)page.FindName("BackdropImage");
        Assert.Equal(Steamy.Controls.MotionPreferences.BackdropBlurEnabled ? Visibility.Visible : Visibility.Collapsed, backdrop.Visibility);
        var dialog = (FrameworkElement)page.FindName("DialogPanel");
        Assert.Equal(680, dialog.Width);
        Assert.True(dialog.ActualWidth < page.ActualWidth);
        Assert.True(dialog.ActualHeight <= page.ActualHeight - 30);
        foreach (var (name, stage) in new[] { ("SourceStepPanel", 0), ("DepotStepPanel", 1), ("LocationStepPanel", 2) })
            Assert.Equal(stage == selectedStage ? Visibility.Visible : Visibility.Collapsed,
                ((FrameworkElement)page.FindName(name)).Visibility);
    }

    private static void CheckDepotPageChoices(LibraryPage page)
    {
        page.UpdateLayout();
        var panel = (FrameworkElement)page.FindName("DepotStepPanel");
        Assert.Equal(page.DownloadDepotChoices.Count, ((ListBox)page.FindName("DepotChoices")).Items.Count);
        foreach (var choice in page.DownloadDepotChoices)
        {
            var checkbox = Assert.Single(Descendants<CheckBox>(panel),
                control => ReferenceEquals(control.DataContext, choice));
            Assert.True(checkbox.IsVisible);
            Assert.True(checkbox.Focusable); // Selecting depots also works without a pointer.
            var label = Assert.Single(Descendants<TextBlock>(panel),
                text => ReferenceEquals(text.DataContext, choice) && text.Text == choice.Name);
            var checkboxStart = checkbox.TranslatePoint(new Point(0, 0), panel).X;
            var labelEnd = label.TranslatePoint(new Point(label.ActualWidth, 0), panel).X;
            Assert.True(checkboxStart >= labelEnd,
                $"Depot {choice.DepotId}: selection control overlaps or precedes the depot description.");
        }
        var displayedText = string.Join("\n", Descendants<TextBlock>(panel).Where(text => text.IsVisible).Select(text => text.Text));
        Assert.Contains("Windows", displayedText);
        Assert.Contains("English", displayedText);
        Assert.Contains("Offline sample metadata", displayedText);
        Assert.Contains("Build 100", displayedText);
        var withoutMetadata = page.DownloadDepotChoices[1];
        var unknownDepotText = string.Join("\n", Descendants<TextBlock>(panel)
            .Where(text => text.IsVisible && ReferenceEquals(text.DataContext, withoutMetadata)).Select(text => text.Text));
        Assert.DoesNotContain("Windows", unknownDepotText);
        Assert.DoesNotContain("English", unknownDepotText); // Missing Steam metadata never borrows another depot's details.
    }

    private sealed record PreparedDownloadCall(PreparedGameDownload Plan, IReadOnlyList<CachedDepotManifest> Selections, string TargetFolder);

    private sealed class WizardDownloadFixture : IRyuuGameDownloadService
    {
        private readonly ConcurrentDictionary<Guid, PreparedGameDownload> _prepared = new();
        public ConcurrentQueue<(int AppId, ManifestSource Source)> PreparationCalls { get; } = new();
        public ConcurrentQueue<CancellationToken> PreparationTokens { get; } = new();
        public ConcurrentQueue<PreparedDownloadCall> Downloads { get; } = new();
        public ConcurrentQueue<CancellationToken> DownloadTokens { get; } = new();
        public ConcurrentQueue<(int AppId, string TargetFolder)> Resumes { get; } = new();
        public ConcurrentQueue<Guid> DiscardedPlans { get; } = new();
        public ConcurrentQueue<PreparedGameDownload> Plans { get; } = new();
        public Func<int, ManifestSource, CancellationToken, Task<GameDownloadPreparation>>? PrepareOverride { get; set; }
        public Func<CancellationToken, Task<RyuuGameDownloadResult>>? DownloadOverride { get; set; }

        public IReadOnlyList<RyuuDepotInfo> ParseLua(string luaContent) => [];
        public Task<RyuuGameDownloadResult> DownloadGameAsync(int appId, string targetFolder,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The wizard bypassed preparation.");
        public Task<RyuuGameDownloadResult> DownloadGameAsync(int appId, string targetFolder, ManifestSource source,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The wizard downloaded every depot instead of the selected snapshot.");

        public Task<GameDownloadPreparation> PrepareDownloadAsync(int appId, ManifestSource source,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            PreparationCalls.Enqueue((appId, source));
            PreparationTokens.Enqueue(cancellationToken);
            return PrepareOverride?.Invoke(appId, source, cancellationToken)
                ?? Task.FromResult(CreatePreparation(appId, source));
        }

        public GameDownloadPreparation CreatePreparation(int appId, ManifestSource source)
        {
            var screenshotSample = appId == 1091500;
            var plan = new PreparedGameDownload(Guid.NewGuid(), appId, source,
            [
                new PreparedDownloadDepot(appId + 1, $"Game content · {source}",
                    [new PreparedDepotVersion("100", screenshotSample ? 82_892_875_366 : 16 * 1024 * 1024,
                         "Build 100", BranchName: "public"),
                     new PreparedDepotVersion("101", screenshotSample ? 80_315_888_435 : 12 * 1024 * 1024,
                         "Build 99", BranchName: "previous")], "100",
                    ContentType: "Game content", OperatingSystems: "Windows", Languages: "English",
                    MetadataSource: "Offline sample metadata", SteamDbUrl: $"https://steamdb.info/depot/{appId + 1}/"),
                new PreparedDownloadDepot(appId + 2, screenshotSample ? "Language pack" : $"Optional content · {source}",
                    [new PreparedDepotVersion("200", screenshotSample ? 2_254_857_830 : null)], "200",
                    ContentType: screenshotSample ? "Language pack" : null,
                    OperatingSystems: screenshotSample ? "Windows" : null,
                    Languages: screenshotSample ? "English" : null,
                    MetadataSource: screenshotSample ? "Offline sample metadata" : null)
            ]);
            _prepared[plan.Id] = plan;
            Plans.Enqueue(plan);
            return new GameDownloadPreparation(true, "Source snapshot loaded · offline sample", plan);
        }

        public Task<RyuuGameDownloadResult> DownloadPreparedAsync(PreparedGameDownload plan,
            IReadOnlyList<CachedDepotManifest> selectedDepots, string targetFolder,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Assert.True(_prepared.TryGetValue(plan.Id, out var saved));
            Assert.Equal(saved, plan);
            Assert.NotEmpty(selectedDepots);
            foreach (var selection in selectedDepots)
                Assert.Contains(plan.Depots, depot => depot.DepotId == selection.DepotId
                    && depot.Versions.Any(version => version.ManifestId == selection.ManifestId));
            Downloads.Enqueue(new PreparedDownloadCall(plan, selectedDepots.ToArray(), targetFolder));
            DownloadTokens.Enqueue(cancellationToken);
            return DownloadOverride?.Invoke(cancellationToken)
                ?? Task.FromResult(new RyuuGameDownloadResult(true, "Offline fixture completed"));
        }

        public Task<RyuuGameDownloadResult> ResumeDownloadAsync(int appId, string targetFolder,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Resumes.Enqueue((appId, targetFolder));
            return DownloadOverride?.Invoke(cancellationToken)
                ?? Task.FromResult(new RyuuGameDownloadResult(true, "Continued from the saved snapshot"));
        }

        public void DiscardPreparedDownload(Guid id)
        {
            DiscardedPlans.Enqueue(id);
            _prepared.TryRemove(id, out _);
        }
    }

    private sealed class MemoryActivityFixture : IGameActivityService
    {
        private readonly HashSet<int> _favorites = [];
        private readonly Dictionary<int, DateTimeOffset> _launches = [];
        public event EventHandler? Changed;
        public Task<GameActivitySnapshot> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GameActivitySnapshot(_favorites.ToArray(),
                _launches.Select(item => new GameLaunch(item.Key, item.Value)).OrderByDescending(item => item.OpenedAt).ToArray()));
        public Task<bool> ToggleFavoriteAsync(int appId, CancellationToken cancellationToken = default)
        {
            var favorite = !_favorites.Remove(appId);
            if (favorite) _favorites.Add(appId);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(favorite);
        }
        public Task RecordLaunchAsync(int appId, DateTimeOffset? launchedAt = null, CancellationToken cancellationToken = default)
        {
            _launches[appId] = launchedAt ?? DateTimeOffset.UtcNow;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class WizardQueueFixture : IDownloadManager, IDownloadQueueStore
    {
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _operations = new();
        public ConcurrentQueue<Guid> Saved { get; } = new();
        public ConcurrentQueue<Guid> SaveAttempts { get; } = new();
        public int RegisteredCount => _operations.Count;
        public bool RefuseRegistration { get; set; }
        public Func<DownloadJob, CancellationToken, Task>? SaveOverride { get; set; }

        public Task<bool> StartAsync(DownloadJob job, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The wizard must use its prepared source adapter.");

        public Task ForgetAsync(DownloadJob job, CancellationToken cancellationToken = default)
        {
            if (_operations.ContainsKey(job.Id))
                throw new InvalidOperationException("The previous operation is still finishing.");
            return Task.CompletedTask;
        }

        public Task PauseAsync(DownloadJob job, CancellationToken cancellationToken = default)
        {
            if (_operations.TryGetValue(job.Id, out var operation)) operation.Cancel();
            job.State = DownloadJobState.Paused;
            return Task.CompletedTask;
        }

        public Task CancelAsync(DownloadJob job, CancellationToken cancellationToken = default)
        {
            if (_operations.TryGetValue(job.Id, out var operation)) operation.Cancel();
            job.State = DownloadJobState.Cancelled;
            return Task.CompletedTask;
        }

        public Task RetryAsync(DownloadJob job, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unexpected queue retry in the wizard fixture.");

        public Task<bool> VerifyAsync(DownloadJob job, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unexpected verification in the wizard fixture.");

        public void RegisterJob(Guid id, CancellationTokenSource cancellation)
        {
            if (RefuseRegistration || !_operations.TryAdd(id, cancellation))
                throw new InvalidOperationException("The previous operation is still finishing.");
        }

        public void UnregisterJob(Guid id) => _operations.TryRemove(id, out _);
        public void CancelPendingOperations()
        {
            foreach (var operation in _operations.Values)
                try { operation.Cancel(); } catch (ObjectDisposedException) { }
        }
        public bool IsPauseRequested(Guid id) => false;
        public Task RestoreAsync(ObservableCollection<DownloadJob> jobs, bool autoResume, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public async Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveAttempts.Enqueue(job.Id);
            if (SaveOverride is { } save) await save(job, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Saved.Enqueue(job.Id);
        }
        public Task RemoveAsync(DownloadJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
