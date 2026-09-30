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
        var app = new SmokeApplication();
        app.InitializeComponent();
        var services = new ServiceCollection().AddSteamyServices();
        // Real view models, no downloader processes, network clients, database or user credentials.
        foreach (var type in services.Select(s => s.ServiceType).Where(t => t.IsInterface).Distinct().ToArray())
            services.AddSingleton(type, _ => OfflineServiceProxy.Create(type));
        var store = new AppDataStore();
        store.Downloads.Add(new DownloadJob { GameName = "A game ready to continue", State = DownloadJobState.Paused, Progress = 42.5, Status = "Paused — existing files are retained", TargetFolder = "C:\\Games\\Example", DownloadMode = "DepotDownloaderMod (Zaza)" });
        store.Downloads.Add(new DownloadJob { GameName = "A completed download", State = DownloadJobState.Completed, Progress = 100, Status = "Download completed", DownloadMode = "DepotDownloader" });
        services.AddSingleton<IAppDataStore>(store);
        services.AddSingleton<ISettingsService>(new MemorySettings());
        using var provider = services.BuildServiceProvider();
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, provider);
        var bindingLog = new BindingLog();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingLog);
        try
        {
            foreach (var theme in new[] { "Dark", "Light" })
            {
                UiThemeService.Apply(theme);
                foreach (var page in new Page[] { new DashboardPage(), new DownloadsPage(), new SettingsPage(), new DepotDownloaderPage() })
                {
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
                    if (page is SettingsPage settings)
                    {
                        var search = Assert.IsAssignableFrom<TextBox>(settings.FindName("SettingsSearch"));
                        search.Text = "resume";
                        var sections = Assert.IsType<StackPanel>(settings.FindName("SectionsPanel"));
                        Assert.Contains(sections.Children.Cast<FrameworkElement>(), section => section.Visibility == Visibility.Visible);
                        search.Text = "this-setting-does-not-exist-987654";
                        Assert.DoesNotContain(sections.Children.Cast<FrameworkElement>(), section => section.Visibility == Visibility.Visible);
                        search.Clear();
                    }
                }
            }
            Assert.DoesNotContain(bindingLog.Lines, line => line.Contains("Steamy.ViewModels", StringComparison.Ordinal) && line.Contains("property not found", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingLog);
            app.Shutdown();
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

    private sealed class SmokeApplication : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
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
        if (method.Name == nameof(IDisposable.Dispose)) return null;
        throw new InvalidOperationException($"UI smoke attempted an external operation: {method.DeclaringType?.Name}.{method.Name}");
    }
}
