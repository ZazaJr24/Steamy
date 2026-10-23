using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;
using Steamy.Controls;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckLibraryInstallStepsOffscreen(LibraryPage page, IServiceProvider provider, WizardDownloadFixture downloads)
    {
        UiThemeService.Apply("Dark");
        RenderLibraryInstallStep(page, "games-grid");
        var gallery = (ItemsControl)page.FindName("LibraryGallery");
        Assert.DoesNotContain(Descendants<TextBlock>(gallery), text => text.Text == "Game");
        var card = Descendants<Button>(gallery).First(button => button.Tag is SteamCatalogItem { AppId: 1593500 });
        var ambient = ((ShellBackdropView)page.FindName("LibraryAmbientBackdrop")).BaseBrush;
        card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
        PumpDispatcher(TimeSpan.FromMilliseconds(180));
        Assert.Same(ambient, ((ShellBackdropView)page.FindName("LibraryAmbientBackdrop")).BaseBrush);
        card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
        PumpDispatcher(TimeSpan.FromMilliseconds(180));
        Assert.Same(ambient, ((ShellBackdropView)page.FindName("LibraryAmbientBackdrop")).BaseBrush);

        UiThemeService.ConfigureBackground(new AppSettings { AmbientEnabled = false, BackgroundColor = "#000000" });
        Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(((ShellBackdropView)page.FindName("LibraryAmbientBackdrop")).BaseBrush).Color);
        UiThemeService.ConfigureBackground(new AppSettings { AmbientEnabled = true, BackgroundColor = "#000000" });
        UiThemeService.ApplyGlassPolicy(true);
        Assert.Same(SystemColors.WindowBrush, ((ShellBackdropView)page.FindName("LibraryAmbientBackdrop")).BaseBrush);
        UiThemeService.ApplyGlassPolicy(false);
        Assert.True(((ShellBackdropView)page.FindName("LibraryAmbientBackdrop")).BaseBrush.IsFrozen);
        if (!UiThemeService.IsLight) Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(((ShellBackdropView)page.FindName("LibraryAmbientBackdrop")).BaseBrush).Color);


        // A normal click opens the game page, including games already installed.
        var navigation = provider.GetRequiredService<INavigationService>();
        Type? route = null;
        navigation.Attach(target => route = target);
        var detailsModel = provider.GetRequiredService<GameDetailsViewModel>();
        card.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(typeof(GameDetailsPage), route);
        Assert.Equal(1593500, detailsModel.Game!.AppId);
        Assert.True(detailsModel.IsInstalled);
        Assert.True(detailsModel.PlayCommand.CanExecute(null));
        var initialGame = detailsModel.Game!;
        var gameStore = provider.GetRequiredService<IAppDataStore>(); var gameIndex = gameStore.Games.IndexOf(initialGame);
        gameStore.Games[gameIndex] = new Game { AppId = initialGame.AppId, Name = initialGame.Name, InstallState = GameInstallState.Updating, InstallFolder = "C:\\New game folder" };
        Assert.False(detailsModel.PlayCommand.CanExecute(null));
        Assert.Equal("C:\\New game folder", detailsModel.Game!.InstallFolder);
        gameStore.Games[gameIndex] = initialGame;
        Assert.True(detailsModel.PlayCommand.CanExecute(null));
        Assert.False(detailsModel.CanInstall);
        Assert.Equal(Visibility.Collapsed, ((Grid)page.FindName("OverlayGrid")).Visibility);
        detailsModel.BackCommand.Execute(null);
        Assert.Equal(typeof(LibraryPage), route);

        // Install is a separate action on the real game page, tested without showing a window.
        var installCard = Descendants<Button>(gallery).First(button => button.Tag is SteamCatalogItem { AppId: 1808500 });
        var originalLibrary = page;
        installCard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(typeof(GameDetailsPage), route);
        originalLibrary.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        var details = new GameDetailsPage();
        details.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        try
        {
        RenderLibraryInstallStep(details, "game-page");
        var statusStore = provider.GetRequiredService<IAppDataStore>();
        var statusJob = new DownloadJob { AppId = detailsModel.Game!.AppId, GameName = detailsModel.Name, State = DownloadJobState.Downloading, Progress = 38 };
        statusStore.Downloads.Add(statusJob); PumpDispatcher(TimeSpan.FromMilliseconds(50));
        Assert.Same(statusJob, detailsModel.Download); Assert.Equal("Downloading", detailsModel.InstallLabel);
        var queuedCopy = new DownloadJob { AppId = statusJob.AppId, State = DownloadJobState.Queued };
        statusStore.Downloads.Add(queuedCopy); Assert.Same(statusJob, detailsModel.Download);
        statusStore.Downloads.Remove(queuedCopy);
        Assert.Contains(Descendants<TextBlock>((FrameworkElement)details.FindName("InstallButton")), text => text.Text == "Downloading");
        statusJob.Progress = 62; Assert.Equal(62, detailsModel.Download!.Progress);
        RenderLibraryInstallStep(details, "game-downloading");
        statusJob.State = DownloadJobState.Paused; Assert.Equal("Paused", detailsModel.InstallLabel);
        statusStore.Downloads.Remove(statusJob); Assert.False(detailsModel.HasDownload); Assert.Equal("Install", detailsModel.InstallLabel);
        UiThemeService.Apply("Dark");
        RenderLibraryInstallStep(new SteamlessPage(), "steamless-page");
        var activation = new DenuvoActivationPage();
        provider.GetRequiredService<DenuvoActivationViewModel>().DummyPackage = true;
        RenderLibraryInstallStep(activation, "denuvo-page");
        var ucPage = new UcOnlinePage(provider.GetRequiredService<UcOnlineViewModel>());
        RenderLibraryInstallStep(ucPage, "uc-online-page");
        Descendants<Expander>(ucPage).Single(expander => expander.Header as string == "Settings").IsExpanded = true;
        RenderLibraryInstallStep(ucPage, "uc-online-settings");
        Descendants<Expander>(ucPage).Single(expander => expander.Header as string == "Backups and restore").IsExpanded = true;
        var host = new Wpf.Ui.Controls.NavigationView { Template = (ControlTemplate)Application.Current.FindResource("GlassNavigationTemplate"), Transition = Wpf.Ui.Animations.Transition.None };
        host.SetPageProviderService(new NavigationViewPageProvider(provider));
        UcOnlinePage? hostedPage = null; host.Navigated += (_, args) => hostedPage = args.Page as UcOnlinePage;
        host.ApplyTemplate(); host.Measure(new Size(1280, 800)); host.Arrange(new Rect(0, 0, 1280, 800));
        host.Navigate(typeof(UcOnlinePage)); host.UpdateLayout(); PumpDispatcher(TimeSpan.FromMilliseconds(100));
        Assert.NotNull(hostedPage);
        Descendants<Expander>(hostedPage).Single(expander => expander.Header as string == "Settings").IsExpanded = true;
        Descendants<Expander>(hostedPage).Single(expander => expander.Header as string == "Backups and restore").IsExpanded = true;
        RenderLibraryInstallStep(host, "uc-navigation-host");
        var ucScroll = (ScrollViewer)hostedPage.FindName("ToolScroll");
        Assert.True(ucScroll.ScrollableHeight > 0, "The expanded tool must scroll inside its real navigation host.");
        Assert.InRange(ucScroll.ViewportHeight, 100, 800);
        ucScroll.ScrollToEnd(); PumpDispatcher(TimeSpan.FromMilliseconds(80));
        Assert.True(ucScroll.VerticalOffset > 0);
        var restore = Descendants<Button>(hostedPage).Single(button => button.Content as string == "Restore original files");
        Assert.Equal(Visibility.Visible, restore.Visibility);
        Assert.InRange(restore.TransformToAncestor(host).Transform(new Point()).Y, 0, 790);
        RenderLibraryInstallStep(host, "uc-settings-bottom");
        var scrollJobs = Enumerable.Range(0, 40).Select(index => new DownloadJob { AppId = 1808500, GameName = "Scroll fixture " + index, State = DownloadJobState.Completed, Progress = 100 }).ToArray();
        foreach (var job in scrollJobs) statusStore.Downloads.Add(job);
        DownloadsPage? hostedDownloads = null; host.Navigated += (_, args) => hostedDownloads = args.Page as DownloadsPage;
        host.Navigate(typeof(DownloadsPage)); RenderLibraryInstallStep(host, "downloads-navigation-host");
        Assert.NotNull(hostedDownloads);
        var queue = (ListBox)hostedDownloads.FindName("DownloadQueue");
        var queueScroll = Descendants<ScrollViewer>(queue).First();
        Assert.True(queueScroll.ScrollableHeight > 0); Assert.InRange(queueScroll.ViewportHeight, 50, 800);
        queueScroll.ScrollToEnd(); PumpDispatcher(TimeSpan.FromMilliseconds(100)); Assert.True(queueScroll.VerticalOffset > 0);
        Assert.Equal("✕", ((Button)hostedDownloads.FindName("DownloadOptionsClose")).Content);
        Assert.EndsWith("…", ((Wpf.Ui.Controls.TextBox)hostedDownloads.FindName("DownloadSearch")).PlaceholderText);
        foreach (var job in scrollJobs) statusStore.Downloads.Remove(job);
        PumpUntil(() => ReferenceEquals(details.AppliedAmbientArtwork, detailsModel.HeroArtwork));
        var ambientBrush = ((Border)details.FindName("GameDetailsWarmGradient")).Background;
        Assert.True(ambientBrush.IsFrozen);
        if (detailsModel.HeroArtwork is not null && UiThemeService.AmbientEnabled && !UiThemeService.IsHighContrastActive)
            Assert.IsType<DrawingBrush>(ambientBrush);
        OpenInstallFromGamePage();
        RenderLibraryInstallStep(details, "install-method");
        CheckWizardStage(page, 0);
        var next = (Button)page.FindName("StartButton");
        var back = (Button)page.FindName("BackButton");
        Assert.False(next.IsEnabled);
        Assert.Contains(Descendants<TextBlock>((FrameworkElement)page.FindName("MethodStepPanel")), text => text.Text == "Download in Steamy");
        Assert.Contains(Descendants<TextBlock>((FrameworkElement)page.FindName("MethodStepPanel")), text => text.Text == "Install with Steam");

        ((Button)page.FindName("MethodDepotDownloader")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(next.IsEnabled);
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        RenderLibraryInstallStep(details, "install-source");
        CheckWizardStage(page, 1);
        Assert.Equal(Visibility.Visible, back.Visibility);
        Assert.Equal("Next", next.Content);
        page.SelectDownloadSource(ManifestSource.Local);
        Assert.False(next.IsEnabled); // A local package is required before the next step.
        var dimmer = (Border)page.FindName("DialogDimmer");
        void ReleaseDimmer() => dimmer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        page.ChooseLocalPackage(_ =>
        {
            page.CloseOverlay();
            ReleaseDimmer();
            Assert.True(details.IsInstallationOpen);
            return Path.Combine(Path.GetTempPath(), "2183900.zip");
        });
        ReleaseDimmer(); // Returning from the native picker is not a backdrop click.
        Assert.True(details.IsInstallationOpen);
        Assert.Equal(Visibility.Visible, ((Grid)page.FindName("OverlayGrid")).Visibility);
        Assert.Equal("2183900.zip", ((TextBlock)page.FindName("LocalPackageName")).Text);
        Assert.Equal(1, page.DownloadWizardStep);
        Assert.True(next.IsEnabled);
        page.ChooseLocalPackage(_ => null);
        Assert.True(details.IsInstallationOpen);
        Assert.Equal("2183900.zip", ((TextBlock)page.FindName("LocalPackageName")).Text);
        var outsideClickSheet = new LibraryPage(installationOnly: true);
        outsideClickSheet.OpenDownloadSetup(new SteamCatalogItem { AppId = 2183900, Name = "Space Marine 2" });
        var outsideDimmer = (Border)outsideClickSheet.FindName("DialogDimmer");
        outsideDimmer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        outsideDimmer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Assert.Equal(Visibility.Collapsed, ((Grid)outsideClickSheet.FindName("OverlayGrid")).Visibility);
        page.SelectDownloadSource(ManifestSource.Sushi);
        PumpUntil(() => next.IsEnabled);
        downloads.PrepareOverride = (_, _, _) => Task.FromResult(new GameDownloadPreparation(false, "Hubcap: The file contains unsupported Lua code. Select addappid/setManifestid metadata."));
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => ((TextBlock)page.FindName("SourceAvailabilityText")).Text.Contains("not compatible"));
        Assert.False(next.IsEnabled);
        Assert.Equal(1, page.DownloadWizardStep);
        Assert.Empty(downloads.Downloads);
        Assert.Contains("Choose another source", ((TextBlock)page.FindName("OverlayStatus")).Text);
        downloads.PrepareOverride = null;
        page.SelectDownloadSource(ManifestSource.Sushi);
        PumpUntil(() => next.IsEnabled);
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => page.DownloadWizardStep == 2);
        RenderLibraryInstallStep(details, "install-files");
        CheckWizardStage(page, 2);
        Assert.Equal(2, page.DownloadDepotChoices.Count);
        Assert.Empty(downloads.Downloads); // Reviewing files never starts a download.
        foreach (var choice in page.DownloadDepotChoices) choice.IsSelected = false;
        Assert.False(next.IsEnabled);
        var choiceToKeep = page.DownloadDepotChoices[0];
        var checkbox = Descendants<CheckBox>((FrameworkElement)page.FindName("DepotStepPanel"))
            .Single(control => ReferenceEquals(control.DataContext, choiceToKeep));
        checkbox.IsChecked = true;
        Assert.True(choiceToKeep.IsSelected);
        choiceToKeep.SelectedVersion = choiceToKeep.Versions[1];
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => page.DownloadWizardStep == 3 && next.IsEnabled);
        RenderLibraryInstallStep(details, "install-folder");
        CheckWizardStage(page, 3);
        Assert.Equal("Install game", next.Content);
        Assert.Empty(downloads.Downloads);
        back.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(2, page.DownloadWizardStep);
        Assert.Equal("101", choiceToKeep.SelectedVersion!.ManifestId);
        Assert.False(page.DownloadDepotChoices[1].IsSelected);
        back.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(1, page.DownloadWizardStep);
        back.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(0, page.DownloadWizardStep);
        Assert.Equal(Visibility.Collapsed, back.Visibility);
        ((Button)page.FindName("SetupCloseButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);
        Assert.Empty(downloads.Downloads);

        OpenInstallFromGamePage();
        ((Button)page.FindName("MethodBetterSteamTools")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(1, page.DownloadWizardStep);
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        RenderLibraryInstallStep(details, "install-steam");
        CheckWizardStage(page, 2);
        Assert.Equal("Add to Steam", next.Content);
        Assert.True(next.IsEnabled);
        Assert.Empty(downloads.Downloads);
        back.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(1, page.DownloadWizardStep);
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => Equals(next.Content, "Open in Steam"));
        Assert.Equal((1808500, (ManifestSource?)ManifestSource.Sushi),
            Assert.IsType<BetterSteamToolsFixture>(provider.GetRequiredService<IBetterSteamToolsService>()).LastSource);
        Assert.Empty(downloads.Downloads); // Steam configuration uses its own adapter, and Steam is not opened here.
        page.CloseOverlay();
        PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);

        // The context menu also provides a direct install shortcut, laid out offscreen.
        var menu = card.ContextMenu!;
        menu.PlacementTarget = card;
        menu.Measure(new Size(220, 80)); menu.Arrange(new Rect(0, 0, 220, 80));
        PumpDispatcher(TimeSpan.FromMilliseconds(80));
        Assert.Same(card.Tag, ((MenuItem)menu.Items[0]).Tag);
        var shortcut = (MenuItem)menu.Items[1];
        Assert.Equal("Install…", shortcut.Header);
        Assert.Same(card.Tag, shortcut.Tag);
        shortcut.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        CheckWizardStage(originalLibrary, 0);
        originalLibrary.CloseOverlay();
        PumpUntil(() => ((Grid)originalLibrary.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);

        // Compact windows keep the actions inside the sheet, with a scrolling body.
        foreach (var size in new[] { new Size(980, 620), new Size(660, 430) })
        {
            details.Measure(size); details.Arrange(new Rect(size)); details.UpdateLayout();
            OpenInstallFromGamePage();
            RenderLibraryInstallStep(details, $"install-method-{size.Width}", size);
            var dialog = (FrameworkElement)page.FindName("DialogPanel");
            Assert.True(dialog.ActualHeight <= size.Height - 30);
            var footer = (FrameworkElement)page.FindName("DownloadFooter");
            var footerTop = footer.TranslatePoint(new Point(0, 0), dialog).Y;
            Assert.True(footerTop >= 0 && footerTop + footer.ActualHeight <= dialog.ActualHeight + 1);
            page.CloseOverlay();
            PumpUntil(() => ((Grid)page.FindName("OverlayGrid")).Visibility == Visibility.Collapsed);
        }

        // The final button uses the exact selected source, files and folder with the offline adapter.
        detailsModel.SelectCatalogGame(new SteamCatalogItem { AppId = 10, Name = "Offline install test" });
        OpenInstallFromGamePage();
        ((Button)page.FindName("MethodDepotDownloader")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => page.DownloadWizardStep == 2);
        page.DownloadDepotChoices[0].SelectedVersion = page.DownloadDepotChoices[0].Versions[1];
        page.DownloadDepotChoices[1].IsSelected = false;
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var fixtureFolder = Path.Combine(Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") ?? Path.GetTempPath(), "fixture-download");
        page.ConfigureDownloadLocation(fixtureFolder);
        PumpUntil(() => next.IsEnabled);
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var store = provider.GetRequiredService<IAppDataStore>();
        PumpUntil(() => store.Downloads.Any(job => job.AppId == 10 && job.State == DownloadJobState.Completed));
        var downloaded = Assert.Single(downloads.Downloads);
        Assert.Equal(ManifestSource.Sushi, downloaded.Plan.Source);
        Assert.Equal("101", Assert.Single(downloaded.Selections).ManifestId);
        Assert.Equal(Path.GetFullPath(Path.Combine(fixtureFolder, "Offline install test")), Path.GetFullPath(downloaded.TargetFolder));
        Assert.Equal(Visibility.Collapsed, ((Grid)page.FindName("OverlayGrid")).Visibility);
        Assert.Equal(typeof(DownloadsPage), route);
        }
        finally
        {
            details.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            originalLibrary.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            navigation.Detach();
        }

        void OpenInstallFromGamePage()
        {
            Assert.False(details.IsInstallationOpen);
            Assert.True(detailsModel.CanInstall);
            Assert.False(detailsModel.PlayCommand.CanExecute(null));
            var install = (Button)details.FindName("InstallButton");
            Assert.Equal(Visibility.Visible, install.Visibility);
            var previousRoute = route;
            install.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            details.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(80));
            Assert.Equal(previousRoute, route); // Opening the choice must not navigate away.
            Assert.True(details.IsInstallationOpen);
            Assert.Null(provider.GetRequiredService<LibraryViewModel>().RequestedDownload);
            var previousSheet = page == originalLibrary ? null : page;
            page = Assert.IsType<LibraryPage>(details.InstallationSheet);
            if (previousSheet is not null) Assert.Same(previousSheet, page);
            Assert.Contains(Descendants<LibraryPage>(details), child => ReferenceEquals(child, page));
            Assert.Equal(Visibility.Collapsed, ((Grid)page.FindName("MainContentGrid")).Visibility);
            Assert.Empty(downloads.Downloads);
        }
    }

    private static void RenderLibraryInstallStep(FrameworkElement page, string name, Size? requested = null)
    {
        var size = requested ?? new Size(1280, 800);
        page.Measure(size); page.Arrange(new Rect(size)); page.UpdateLayout();
        PumpDispatcher(TimeSpan.FromMilliseconds(220));
        page.Measure(size); page.Arrange(new Rect(size)); page.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        if (name == "games-grid")
        {
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(4, (int)size.Height - 2, 1, 1), pixel, 4, 0);
            Assert.True(pixel.Take(3).Max() <= 36, "The background must remain dark without a gray band or an abrupt black footer zone.");
        }
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name + ".png")); png.Save(output);
    }
}
