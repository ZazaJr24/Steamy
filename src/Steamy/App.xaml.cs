using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static bool ShouldShowFirstRunSetup { get; private set; }
    private static TaskCompletionSource? _firstRunSetupDismissed;
    private UiResponsivenessMonitor? _responsivenessMonitor;

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

        var roamingData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy");
        var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy");
        var settingsPath = Path.Combine(roamingData, "settings.json");
        var existingDatabase = Path.Combine(roamingData, "content-manager.db");
        var existingActivity = Path.Combine(localData, "game-activity.json");
        ShouldShowFirstRunSetup = !File.Exists(settingsPath)
            && !File.Exists(existingDatabase)
            && !File.Exists(existingActivity)
            && !Directory.Exists(localData)
            && !Directory.Exists(Path.Combine(roamingData, "credentials"));
        _firstRunSetupDismissed = ShouldShowFirstRunSetup
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;

        ApplySavedCulture();

        Services = new ServiceCollection().AddSteamyServices().BuildServiceProvider();
        base.OnStartup(e);
        ApplySavedAppearance();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        _responsivenessMonitor = new UiResponsivenessMonitor(window, Services.GetRequiredService<ILoggingService>());

        ApplySavedBackdrop();

        _ = RestoreDownloadQueueAsync();
        _ = RunStartupUpdateFlowAsync(window);
    }

    /// <summary>
    /// Removes what the last update left behind, then asks GitHub in the background whether a newer
    /// release exists, a moment after start so the window is responsive first.
    /// </summary>
    private static async Task RunStartupUpdateFlowAsync(Window window)
    {
        try
        {
            var updates = Services.GetRequiredService<IUpdateService>();
            await Task.Run(updates.CleanUpPreviousInstall);

            if (!Services.GetRequiredService<ISettingsService>().Load().AutoUpdate) return;
            if (ShouldShowFirstRunSetup && _firstRunSetupDismissed is not null)
                await _firstRunSetupDismissed.Task;
            await Task.Delay(TimeSpan.FromSeconds(2));
            await CheckForUpdatesAsync();
        }
        catch (Exception exception)
        {
            LogException("The update check failed", exception);
        }
    }

    /// <summary>Checks GitHub and offers a newer release. Returns a short status for the settings page.</summary>
    public static void MarkFirstRunSetupCompleted()
    {
        ShouldShowFirstRunSetup = false;
        _firstRunSetupDismissed?.TrySetResult();
        _firstRunSetupDismissed = null;
    }

    public static async Task<string> CheckForUpdatesAsync()
    {
        var updates = Services.GetRequiredService<IUpdateService>();
        var logging = Services.GetRequiredService<ILoggingService>();

        UpdateInfo? update;
        try
        {
            update = await updates.CheckAsync();
        }
        catch (UpdateException exception)
        {
            logging.Add(LogLevel.Warning, "Updater", $"Update check failed: {exception.Message}");
            return $"Could not check for updates: {exception.Message}";
        }

        if (update is null) return $"You're up to date — version {updates.CurrentVersion}.";

        logging.Add(LogLevel.Info, "Updater", $"Version {update.Version} is available (running {updates.CurrentVersion}).");
        if (Current.MainWindow is not MainWindow main) return $"Version {update.Version} is available.";
        var prompt = new Views.UpdatePrompt(updates, update);
        main.ShowOverlay(prompt);
        await prompt.Closed;
        main.HideOverlay();
        return $"Version {update.Version} is available.";
    }

    /// <summary>
    /// Brings back the stored queue after the window exists so the collection is filled on the
    /// UI thread. Jobs that were interrupted are restored as interrupted, never as completed.
    /// </summary>
    private static async Task RestoreDownloadQueueAsync()
    {
        try
        {
            if (ShouldShowFirstRunSetup && _firstRunSetupDismissed is not null)
                await _firstRunSetupDismissed.Task;

            var store = Services.GetRequiredService<IAppDataStore>();
            var queueStore = Services.GetRequiredService<IDownloadQueueStore>();
            await queueStore.RestoreAsync(store.Downloads, Services.GetRequiredService<ISettingsService>().Load().AutoResume);
        }
        catch (Exception exception)
        {
            Services.GetRequiredService<ILoggingService>()
                .Add(LogLevel.Warning, "DownloadManager", $"The stored download queue could not be restored: {exception.GetType().Name}.");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _responsivenessMonitor?.Dispose();
        SaveAllDownloads();
        StopOwnedDownloads();
        FlushLogsOnExit();
        base.OnExit(e);
    }

    private static void FlushLogsOnExit()
    {
        try
        {
            if (Services?.GetService<ILoggingService>() is not InMemoryLoggingService logging) return;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            // Persistence runs on a worker and never waits for the UI dispatcher. A busy or
            // failing disk cannot keep application shutdown waiting indefinitely.
            logging.FlushAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch { }
    }

    private static void SaveAllDownloads()
    {
        try
        {
            var store = Services.GetRequiredService<IAppDataStore>();
            var queueStore = Services.GetRequiredService<IDownloadQueueStore>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            foreach (var job in store.Downloads.ToList())
            {
                if (deadline.IsCancellationRequested) break;
                try { queueStore.SaveAsync(job, deadline.Token).WaitAsync(deadline.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }
        catch { }
    }

    private static void StopOwnedDownloads()
    {
        // Cancelling registered operations invokes their process-tree cancellation hooks.
        // Independently launched downloader processes belong to the user.
        try { (Services?.GetService<IDownloadManager>() as IDisposable)?.Dispose(); } catch { }
        try { (Services?.GetService<IDepotDownloaderService>() as IDisposable)?.Dispose(); } catch { }
    }

    /// <summary>
    /// Shows the real error, not just the wrapper: TargetInvocationException & Co. hide the cause
    /// in InnerException, so the dialog walks the full chain and appends the failing stack frames.
    /// The same text is written to %AppData%\Steamy\crash.log for later inspection.
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
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy");
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
            "Steamy",
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
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy", "settings.json");
            if (!File.Exists(path)) return;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("Language", out var language)
                && !document.RootElement.TryGetProperty("language", out language)) return;
            var value = language.GetString();
            if (string.IsNullOrWhiteSpace(value) || value.Equals("System Default", StringComparison.OrdinalIgnoreCase)) return;
            var culture = new CultureInfo(value.StartsWith("Deutsch", StringComparison.OrdinalIgnoreCase) ? "de-DE" : "en-US");
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
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
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy", "settings.json");
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
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steamy", "settings.json");
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
