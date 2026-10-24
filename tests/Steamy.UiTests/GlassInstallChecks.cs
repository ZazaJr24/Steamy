using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private static void CheckGlassInstallFlow(IServiceProvider provider, AppDataStore store, FixtureArtwork artwork,
        BetterSteamToolsFixture steam, WizardDownloadFixture downloads)
    {
        var originalWindow = Application.Current.MainWindow;
        var originalJobs = store.Downloads.ToArray();
        store.Downloads.Clear();
        AddScreenshotLibrary(store, artwork);
        var navigation = provider.GetRequiredService<INavigationService>();
        UiThemeService.Apply("Dark");
        var window = new MainWindow { Width = 1440, Height = 900, WindowState = WindowState.Normal, ShowInTaskbar = false };
        Application.Current.MainWindow = window;
        window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(250));
        try
        {
            _phase = "Shell download states";
            var shellButton = (Button)window.FindName("ShellDownloadButton");
            var shell = (ShellDownloadViewModel)shellButton.DataContext;
            Assert.Null(shell.Job);
            var queued = new DownloadJob { AppId = 1091500, GameName = "Cyberpunk 2077", State = DownloadJobState.Queued, QueuePosition = 2, TotalSize = "64,0 GB" };
            var active = new DownloadJob { AppId = 1808500, GameName = "ARC Raiders", State = DownloadJobState.Preparing, DownloadMode = "DepotDownloaderMod (Sushi)" };
            store.Downloads.Add(queued); store.Downloads.Add(active);
            Assert.Same(active, shell.Job);
            Assert.Equal(1, shell.QueuedCount);
            Assert.True(Descendants<ProgressBar>(shellButton).Single().IsIndeterminate);
            active.State = DownloadJobState.Downloading; active.Progress = 41; active.HasWholeGameProgress = true;
            active.Downloaded = "967 MB"; active.TotalSize = "1.8 GB"; active.Speed = "11.5 MB/s"; active.Eta = "29m";
            // A minute of fixture telemetry also checks that visiting Downloads retains history.
            var rateNow = Environment.TickCount64 / 1000d;
            for (var sample = 0; sample < 60; sample++)
                active.RecordTransferRate((.72 + Math.Sin(sample / 7d) * .23) * 11_475_000, rateNow - 60 + sample);
            active.BytesPerSecond = 11_475_000; active.DiskSpeed = "262.3 MiB/s";
            active.TransferredBytes = 967_100_000; active.TransferTotalBytes = 1_800_000_000;
            active.ContentBytes = 1_250_000_000; active.InstallationBytes = 25_000_000_000; active.EtaSeconds = 29 * 60 + 36;
            active.Status = "Downloading selected depot files";
            PumpDispatcher(TimeSpan.FromMilliseconds(200));
            Assert.False(Descendants<ProgressBar>(shellButton).Single().IsIndeterminate);
            Assert.Equal(41, Descendants<SmoothProgressBar>(shellButton).Single().SmoothValue);
            shellButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => Descendants<DownloadsPage>(window).Any());
            Assert.Same(active, provider.GetRequiredService<DownloadsViewModel>().HeroJob);
            PumpDispatcher(TimeSpan.FromMilliseconds(1200));
            var graph = Descendants<DownloadRateGraph>(window).Single();
            Assert.Same(active, graph.Job); Assert.Contains("MB/s", graph.PeakDisplay);
            var downloadPage = Descendants<DownloadsPage>(window).Single();
            Assert.True(((FrameworkElement)window.FindName("PersistentNavigation")).IsVisible);
            var shellIcon = (FrameworkElement)window.FindName("ShellDownloadIcon");
            var shellProgress = (FrameworkElement)window.FindName("ShellDownloadProgress");
            Assert.True(shellProgress.IsVisible);
            Assert.True(shellProgress.TranslatePoint(new Point(), shellButton).Y >=
                shellIcon.TranslatePoint(new Point(0, shellIcon.ActualHeight), shellButton).Y + 3,
                "Download progress must sit below the artwork, where it remains readable.");
            Assert.True(downloadPage.TranslatePoint(new Point(), window).Y >= 54,
                "Downloads must leave space for the persistent shell navigation.");
            var downloadModel = provider.GetRequiredService<DownloadsViewModel>();
            Assert.Same(queued, Assert.Single(downloadModel.QueueJobs));
            var queueOptions = Descendants<Button>(downloadPage).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Queue options");
            queueOptions.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpDispatcher(TimeSpan.FromMilliseconds(150));
            Assert.True(queueOptions.ContextMenu.IsOpen);
            Assert.All(queueOptions.ContextMenu.Items.Cast<MenuItem>(), item => Assert.Same(queued, item.CommandParameter));
            Assert.Same(downloadModel.MoveUpCommand, ((MenuItem)queueOptions.ContextMenu.Items[0]).Command);
            Assert.All(queueOptions.ContextMenu.Items.Cast<MenuItem>().Take(3), item => Assert.True(item.IsEnabled));
            Assert.False(((MenuItem)queueOptions.ContextMenu.Items[4]).IsEnabled);
            Assert.False(((MenuItem)queueOptions.ContextMenu.Items[5]).IsEnabled);
            queueOptions.ContextMenu.IsOpen = false;
            var detailsJob = downloadModel.SelectedJob ?? active;
            ((Button)downloadPage.FindName("DownloadOptionsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            ((Button)Descendants<Button>(downloadPage).Single(button => button.Content is string text && text == "Details")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            Assert.Same(detailsJob, downloadModel.SelectedJob);
            Assert.True(((Expander)downloadPage.FindName("DownloadDetails")).IsExpanded);
            ((Expander)downloadPage.FindName("DownloadDetails")).IsExpanded = false;
            downloadModel.SelectedJob = null;
            ((Button)downloadPage.FindName("DownloadOptionsClose")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var artPane = (FrameworkElement)downloadPage.FindName("DownloadArtworkPane");
            var transferPane = (FrameworkElement)downloadPage.FindName("DownloadHeroContent");
            Assert.True(artPane.TransformToAncestor(downloadPage).TransformBounds(new Rect(artPane.RenderSize)).Right
                <= transferPane.TransformToAncestor(downloadPage).TransformBounds(new Rect(transferPane.RenderSize)).Left);
            store.Downloads.Add(new DownloadJob { AppId = 1245620, GameName = "ELDEN RING", State = DownloadJobState.Queued, QueuePosition = 3, TotalSize = "48,2 GB" });
            store.Downloads.Add(new DownloadJob { AppId = 270880, GameName = "American Truck Simulator", State = DownloadJobState.Queued, QueuePosition = 4, TransferredBytes = 384, TotalSize = "384 B", Progress = 0 });
            PumpDispatcher(TimeSpan.FromMilliseconds(150));
            SaveGameDetailsVisual(window, "glass-downloads");
            var keyboardSource = PresentationSource.FromVisual(window)!;
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, keyboardSource, Environment.TickCount, System.Windows.Input.Key.F10)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, keyboardSource, Environment.TickCount, System.Windows.Input.Key.F10)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyUpEvent });
            Assert.True(((FrameworkElement)downloadPage.FindName("DownloadOptionsLayer")).IsVisible);
            Assert.False(((FrameworkElement)downloadPage.FindName("DeckFooter")).IsEnabled);
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, keyboardSource, Environment.TickCount, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, keyboardSource, Environment.TickCount, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyUpEvent });
            Assert.False(((FrameworkElement)downloadPage.FindName("DownloadOptionsLayer")).IsVisible);
            Assert.True(((FrameworkElement)downloadPage.FindName("DeckFooter")).IsEnabled);
            Descendants<Button>(downloadPage).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Open app menu")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(window.RootNavigationView.IsPaneOpen);
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, keyboardSource, Environment.TickCount, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, keyboardSource, Environment.TickCount, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyUpEvent });
            Assert.False(window.RootNavigationView.IsPaneOpen);
            Assert.Same(downloadPage, Descendants<DownloadsPage>(window).Single());
            window.Width = 980; window.UpdateLayout();
            active.BytesPerSecond = 14.5 * 1048576;
            PumpDispatcher(TimeSpan.FromMilliseconds(1200));
            Assert.True(graph.IsVisible);
            Assert.StartsWith("14", graph.PeakDisplay);
            window.Width = 1440; window.UpdateLayout();
            active.State = DownloadJobState.Paused;
            Assert.Same(active, shell.Job); Assert.Contains("Paused", shell.Summary);
            Assert.Equal(((SolidColorBrush)window.FindResource("WarningBrush")).Color,
                Assert.IsType<SolidColorBrush>(((ProgressBar)shellProgress).Foreground).Color);
            active.State = DownloadJobState.Completed;
            Assert.Same(queued, shell.Job);
            store.Downloads.Clear();
            Assert.Null(shell.Job); Assert.Null(shell.Artwork);

            _phase = "Install method and direct download steps";
            navigation.Navigate<LibraryPage>();
            PumpUntil(() => Descendants<LibraryPage>(window).Any());
            var page = Descendants<LibraryPage>(window).Single();
            SaveGameDetailsVisual(window, "glass-library");
            var game = new SteamCatalogItem { AppId = 1091500, Name = "Cyberpunk 2077", AppType = SteamCatalogAppType.Game,
                IsReleaseVerified = true, HeaderImage = FixtureArtwork.Read(1091500, "header"), ArtworkImage = FixtureArtwork.Read(1091500, "portrait") };
            var preparedBefore = downloads.PreparationCalls.Count;
            page.OpenDownloadSetup(game);
            CheckWizardStage(page, 0);
            Assert.False(((Button)page.FindName("StartButton")).IsEnabled);
            AwaitWizardStep(page); Assert.Equal(0, page.DownloadWizardStep);
            Assert.Equal(preparedBefore, downloads.PreparationCalls.Count);
            SaveGameDetailsVisual(window, "install-method");
            ((Button)page.FindName("MethodDepotDownloader")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(((Button)page.FindName("StartButton")).IsEnabled);
            AwaitWizardStep(page); CheckWizardStage(page, 1);
            page.SelectDownloadSource(ManifestSource.Zaza);
            AwaitWizardStep(page); CheckWizardStage(page, 2);
            SaveGameDetailsVisual(window, "install-depots");
            page.DownloadDepotChoices[0].SelectedVersion = page.DownloadDepotChoices[0].Versions.Last();
            var version = page.DownloadDepotChoices[0].SelectedVersion;
            AwaitWizardStep(page); CheckWizardStage(page, 3);
            PumpUntil(() => ((ItemsControl)page.FindName("InstallDriveChoices")).Items.Count > 0);
            Assert.All(((ItemsControl)page.FindName("InstallDriveChoices")).Items.Cast<LibraryPage.InstallDriveChoice>(), choice =>
            { Assert.True(Path.IsPathFullyQualified(choice.Folder)); Assert.True(choice.FreeBytes >= 0); });
            PumpUntil(() => !((TextBlock)page.FindName("LocationSpaceText")).Text.StartsWith("Checking", StringComparison.Ordinal));
            var drives = ((ItemsControl)page.FindName("InstallDriveChoices")).Items.Cast<LibraryPage.InstallDriveChoice>().ToArray();
            var selectedDrive = drives.FirstOrDefault(drive => drive.IsSelected);
            if (selectedDrive is { HasEnoughSpace: false }) Assert.False(((Button)page.FindName("StartButton")).IsEnabled);
            if (drives.FirstOrDefault(drive => drive.HasEnoughSpace) is { } available)
            {
                Descendants<Button>((ItemsControl)page.FindName("InstallDriveChoices")).Single(button => Equals(button.Tag, available.Folder))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpUntil(() => !((TextBlock)page.FindName("LocationSpaceText")).Text.StartsWith("Checking", StringComparison.Ordinal));
                Assert.True(((Button)page.FindName("StartButton")).IsEnabled);
                Assert.Equal(available.Folder, ((TextBlock)page.FindName("DownloadPathText")).Text);
            }
            SaveGameDetailsVisual(window, "install-location");
            ((Button)page.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Same(version, page.DownloadDepotChoices[0].SelectedVersion);
            ((Button)page.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            ((Button)page.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            CheckWizardStage(page, 0);

            _phase = "BetterSteamTools install verification and source routing";
            page.SelectInstallMethod(GameInstallMethod.BetterSteamTools);
            AwaitWizardStep(page); Assert.Equal(1, page.DownloadWizardStep);
            page.SelectDownloadSource(ManifestSource.Sushi);
            var depotCalls = downloads.PreparationCalls.Count;
            AwaitWizardStep(page); Assert.Equal(2, page.DownloadWizardStep);
            Assert.Equal(depotCalls, downloads.PreparationCalls.Count);
            Assert.True(((FrameworkElement)page.FindName("SteamToolsStepPanel")).IsVisible);
            Assert.False(((FrameworkElement)page.FindName("DepotStepPanel")).IsVisible);
            SaveGameDetailsVisual(window, "install-steam");
            var sourceBefore = steam.LastSource;
            steam.Installed = false; steam.InstallFails = true;
            AwaitWizardStep(page); Assert.Equal(sourceBefore, steam.LastSource);
            Assert.Contains("failed", ((TextBlock)page.FindName("OverlayStatus")).Text, StringComparison.OrdinalIgnoreCase);
            steam.InstallFails = false; steam.PretendInstall = true;
            AwaitWizardStep(page); Assert.Equal(sourceBefore, steam.LastSource);
            Assert.Contains("verified", ((TextBlock)page.FindName("OverlayStatus")).Text);
            steam.PretendInstall = false;
            AwaitWizardStep(page);
            Assert.Equal((game.AppId, (ManifestSource?)ManifestSource.Sushi), steam.LastSource);
            Assert.Equal("Open in Steam", ((Button)page.FindName("StartButton")).Content);
            Assert.Equal(depotCalls, downloads.PreparationCalls.Count);
            page.CloseOverlay();

            page.OpenDownloadSetup(game); page.SelectInstallMethod(GameInstallMethod.BetterSteamTools);
            page.SelectLocalPackage(Path.Combine(Path.GetTempPath(), "sample-metadata.zip"));
            var imported = steam.ImportCalls;
            var importFromSheet = page.InstallFromSheetAsync();
            PumpUntil(() => importFromSheet.IsCompleted); importFromSheet.GetAwaiter().GetResult();
            Assert.Equal(imported + 1, steam.ImportCalls);
            Assert.Equal(game.AppId, steam.ImportAppId);
            page.CloseOverlay();

            page.OpenDownloadSetup(game); page.SelectInstallMethod(GameInstallMethod.BetterSteamTools);
            AwaitWizardStep(page); page.SelectDownloadSource(ManifestSource.Sushi); AwaitWizardStep(page);
            steam.HoldOperation = true;
            var pending = page.AdvanceDownloadWizardAsync();
            Assert.False(pending.IsCompleted);
            page.CloseOverlay(); PumpUntil(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
            Assert.False(((Grid)page.FindName("OverlayGrid")).IsVisible);
            steam.HoldOperation = false;

            _phase = "Settings categories, search and compact shell";
            navigation.Navigate<SettingsPage>(); PumpUntil(() => Descendants<SettingsPage>(window).Any());
            var settings = Descendants<SettingsPage>(window).Single();
            var categories = (ListBox)settings.FindName("CategorySelector");
            Assert.Equal(6, categories.Items.Count);
            Assert.Equal(TextRenderingMode.ClearType, TextOptions.GetTextRenderingMode(settings));
            foreach (ListBoxItem category in categories.Items)
            {
                categories.SelectedItem = category; window.UpdateLayout();
                Assert.Contains(((StackPanel)settings.FindName("SectionsPanel")).Children.Cast<FrameworkElement>(), section => section.IsVisible);
            }
            categories.SelectedIndex = 0;
            SaveGameDetailsVisual(window, "glass-settings");
            var search = (TextBox)settings.FindName("SettingsSearch");
            search.Text = "download"; PumpDispatcher(TimeSpan.FromMilliseconds(240));
            Assert.Equal("Search results", ((TextBlock)settings.FindName("CategoryTitle")).Text);
            Assert.False(((FrameworkElement)settings.FindName("NoSettingsResults")).IsVisible);
            search.Clear(); PumpDispatcher(TimeSpan.FromMilliseconds(240));
            window.Width = 980; window.Height = 620; window.UpdateLayout();
            var shellBounds = shellButton.TranslatePoint(new Point(0, 0), window);
            var nav = (FrameworkElement)window.FindName("PersistentNavigation");
            var navBounds = nav.TranslatePoint(new Point(nav.ActualWidth, 0), window);
            Assert.True(shellBounds.X > navBounds.X + 8, "Download shortcut must not cover the centered navigation.");
            Assert.True(categories.IsVisible);
            SaveGameDetailsVisual(window, "glass-settings-small");
            UiThemeService.Apply("Light"); PumpDispatcher(TimeSpan.FromMilliseconds(100));
            SaveGameDetailsVisual(window, "glass-settings-light");
            UiThemeService.Apply("Dark");
        }
        finally
        {
            steam.Installed = true; steam.InstallFails = steam.PretendInstall = steam.HoldOperation = false;
            window.Close(); Application.Current.MainWindow = originalWindow;
            store.Downloads.Clear(); foreach (var job in originalJobs) store.Downloads.Add(job);
            UiThemeService.Apply("Dark");
        }
    }
}
