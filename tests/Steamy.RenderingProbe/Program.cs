using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

// Isolated diagnostic only. No Steamy services, account data, network or settings.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2) return 2;
        var report = Path.GetFullPath(args[0]);
        if (File.Exists(report)) return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        if (args.Length == 2)
        {
            if (args[1] != "--software") return 2;
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var content = new StackPanel { Margin = new Thickness(32) };
        content.Children.Add(new TextBlock { Text = "Steamy · rendering diagnostic", FontSize = 18, Foreground = Brushes.White });
        content.Children.Add(new Button { Content = "Static WPF control", Width = 180, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
        var window = new Window
        {
            Title = "Steamy rendering diagnostic", Width = 1440, Height = 900,
            Background = new SolidColorBrush(Color.FromRgb(32, 33, 36)),
            Content = content, ShowInTaskbar = false
        };
        var began = false;
        app.DispatcherUnhandledException += (_, error) =>
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { error = error.Exception.ToString() }));
            error.Handled = true; app.Shutdown(1);
        };
        window.ContentRendered += async (_, _) =>
        {
            if (began) return;
            began = true;
            await Task.Delay(3000);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var dpi = VisualTreeHelper.GetDpi(window);
            var sample = new
            {
                scope = "One static WPF window with text and a stock button. No Steamy artwork, effects or services. Diagnostic comparison only, not a product performance target.",
                assembly = typeof(Program).Assembly.Location,
                runtime = RuntimeInformation.FrameworkDescription,
                renderPreference = RenderOptions.ProcessRenderMode.ToString(),
                renderTier = RenderCapability.Tier >> 16,
                windowWidth = window.ActualWidth, windowHeight = window.ActualHeight,
                contentWidth = content.ActualWidth, contentHeight = content.ActualHeight,
                dpiScaleX = dpi.DpiScaleX, dpiScaleY = dpi.DpiScaleY,
                isActive = window.IsActive,
                privateBytes = process.PrivateMemorySize64,
                workingSetBytes = process.WorkingSet64,
                managedBytes = GC.GetTotalMemory(false),
                nativeMemory = NativeMemoryMap.Capture()
            };
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(sample));
            app.Shutdown(0);
        };
        window.Show();
        return app.Run();
    }
}
