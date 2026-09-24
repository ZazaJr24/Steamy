using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SteamContentManager;
using SteamContentManager.Models;
using SteamContentManager.Pages;
using SteamContentManager.Services;

namespace UiPreview;

/// <summary>Runs the real app on a CI runner, seeds sample data and renders the dashboard to PNG files.</summary>
public static class Program
{
    private const string CatalogUrl = "http://localhost:18765/";
    private static string _outDir = string.Empty;

    [STAThread]
    public static int Main(string[] args)
    {
        _outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "preview");
        Directory.CreateDirectory(_outDir);

        // Registered before App.OnStartup adds its own handlers, so a crash is written out instead of
        // waiting on the app's modal crash dialog forever.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fail("AppDomain", e.ExceptionObject as Exception);
        var app = new App();
        app.DispatcherUnhandledException += (_, e) => Fail("Dispatcher", e.Exception);
        app.InitializeComponent();
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try { await RunAsync(app); }
            catch (Exception exception) { Fail("Preview", exception); }
            app.Shutdown();
        }));

        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(3));
            Console.WriteLine("Watchdog: preview did not finish in time.");
            Environment.Exit(4);
        }) { IsBackground = true }.Start();

        return app.Run();
    }

    private static async Task RunAsync(App app)
    {
        var window = app.MainWindow ?? throw new InvalidOperationException("No main window.");
        window.WindowState = WindowState.Normal;
        window.Left = 0;
        window.Top = 0;
        window.Width = 1440;
        window.Height = 940;

        // Mica leaves the page background transparent, which a bitmap render cannot show.
        UiThemeService.ApplyBackdrop("None");
        UiThemeService.Apply("Dark");
        await Delay(4000);
        Capture(window, "1-first-run-dark");

        Seed();
        await Delay(10000);
        Capture(window, "2-dashboard-dark");

        UiThemeService.Apply("Light");
        await Delay(2000);
        Capture(window, "3-dashboard-light");

        UiThemeService.Apply("Dark");
        using var catalog = StartCatalogServer();
        var settingsService = App.Services.GetRequiredService<ISettingsService>();
        var settings = settingsService.Load();
        settings.FixMirrorUrl = CatalogUrl;
        await settingsService.SaveAsync(settings);

        var navigation = App.Services.GetRequiredService<INavigationService>();
        navigation.Navigate<GameFixesPage>();
        await Delay(9000);
        Capture(window, "4-fixes-dark");

        navigation.Navigate<LibraryPage>();
        await Delay(3000);
        navigation.Navigate<SettingsPage>();
        await Delay(2500);
        Capture(window, "5-settings-after-games-dark");
    }

    private static HttpListener StartCatalogServer()
    {
        var games = new (string AppId, string Name, int Fixes)[]
        {
            ("1245620", "ELDEN RING", 1), ("1091500", "Cyberpunk 2077", 2), ("292030", "The Witcher 3: Wild Hunt", 1),
            ("1174180", "Red Dead Redemption 2", 1), ("413150", "Stardew Valley", 1), ("1145360", "Hades", 1),
            ("271590", "Grand Theft Auto V", 1), ("620", "Portal 2", 1), ("730", "Counter-Strike 2", 1), ("1086940", "Baldur's Gate 3", 2)
        };
        var json = "[" + string.Join(",", games.Select(g => $$"""{"appid":"{{g.AppId}}","name":"{{g.Name}}","fixes":[{{string.Join(",", Enumerable.Range(1, g.Fixes).Select(i => $$"""{"filename":"Sample fix {{i}}.zip","path":"fixes/{{g.AppId}}/{{i}}.zip","size":"12.{{i}} MB","badges":["Sample"]}"""))}}]}""")) + "]";
        var body = Encoding.UTF8.GetBytes(json);

        var listener = new HttpListener();
        listener.Prefixes.Add(CatalogUrl);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch { return; }
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });
        return listener;
    }

    private static void Seed()
    {
        var store = App.Services.GetRequiredService<IAppDataStore>();
        var samples = new (int AppId, string Name, double Gb, int DaysAgo, bool Update)[]
        {
            (1245620, "ELDEN RING", 49.8, 1, false),
            (1091500, "Cyberpunk 2077", 70.2, 2, true),
            (292030, "The Witcher 3: Wild Hunt", 48.1, 4, false),
            (1174180, "Red Dead Redemption 2", 121.3, 6, false),
            (413150, "Stardew Valley", 0.6, 9, false),
            (1145360, "Hades", 15.2, 12, false),
            (271590, "Grand Theft Auto V", 94.7, 20, false),
            (620, "Portal 2", 12.3, 45, false),
            (730, "Counter-Strike 2", 33.1, 60, false),
        };

        foreach (var sample in samples)
        {
            var (color, glyph) = GameFactory.CoverStyleFor(sample.AppId);
            var bytes = (long)(sample.Gb * 1024 * 1024 * 1024);
            var updated = DateTime.Now.AddDays(-sample.DaysAgo);
            store.Games.Add(new Game
            {
                AppId = sample.AppId,
                Name = sample.Name,
                CoverColor = color,
                CoverGlyph = glyph,
                Size = ByteSize.Format(bytes),
                SizeOnDiskBytes = bytes,
                InstallState = GameInstallState.Installed,
                UpdateRequired = sample.Update,
                LastUpdated = updated,
                LastPlayed = $"Updated {updated:dd.MM.yyyy HH:mm}",
                InstallFolder = Path.GetTempPath()
            });
        }

        _ = App.Services.GetRequiredService<IArtworkService>().LoadManyAsync(store.Games.ToList());

        store.Downloads.Add(Job(1086940, "Baldur's Gate 3", DownloadJobState.Downloading, 42.5, "54.6 GB", "128.4 GB", "38.2 MB/s", "33 min"));
        store.Downloads.Add(Job(1145360, "Hades", DownloadJobState.Paused, 71, "10.8 GB", "15.2 GB", string.Empty, string.Empty));
        store.Downloads.Add(Job(620, "Portal 2", DownloadJobState.Paused, 12, "1.5 GB", "12.3 GB", string.Empty, string.Empty));
    }

    private static DownloadJob Job(int appId, string name, DownloadJobState state, double progress, string done, string total, string speed, string eta)
    {
        var (color, glyph) = GameFactory.CoverStyleFor(appId);
        return new DownloadJob
        {
            AppId = appId,
            GameName = name,
            CoverColor = color,
            CoverGlyph = glyph,
            Started = DateTime.Now.AddMinutes(-appId % 50),
            State = state,
            Progress = progress,
            Downloaded = done,
            TotalSize = total,
            Speed = speed,
            Eta = eta,
            Status = state == DownloadJobState.Paused ? "Paused" : "Downloading content"
        };
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        Save(window, (int)window.ActualWidth, (int)window.ActualHeight, Path.Combine(_outDir, $"{name}-window.png"), null, new Point());

        var page = FindChild<Page>(window);
        if (page?.Content is ScrollViewer { Content: FrameworkElement content } scroll)
        {
            var padding = scroll.Padding;
            var width = (int)Math.Ceiling(content.ActualWidth + padding.Left + padding.Right);
            var height = (int)Math.Ceiling(content.ActualHeight + padding.Top + padding.Bottom);
            Save(content, width, height, Path.Combine(_outDir, $"{name}-page.png"), page.Background, new Point(padding.Left, padding.Top));
        }

        Console.WriteLine($"Captured {name}");
    }

    private static void Save(Visual visual, int width, int height, string path, Brush? background, Point offset)
    {
        var element = (FrameworkElement)visual;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            if (background is not null) context.DrawRectangle(background, null, new Rect(0, 0, width, height));
            var brush = new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            context.DrawRectangle(brush, null, new Rect(offset.X, offset.Y, element.ActualWidth, element.ActualHeight));
        }

        var bitmap = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static async Task Delay(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until) await Task.Delay(100);
    }

    private static void Fail(string where, Exception? exception)
    {
        var text = $"{where} failure:{Environment.NewLine}{exception}";
        Console.WriteLine(text);
        try { File.WriteAllText(Path.Combine(_outDir, "error.txt"), text); } catch { }
        Environment.Exit(3);
    }
}
