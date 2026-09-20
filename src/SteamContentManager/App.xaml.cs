using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SteamContentManager.Models;
using SteamContentManager.Services;
using SteamContentManager.ViewModels;

namespace SteamContentManager;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            // WPF-UI 4.x throws a non-fatal type-conversion error for ContentDialogButton
            // during resource loading. Swallow it silently — it does not affect the app.
            if (args.Exception.Message.Contains("ContentDialogButton", StringComparison.Ordinal))
            {
                args.Handled = true;
                return;
            }

            ShowCrashDialog("An unexpected error occurred", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                ShowCrashDialog("A critical error occurred", exception);
            }
        };
        // A faulted background Task nobody awaits would otherwise tear the process down when it is
        // finalized. Record it and mark it observed so the app keeps running.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException("A background task failed", args.Exception);
            args.SetObserved();
        };

        ApplySavedCulture();

        Services = new ServiceCollection().AddSteamContentManagerServices().BuildServiceProvider();
        Services.GetRequiredService<ILocalDatabase>().Initialize();
        base.OnStartup(e);
        ApplySavedAppearance();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        ApplySavedBackdrop();

        _ = RestoreDownloadQueueAsync();
    }

    /// <summary>
    /// Brings back the stored queue after the window exists so the collection is filled on the
    /// UI thread. Jobs that were interrupted are restored as interrupted, never as completed.
    /// </summary>
    private static async Task RestoreDownloadQueueAsync()
    {
        try
        {
            var store = Services.GetRequiredService<IAppDataStore>();
            var queueStore = Services.GetRequiredService<IDownloadQueueStore>();
            await queueStore.RestoreAsync(store.Downloads, autoResume: false);
        }
        catch (Exception exception)
        {
            Services.GetRequiredService<ILoggingService>()
                .Add(LogLevel.Warning, "DownloadManager", $"The stored download queue could not be restored: {exception.GetType().Name}.");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SaveAllDownloads();
        KillDepotDownloaderProcesses();
        base.OnExit(e);
    }

    private static void SaveAllDownloads()
    {
        try
        {
            var store = Services.GetRequiredService<IAppDataStore>();
            var queueStore = Services.GetRequiredService<IDownloadQueueStore>();
            foreach (var job in store.Downloads.ToList())
            {
                try { queueStore.SaveAsync(job).GetAwaiter().GetResult(); }
                catch { }
            }
        }
        catch { }
    }

    private static void KillDepotDownloaderProcesses()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("DepotDownloaderMod"))
            {
                try { proc.Kill(true); } catch { }
                proc.Dispose();
            }
        }
        catch { }
    }

    /// <summary>
    /// Shows the real error, not just the wrapper: TargetInvocationException & Co. hide the cause
    /// in InnerException, so the dialog walks the full chain and appends the failing stack frames.
    /// The same text is written to %AppData%\SteamContentManager\crash.log for later inspection.
    /// </summary>
    private static void ShowCrashDialog(string headline, Exception exception)
    {
        var details = new System.Text.StringBuilder();
        var current = exception;
        while (current is not null)
        {
            details.AppendLine($"[{current.GetType().FullName}] {current.Message}");
            var frames = current.StackTrace?
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrEmpty(line))
                .Take(8);
            if (frames is not null)
                foreach (var frame in frames)
                    details.AppendLine("   at " + frame);
            details.AppendLine();
            current = current.InnerException;
        }

        LogException(headline, exception);
        try
        {
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamContentManager");
            Directory.CreateDirectory(logFolder);
            File.AppendAllText(
                Path.Combine(logFolder, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} — {headline}\n{BuildStamp.Describe()}\n{details}\n---\n");
        }
        catch
        {
            // Crash reporting must never throw a second exception.
        }

        MessageBox.Show(
            $"{headline}:\n\n{details}\n{BuildStamp.Describe()}",
            "ResonanceTools",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>Writes an unhandled error to the in-app log; logging itself must never throw.</summary>
    private static void LogException(string headline, Exception exception)
    {
        try
        {
            var logging = Services?.GetService<ILoggingService>();
            logging?.Add(LogLevel.Error, "Application", $"{headline}: {exception.GetType().Name} — {exception.Message} ({BuildStamp.Describe()})");
        }
        catch
        {
            // Nothing left to do: the dialog below is the last line of defence.
        }
    }

    private static void ApplySavedCulture()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamContentManager", "settings.json");
            if (!File.Exists(path)) return;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("Language", out var language)
                && !document.RootElement.TryGetProperty("language", out language)) return;
            var value = language.GetString();
            if (string.IsNullOrWhiteSpace(value) || value.Equals("System Default", StringComparison.OrdinalIgnoreCase)) return;
            var culture = new CultureInfo(value.StartsWith("Deutsch", StringComparison.OrdinalIgnoreCase) ? "de-DE" : "en-US");
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        catch
        {
            // A malformed optional settings file must never prevent the app from starting.
        }
    }

    private static void ApplySavedAppearance()
    {
        var saved = "Dark";
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamContentManager", "settings.json");
            if (File.Exists(path))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("Appearance", out var appearance)
                    || document.RootElement.TryGetProperty("appearance", out appearance))
                {
                    saved = appearance.GetString() ?? saved;
                }
            }
        }
        catch { }

        UiThemeService.Apply(saved);
    }

    private static void ApplySavedBackdrop()
    {
        var saved = "Mica";
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamContentManager", "settings.json");
            if (File.Exists(path))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("BackdropStyle", out var backdrop)
                    || document.RootElement.TryGetProperty("backdropStyle", out backdrop))
                {
                    saved = backdrop.GetString() ?? saved;
                }
            }
        }
        catch { }

        UiThemeService.ApplyBackdrop(saved);
    }
}
