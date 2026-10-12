using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;
using Steamy.Views;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckStorageLibrary(IServiceProvider provider, AppDataStore store, FixtureArtwork artwork)
    {
        AddScreenshotLibrary(store, artwork);
        var original = Application.Current.MainWindow;
        var window = new MainWindow { Width = 1440, Height = 900, WindowState = WindowState.Normal, ShowInTaskbar = false };
        Application.Current.MainWindow = window; window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(300));
        try
        {
            var ownedIds = store.Games.Select(game => game.AppId).ToHashSet();
            OfflineServiceProxy.ScreenshotCatalog = store.Games.Select(game => new SteamCatalogItem { AppId = game.AppId, Name = game.Name, AppType = SteamCatalogAppType.Game, ArtworkImage = game.ArtworkImage, IsReleaseVerified = true })
                .Concat([new SteamCatalogItem { AppId = 1196590, Name = "Resident Evil Village", AppType = SteamCatalogAppType.Game, IsReleaseVerified = true },
                    new SteamCatalogItem { AppId = 374320, Name = "DARK SOULS III", AppType = SteamCatalogAppType.Game, IsReleaseVerified = true },
                    new SteamCatalogItem { AppId = 1808500, Name = "ARC Raiders", AppType = SteamCatalogAppType.Game, IsReleaseVerified = true }]).ToArray();
            var model = provider.GetRequiredService<LibraryViewModel>();
            Assert.Equal("Browse all", model.SelectedMode);
            model.SelectedMode="Your games";
            provider.GetRequiredService<INavigationService>().Navigate<LibraryPage>();
            var refresh = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)model.RefreshCatalogCommand).ExecuteAsync(null);
            PumpUntil(() => refresh.IsCompleted && model.PagedCatalogItems.Count == ownedIds.Count);
            Assert.All(model.PagedCatalogItems, item => Assert.Contains(item.AppId, ownedIds));
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            var libraryPage = Descendants<LibraryPage>(window).Single();
            var libraryScene = (Grid)libraryPage.FindName("LibraryScene");
            PumpDispatcher(TimeSpan.FromMilliseconds(160));
            var persistentGradient = Assert.IsType<System.Windows.Media.DrawingBrush>(libraryScene.Background);
            Assert.True(persistentGradient.IsFrozen);
            Assert.Same(persistentGradient, window.FindResource("WorkspaceBackgroundBrush"));
            var gameTile = Descendants<Button>(libraryPage).First(button => button.Tag is SteamCatalogItem);
            gameTile.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            PumpDispatcher(TimeSpan.FromMilliseconds(150));
            Assert.Same(persistentGradient, libraryScene.Background);
            gameTile.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
            PumpDispatcher(TimeSpan.FromMilliseconds(150));
            Assert.Same(persistentGradient, libraryScene.Background); // Ambient is permanent; hovering cannot change it.
            UiThemeService.ApplyGlassPolicy(true);
            Assert.Same(SystemColors.WindowBrush, libraryScene.Background);
            UiThemeService.ApplyGlassPolicy(false);
            Assert.IsType<System.Windows.Media.DrawingBrush>(libraryScene.Background);
            Assert.Equal("All genres", model.SelectedGenre);
            model.SearchText = "Resident Evil Village";
            PumpUntil(() => !model.IsSearchBusy && model.PagedCatalogItems.Count == 0);
            model.SearchText = "Baldur";
            PumpUntil(() => !model.IsSearchBusy && model.PagedCatalogItems.Count == 1);
            Assert.Contains(model.PagedCatalogItems[0].AppId, ownedIds);
            model.SearchText = "";
            PumpUntil(() => model.PagedCatalogItems.Count == ownedIds.Count);
            window.UpdateLayout(); SaveGameDetailsVisual(window, "installed-library");
            Assert.True(((Steamy.Controls.ShellBackdropView)window.FindName("WorkspaceBackdrop")).IsPageBackdrop);
            model.SelectedMode = "For you";
            PumpUntil(() => model.PagedCatalogItems.Any(item => item.AppId == 1196590));
            Assert.All(model.PagedCatalogItems, item => Assert.DoesNotContain(item.AppId, ownedIds));
            Assert.All(model.PagedCatalogItems, item => Assert.NotEmpty(item.RecommendationLabel));
            window.UpdateLayout(); SaveGameDetailsVisual(window, "recommendations");
            var catalogGame = model.PagedCatalogItems.Single(item => item.AppId == 1808500);
            Descendants<Button>(libraryPage).Single(button => button.Tag is SteamCatalogItem item && item.AppId == catalogGame.AppId).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => Descendants<GameDetailsPage>(window).Any());
            var details = Descendants<GameDetailsPage>(window).Single();
            var detailModel = (GameDetailsViewModel)details.DataContext;
            Assert.True(detailModel.CanInstall);
            Assert.False(detailModel.PlayCommand.CanExecute(null));
            Assert.True(((Button)details.FindName("InstallButton")).IsVisible);
            Assert.False(((Button)details.FindName("PlayButton")).IsVisible);
            SaveGameDetailsVisual(window, "catalog-install-page");
            ((Button)details.FindName("InstallButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => details.IsInstallationOpen);
            Assert.Same(details, Descendants<GameDetailsPage>(window).Single());
            Assert.Equal(catalogGame.AppId, ((GameDetailsViewModel)details.DataContext).CatalogGame!.AppId);
            var installPage = Assert.IsType<LibraryPage>(details.InstallationSheet);
            Assert.True(((Grid)installPage.FindName("OverlayGrid")).IsVisible);
            installPage.CloseOverlay();
            detailModel.BackCommand.Execute(null);
            PumpUntil(() => Descendants<LibraryPage>(window).Any());
            model.SelectedMode = "Browse all";
            PumpUntil(() => model.PagedCatalogItems.Count > ownedIds.Count);

            var settings = new FirstRunMemorySettings();
            var notice = new TransparencyNotice(settings);
            var completed = false; notice.Completed += (_, _) => completed = true;
            window.ShowOverlay(notice); PumpDispatcher(TimeSpan.FromMilliseconds(250)); window.UpdateLayout();
            Assert.False(((Button)notice.FindName("ContinueButton")).IsEnabled);
            var scroll = (ScrollViewer)notice.FindName("NoticeScroll"); Assert.True(scroll.ScrollableHeight > 0);
            scroll.ScrollToEnd(); PumpDispatcher(TimeSpan.FromMilliseconds(100));
            var checkbox = (CheckBox)notice.FindName("Acknowledge"); Assert.True(checkbox.IsEnabled); checkbox.IsChecked = true;
            var button = (Button)notice.FindName("ContinueButton"); Assert.True(button.IsEnabled);
            SaveGameDetailsVisual(window, "transparency-notice");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => completed); Assert.Equal(TransparencyNotice.CurrentAcknowledgement, settings.Load().TransparencyAcknowledgement);

            // Opening another overlay during a fade must not cause the new setup to disappear.
            window.HideOverlay();
            var setup = new FirstRunSetup(new FirstRunMemorySettings(), new ReadyDownloaderFixture(), gameScanner: new ReadyScannerFixture());
            window.ShowOverlay(setup); PumpDispatcher(TimeSpan.FromMilliseconds(250));
            Assert.True(setup.IsVisible);
            Assert.True(((FrameworkElement)setup.FindName("GameScanStep")).IsVisible);
            SaveGameDetailsVisual(window, "installed-onboarding");
            window.Width = 980; window.Height = 620; window.UpdateLayout();
            window.HideOverlay(); PumpDispatcher(TimeSpan.FromMilliseconds(180));
            provider.GetRequiredService<INavigationService>().Navigate<SettingsPage>();
            PumpUntil(() => Descendants<SettingsPage>(window).Any());
            var page = Descendants<SettingsPage>(window).Single();
            var selector = (ListBox)page.FindName("CategorySelector");
            selector.SelectedItem = selector.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, "transparency"));
            window.UpdateLayout();
            var transparency = (Steamy.Controls.StorageTransparencyView)page.FindName("TransparencyView");
            Assert.False(((TextBox)transparency.FindName("DataFolderInput")).IsReadOnly);
            Assert.Equal(Steamy.Storage.AppDataPaths.Root, ((TextBox)transparency.FindName("DataFolderInput")).Text);
            SaveGameDetailsVisual(window, "storage-location-small");
        }
        finally { window.Close(); Application.Current.MainWindow = original; OfflineServiceProxy.ScreenshotCatalog = null; }
    }
}
