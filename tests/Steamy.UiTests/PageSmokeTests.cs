using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Pages;
using Steamy.ViewModels;
using Steamy.Services;
using Steamy.Controls;
using System.Windows.Threading;
using System.IO.Compression;
using System.Net.Http;
using System.Net;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static volatile string _phase = "Starting";
    [Fact]
    public void RealPagesLoadTheirResourcesAndLayoutInBothThemes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { ExercisePages(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(180)), "WPF smoke test did not finish within 180 seconds. Last phase: " + _phase);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void ExercisePages()
    {
        // A plain test Application avoids production startup/exit side effects. Load the
        // same compiled dictionaries as App.xaml; WPF cannot load App.xaml into a subclass
        // declared in a different assembly.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary());
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Steamy;component/Resources/Themes/Dark.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Steamy;component/Resources/Styles.xaml") });
        var services = new ServiceCollection().AddSteamyServices();
        // Real view models, no downloader processes, network clients, database or user credentials.
        foreach (var type in services.Select(s => s.ServiceType).Where(t => t.IsInterface).Distinct().ToArray())
            services.AddSingleton(type, _ => OfflineServiceProxy.Create(type));
        var store = new AppDataStore();
        store.Downloads.Add(new DownloadJob { GameName = "A game ready to continue", State = DownloadJobState.Paused, Progress = 42.5, Status = "Paused — existing files are retained", TargetFolder = "C:\\Games\\Example", DownloadMode = "DepotDownloaderMod (Zaza)" });
        store.Downloads.Add(new DownloadJob { GameName = "A completed download", State = DownloadJobState.Completed, Progress = 100, Status = "Download completed", DownloadMode = "DepotDownloader" });
        store.Downloads.Add(new DownloadJob { GameName = "A free Sushi source download", State = DownloadJobState.Paused, Progress = 61, Status = "Paused · resume keeps the saved manifests", DownloadMode = "DepotDownloaderMod (Sushi)" });
        var betterSteamTools = new BetterSteamToolsFixture();
        services.AddSingleton<IBetterSteamToolsService>(betterSteamTools);
        var fixtureArtwork = new FixtureArtwork();
        services.AddSingleton<IArtworkService>(fixtureArtwork);
        services.AddSingleton<ISpotlightService>(new FixtureSpotlight());
        var wizardQueue = new WizardQueueFixture();
        services.AddSingleton<IDownloadManager>(wizardQueue);
        services.AddSingleton<IDownloadQueueStore>(wizardQueue);
        var wizardDownloads = new WizardDownloadFixture();
        services.AddSingleton<IRyuuGameDownloadService>(wizardDownloads);
        var activity = new MemoryActivityFixture();
        services.AddSingleton<IGameActivityService>(activity);
        var navigation = new NavigationService();
        services.AddSingleton<INavigationService>(navigation);
        services.AddSingleton<IAppDataStore>(store);
        services.AddSingleton<ISettingsService>(new MemorySettings());
        using var provider = services.BuildServiceProvider();
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, provider);
        var bindingLog = new BindingLog();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingLog);
        try
        {
            _phase = "CheckBurstUpdates()";
            CheckBurstUpdates();
            _phase = "CheckLoggingBatches()";
            CheckLoggingBatches();
            _phase = "CheckReducedEffectsNativeWindow()";
            CheckReducedEffectsNativeWindow();
            _phase = "CheckSettingsCache()";
            CheckSettingsCache();
            _phase = "CheckArtworkDecoding()";
            CheckArtworkDecoding();
            _phase = "CheckScrollReversal()";
            CheckScrollReversal();
            _phase = "CheckDlcSelection(provider)";
            CheckDlcSelection(provider);
            _phase = "CheckSushiImport(provider)";
            CheckSushiImport(provider);
            _phase = "CheckFeaturedGames(provider, fixtureArtwork, navigation)";
            CheckFeaturedGames(provider, fixtureArtwork, navigation);
            _phase = "CheckDashboardSearch(provider)";
            CheckDashboardSearch(provider);
            _phase = "CheckPersonalDashboard(provider)";
            CheckPersonalDashboard(provider, activity);
            _phase = "CheckReleaseTransition(provider)";
            CheckReleaseTransition(provider);
            _phase = "CheckDepotQueue(provider)";
            CheckDepotQueue(provider);
            _phase = "CheckDownloadsControls(provider)";
            CheckDownloadsControls(provider);
            _phase = "CheckDownloadWizard(provider)";
            CheckDownloadWizard(provider, wizardDownloads, wizardQueue);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK")))
            {
                AddScreenshotLibrary(store, fixtureArtwork);
                activity.ToggleFavoriteAsync(1091500).GetAwaiter().GetResult();
                activity.ToggleFavoriteAsync(1245620).GetAwaiter().GetResult();
                activity.RecordLaunchAsync(1091500, DateTimeOffset.UtcNow.AddHours(-2)).GetAwaiter().GetResult();
            }
            foreach (var theme in new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme);
                _phase = "Library dialog " + theme;
                CheckLibraryDialog(provider, theme);
                foreach (var page in new Page[] { new DashboardPage(), new DownloadsPage(), new SettingsPage(), new DepotDownloaderPage(), new LibraryPage(), new DenuvoActivationPage(), new HypervisorFixesPage(), new GameFixesPage(), new CreamApiPage(), new BetterSteamToolsPage() })
                {
                    _phase = theme + " " + page.GetType().Name;
                    PumpDispatcher(TimeSpan.FromMilliseconds(100));
                    foreach (var size in new[] { new Size(780, 560), new Size(1280, 800) })
                    {
                        page.Width = size.Width;
                        page.Height = size.Height;
                        page.Measure(size);
                        page.Arrange(new Rect(size));
                        page.UpdateLayout();
                        Assert.Equal(size.Width, page.ActualWidth);
                        Assert.Equal(size.Height, page.ActualHeight);
                        Assert.NotNull(page.DataContext);
                        if (page is DashboardPage or LibraryPage or BetterSteamToolsPage)
                        {
                            var background = Assert.IsType<SolidColorBrush>(page.Background).Color;
                            Assert.True(theme == "Light" ? background.R > 220 && background.G > 220 && background.B > 220
                                : background.R < 40 && background.G < 40 && background.B < 40,
                                "Discovery, Games and SteamTools must use a legible background after a theme switch.");
                        }
                        if (page is DashboardPage dashboard) CheckDashboardLayout(dashboard);
                        if (page is BetterSteamToolsPage steamTools) CheckSteamToolsLayout(steamTools);
                        SaveScreenshot(page, theme, size);
                    }
                    if (page is LibraryPage)
                    {
                        var viewModel = Assert.IsType<Steamy.ViewModels.LibraryViewModel>(page.DataContext);
                        PumpUntil(() => !viewModel.IsCatalogLoading && viewModel.CatalogItems.Count > 0);
                        viewModel.SelectedSourceFilter = "Sushi";
                        PumpDispatcher(TimeSpan.FromMilliseconds(100));
                        Assert.True(viewModel.PagedCatalogItems.Count > 0, $"Sushi filter returned no rows: {viewModel.CatalogStatus}; {viewModel.SourceFilterHint}");
                        Assert.Single(viewModel.PagedCatalogItems);
                        viewModel.SelectedSourceFilter = "Hubcap";
                        PumpDispatcher(TimeSpan.FromMilliseconds(100));
                        Assert.Empty(viewModel.PagedCatalogItems);
                        viewModel.SelectedSourceFilter = "All sources";
                        PumpUntil(() => viewModel.PagedCatalogItems.Count == 1);
                        var pageChanges = 0;
                        viewModel.PagedCatalogItems.CollectionChanged += (_, _) => pageChanges++;
                        viewModel.SearchText = "this text must never flash as an empty result";
                        viewModel.SearchText = "offline";
                        PumpDispatcher(TimeSpan.FromMilliseconds(350));
                        Assert.Single(viewModel.PagedCatalogItems);
                        Assert.Equal(0, pageChanges); // Unchanged rows keep their containers and loaded artwork.
                        viewModel.SearchText = "offlien library";
                        PumpUntil(() => viewModel.IsTypoMatch);
                        Assert.Equal(10, Assert.Single(viewModel.PagedCatalogItems).AppId);
                        Assert.Equal(10, Assert.Single(viewModel.SearchSuggestions).AppId);
                        Assert.Equal(0, pageChanges);
                        viewModel.SearchText = "no game has this title 987654";
                        PumpUntil(() => viewModel.PagedCatalogItems.Count == 0);
                        Assert.Equal(1, pageChanges);
                        viewModel.SearchText = string.Empty;
                        PumpUntil(() => viewModel.PagedCatalogItems.Count == 1);
                    }
                    if (page is DownloadsPage)
                    {
                        var viewModel = Assert.IsType<Steamy.ViewModels.DownloadsViewModel>(page.DataContext);
                        viewModel.SelectedSourceFilter = "Sushi";
                        Assert.Single(viewModel.FilteredJobs);
                        viewModel.SelectedFilter = "Completed";
                        Assert.Empty(viewModel.FilteredJobs);
                        viewModel.ClearFiltersCommand.Execute(null);
                        Assert.Equal(3, viewModel.FilteredJobs.Count);
                    }
                    if (page is SettingsPage settings)
                    {
                        var search = Assert.IsAssignableFrom<TextBox>(settings.FindName("SettingsSearch"));
                        search.Text = "resume";
                        PumpDispatcher(TimeSpan.FromMilliseconds(250));
                        var sections = Assert.IsType<StackPanel>(settings.FindName("SectionsPanel"));
                        Assert.Contains(sections.Children.Cast<FrameworkElement>(), section => section.Visibility == Visibility.Visible);
                        search.Text = "this-setting-does-not-exist-987654";
                        PumpDispatcher(TimeSpan.FromMilliseconds(250));
                        Assert.DoesNotContain(sections.Children.Cast<FrameworkElement>(), section => section.Visibility == Visibility.Visible);
                        search.Clear();
                        PumpDispatcher(TimeSpan.FromMilliseconds(250));
                    }
                }
            }
            _phase = "CheckBetterSteamTools";
            CheckBetterSteamTools(provider, betterSteamTools);
            _phase = "CheckShellScrolling(provider)";
            CheckShellScrolling(provider);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK")))
                SaveShellScreenshots(provider, fixtureArtwork);
            Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Steamy.ViewModels", StringComparison.Ordinal) && line.Contains("property not found", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingLog);
            app.Shutdown();
        }
    }

    private static void CheckFeaturedGames(IServiceProvider provider, FixtureArtwork artwork, NavigationService navigation)
    {
        var model = provider.GetRequiredService<Steamy.ViewModels.DashboardViewModel>();
        var loading = model.EnsureDiscoveryArtworkAsync();
        PumpUntil(() => loading.IsCompleted);
        loading.GetAwaiter().GetResult();
        Assert.True(model.DiscoverGames.Count >= 3);
        var firstId = model.DiscoverGames[0].Game.AppId;
        Assert.Equal(firstId, model.FeaturedGame!.Game.AppId);
        Assert.Contains(model.DiscoverGames, feature => feature.Metadata.ComingSoon);
        Assert.Equal("UPCOMING", model.DiscoverGames.First(feature => feature.Metadata.ComingSoon).ReleaseStatus);
        model.SelectFeaturedCommand.Execute(model.DiscoverGames[2]);
        Assert.Equal(model.DiscoverGames[2], model.FeaturedGame);
        Assert.Single(model.SpotlightPreviews, feature => feature.IsSelected);
        model.SelectFeaturedCommand.Execute(model.DiscoverGames[0]);
        Assert.False(model.HasRecentGames); // Discovery must not pretend these games are installed.
        model.PreviousFeaturedCommand.Execute(null);
        var lastId = model.DiscoverGames[^1].Game.AppId;
        Assert.Equal(lastId, model.FeaturedGame!.Game.AppId);
        PumpUntil(() => artwork.HeroRequests.ContainsKey(lastId));
        model.NextFeaturedCommand.Execute(null);
        model.NextFeaturedCommand.Execute(null);
        Assert.Equal(model.DiscoverGames[1].Game.AppId, model.FeaturedGame!.Game.AppId);
        model.PreviousFeaturedCommand.Execute(null);
        PumpDispatcher(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, artwork.HeroRequests[firstId]); // Returning to a slide reuses its image task.
        var library = provider.GetRequiredService<Steamy.ViewModels.LibraryViewModel>();
        library.SelectedSourceFilter = "Sushi";
        library.SelectedTypeFilter = "DLC";
        Type? route = null;
        navigation.Attach(page => route = page);
        model.ViewFeaturedCommand.Execute(null);
        Assert.Equal(typeof(LibraryPage), route);
        Assert.Equal(firstId.ToString(System.Globalization.CultureInfo.InvariantCulture), library.SearchText);
        Assert.Equal("All sources", library.SelectedSourceFilter);
        Assert.Equal("All games", library.SelectedTypeFilter);
        model.DownloadFeaturedCommand.Execute(null);
        Assert.Equal(typeof(LibraryPage), route);
        Assert.Equal(firstId, library.RequestedDownload!.AppId);
        library.RequestedDownload = null;
        model.OpenSpotlightCommand.Execute(model.DiscoverGames[1]);
        Assert.Same(model.DiscoverGames[1], model.SelectedDiscovery);
        Assert.Null(library.RequestedDownload);
        model.BrowseDiscoverySourcesCommand.Execute(null);
        Assert.Equal(model.DiscoverGames[1].Game.AppId, library.RequestedDownload!.AppId);
        Assert.False(model.HasSelectedDiscovery);
        library.RequestedDownload = null;
        library.SearchText = string.Empty;
        navigation.Detach();
    }

    private static void AddScreenshotLibrary(AppDataStore store, FixtureArtwork artwork)
    {
        var names = new[] { (1245620, "ELDEN RING", "61.1 GB"), (1091500, "Cyberpunk 2077", "86.3 GB"),
            (1174180, "Red Dead Redemption 2", "119 GB"), (2358720, "Black Myth: Wukong", "128 GB") };
        foreach (var (id, name, size) in names)
        {
            _phase = "Read sample artwork " + id;
            var game = new Game { AppId = id, Name = name, Size = size, InstallState = GameInstallState.Installed, LastUpdated = DateTime.Today.AddMinutes(-store.Games.Count) };
            artwork.LoadAsync(game).GetAwaiter().GetResult();
            store.Games.Add(game);
        }
        PumpDispatcher(TimeSpan.FromMilliseconds(100));
    }

    private static void SaveShellScreenshots(IServiceProvider provider, FixtureArtwork artwork)
    {
        _phase = "Prepare shell screenshots";
        UiThemeService.Apply("Dark");
        Application.Current.Resources["ArtworkImage"] = new ScreenshotArtworkConverter();
        var jobs = provider.GetRequiredService<IAppDataStore>().Downloads;
        jobs.Clear();
        jobs.Add(new DownloadJob { AppId = 1091500, GameName = "Cyberpunk 2077", State = DownloadJobState.Downloading,
            Progress = 42.5, Downloaded = "36.7 GB", TotalSize = "86.3 GB", Speed = "12.4 MB/s", BytesPerSecond = 12.4 * 1024 * 1024,
            Eta = "1h 08m", EtaSeconds = 4080, Status = "Downloading depot 1 of 2", DownloadMode = "DepotDownloaderMod (Sushi)", TargetFolder = @"C:\Games\Cyberpunk 2077" });
        jobs.Add(new DownloadJob { AppId = 1245620, GameName = "ELDEN RING", State = DownloadJobState.Paused,
            Progress = 61, Downloaded = "37.3 GB", TotalSize = "61.1 GB", Status = "Paused — existing files and manifests are retained", DownloadMode = "DepotDownloaderMod (Zaza)", TargetFolder = @"C:\Games\ELDEN RING" });
        jobs.Add(new DownloadJob { AppId = 2358720, GameName = "Black Myth: Wukong", State = DownloadJobState.Queued,
            TotalSize = "128 GB", Status = "Ready when you are", DownloadMode = "DepotDownloader", TargetFolder = @"C:\Games\Wukong" });
        var window = new MainWindow { WindowState = WindowState.Normal, Width = 1600, Height = 1050 };
        var dashboard = provider.GetRequiredService<DashboardViewModel>();
        if (dashboard.DiscoverGames.Count > 0) dashboard.SelectFeaturedCommand.Execute(dashboard.DiscoverGames[0]);
        _phase = "Show main window";
        window.Show();
        MoveCursorAway(0, 0);
        PumpDispatcher(TimeSpan.FromMilliseconds(500));
        window.UpdateLayout();
        SaveVisual(window, "dashboard.png");
        var dashboardPage = Descendants<DashboardPage>(window).Single();
        var discoveryScroll = (ScrollViewer)dashboardPage.FindName("DashboardScroll");
        discoveryScroll.ScrollToVerticalOffset(((Border)dashboardPage.FindName("DashboardHero")).ActualHeight + 80);
        window.UpdateLayout();
        SaveVisual(window, "discover-games.png");
        dashboard.OpenSpotlightCommand.Execute(dashboard.DiscoverGames[0]);
        PumpDispatcher(TimeSpan.FromMilliseconds(150));
        window.UpdateLayout();
        var discoveryPanel = (Border)dashboardPage.FindName("DiscoveryDetailsPanel");
        Assert.Equal(255, Assert.IsType<System.Windows.Media.SolidColorBrush>(discoveryPanel.Background).Color.A);
        Assert.False(discoveryScroll.IsEnabled);
        SaveVisual(window, "upcoming-details.png");
        dashboard.CloseDiscoveryCommand.Execute(null);
        discoveryScroll.ScrollToTop();
        foreach (var (route, filename) in new[] { (typeof(DownloadsPage), "downloads.png"), (typeof(SettingsPage), "settings.png") })
        {
            _phase = "Navigate " + route.Name;
            Assert.True(window.RootNavigationView.Navigate(route));
            PumpDispatcher(TimeSpan.FromMilliseconds(500));
            window.UpdateLayout();
            SaveVisual(window, filename);
            if (route == typeof(DownloadsPage))
                Assert.DoesNotContain(Descendants<TextBlock>(window), text => text.Text == "NETWORK ACTIVITY");
            if (route == typeof(SettingsPage))
            {
                var settingsPage = Descendants<SettingsPage>(window).Single();
                ((Wpf.Ui.Controls.TextBox)settingsPage.FindName("SettingsSearch")).Text = "transfer";
                PumpUntil(() => Descendants<TextBlock>(settingsPage).Any(text => text.IsVisible && text.Text == "TRANSFER CONTROLS"));
                window.UpdateLayout();
                var transferHeading = Descendants<TextBlock>(settingsPage).Single(text => text.IsVisible && text.Text == "TRANSFER CONTROLS");
                var settingsScroll = (ScrollViewer)settingsPage.FindName("SettingsScrollViewer");
                var position = transferHeading.TranslatePoint(new Point(0, 0), settingsScroll);
                settingsScroll.ScrollToVerticalOffset(settingsScroll.VerticalOffset + position.Y - 24);
                window.UpdateLayout();
                PumpDispatcher(TimeSpan.FromMilliseconds(100));
                SaveVisual(window, "settings-transfers.png");
                ((Wpf.Ui.Controls.TextBox)settingsPage.FindName("SettingsSearch")).Text = "";
            }
        }
        Assert.True(window.RootNavigationView.Navigate(typeof(CreamApiPage)));
        PumpDispatcher(TimeSpan.FromMilliseconds(150));
        var dlcModel = provider.GetRequiredService<Steamy.ViewModels.CreamApiViewModel>();
        dlcModel.GameFolder = @"C:\Games\Cyberpunk 2077";
        dlcModel.AppIdText = "1091500";
        dlcModel.FilteredGames.Add(new InstalledGameEntry(1091500, "Cyberpunk 2077", dlcModel.GameFolder));
        dlcModel.FilteredGames.Add(new InstalledGameEntry(1245620, "ELDEN RING", @"C:\Games\ELDEN RING"));
        dlcModel.DlcList.Add(new Steamy.ViewModels.CreamApiDlcItem { AppId = 2138330, Name = "Cyberpunk 2077: Phantom Liberty" });
        dlcModel.NotifyDlcCountChanged();
        dlcModel.Status = "Cyberpunk 2077 selected · 1 DLC available";
        window.UpdateLayout();
        SaveVisual(window, "dlc-unlocker.png");
        OfflineServiceProxy.ScreenshotCatalog = new[] { (2322010, "God of War Ragnarök"), (1245620, "ELDEN RING"),
            (1091500, "Cyberpunk 2077"), (1174180, "Red Dead Redemption 2"), (2358720, "Black Myth: Wukong"), (1086940, "Baldur’s Gate 3") }
            .Concat(SpotlightCatalogService.LoadBundled().Games.Where(game => !game.ComingSoon).Select(game => (game.AppId, game.Name)))
            .DistinctBy(entry => entry.Item1)
            .Select(entry => new SteamCatalogItem { AppId = entry.Item1, Name = entry.Item2, AppType = SteamCatalogAppType.Game,
                ArtworkImage = FixtureArtwork.Read(entry.Item1, "portrait") ?? FixtureArtwork.Read(entry.Item1, "header"),
                HeaderImage = FixtureArtwork.Read(entry.Item1, "header") }).ToArray();
        _phase = "Navigate Library";
        Assert.True(window.RootNavigationView.Navigate(typeof(LibraryPage)));
        var library = provider.GetRequiredService<Steamy.ViewModels.LibraryViewModel>();
        _phase = "Refresh screenshot catalog";
        var refresh = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)library.RefreshCatalogCommand).ExecuteAsync(null);
        PumpUntil(() => refresh.IsCompleted);
        refresh.GetAwaiter().GetResult();
        PumpUntil(() => library.PagedCatalogItems.Count == Math.Min(library.PageSize, OfflineServiceProxy.ScreenshotCatalog.Length) && !library.IsCatalogLoading);
        PumpDispatcher(TimeSpan.FromMilliseconds(400));
        window.UpdateLayout();
        var page = Descendants<LibraryPage>(window).Single();
        var hoveredCard = Descendants<Button>(page).Single(button => button.Tag is SteamCatalogItem item && ReferenceEquals(item, library.PagedCatalogItems[0]));
        window.Activate();
        _phase = "Capture gallery without hover";
        var outsideCard = page.PointToScreen(new Point(page.ActualWidth - 12, 12));
        Assert.True(MoveCursorAway((int)Math.Round(outsideCard.X), (int)Math.Round(outsideCard.Y)));
        System.Windows.Input.Mouse.Synchronize();
        PumpUntil(() => !hoveredCard.IsMouseOver);
        PumpDispatcher(TimeSpan.FromMilliseconds(200));
        var galleryPanels = Descendants<Steamy.Controls.AdaptiveGridPanel>(page).Where(panel => panel.IsVisible && panel.Children.Count >= 5).ToArray();
        Assert.Single(galleryPanels);
        Assert.Null(page.FindName("UpcomingSection"));
        Assert.All(library.PagedCatalogItems, item => Assert.False(item.IsUpcoming));
        foreach (var galleryPanel in galleryPanels)
        {
            var firstFive = galleryPanel.Children.Cast<FrameworkElement>().Take(5).ToArray();
            var positions = firstFive.Select(child => child.TranslatePoint(new Point(0, 0), galleryPanel)).ToArray();
            Assert.All(positions, point => Assert.Equal(positions[0].Y, point.Y));
            Assert.True(firstFive[0].ActualWidth < 180, "The native Games window should fit five compact covers per row.");
        }
        SaveVisual(window, "games.png");
        _phase = "Hover a gallery card";
        var cardSize = new Size(hoveredCard.ActualWidth, hoveredCard.ActualHeight);
        var cardPosition = hoveredCard.TranslatePoint(new Point(0,0), page);
        var pointer = hoveredCard.PointToScreen(new Point(hoveredCard.ActualWidth / 2, hoveredCard.ActualHeight / 2));
        Assert.True(MoveCursorAway((int)Math.Round(pointer.X), (int)Math.Round(pointer.Y)));
        System.Windows.Input.Mouse.Synchronize();
        PumpUntil(() => hoveredCard.IsMouseOver);
        PumpDispatcher(TimeSpan.FromMilliseconds(300));
        Assert.Equal(cardSize, new Size(hoveredCard.ActualWidth, hoveredCard.ActualHeight));
        Assert.Equal(cardPosition, hoveredCard.TranslatePoint(new Point(0,0), page));
        SaveVisual(window, "games-hover.png");
        var screenshots = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS")!;
        Assert.False(File.ReadAllBytes(Path.Combine(screenshots,"games.png"))
            .SequenceEqual(File.ReadAllBytes(Path.Combine(screenshots,"games-hover.png"))),
            "The native mouse hover must produce a visible card highlight.");
        MoveCursorAway(0,0);
        PumpDispatcher(TimeSpan.FromMilliseconds(200));
        _phase = "Open game details";
        page.OpenDownloadSetup(library.PagedCatalogItems.First(item => item.AppId == 1091500));
        PumpDispatcher(TimeSpan.FromMilliseconds(350));
        window.UpdateLayout();
        SaveVisual(window, "game-details.png");
        Assert.Equal(255, Assert.IsType<System.Windows.Media.SolidColorBrush>(((Border)page.FindName("DialogPanel")).Background).Color.A);
        page.SelectDownloadSource(ManifestSource.Local);
        PumpDispatcher(TimeSpan.FromMilliseconds(150));
        window.UpdateLayout();
        SaveVisual(window, "local-package.png");
        page.SelectDownloadSource(ManifestSource.Sushi);
        _phase = "Prepare screenshot depot choices";
        AwaitWizardStep(page);
        Assert.Equal(1, page.DownloadWizardStep);
        window.UpdateLayout();
        SaveVisual(window, "download-depots.png");
        _phase = "Prepare screenshot download location";
        AwaitWizardStep(page);
        Assert.Equal(2, page.DownloadWizardStep);
        page.ConfigureDownloadLocation(@"C:\Games");
        PumpUntil(() => !((TextBlock)page.FindName("LocationSpaceText")).Text.StartsWith("Checking", StringComparison.Ordinal));
        window.UpdateLayout();
        SaveVisual(window, "download-location.png");
        page.CloseOverlay();
        PumpDispatcher(TimeSpan.FromMilliseconds(250));
        Assert.True(window.RootNavigationView.Navigate(typeof(HypervisorFixesPage)));
        PumpDispatcher(TimeSpan.FromMilliseconds(300));
        SaveVisual(window, "hypervisor-fixes.png");
        Assert.True(window.RootNavigationView.Navigate(typeof(GameFixesPage)));
        var fixesModel = provider.GetRequiredService<GameFixesViewModel>();
        var fixesFetch = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)fixesModel.FetchCommand).ExecuteAsync(null);
        PumpUntil(() => fixesFetch.IsCompleted);
        fixesFetch.GetAwaiter().GetResult();
        PumpDispatcher(TimeSpan.FromMilliseconds(300));
        window.UpdateLayout();
        SaveVisual(window, "game-fixes.png");
        Assert.True(window.RootNavigationView.Navigate(typeof(BetterSteamToolsPage)));
        provider.GetRequiredService<BetterSteamToolsViewModel>().GameInput = "";
        PumpDispatcher(TimeSpan.FromMilliseconds(250));
        window.UpdateLayout();
        SaveVisual(window, "better-steamtools.png");
        window.Close();
    }

    private sealed class ScreenshotArtworkConverter : System.Windows.Data.IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var match = System.Text.RegularExpressions.Regex.Match(value?.ToString() ?? "", @"/apps/(\d+)/");
            return match.Success ? FixtureArtwork.Read(int.Parse(match.Groups[1].Value), "header") : null;
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool MoveCursorAway(int x, int y);

    private static void SaveVisual(MainWindow window, string filename)
    {
        _phase = "Capture " + filename;
        // Capture the complete native WPF window at its actual size. Windows runners
        // may constrain the window to their virtual desktop; never pad a clipped image.
        var width = (int)window.ActualWidth;
        var height = (int)window.ActualHeight;
        var folder = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS")!;
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        foreach (var point in new[] { new Int32Rect(width - 20, height / 2, 1, 1), new Int32Rect(width / 2, height - 20, 1, 1) })
        {
            var pixel = new byte[4];
            bitmap.CopyPixels(point, pixel, 4, 0);
            Assert.Equal(255, pixel[3]);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(folder, filename));
        encoder.Save(output);
    }

    private static void CheckReleaseTransition(IServiceProvider provider)
    {
        var library = provider.GetRequiredService<LibraryViewModel>();
        var spotlight = Assert.IsType<FixtureSpotlight>(provider.GetRequiredService<ISpotlightService>());
        var original = spotlight.Cached;
        var previousSource = library.SelectedSourceFilter;
        var previousSearch = library.SearchText;
        var upcoming = original.Games.First(game => game.ComingSoon);
        try
        {
            library.SearchText = string.Empty;
            library.SelectedSourceFilter = "All sources";
            OfflineServiceProxy.ScreenshotCatalog = new[] {
                new SteamCatalogItem { AppId = upcoming.AppId, Name = upcoming.Name, IsUpcoming = true }
            };
            var refresh = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)library.RefreshCatalogCommand).ExecuteAsync(null);
            PumpUntil(() => refresh.IsCompleted);
            refresh.GetAwaiter().GetResult();
            PumpDispatcher(TimeSpan.FromMilliseconds(200));
            Assert.Empty(library.PagedCatalogItems);

            // Steam's refreshed feed now confirms the same title released. An old item flag must not hide it.
            spotlight.Cached = new SpotlightSnapshot(DateTimeOffset.UtcNow,
                original.Games.Select(game => game.AppId == upcoming.AppId ? game with { ComingSoon = false } : game).ToArray());
            refresh = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)library.RefreshCatalogCommand).ExecuteAsync(null);
            PumpUntil(() => refresh.IsCompleted);
            refresh.GetAwaiter().GetResult();
            PumpUntil(() => library.PagedCatalogItems.Count == 1);
            Assert.Equal(upcoming.AppId, Assert.Single(library.PagedCatalogItems).AppId);
            Assert.False(library.PagedCatalogItems[0].IsUpcoming);
        }
        finally
        {
            spotlight.Cached = original;
            OfflineServiceProxy.ScreenshotCatalog = null;
            library.SelectedSourceFilter = previousSource;
            library.SearchText = previousSearch;
            var refresh = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)library.RefreshCatalogCommand).ExecuteAsync(null);
            PumpUntil(() => refresh.IsCompleted);
            refresh.GetAwaiter().GetResult();
            PumpUntil(() => library.PagedCatalogItems.Count == 1 && library.PagedCatalogItems[0].AppId == 10);
        }
    }

    private sealed class FixtureSpotlight : ISpotlightService
    {
        public SpotlightSnapshot Cached { get; set; } = SpotlightCatalogService.LoadBundled();
        public Task<SpotlightSnapshot> GetAsync(bool force = false, CancellationToken cancellationToken = default) => Task.FromResult(Cached);
    }

    internal sealed class FixtureArtwork : IArtworkService
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, string), BitmapImage> Images = new();
        public System.Collections.Concurrent.ConcurrentDictionary<int, int> HeroRequests { get; } = new();
        public Task<BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default)
        {
            HeroRequests.AddOrUpdate(appId, 1, (_, count) => count + 1);
            return Task.FromResult(Read(appId, "hero"));
        }
        public Task LoadAsync(Game game, CancellationToken cancellationToken = default)
        {
            game.HeaderImage = Read(game.AppId, "header");
            game.ArtworkImage = Read(game.AppId, "portrait") ?? game.HeaderImage;
            return Task.CompletedTask;
        }
        public Task LoadHeadersAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => LoadManyAsync(games, cancellationToken);
        public async Task LoadManyAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default)
        {
            foreach (var game in games) await LoadAsync(game, cancellationToken);
        }
        public static BitmapImage? Read(int appId, string kind)
        {
            if (Images.TryGetValue((appId, kind), out var cached)) return cached;
            var folder = Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK");
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var path = Path.Combine(folder, $"{appId}_{kind}.jpg");
            if (!File.Exists(path)) return null;
            var nativeWidth = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad).PixelWidth;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = Math.Min(nativeWidth, kind == "hero" ? 2048 : kind == "portrait" ? 600 : 460);
            image.EndInit();
            image.Freeze();
            Images.TryAdd((appId, kind), image);
            return image;
        }
    }

    private static void CheckDashboardSearch(IServiceProvider provider)
    {
        var model = provider.GetRequiredService<Steamy.ViewModels.DashboardViewModel>();
        var settings = provider.GetRequiredService<ISettingsService>().Load();
        Assert.False(settings.DashboardSearch);
        settings.DashboardSearch = true;
        model.RefreshSearchPreference();
        var requests = OfflineServiceProxy.CatalogRequests;
        model.SearchText = "offline";
        PumpUntil(() => !model.IsSearchBusy);
        Assert.Equal(10, Assert.Single(model.SearchResults).AppId);
        model.SearchText = "this query should be superseded";
        model.SearchText = "OFFLINE library";
        PumpUntil(() => !model.IsSearchBusy);
        Assert.Equal(10, Assert.Single(model.SearchResults).AppId);
        Assert.Equal(10, Assert.Single(model.SearchMatches).Item.AppId);
        model.SearchText = "offlien library";
        PumpUntil(() => !model.IsSearchBusy);
        Assert.Equal(10, Assert.Single(model.SearchResults).AppId);
        Assert.Contains("Closest", model.SearchStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(requests + 1, OfflineServiceProxy.CatalogRequests);
        var navigation = Assert.IsType<NavigationService>(provider.GetRequiredService<INavigationService>());
        Type? route = null;
        navigation.Attach(page => route = page);
        try
        {
            model.OpenSearchCommand.Execute(null);
            Assert.Equal(typeof(LibraryPage), route);
            model.SearchText = string.Empty;
            model.IsSearchFocused = true;
            Assert.False(model.ShowSearchPanel, "An empty focused search must not open a history panel.");
        }
        finally { navigation.Detach(); model.IsSearchFocused = false; }
        model.SearchText = string.Empty;
        Assert.Empty(model.SearchResults);
        Assert.False(model.HasSearchQuery);
        settings.DashboardSearch = false;
        model.RefreshSearchPreference();
        Assert.Empty(model.SearchMatches);
        Assert.False(model.ShowSearchPanel);
        Assert.True(SteamCatalogQuery.MatchesSearch("God of War Ragnarök", 2322010, "god ragnarok"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SteamCatalogQuery.FilterAndSort(
            new[] { new SteamCatalogItem { AppId = 10, Name = "Offline" } }, "off", "All games", "Name A–Z", cancellationToken: cancellation.Token));
    }

    private static void CheckArtworkDecoding()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-artwork-smoke-" + Guid.NewGuid().ToString("N"));
        using var handler = new ArtworkHandler();
        using var client = new HttpClient(handler);
        using var service = new SteamArtworkService(client, directory);
        try
        {
            var small = Task.Run(() => service.LoadHeroAsync(1)).GetAwaiter().GetResult();
            Assert.Equal(16, small!.PixelWidth); // Small CDN headers must never be enlarged in memory.
            Assert.True(small.IsFrozen);
            handler.Width = 3200;
            var cached = Task.Run(() => service.LoadHeroAsync(1)).GetAwaiter().GetResult();
            Assert.Equal(16, cached!.PixelWidth);
            Assert.Equal(1, handler.Requests); // Revisiting a game reads the local cache.
            var large = Task.Run(() => service.LoadHeroAsync(2)).GetAwaiter().GetResult();
            Assert.Equal(2048, large!.PixelWidth);
            var spotlight = SpotlightCatalogService.LoadBundled().Games[0];
            var hero = Task.Run(() => service.LoadSpotlightHeroAsync(spotlight)).GetAwaiter().GetResult();
            Assert.True(hero!.IsFrozen);
            Assert.Equal(spotlight.HeroUrl, handler.LastUri!.AbsoluteUri);
            var requestCount = handler.Requests;
            Task.Run(() => service.LoadSpotlightHeroAsync(spotlight)).GetAwaiter().GetResult();
            Assert.Equal(requestCount, handler.Requests);
            var revised = spotlight with { HeroUrl = spotlight.HeroUrl + "&revision=2" };
            Task.Run(() => service.LoadSpotlightHeroAsync(revised)).GetAwaiter().GetResult();
            Assert.Equal(requestCount + 1, handler.Requests); // A changed Steam asset URL invalidates its cache.
            Task.Run(() => service.LoadSpotlightHeaderAsync(spotlight)).GetAwaiter().GetResult();
            Assert.Equal(spotlight.HeaderUrl, handler.LastUri!.AbsoluteUri);
            handler.TimeoutNextRequest = true;
            var recovered = Task.Run(() => service.LoadHeroAsync(3)).GetAwaiter().GetResult();
            Assert.NotNull(recovered); // A CDN timeout still tries the alternate artwork host.
            Assert.Contains("cdn.akamai", handler.LastUri!.Host);
            var blockedCache = Path.Combine(directory, "blocked-cache");
            File.WriteAllText(blockedCache, "This is a file, not a cache directory.");
            using var uncachedClient = new HttpClient(new ArtworkHandler());
            using var uncached = new SteamArtworkService(uncachedClient, blockedCache);
            Assert.NotNull(Task.Run(() => uncached.LoadHeroAsync(4)).GetAwaiter().GetResult());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class ArtworkHandler : HttpMessageHandler
    {
        public int Width { get; set; } = 16;
        public int Requests { get; private set; }
        public Uri? LastUri { get; private set; }
        public bool TimeoutNextRequest { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUri = request.RequestUri;
            if (TimeoutNextRequest)
            {
                TimeoutNextRequest = false;
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated CDN timeout"));
            }
            var source = BitmapSource.Create(Width, 2, 96, 96, PixelFormats.Bgra32, null, new byte[Width * 2 * 4], Width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) });
        }
    }

    private static void CheckBurstUpdates()
    {
        var notifications = 0;
        var job = new DownloadJob();
        job.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(DownloadJob.Status)) notifications++; };
        Task.Run(() => { for (var index = 0; index < 10000; index++) job.Status = $"Message {index}"; }).GetAwaiter().GetResult();
        PumpDispatcher(TimeSpan.FromMilliseconds(30));
        Assert.Equal("Message 9999", job.Status);
        Assert.Equal(1, notifications);
        var received = new List<string>();
        using (var progress = new BufferedDownloadProgress(received.Add))
        {
            Task.Run(() =>
            {
                for (var index = 0; index < 10000; index++) { progress.Report($"Line {index}"); progress.Report($"PROGRESS|{index}"); }
            }).GetAwaiter().GetResult();
            PumpDispatcher(TimeSpan.FromMilliseconds(180));
            Assert.Equal(new[] { "Line 9999", "PROGRESS|9999" }, received);
        }
        PumpDispatcher(TimeSpan.FromMilliseconds(150));
        Assert.Equal(2, received.Count);
    }

    private static void CheckSettingsCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-settings-smoke-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, "{\"Appearance\":\"Light\"}");
            var service = new JsonSettingsService(path);
            var settings = service.Load();
            Assert.Equal("Light", settings.Appearance);
            File.WriteAllText(path, "not valid JSON");
            Assert.Same(settings, service.Load());
            Assert.False(settings.DashboardSearch);
            settings.DashboardSearch = true;
            settings.Appearance = "Dark";
            service.SaveAsync(settings).GetAwaiter().GetResult();
            var reloaded = new JsonSettingsService(path).Load();
            Assert.Equal("Dark", reloaded.Appearance);
            Assert.True(reloaded.DashboardSearch);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            service.ResetAsync().GetAwaiter().GetResult();
            Assert.NotSame(settings, service.Load());
            Assert.False(service.Load().DashboardSearch);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) PumpDispatcher(TimeSpan.FromMilliseconds(20));
        Assert.True(condition(), "The asynchronous UI operation did not settle within 5 seconds.");
    }

    private static void CheckSushiImport(IServiceProvider provider)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Steamy-source-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var transport = new SushiTransport();
            using var client = new HttpClient(transport);
            using var service = new ManifestSourceService(provider.GetRequiredService<ISettingsService>(), provider.GetRequiredService<ISecureCredentialService>(),
                provider.GetRequiredService<IRyuuSecureDownloadService>(), provider.GetRequiredService<ILoggingService>(), client, directory);
            var available = Task.Run(() => service.CheckAvailabilityAsync(ManifestSource.Sushi, 10)).GetAwaiter().GetResult();
            Assert.True(available.Available && available.Certain);
            var imported = Task.Run(() => service.DownloadManifestsAsync(ManifestSource.Sushi, 10)).GetAwaiter().GetResult();
            Assert.True(imported.Succeeded, imported.Message);
            Assert.Equal("addappid(10)", imported.LuaContent);
            Assert.True(File.Exists(Path.Combine(imported.WorkDirectory!, "1_123.manifest")));
            Assert.False(File.Exists(Path.Combine(imported.WorkDirectory!, "untrusted.exe")));
            transport.Status = HttpStatusCode.NotFound;
            var missing = Task.Run(() => service.CheckAvailabilityAsync(ManifestSource.Sushi, 999)).GetAwaiter().GetResult();
            Assert.False(missing.Available);
            Assert.True(missing.Certain);
            transport.Status = HttpStatusCode.ServiceUnavailable;
            var unreachable = Task.Run(() => service.CheckAvailabilityAsync(ManifestSource.Sushi, 10)).GetAwaiter().GetResult();
            Assert.False(unreachable.Certain);
            transport.Status = HttpStatusCode.OK;
            transport.CorruptArchive = true;
            var corrupt = Task.Run(() => service.DownloadManifestsAsync(ManifestSource.Sushi, 10)).GetAwaiter().GetResult();
            Assert.False(corrupt.Succeeded);
            Assert.Equal("addappid(10)", File.ReadAllText(Path.Combine(imported.WorkDirectory!, "10.lua")));
            Assert.Empty(Directory.GetFiles(imported.WorkDirectory!, "*.zip"));
            Assert.Empty(Directory.GetDirectories(imported.WorkDirectory!));
            var second = Task.Run(() => { transport.CorruptArchive = false; return service.DownloadManifestsAsync(ManifestSource.Sushi, 10); }).GetAwaiter().GetResult();
            Assert.True(second.Succeeded, second.Message);
            Assert.NotEqual(imported.WorkDirectory, second.WorkDirectory);
            Assert.True(File.Exists(Path.Combine(imported.WorkDirectory!, "1_123.manifest")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class SushiTransport : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool CorruptArchive { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Contains("sushi-dev55/sushitools-games-repo", request.RequestUri!.AbsoluteUri);
            var response = new HttpResponseMessage(Status);
            if (request.RequestUri.Host == "raw.githubusercontent.com")
            {
                if (CorruptArchive) response.Content = new StringContent("this is not a ZIP");
                else
                {
                    using var output = new MemoryStream();
                    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                        foreach (var item in new[] { ("10.lua", "addappid(10)"), ("1_123.manifest", "manifest data"), ("untrusted.exe", "never run") })
                        {
                            using var writer = new StreamWriter(zip.CreateEntry(item.Item1).Open());
                            writer.Write(item.Item2);
                        }
                    response.Content = new ByteArrayContent(output.ToArray());
                }
            }
            else response.Content = new StringContent("{}");
            return Task.FromResult(response);
        }
    }

    private static void SaveScreenshot(Page page, string theme, Size size)
    {
        var folder = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(folder, $"{theme}-{page.GetType().Name}-{size.Width}.png"));
        encoder.Save(output);
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly AppSettings _settings = new() { AutoUpdate = false };
        public AppSettings Load() => _settings;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BindingLog : TraceListener
    {
        public List<string> Lines { get; } = new();
        public override void Write(string? message) { if (message is not null) Lines.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}

// Constructors may subscribe to events and read optional credentials. Any real operation is rejected.
public class OfflineServiceProxy : DispatchProxy
{
    public static int CatalogRequests;
    public static SteamCatalogItem[]? ScreenshotCatalog;
    public static object Create(Type interfaceType) => DispatchProxy.Create(interfaceType, typeof(OfflineServiceProxy));
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod ?? throw new InvalidOperationException();
        if (method.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal)) return null;
        if (method.DeclaringType == typeof(ISecureCredentialService) && method.Name == nameof(ISecureCredentialService.ReadAsync)) return Task.FromResult<string?>(null);
        if (method.DeclaringType == typeof(ILoggingService)) return null;
        if (method.DeclaringType == typeof(IGameLocatorService) && method.Name == nameof(IGameLocatorService.ListInstalledGames)) return Array.Empty<InstalledGameEntry>();
        if (method.DeclaringType == typeof(ICreamApiService) && method.Name == nameof(ICreamApiService.HasCachedDlls)) return true;
        if (method.DeclaringType == typeof(IDenuvoGeneratorDownloadService))
        {
            if (method.Name == "get_HasCachedExecutable") return false;
            if (method.Name == "get_CachedPath") return null;
            if (method.Name == nameof(IDenuvoGeneratorDownloadService.DownloadLatestAsync))
                return Task.FromResult(new DenuvoGeneratorDownloadResult(false, "Offline fixture", null, null));
        }
        if (method.DeclaringType == typeof(ILibrarySyncService) && method.Name == nameof(ILibrarySyncService.RefreshAsync))
            return Task.FromResult(SteamLibraryScanResult.Failure("Offline UI fixture"));
        if (method.DeclaringType == typeof(IFixCatalogService))
            return Task.FromResult(new FixFeedSnapshot(true, (ScreenshotCatalog ?? new[] { new SteamCatalogItem { AppId = 10, Name = "An offline library game" } })
                .Select(game => new FixGame { AppId = game.AppId.ToString(), Name = game.Name,
                    Fixes = new[] { new FixEntry { Filename = "Sample fix.zip", Path = "sample.zip", Size = "Offline sample" } } }).ToArray(), DateTimeOffset.UtcNow, true, "Offline sample fixes"));
        if (method.DeclaringType == typeof(ISteamCatalogService) && method.Name == nameof(ISteamCatalogService.GetCatalogAsync))
        {
            Interlocked.Increment(ref CatalogRequests);
            return Task.FromResult(new SteamCatalogSnapshot(true, ScreenshotCatalog ?? new[] { new SteamCatalogItem { AppId = 10, Name = "An offline library game", AppType = SteamCatalogAppType.Game } }, DateTimeOffset.UtcNow, true, "Offline fixture"));
        }
        if (method.DeclaringType == typeof(ISteamCatalogService) && method.Name == nameof(ISteamCatalogService.PrepareReleaseStatusAsync))
        {
            foreach (var item in (IReadOnlyList<SteamCatalogItem>)args![0]!) item.IsReleaseVerified = true;
            return Task.CompletedTask;
        }
        if (method.DeclaringType == typeof(ISteamCatalogService) && method.Name == nameof(ISteamCatalogService.PrepareArtworkAsync)) return Task.CompletedTask;
        if (method.DeclaringType == typeof(ISteamCatalogService) && method.Name == nameof(ISteamCatalogService.EnsureArtworkAsync))
        {
            var item = (SteamCatalogItem)args![0]!;
            item.ArtworkImage ??= PageSmokeTests.FixtureArtwork.Read(item.AppId, "portrait");
            item.HeaderImage ??= PageSmokeTests.FixtureArtwork.Read(item.AppId, "header");
            return Task.FromResult(item.ArtworkImage is not null);
        }
        if ((method.DeclaringType == typeof(IRyuuCatalogService) || method.DeclaringType == typeof(IHubcapCatalogService)) && method.Name == "GetGamesAsync")
            return Task.FromResult(SteamCatalogSnapshot.Failure("Offline fixture"));
        if (method.DeclaringType == typeof(IManifestSourceService) && method.Name == nameof(IManifestSourceService.CheckAvailabilityAsync)) return Task.FromResult(new ManifestAvailability(true, true, "Available · offline fixture"));
        if (method.DeclaringType == typeof(IManifestSourceService) && method.Name == "get_Sources")
            return Enum.GetValues<ManifestSource>().Select(source => new ManifestSourceInfo(source, source.ToString(),
                source == ManifestSource.Sushi ? "Free Lua metadata and depot manifests · no API key" : $"{source} manifest source",
                "https://example.invalid/", source is ManifestSource.Ryuu or ManifestSource.Hubcap or ManifestSource.DepotBox)).ToArray();
        if (method.DeclaringType == typeof(IFreeManifestCatalogService)) return Task.FromResult(new FreeManifestIndex(true, ScreenshotCatalog?.Select(item => item.AppId).ToHashSet() ?? new HashSet<int> { 10 }, "Offline fixture"));
        if (method.Name == nameof(IDisposable.Dispose)) return null;
        throw new InvalidOperationException($"UI smoke attempted an external operation: {method.DeclaringType?.Name}.{method.Name}");
    }
}
