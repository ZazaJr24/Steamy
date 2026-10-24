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
    private static void TracePhase(string phase)
    {
        _phase = phase;
        if (Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "phases.txt"), $"{DateTimeOffset.Now:O} {phase}{Environment.NewLine}");
        }
    }
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(600)), "WPF smoke test did not finish within 600 seconds. Last phase: " + _phase);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void ExercisePages()
    {
        DiscordIntegration.TestOverride = Environment.GetEnvironmentVariable("STEAMY_UI_DISCORD_REMOVED_ONLY") != "1";
        TracePhase("Creating WPF application");
        // A plain test Application avoids production startup/exit side effects. Load the
        // same compiled dictionaries as App.xaml; WPF cannot load App.xaml into a subclass
        // declared in a different assembly.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Steamy;component/Resources/Themes/Dark.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Steamy;component/Resources/Styles.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Steamy;component/Resources/Shell.xaml") });
        TracePhase("WPF resources loaded");
        var services = new ServiceCollection().AddSteamyServices();
        // Real view models, no downloader processes, network clients, database or user credentials.
        foreach (var type in services.Select(s => s.ServiceType).Where(t => t.IsInterface && t.IsVisible).Distinct().ToArray())
            services.AddSingleton(type, _ => OfflineServiceProxy.Create(type));
        var store = new AppDataStore();
        services.AddSingleton<IWindowsControllers>(new NoWindowsControllers());
        store.Downloads.Add(new DownloadJob { GameName = "A game ready to continue", State = DownloadJobState.Paused, Progress = 42.5, Status = "Paused — existing files are retained", TargetFolder = "C:\\Games\\Example", DownloadMode = "DepotDownloaderMod (Zaza)" });
        store.Downloads.Add(new DownloadJob { GameName = "A completed download", State = DownloadJobState.Completed, Progress = 100, Status = "Download completed", DownloadMode = "DepotDownloader" });
        store.Downloads.Add(new DownloadJob { GameName = "A free Sushi source download", State = DownloadJobState.Paused, Progress = 61, Status = "Paused · resume keeps the saved manifests", DownloadMode = "DepotDownloaderMod (Sushi)" });
        var betterSteamTools = new BetterSteamToolsFixture();
        services.AddSingleton<IBetterSteamToolsService>(betterSteamTools);
        var fixtureArtwork = new FixtureArtwork();
        services.AddSingleton<IArtworkService>(fixtureArtwork);
        services.AddSingleton<IImagePipeline>(new ScreenshotImagePipeline());
        services.AddSingleton<ISpotlightService>(new FixtureSpotlight());
        services.AddSingleton<ITrailerPreviewService>(new FixtureTrailers());
        var wizardQueue = new WizardQueueFixture();
        services.AddSingleton<IDownloadManager>(wizardQueue);
        services.AddSingleton<IDownloadQueueStore>(wizardQueue);
        var wizardDownloads = new WizardDownloadFixture();
        services.AddSingleton<IRyuuGameDownloadService>(wizardDownloads);
        var activity = new MemoryActivityFixture();
        services.AddSingleton<IGameActivityService>(activity);
        var navigation = new NavigationService();
        services.AddSingleton<INavigationService>(navigation);
        // The production navigation provider is an interface, so replace the generic
        // offline proxy registered above with its real page factory for MainWindow tests.
        services.AddSingleton<Wpf.Ui.Abstractions.INavigationViewPageProvider>(
            _ => new NavigationViewPageProvider(App.Services));
        services.AddSingleton<IAppDataStore>(store);
        services.AddSingleton<ISettingsService>(new MemorySettings());
        if (Environment.GetEnvironmentVariable("STEAMY_UI_SETTINGS_OFFSCREEN_ONLY") == "1")
            services.AddSingleton<ISecureCredentialService>(new SettingsCredentialsFixture());
        services.AddSingleton<IControllerBackend>(new ControllerOfflineFixture());
        if (DiscordIntegration.Enabled) services.AddSingleton<IDiscordSocialService>(Environment.GetEnvironmentVariable("STEAMY_UI_ACCEPTANCE_ONLY") == "1" || Environment.GetEnvironmentVariable("STEAMY_UI_DISCORD_ONLY") == "1" || Environment.GetEnvironmentVariable("STEAMY_UI_WINDOWED_ONLY") == "1" || Environment.GetEnvironmentVariable("STEAMY_UI_REWORK_ONLY") == "1" || Environment.GetEnvironmentVariable("STEAMY_UI_PERFORMANCE_ONLY") == "1" ? new DiscordUiFixture() : new DiscordOfflineFixture());
        var detailsFixture = new GameDetailsFixture();
        services.AddSingleton<IGameDetailsDataService>(detailsFixture);
        services.AddSingleton<IGamePreferencesStore>(detailsFixture);
        services.AddSingleton<IGameLaunchService>(detailsFixture);
        if (Environment.GetEnvironmentVariable("STEAMY_UI_GAME_DETAILS_ONLY") == "1" || Environment.GetEnvironmentVariable("STEAMY_UI_ACCEPTANCE_ONLY") == "1" || Environment.GetEnvironmentVariable("STEAMY_UI_FLOW_OFFSCREEN_ONLY") == "1")
            services.AddSingleton<IPlayTimeService>(detailsFixture);
        TracePhase("Test services registered");
        if (Environment.GetEnvironmentVariable("STEAMY_UI_LOCAL_CATALOG_ONLY") == "1")
            services.AddSingleton<LocalGameCatalog>(_ => new LocalGameCatalog(Path.Combine(Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS")!, "local-catalog.json")));
        using var provider = services.BuildServiceProvider();
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, provider);
        var bindingLog = new BindingLog();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingLog);
        try
        {
            if (Environment.GetEnvironmentVariable("STEAMY_UI_SMOOTH_ONLY") == "1")
            {
                CheckLibrarySmoothnessOffscreen(provider, store, fixtureArtwork);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_DETAILS_FADE_ONLY") == "1")
            {
                CheckDetailsFadeAndStickyBack(provider, store, fixtureArtwork);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_LIBRARY_COLLAPSE_ONLY") == "1")
            {
                CheckLibraryCollapseOffscreen(provider, store, fixtureArtwork);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_FLOW_OFFSCREEN_ONLY") == "1")
            {
                CheckPageFlowsOffscreen(provider, store, fixtureArtwork);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_DISCOVERY_OFFSCREEN_ONLY") == "1")
            {
                CheckDiscoveryLifetimeOffscreen(provider);
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_SETTINGS_OFFSCREEN_ONLY") == "1")
            {
                CheckSettingsOffscreen(provider);
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_CHROME_ONLY") == "1")
            {
                CheckChromeVisuals(provider, store, fixtureArtwork);
                return;
            }
            if (!DiscordIntegration.Enabled)
            {
                CheckDiscordRemoved(provider);
                if (Environment.GetEnvironmentVariable("STEAMY_UI_GAME_DETAILS_ONLY") == "1") CheckGameDetails(provider, store, fixtureArtwork);
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_SHELL_SCREENSHOTS_ONLY") == "1")
            {
                AddScreenshotLibrary(store, fixtureArtwork);
                SaveShellScreenshots(provider, fixtureArtwork);
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_ACCEPTANCE_ONLY") == "1")
            {
                TracePhase("Acceptance: game details and image viewer"); CheckGameDetails(provider, store, fixtureArtwork);
                TracePhase("Acceptance: windowed shell and retained search"); CheckWindowedShell(provider, store, fixtureArtwork);
                TracePhase("Acceptance: GIF playback and release"); CheckGifRuntime();
                TracePhase("Acceptance: Discord and voice controls"); CheckDiscordFriends(); CheckVoiceUi(provider);
                TracePhase("Acceptance: theme, size and render-scale matrix"); CheckReworkShell(provider, store, fixtureArtwork);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                TracePhase("Acceptance complete"); return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_WINDOWED_ONLY") == "1")
            {
                CheckWindowedShell(provider, store, fixtureArtwork);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_PERFORMANCE_ONLY") == "1") { CheckResponsiveness(provider); return; }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_REWORK_ONLY") == "1")
            {
                CheckReworkShell(provider, store, fixtureArtwork);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_DISCORD_ONLY") == "1")
            {
                CheckGifRuntime();
                if (Environment.GetEnvironmentVariable("STEAMY_DISCORD_SDK_CHECK") == "1") { CheckDiscordSdkRuntime(); CheckHostedDiscordSdkRuntime(); }
                CheckFirstRunSetup();
                CheckDiscordFriends();
                CheckVoiceUi(provider);
                CheckNativeFriendsNavigation(provider);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_LIBRARY_INSTALL_OFFSCREEN_ONLY") == "1" || Environment.GetEnvironmentVariable("STEAMY_UI_LIBRARY_BLACK_OFFSCREEN_ONLY") == "1")
            {
                AddScreenshotLibrary(store,fixtureArtwork);
                OfflineServiceProxy.ScreenshotCatalog=store.Games.Select(game=>new SteamCatalogItem
                { AppId=game.AppId,Name=game.Name,AppType=SteamCatalogAppType.Game,ArtworkImage=game.ArtworkImage,IsReleaseVerified=true }).Concat(new[] { (1808500,"ARC Raiders"), (945360,"Among Us"), (553850,"HELLDIVERS 2"), (958520,"Construction Simulator"), (812140,"Assassin's Creed Odyssey"), (4435490,"Fable"), (48190,"Assassin's Creed Brotherhood"), (33230,"Assassin's Creed 2") }.Select(game=>new SteamCatalogItem
                { AppId=game.Item1,Name=game.Item2,AppType=game.Item1==33230 ? SteamCatalogAppType.Unknown : SteamCatalogAppType.Game,ArtworkImage=FixtureArtwork.Read(game.Item1,"portrait") })).ToArray();
                var catalogSize=OfflineServiceProxy.ScreenshotCatalog.Count(item => item.AppType == SteamCatalogAppType.Game);
                var releaseGate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                OfflineServiceProxy.ReleaseMetadataGate=releaseGate.Task;
                var model=provider.GetRequiredService<LibraryViewModel>();
                Assert.Equal("Browse all",model.SelectedMode);
                Assert.Equal("Game",model.SelectedTypeFilter);
                var library=new LibraryPage();
                library.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                try
                {
                    var refresh=((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)model.RefreshCatalogCommand).ExecuteAsync(null);
                    PumpUntil(()=>model.PagedCatalogItems.Count==catalogSize);
                    Assert.DoesNotContain(model.PagedCatalogItems,item=>item.AppType != SteamCatalogAppType.Game);
                    releaseGate.SetResult(); OfflineServiceProxy.ReleaseMetadataGate=null;
                    PumpUntil(()=>refresh.IsCompleted);
                    foreach(var theme in new[] { "Dark","Light" })
                    {
                        UiThemeService.Apply(theme);
                        var size=new Size(1280,800);
                        library.Measure(size); library.Arrange(new Rect(size)); library.UpdateLayout();
                        PumpDispatcher(TimeSpan.FromMilliseconds(200));
                        Assert.True(((ShellBackdropView)library.FindName("LibraryAmbientBackdrop")).BaseBrush.IsFrozen);
                        Assert.Equal(theme == "Light" ? Colors.Black : Colors.White,Assert.IsType<SolidColorBrush>(library.Resources["TextPrimaryBrush"]).Color);
                        Assert.Contains(Descendants<Button>(library),button=>button.Tag is SteamCatalogItem);
                        var image=new RenderTargetBitmap(1280,800,96,96,PixelFormats.Pbgra32); image.Render(library);
                        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                        var directory=Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") ?? Path.GetTempPath();
                        Directory.CreateDirectory(directory);
                        using var file=File.Create(Path.Combine(directory,$"games-grid-{theme.ToLowerInvariant()}.png")); encoder.Save(file);
                    }
                    model.SearchText="Cyberpunk";
                    PumpUntil(()=>!model.IsSearchBusy && model.PagedCatalogItems.Count==1);
                    Assert.Equal(1091500,model.PagedCatalogItems[0].AppId);
                    model.SearchText="";
                    PumpUntil(()=>model.PagedCatalogItems.Count==catalogSize);
                    CheckLibraryInstallStepsOffscreen(library, provider, wizardDownloads);
                    CheckEnglishShellAndNoticeOffscreen(provider);
                    Assert.DoesNotContain(bindingLog.Lines,line=>line.Contains("Error:",StringComparison.OrdinalIgnoreCase));
                }
                finally { releaseGate.TrySetResult(); OfflineServiceProxy.ReleaseMetadataGate=null; library.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_DOWNLOAD_CARD_OFFSCREEN_ONLY") == "1")
            {
                store.Downloads.Clear();
                store.Downloads.Add(new DownloadJob { AppId = 1808500, GameName = "ARC Raiders", State = DownloadJobState.Downloading,
                    TransferredBytes = 130_570_000, TransferTotalBytes = 78_250_000_000, BytesPerSecond = 15_981_250, EtaSeconds = 1200 });
                store.Downloads.Add(new DownloadJob { AppId=289650, GameName="Assassin's Creed Unity", State=DownloadJobState.Queued, QueuePosition=1 });
                store.Downloads.Add(new DownloadJob { AppId=1091500, GameName="Cyberpunk 2077", State=DownloadJobState.Queued, QueuePosition=2, TotalSize="64 GB" });
                var historyJob = new DownloadJob { AppId=1174180, GameName="Red Dead Redemption 2", State=DownloadJobState.Failed, Status="Download failed. Existing files were kept; you can retry or remove this entry." };
                store.Downloads.Add(historyJob);
                var page = new DownloadsPage();
                page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                try
                {
                    var cover = FixtureArtwork.Read(1808500, "portrait");
                    Assert.NotNull(cover);
                    ((Border)page.FindName("DownloadHeroArtwork")).Background = new ImageBrush(cover) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
                    foreach (var size in new[] { new Size(1880,800), new Size(1280,720), new Size(660,520) })
                    {
                        page.Measure(size); page.Arrange(new Rect(size)); page.UpdateLayout();
                        PumpDispatcher(TimeSpan.FromMilliseconds(350));
                        page.Measure(size); page.Arrange(new Rect(size)); page.UpdateLayout();
                        CheckDownloadsLayout(page);
                        var image = new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);
                        image.Render(page);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                        var directory = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") ?? Path.GetTempPath();
                        Directory.CreateDirectory(directory);
                        using var stream = File.Create(Path.Combine(directory, $"download-card-{size.Width:0}.png")); encoder.Save(stream);
                    }
                    store.Downloads[0].TransferTotalBytes = null;
                    var unknownSize = new Size(1280,720);
                    page.Measure(unknownSize); page.Arrange(new Rect(unknownSize)); page.UpdateLayout();
                    PumpDispatcher(TimeSpan.FromMilliseconds(350));
                    var unknownBar = (Steamy.Controls.SmoothProgressBar)page.FindName("FocusedProgress");
                    Assert.True(unknownBar.IsIndeterminate);
                    var unknownActivity = Assert.IsType<Grid>(unknownBar.Template.FindName("UnknownActivity", unknownBar));
                    Assert.Equal(Visibility.Visible, unknownActivity.Visibility);
                    Assert.True(unknownActivity.ActualWidth > 100, "Unknown totals must retain a visible activity track.");
                    Assert.Equal(Visibility.Collapsed, ((TextBlock)page.FindName("DownloadTransferPercentage")).Visibility);
                    var unknownImage = new RenderTargetBitmap(1280,720,96,96,PixelFormats.Pbgra32);
                    unknownImage.Render(page);
                    var unknownEncoder = new PngBitmapEncoder(); unknownEncoder.Frames.Add(BitmapFrame.Create(unknownImage));
                    using (var unknownFile = File.Create(Path.Combine(Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") ?? Path.GetTempPath(), "download-card-unknown.png"))) unknownEncoder.Save(unknownFile);
                    var deleteButton = Descendants<Button>(page).Single(button => System.Windows.Automation.AutomationProperties.GetName(button)=="Remove history entry" && ReferenceEquals(button.CommandParameter,historyJob));
                    Assert.Equal(Visibility.Visible,deleteButton.Visibility);
                    Assert.NotNull(deleteButton.Command);
                    ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<DownloadJob>)deleteButton.Command).ExecuteAsync(historyJob).GetAwaiter().GetResult();
                    Assert.DoesNotContain(historyJob,store.Downloads);
                    var keySettings = new SettingsPage();
                    keySettings.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    try
                    {
                        ((Wpf.Ui.Controls.TextBox)keySettings.FindName("SettingsSearch")).Text="auth key";
                        PumpDispatcher(TimeSpan.FromMilliseconds(300));
                        var settingsSize=new Size(1280,720);
                        keySettings.Measure(settingsSize); keySettings.Arrange(new Rect(settingsSize)); keySettings.UpdateLayout();
                        var authBox=(PasswordBox)keySettings.FindName("RyuuAuthKeyBox");
                        authBox.BringIntoView(); PumpDispatcher(TimeSpan.FromMilliseconds(100)); keySettings.UpdateLayout();
                        Assert.True(authBox.ActualWidth>=150 && authBox.ActualHeight>=44);
                        Assert.NotEqual(Brushes.Transparent,authBox.Background);
                        Assert.NotEqual(Brushes.Transparent,authBox.BorderBrush);
                        var placeholder=Assert.IsType<TextBlock>(authBox.Template.FindName("Placeholder",authBox));
                        Assert.Equal(Visibility.Visible,placeholder.Visibility);
                        var settingsImage=new RenderTargetBitmap(1280,720,96,96,PixelFormats.Pbgra32); settingsImage.Render(keySettings);
                        var settingsEncoder=new PngBitmapEncoder(); settingsEncoder.Frames.Add(BitmapFrame.Create(settingsImage));
                        using var settingsFile=File.Create(Path.Combine(Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") ?? Path.GetTempPath(),"auth-key-settings.png")); settingsEncoder.Save(settingsFile);
                    }
                    finally { keySettings.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
                    store.Downloads[0] = new DownloadJob { AppId=33230, GameName="Assassin's Creed 2", State=DownloadJobState.Downloading,
                        TransferredBytes=130_570_000, TransferTotalBytes=78_250_000_000, BytesPerSecond=15_981_250, EtaSeconds=1200 };
                    PumpDispatcher(TimeSpan.FromMilliseconds(500));
                    ((Border)page.FindName("DownloadHeroArtwork")).Background=new ImageBrush(FixtureArtwork.Read(33230,"portrait")) { Stretch=Stretch.UniformToFill };
                    page.Measure(unknownSize); page.Arrange(new Rect(unknownSize)); page.UpdateLayout();
                    var neutralImage=new RenderTargetBitmap(1280,720,96,96,PixelFormats.Pbgra32); neutralImage.Render(page);
                    var neutralEncoder=new PngBitmapEncoder(); neutralEncoder.Frames.Add(BitmapFrame.Create(neutralImage));
                    using (var neutralFile=File.Create(Path.Combine(Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS") ?? Path.GetTempPath(),"download-ac2.png"))) neutralEncoder.Save(neutralFile);
                    var neutralPixels=new byte[1280*720*4]; neutralImage.CopyPixels(neutralPixels,1280*4,0);
                    var panelPixel=(110*1280+1100)*4;
                    Assert.True(Math.Abs(neutralPixels[panelPixel+2]-neutralPixels[panelPixel+1])<15,"A neutral cover with a small red logo must not make the panel red.");
                    CheckTrailerOffscreen(provider);
                    Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                }
                finally { page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_TOOL_STYLE_ONLY") == "1")
            {
                CheckSteamToolsAppearance(provider, betterSteamTools);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_DOWNLOAD_AMBIENT_ONLY") == "1")
            {
                CheckDownloadAmbient(provider, store, fixtureArtwork);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_DOWNLOAD_WIZARD_ONLY") == "1")
            {
                CheckDownloadWizard(provider, wizardDownloads, wizardQueue);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_INSTALL_GLASS_ONLY") == "1")
            {
                CheckGlassInstallFlow(provider, store, fixtureArtwork, betterSteamTools, wizardDownloads);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_SCROLL_ONLY") == "1")
            {
                CheckScrollReversal();
                provider.GetRequiredService<LibraryViewModel>().SelectedMode = "Browse all";
                CheckShellScrolling(provider);
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_STORAGE_LIBRARY_ONLY") == "1")
            {
                CheckFirstRunSetup();
                CheckStorageLibrary(provider, store, fixtureArtwork);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_GAME_DETAILS_ONLY") == "1")
            {
                CheckGameDetails(provider, store, fixtureArtwork);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_HOME_VIRTUALIZATION_ONLY") == "1")
            {
                AddScreenshotLibrary(store, fixtureArtwork);
                foreach (var (game, index) in store.Games.Select((game, index) => (game, index)))
                    activity.RecordLaunchAsync(game.AppId, DateTimeOffset.UtcNow.AddHours(-index - 1)).GetAwaiter().GetResult();
                TracePhase("Home virtualization: trailers"); CheckTrailerPreviews(provider);
                TracePhase("Home virtualization: ambient and glass"); CheckGlassShell(provider);
                TracePhase("Home virtualization: scrolling"); CheckShellScrolling(provider);
                Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (Environment.GetEnvironmentVariable("STEAMY_UI_GLASS_ONLY") == "1")
            {
                if (Environment.GetEnvironmentVariable("STEAMY_UI_BACKGROUND_ONLY") == "1")
                { _phase = "CheckFirstRunSetup()"; CheckFirstRunSetup(); }
                AddScreenshotLibrary(store, fixtureArtwork);
                foreach (var (game, index) in store.Games.Select((game, index) => (game, index)))
                    activity.RecordLaunchAsync(game.AppId, DateTimeOffset.UtcNow.AddHours(-index - 1)).GetAwaiter().GetResult();
                _phase = "CheckGlassShell(provider)";
                CheckGlassShell(provider);
                if (Environment.GetEnvironmentVariable("STEAMY_UI_VIDEO_CHECK") == "1")
                { _phase = "CheckTrailerPreviews(provider)"; CheckTrailerPreviews(provider); }
                return;
            }
            _phase = "CheckBurstUpdates()";
            CheckBurstUpdates();
            _phase = "CheckLoggingBatches()";
            CheckLoggingBatches();
            _phase = "CheckReducedEffectsNativeWindow()";
            CheckReducedEffectsNativeWindow();
            _phase = "CheckSettingsCache()";
            CheckSettingsCache();
            _phase = "CheckFirstRunSetup()";
            CheckFirstRunSetup();
            _phase = "CheckDiscordFriends()";
            CheckDiscordFriends();
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
            // Shell interaction checks need installed/recent titles even when optional
            // screenshot artwork is unavailable (for example on a fresh CI machine).
            {
                AddScreenshotLibrary(store, fixtureArtwork);
                activity.ToggleFavoriteAsync(1091500).GetAwaiter().GetResult();
                activity.ToggleFavoriteAsync(1245620).GetAwaiter().GetResult();
                foreach (var (game, index) in store.Games.Select((game, index) => (game, index)))
                    activity.RecordLaunchAsync(game.AppId, DateTimeOffset.UtcNow.AddHours(-index - 1)).GetAwaiter().GetResult();
            }
            foreach (var theme in new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme);
                _phase = "Library dialog " + theme;
                CheckLibraryDialog(provider, theme);
                foreach (var page in new Page[] { new DashboardPage(), new DownloadsPage(), new SettingsPage(), new DepotDownloaderPage(), new LibraryPage(), new DenuvoActivationPage(), new GameFixesPage(), new CreamApiPage(), new BetterSteamToolsPage() })
                {
                    _phase = theme + " " + page.GetType().Name;
                    PumpDispatcher(TimeSpan.FromMilliseconds(100));
                    foreach (var size in page is DownloadsPage or DepotDownloaderPage
                        ? new[] { new Size(520, 560), new Size(520, 800), new Size(780, 560), new Size(1280, 800) }
                        : new[] { new Size(780, 560), new Size(1280, 800) })
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
                            Assert.Equal(0, Assert.IsType<SolidColorBrush>(page.Background).Color.A);
                            Assert.NotNull(app.Resources["WorkspaceBackgroundBrush"]);
                        }
                        if (page is DashboardPage dashboard) CheckDashboardLayout(dashboard);
                        if (page is DownloadsPage downloads) CheckDownloadsLayout(downloads);
                        if (page is DepotDownloaderPage depotDownloader) CheckDepotDownloaderLayout(depotDownloader);
                        if (page is BetterSteamToolsPage steamTools) CheckSteamToolsLayout(steamTools);
                        SaveScreenshot(page, theme, size);
                        if (page is DownloadsPage)
                            foreach (var scale in new[] { 1.25, 1.5 }) SaveScreenshot(page, theme, size, scale);
                    }
                    if (page is LibraryPage)
                    {
                        var viewModel = Assert.IsType<Steamy.ViewModels.LibraryViewModel>(page.DataContext);
                        viewModel.SelectedMode = "Browse all";
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
                        PumpUntil(() => viewModel.FilteredGames.Count == viewModel.Games.Count);
                        var unfilteredLocalOrder = viewModel.FilteredGames.ToArray();
                        viewModel.SearchText = " \t\r\n";
                        PumpDispatcher(TimeSpan.FromMilliseconds(350));
                        PumpUntil(() => !viewModel.IsSearchBusy && viewModel.FilteredGames.Count == viewModel.Games.Count);
                        Assert.Equal(unfilteredLocalOrder, viewModel.FilteredGames);
                        viewModel.SearchText = string.Empty;
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
                        var categories = Assert.IsType<ListBox>(settings.FindName("CategorySelector"));
                        categories.SelectedItem = categories.Items.Cast<ListBoxItem>().Single(item => Equals(item.Tag, "services"));
                        settings.Measure(new Size(settings.Width, settings.Height));
                        settings.Arrange(new Rect(0, 0, settings.Width, settings.Height));
                        settings.UpdateLayout();
                        foreach (var name in new[] { "SteamApiKeyBox", "RyuuAuthKeyBox", "HubcapApiKeyBox", "DepotBoxApiKeyBox", "ManifestHubApiKeyBox", "MirrorTokenBox" })
                        {
                            var box = Assert.IsType<PasswordBox>(settings.FindName(name));
                            Assert.Same(settings.FindResource("SurfaceElevatedBrush"), box.Background);
                            Assert.Equal(((SolidColorBrush)settings.FindResource("TextTertiaryBrush")).Color, Assert.IsType<SolidColorBrush>(box.BorderBrush).Color);
                            Assert.True(box.Background.Opacity > 0);
                            Assert.True(((SolidColorBrush)box.BorderBrush).Color.A > 0);
                            Assert.True(box.ActualWidth >= 160);
                            Assert.True(box.ActualHeight >= 36);
                        }
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
            _phase = "CheckTrailerPreviews(provider)";
            CheckTrailerPreviews(provider);
            _phase = "CheckGlassShell(provider)";
            CheckGlassShell(provider);
            _phase = "CheckShellScrolling(provider)";
            CheckShellScrolling(provider);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK"))
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS"))
                && Environment.GetEnvironmentVariable("STEAMY_UI_QUICK_CAPTURE") != "1")
                SaveShellScreenshots(provider, fixtureArtwork);
            Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Steamy.ViewModels", StringComparison.Ordinal) && line.Contains("property not found", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingLog);
            app.Shutdown();
            DiscordIntegration.TestOverride = null;
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
        // Keep a deterministic fixture image alive like a visible Image control.
        // Offscreen weak artwork from earlier checks may already be collected.
        var firstHero = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 80, 100, 140, 255 }, 4);
        firstHero.Freeze();
        model.FeaturedGame.HeroArtwork = firstHero;
        var firstHeroRequests = artwork.HeroRequests[firstId];
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
        Assert.Same(firstHero, model.FeaturedGame!.HeroArtwork);
        Assert.Equal(firstHeroRequests, artwork.HeroRequests[firstId]);
        var library = provider.GetRequiredService<Steamy.ViewModels.LibraryViewModel>();
        library.SelectedSourceFilter = "Sushi";
        library.SelectedTypeFilter = "DLC";
        Type? route = null;
        navigation.Attach(page => route = page);
        model.ViewFeaturedCommand.Execute(null);
        Assert.Equal(typeof(LibraryPage), route);
        Assert.Equal(firstId.ToString(System.Globalization.CultureInfo.InvariantCulture), library.SearchText);
        Assert.Equal("All sources", library.SelectedSourceFilter);
        Assert.Equal("Game", library.SelectedTypeFilter);
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
            (1174180, "Red Dead Redemption 2", "119 GB"), (2358720, "Black Myth: Wukong", "128 GB"),
            (1086940, "Baldur’s Gate 3", "150 GB"), (1593500, "God of War", "70 GB"),
            (2050650, "Resident Evil 4", "67 GB"), (990080, "Hogwarts Legacy", "85 GB") };
        foreach (var (id, name, size) in names)
        {
            _phase = "Read sample artwork " + id;
            var existing = store.Games.FirstOrDefault(entry => entry.AppId == id);
            if (existing is not null) store.Games.Remove(existing);
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
            Progress = 42.5, Phase = "downloading", TransferredBytes = 36700000000, TransferTotalBytes = 86300000000, InstallationBytes = 120000000000, ContentBytes = 51000000000, ReusedBytes = 9000000000, Downloaded = "34.18 GiB", TotalSize = "80.37 GiB", Speed = "12.4 MiB/s", BytesPerSecond = 12.4 * 1024 * 1024,
            Eta = "1h 08m", EtaSeconds = 4080, Status = "Downloading depot 1 of 2", DownloadMode = "DepotDownloaderMod (Sushi)", TargetFolder = @"C:\Games\Cyberpunk 2077" });
        jobs.Add(new DownloadJob { AppId = 1245620, GameName = "ELDEN RING", State = DownloadJobState.Paused,
            Progress = 61, Downloaded = "37.3 GB", TotalSize = "61.1 GB", Status = "Paused — existing files and manifests are retained", DownloadMode = "DepotDownloaderMod (Zaza)", TargetFolder = @"C:\Games\ELDEN RING" });
        jobs.Add(new DownloadJob { AppId = 2358720, GameName = "Black Myth: Wukong", State = DownloadJobState.Queued,
            TotalSize = "128 GB", Status = "Ready when you are", DownloadMode = "DepotDownloader", TargetFolder = @"C:\Games\Wukong" });
        var setup = provider.GetRequiredService<DepotDownloaderViewModel>();
        setup.SelectedGame = provider.GetRequiredService<IAppDataStore>().Games.First(game => game.AppId == 1091500);
        setup.TargetFolder = @"C:\Games\Cyberpunk 2077";
        if (!setup.Branches.Any(branch => branch.Name == "public")) setup.Branches.Add(new Branch { Name = "public" });
        setup.SelectedBranch = setup.Branches.First(branch => branch.Name == "public");
        var window = new MainWindow { WindowState = WindowState.Normal, Width = 1600, Height = 1050, Topmost = true, ShowInTaskbar = false };
        Assert.False(window.RootNavigationView.IsFooterSeparatorVisible);
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
        Assert.NotNull(discoveryPanel.Background);
        // The modal is glass now. Verify readability over both extremes instead
        // of requiring the obsolete opaque, single-color implementation.
        var dialogText = ((SolidColorBrush)discoveryPanel.FindResource("TextPrimaryBrush")).Color;
        foreach (var behind in new[] { Brushes.Black, Brushes.White })
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(behind, null, new Rect(0, 0, 3, 3));
                drawing.DrawRectangle(discoveryPanel.Background, null, new Rect(0, 0, 3, 3));
            }
            var rendered = new RenderTargetBitmap(3, 3, 96, 96, PixelFormats.Pbgra32); rendered.Render(visual);
            var pixels = new byte[36]; rendered.CopyPixels(pixels, 12, 0);
            for (var pixel = 0; pixel < pixels.Length; pixel += 4)
            {
                var background = ChromeLuminance(Color.FromRgb(pixels[pixel + 2], pixels[pixel + 1], pixels[pixel]));
                var foreground = ChromeLuminance(dialogText);
                Assert.True((Math.Max(background, foreground) + .05) / (Math.Min(background, foreground) + .05) >= 4.5,
                    "The discovery dialog text must remain readable on glass.");
            }
        }
        Assert.False(discoveryScroll.IsEnabled);
        SaveVisual(window, "upcoming-details.png");
        dashboard.CloseDiscoveryCommand.Execute(null);
        discoveryScroll.ScrollToTop();
        foreach (var (route, filename) in new[] { (typeof(DownloadsPage), "downloads.png"), (typeof(DepotDownloaderPage), "depot-downloader.png"), (typeof(SettingsPage), "settings.png") })
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
                ((Wpf.Ui.Controls.TextBox)settingsPage.FindName("SettingsSearch")).Text = "update";
                PumpUntil(() => Descendants<TextBlock>(settingsPage).Any(text => text.IsVisible && text.Text == "Automatic updates"));
                settingsScroll.ScrollToTop();
                window.UpdateLayout();
                var updateHeading = Descendants<TextBlock>(settingsPage).Single(text => text.IsVisible && text.Text == "Automatic updates");
                position = updateHeading.TranslatePoint(new Point(0, 0), settingsScroll);
                settingsScroll.ScrollToVerticalOffset(settingsScroll.VerticalOffset + position.Y - 32);
                window.UpdateLayout();
                Assert.Contains(Descendants<TextBlock>(settingsPage), text => text.IsVisible && text.Text == $"Version {GitHubUpdateService.Normalize(typeof(App).Assembly.GetName().Version!)}");
                SaveVisual(window, "settings-updates.png");
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
        library.SelectedMode = "Browse all";
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
        var galleryPanels = Descendants<Steamy.Controls.VirtualizingGamePanel>(page).Where(panel => panel.IsVisible && panel.Children.Count >= 5).ToArray();
        Assert.Single(galleryPanels);
        Assert.Null(page.FindName("UpcomingSection"));
        Assert.All(library.PagedCatalogItems, item => Assert.False(item.IsUpcoming));
        foreach (var galleryPanel in galleryPanels)
        {
            var children = galleryPanel.Children.Cast<FrameworkElement>().ToArray();
            var firstRow = children.Where(child => child.TranslatePoint(new Point(0, 0), galleryPanel).Y == 0).ToArray();
            Assert.InRange(firstRow.Length, 5, 6);
            Assert.InRange(firstRow[0].ActualWidth, 184, 280);
            var last = firstRow[^1];
            var right = last.TranslatePoint(new Point(last.ActualWidth, 0), galleryPanel).X;
            Assert.InRange(galleryPanel.ActualWidth - right, -1, 1); // No unused strip at the right edge.
        }
        SaveVisual(window, "games.png");
        _phase = "Hover a gallery card";
        var cardSize = new Size(hoveredCard.ActualWidth, hoveredCard.ActualHeight);
        var cardPosition = hoveredCard.TranslatePoint(new Point(0,0), page);
        var pointer = hoveredCard.PointToScreen(new Point(hoveredCard.ActualWidth / 2, hoveredCard.ActualHeight / 2));
        Assert.True(MoveCursorAway((int)Math.Round(pointer.X), (int)Math.Round(pointer.Y)));
        System.Windows.Input.Mouse.Synchronize();
        PumpDispatcher(TimeSpan.FromMilliseconds(300));
        Assert.True(hoveredCard.IsMouseOver,
            $"Native hover failed. Active={window.IsActive}, enabled={hoveredCard.IsEnabled}, visible={hoveredCard.IsVisible}, pointer={System.Windows.Input.Mouse.GetPosition(window)}, target={hoveredCard.TranslatePoint(new Point(hoveredCard.ActualWidth / 2, hoveredCard.ActualHeight / 2), window)}, directlyOver={System.Windows.Input.Mouse.DirectlyOver}, hit={window.InputHitTest(hoveredCard.TranslatePoint(new Point(hoveredCard.ActualWidth / 2, hoveredCard.ActualHeight / 2), window))}.");
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
        SaveVisual(window, "install-method.png");
        page.SelectInstallMethod(GameInstallMethod.DepotDownloader);
        AwaitWizardStep(page);
        PumpDispatcher(TimeSpan.FromMilliseconds(350));
        window.UpdateLayout();
        SaveVisual(window, "game-details.png");
        Assert.IsType<System.Windows.Media.LinearGradientBrush>(((Border)page.FindName("DialogPanel")).Background);
        page.SelectDownloadSource(ManifestSource.Local);
        PumpDispatcher(TimeSpan.FromMilliseconds(150));
        window.UpdateLayout();
        SaveVisual(window, "local-package.png");
        page.SelectDownloadSource(ManifestSource.Sushi);
        _phase = "Prepare screenshot depot choices";
        AwaitWizardStep(page);
        Assert.Equal(2, page.DownloadWizardStep);
        window.UpdateLayout();
        SaveVisual(window, "download-depots.png");
        _phase = "Prepare screenshot download location";
        AwaitWizardStep(page);
        Assert.Equal(3, page.DownloadWizardStep);
        page.ConfigureDownloadLocation(@"C:\Games");
        PumpUntil(() => !((TextBlock)page.FindName("LocationSpaceText")).Text.StartsWith("Checking", StringComparison.Ordinal));
        window.UpdateLayout();
        SaveVisual(window, "download-location.png");
        page.CloseOverlay();
        PumpDispatcher(TimeSpan.FromMilliseconds(250));
        Assert.True(window.RootNavigationView.Navigate(typeof(GameFixesPage)));
        PumpDispatcher(TimeSpan.FromMilliseconds(300));
        SaveVisual(window, "game-fixes.png");
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

    private sealed class ScreenshotImagePipeline : IImagePipeline
    {
        public Task<BitmapImage?> LoadAsync(ImageRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Source.IsFile)
                return Task.FromResult(ImagePipeline.Decode(File.ReadAllBytes(request.Source.LocalPath), request.PixelWidth, request.PixelHeight));
            return Task.FromResult(new ScreenshotArtworkConverter().Convert(request.Source.AbsoluteUri, typeof(BitmapImage), request.PixelWidth, System.Globalization.CultureInfo.InvariantCulture) as BitmapImage);
        }
    }
    private sealed class ScreenshotArtworkConverter : System.Windows.Data.IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var match = System.Text.RegularExpressions.Regex.Match(value?.ToString() ?? "", @"/apps/(\d+)/");
            var url = value?.ToString() ?? "";
            var kind = url.Contains("library_hero", StringComparison.Ordinal) ? "hero"
                : url.Contains("600x900", StringComparison.Ordinal) ? "portrait" : "header";
            return match.Success ? FixtureArtwork.Read(int.Parse(match.Groups[1].Value), kind) : null;
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
        library.SelectedMode = "Browse all";
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
            Assert.True(Assert.Single(library.PagedCatalogItems).IsUpcoming);

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
        public Func<int, CancellationToken, Task<BitmapImage?>>? HeroOverride { get; set; }
        public Task<BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default)
        {
            HeroRequests.AddOrUpdate(appId, 1, (_, count) => count + 1);
            if (HeroOverride is not null) return HeroOverride(appId, cancellationToken);
            return Task.FromResult(Read(appId, "hero"));
        }
        public Task<BitmapImage?> LoadSpotlightLogoAsync(SpotlightGame game, CancellationToken cancellationToken = default) => Task.FromResult(Read(game.AppId, "logo"));
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
            Assert.Same(small, cached); // Page revisits reuse the frozen decoded bitmap.
            Assert.Equal(16, cached!.PixelWidth);
            Assert.Equal(1, handler.Requests); // Revisiting a game reads the local cache.
            var large = Task.Run(() => service.LoadHeroAsync(2)).GetAwaiter().GetResult();
            Assert.True(large!.PixelWidth is 1600 or 2560);
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
            var beforeLocal = handler.Requests;
            var local = new Game { AppId = -42, Name = "Local icon", LaunchExecutable = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe") };
            Task.Run(() => service.LoadAsync(local)).GetAwaiter().GetResult();
            Assert.NotNull(local.ArtworkImage);
            Assert.True(local.ArtworkImage.IsFrozen);
            Assert.True(local.ArtworkImage.PixelWidth >= 32);
            Assert.Equal(beforeLocal, handler.Requests); // Local games never query a fake Steam app ID.
            var xboxExe = Path.Combine(directory, "XboxGame.exe");
            File.WriteAllBytes(xboxExe, [0]); // No executable icon: packaged publisher artwork must win.
            var packaged = new PngBitmapEncoder();
            packaged.Frames.Add(BitmapFrame.Create(small));
            using (var output = File.Create(Path.Combine(directory, "Square480x480Logo.png"))) packaged.Save(output);
            var xbox = new Game { AppId = -43, Name = "Xbox game", LaunchExecutable = xboxExe };
            Task.Run(() => service.LoadAsync(xbox)).GetAwaiter().GetResult();
            Assert.NotNull(xbox.ArtworkImage);
            Assert.True(xbox.ArtworkImage.IsFrozen);
            Assert.Equal(beforeLocal, handler.Requests);
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
            Assert.True(settings.AutoplayTrailers); // Existing settings files keep previews enabled by default.
            Assert.True(settings.TrackPlayTime);
            File.WriteAllText(path, "not valid JSON");
            Assert.Same(settings, service.Load());
            Assert.False(settings.DashboardSearch);
            settings.DashboardSearch = true;
            settings.AutoplayTrailers = false;
            settings.TrackPlayTime = false;
            settings.Appearance = "Dark";
            service.SaveAsync(settings).GetAwaiter().GetResult();
            var reloaded = new JsonSettingsService(path).Load();
            Assert.Equal("Dark", reloaded.Appearance);
            Assert.True(reloaded.DashboardSearch);
            Assert.False(reloaded.AutoplayTrailers); // Survives a fresh settings service after restart.
            Assert.False(reloaded.TrackPlayTime);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            service.ResetAsync().GetAwaiter().GetResult();
            Assert.NotSame(settings, service.Load());
            Assert.False(service.Load().DashboardSearch);
            Assert.True(service.Load().AutoplayTrailers);
            Assert.True(service.Load().TrackPlayTime);
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

    private static void PumpUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < limit) PumpDispatcher(TimeSpan.FromMilliseconds(20));
        Assert.True(condition(), $"The asynchronous UI operation did not settle within {limit.TotalSeconds} seconds. Phase: {_phase}.");
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

    private static void SaveScreenshot(Page page, string theme, Size size, double scale = 1)
    {
        if (Environment.GetEnvironmentVariable("STEAMY_UI_QUICK_CAPTURE") == "1"
            && page is not DashboardPage && page is not SettingsPage) return;
        var folder = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(folder, $"{theme}-{page.GetType().Name}-{size.Width}{(scale == 1 ? "" : "-scale" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture))}.png"));
        encoder.Save(output);
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly AppSettings _settings = new() { AutoUpdate = false, Language = "en-US · English" };
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

internal sealed class ControllerOfflineFixture : IControllerBackend
{
    public bool IsInstalled => false;
    public Steamy.ControllerProtocol.ControllerState State => new(false, "Stopped", "Not checked", false, []);
    public event EventHandler? Changed { add { } remove { } }
    public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
    public Task CheckDriversAsync(CancellationToken token = default) => Task.CompletedTask;
    public Task ApplyProfileAsync(Steamy.ControllerProtocol.ControllerProfile profile, CancellationToken token = default) => Task.CompletedTask;
    public Task RumbleAsync(int slot, CancellationToken token = default) => Task.CompletedTask;
}

// Constructors may subscribe to events and read optional credentials. Any real operation is rejected.
public class OfflineServiceProxy : DispatchProxy
{
    public static int CatalogRequests;
    public static Task? ReleaseMetadataGate;
    public static SteamCatalogItem[]? ScreenshotCatalog;
    public static object Create(Type interfaceType) => DispatchProxy.Create(interfaceType, typeof(OfflineServiceProxy));
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod ?? throw new InvalidOperationException();
        if (method.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal)) return null;
        if (method.DeclaringType == typeof(ISecureCredentialService) && method.Name == nameof(ISecureCredentialService.ReadAsync)) return Task.FromResult<string?>(null);
        if (method.DeclaringType == typeof(IUpdateService) && method.Name == "get_CurrentVersion")
            return GitHubUpdateService.Normalize(typeof(App).Assembly.GetName().Version!);
        if (method.DeclaringType == typeof(ILoggingService)) return null;
        if (method.DeclaringType == typeof(IPlayTimeService))
        {
            if (method.Name == nameof(IPlayTimeService.ConfigureAsync)) return Task.CompletedTask;
            if (method.Name == nameof(IPlayTimeService.ReadLastPlayedAsync)) return Task.FromResult<DateTimeOffset?>(null);
            return Task.FromResult(new Steamy.Playtime.PlayTimeSnapshot(1, DateTimeOffset.UtcNow,
                new Steamy.Playtime.GamePlayTime[] { new(1245620, 19200, false), new(1091500, 8040, false), new(1174180, 42300, false) }));
        }
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
            if (ReleaseMetadataGate is { } gate) return gate.WaitAsync(args![1] is CancellationToken token ? token : default);
            foreach (var item in (IReadOnlyList<SteamCatalogItem>)args![0]!) item.IsReleaseVerified = true;
            return Task.CompletedTask;
        }
        if (method.DeclaringType == typeof(ISteamCatalogService) && method.Name == nameof(ISteamCatalogService.PrepareArtworkAsync)) return Task.CompletedTask;
        if (method.DeclaringType == typeof(ISteamCatalogService) && method.Name == nameof(ISteamCatalogService.GetDetailsAsync))
            return Task.FromResult<SteamCatalogDetails?>(null); // Offline hub previews retain the already-loaded headers.
        if (method.DeclaringType == typeof(ISteamCatalogService) && method.Name == nameof(ISteamCatalogService.EnsureDetailsAsync))
        {
            if (ReleaseMetadataGate is { } gate)
            {
                async Task<bool> WaitForDetails()
                {
                    await gate.WaitAsync(args![1] is CancellationToken token ? token : default);
                    return true;
                }
                return WaitForDetails();
            }
            var item = (SteamCatalogItem)args![0]!;
            item.HasDetails = true; item.GenresDisplay = "Action · RPG"; // Explicit layout/ranking fixture, no live metadata claim.
            if (item.AppType == SteamCatalogAppType.Unknown) item.AppType = SteamCatalogAppType.Game;
            item.IsReleaseVerified = true;
            return Task.FromResult(true);
        }
        if (method.DeclaringType == typeof(ISteamCatalogService) && method.Name is nameof(ISteamCatalogService.EnsureArtworkAsync) or nameof(ISteamCatalogService.PrefetchArtworkAsync))
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
