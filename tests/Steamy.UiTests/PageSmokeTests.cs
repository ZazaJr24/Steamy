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
using Steamy.Services;
using Steamy.Controls;
using System.Windows.Threading;
using System.IO.Compression;
using System.Net.Http;
using System.Net;

namespace Steamy.UiTests;

public sealed class PageSmokeTests
{
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "WPF smoke test did not finish within 60 seconds.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void ExercisePages()
    {
        // A plain test Application avoids production startup/exit side effects. Load the
        // same compiled dictionaries as App.xaml; WPF cannot load App.xaml into a subclass
        // declared in a different assembly.
        var app = new Application();
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
        var fixtureArtwork = new FixtureArtwork();
        services.AddSingleton<IArtworkService>(fixtureArtwork);
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
            CheckBurstUpdates();
            CheckSettingsCache();
            CheckSushiImport(provider);
            CheckFeaturedGames(provider, fixtureArtwork, navigation);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK")))
                AddScreenshotLibrary(store, fixtureArtwork);
            foreach (var theme in new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme);
                foreach (var page in new Page[] { new DashboardPage(), new DownloadsPage(), new SettingsPage(), new DepotDownloaderPage(), new LibraryPage() })
                {
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
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK")))
                SaveShellScreenshots(provider);
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
        Assert.Equal(2322010, model.FeaturedGame.Game.AppId);
        Assert.False(model.HasRecentGames); // Discovery must not pretend these games are installed.
        model.PreviousFeaturedCommand.Execute(null);
        Assert.Equal(2358720, model.FeaturedGame.Game.AppId);
        PumpUntil(() => artwork.HeroRequests.ContainsKey(2358720));
        model.NextFeaturedCommand.Execute(null);
        model.NextFeaturedCommand.Execute(null);
        Assert.Equal(1245620, model.FeaturedGame.Game.AppId);
        model.PreviousFeaturedCommand.Execute(null);
        PumpDispatcher(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, artwork.HeroRequests[2322010]); // Returning to a slide reuses its image task.
        var library = provider.GetRequiredService<Steamy.ViewModels.LibraryViewModel>();
        library.SelectedSourceFilter = "Sushi";
        library.SelectedTypeFilter = "DLC";
        Type? route = null;
        navigation.Attach(page => route = page);
        model.ViewFeaturedCommand.Execute(null);
        Assert.Equal(typeof(LibraryPage), route);
        Assert.Equal("2322010", library.SearchText);
        Assert.Equal("All sources", library.SelectedSourceFilter);
        Assert.Equal("All games", library.SelectedTypeFilter);
        library.SearchText = string.Empty;
        navigation.Detach();
    }

    private static void AddScreenshotLibrary(AppDataStore store, FixtureArtwork artwork)
    {
        var names = new[] { (1245620, "ELDEN RING", "61.1 GB"), (1091500, "Cyberpunk 2077", "86.3 GB"),
            (1174180, "Red Dead Redemption 2", "119 GB"), (2358720, "Black Myth: Wukong", "128 GB") };
        foreach (var (id, name, size) in names)
        {
            var game = new Game { AppId = id, Name = name, Size = size, InstallState = GameInstallState.Installed, LastUpdated = DateTime.Today.AddMinutes(-store.Games.Count) };
            artwork.LoadAsync(game).GetAwaiter().GetResult();
            store.Games.Add(game);
        }
        PumpDispatcher(TimeSpan.FromMilliseconds(100));
    }

    private static void SaveShellScreenshots(IServiceProvider provider)
    {
        UiThemeService.Apply("Dark");
        var window = new MainWindow { WindowState = WindowState.Normal, Width = 1600, Height = 1050 };
        window.Show();
        MoveCursorAway(0, 0);
        PumpDispatcher(TimeSpan.FromMilliseconds(500));
        window.UpdateLayout();
        SaveVisual(window, "dashboard.png");
        foreach (var (route, filename) in new[] { (typeof(DownloadsPage), "downloads.png"), (typeof(SettingsPage), "settings.png") })
        {
            Assert.True(window.RootNavigationView.Navigate(route));
            PumpDispatcher(TimeSpan.FromMilliseconds(500));
            window.UpdateLayout();
            SaveVisual(window, filename);
        }
        window.Close();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool MoveCursorAway(int x, int y);

    private static void SaveVisual(MainWindow window, string filename)
    {
        // Detach the real shell briefly so the runner's small native window cannot
        // clip a larger render. The same controls, resources and page instances are used.
        var element = (FrameworkElement)window.Content;
        window.Content = null;
        window.UpdateLayout();
        PumpDispatcher(TimeSpan.FromMilliseconds(50));
        Assert.Null(VisualTreeHelper.GetParent(element));
        try
        {
            var size = new Size(1600, 1050);
            element.Width = size.Width;
            element.Height = size.Height;
            element.InvalidateMeasure();
            element.InvalidateArrange();
            element.Measure(size);
            element.Arrange(new Rect(size));
            element.UpdateLayout();
            var folder = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS")!;
            Directory.CreateDirectory(folder);
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            foreach (var point in new[] { new Int32Rect(1500, 700, 1, 1), new Int32Rect(1500, 1000, 1, 1) })
            {
                var pixel = new byte[4];
                bitmap.CopyPixels(point, pixel, 4, 0);
                Assert.Equal(255, pixel[3]); // Reject screenshots clipped to the virtual desktop.
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, filename));
            encoder.Save(output);
        }
        finally { window.Content = element; }
    }

    private sealed class FixtureArtwork : IArtworkService
    {
        public System.Collections.Concurrent.ConcurrentDictionary<int, int> HeroRequests { get; } = new();
        public Task<BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default)
        {
            HeroRequests.AddOrUpdate(appId, 1, (_, count) => count + 1);
            return Task.FromResult(Read(appId, "hero"));
        }
        public Task LoadAsync(Game game, CancellationToken cancellationToken = default)
        {
            game.HeaderImage = Read(game.AppId, "header");
            game.ArtworkImage = game.HeaderImage;
            return Task.CompletedTask;
        }
        public Task LoadHeadersAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => LoadManyAsync(games, cancellationToken);
        public async Task LoadManyAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default)
        {
            foreach (var game in games) await LoadAsync(game, cancellationToken);
        }
        private static BitmapImage? Read(int appId, string kind)
        {
            var folder = Environment.GetEnvironmentVariable("STEAMY_SCREENSHOT_ARTWORK");
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var path = Path.Combine(folder, $"{appId}_{kind}.jpg");
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
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
            settings.Appearance = "Dark";
            service.SaveAsync(settings).GetAwaiter().GetResult();
            Assert.Equal("Dark", new JsonSettingsService(path).Load().Appearance);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            service.ResetAsync().GetAwaiter().GetResult();
            Assert.NotSame(settings, service.Load());
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
    public static object Create(Type interfaceType) => DispatchProxy.Create(interfaceType, typeof(OfflineServiceProxy));
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod ?? throw new InvalidOperationException();
        if (method.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal)) return null;
        if (method.DeclaringType == typeof(ISecureCredentialService) && method.Name == nameof(ISecureCredentialService.ReadAsync)) return Task.FromResult<string?>(null);
        if (method.DeclaringType == typeof(ILoggingService)) return null;
        if (method.DeclaringType == typeof(ILibrarySyncService) && method.Name == nameof(ILibrarySyncService.RefreshAsync))
            return Task.FromResult(SteamLibraryScanResult.Failure("Offline UI fixture"));
        if (method.DeclaringType == typeof(ISteamCatalogService) && method.Name == nameof(ISteamCatalogService.GetCatalogAsync))
            return Task.FromResult(new SteamCatalogSnapshot(true, new[] { new SteamCatalogItem { AppId = 10, Name = "An offline library game", AppType = SteamCatalogAppType.Game } }, DateTimeOffset.UtcNow, true, "Offline fixture"));
        if (method.DeclaringType == typeof(ISteamCatalogService) && method.Name == nameof(ISteamCatalogService.EnsureArtworkAsync)) return Task.CompletedTask;
        if ((method.DeclaringType == typeof(IRyuuCatalogService) || method.DeclaringType == typeof(IHubcapCatalogService)) && method.Name == "GetGamesAsync")
            return Task.FromResult(SteamCatalogSnapshot.Failure("Offline fixture"));
        if (method.DeclaringType == typeof(IFreeManifestCatalogService)) return Task.FromResult(new FreeManifestIndex(true, new HashSet<int> { 10 }, "Offline fixture"));
        if (method.Name == nameof(IDisposable.Dispose)) return null;
        throw new InvalidOperationException($"UI smoke attempted an external operation: {method.DeclaringType?.Name}.{method.Name}");
    }
}
