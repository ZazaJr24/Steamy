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
    public static bool ShouldShowTransparencyNotice { get; private set; }
    public static bool IsLaunched { get; private set; }
    private static TaskCompletionSource? _transparencyDismissed;
    public static void MarkTransparencyCompleted() { ShouldShowTransparencyNotice = false; _transparencyDismissed?.TrySetResult(); }
    private static async Task AwaitTransparencyAsync() { if (_transparencyDismissed is not null) await _transparencyDismissed.Task; }
    private static TaskCompletionSource? _firstRunSetupDismissed;
    private UiResponsivenessMonitor? _responsivenessMonitor;

    protected override async void OnStartup(StartupEventArgs e)
    {
        StartupTimings.Mark("OnStartup");
        // A bootstrap preparation process has no window, navigation, game or Steam services.
        if (e.Args.Length == 2 && e.Args[0] == "--prepare-tools")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await ToolPreparationService.PrepareAsync(e.Args[1]); Shutdown(0); }
            catch (Exception) { Shutdown(1); }
            return;
        }
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

        string roamingData;
        try { roamingData = Steamy.Storage.AppDataPaths.Root; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { MessageBox.Show(error.Message, "Steamy storage", MessageBoxButton.OK, MessageBoxImage.Warning); Shutdown(); return; }
        var localData = Steamy.Storage.AppDataPaths.LegacyLocalRoot;
        var settingsPath = Path.Combine(roamingData, "settings.json");
        var existingDatabase = Path.Combine(roamingData, "content-manager.db");
        var existingActivity = Path.Combine(localData, "game-activity.json");
        ShouldShowFirstRunSetup = !File.Exists(settingsPath)
            && !File.Exists(existingDatabase)
            && !File.Exists(existingActivity)
            && !Directory.Exists(localData)
            && !Directory.Exists(Path.Combine(roamingData, "credentials"));
        try
        {
            if (File.Exists(settingsPath))
            {
                using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (saved.RootElement.TryGetProperty("FirstRunCompleted", out var completed) && completed.ValueKind == System.Text.Json.JsonValueKind.False)
                    ShouldShowFirstRunSetup = true;
            }
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { }
        _firstRunSetupDismissed = ShouldShowFirstRunSetup
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;

        try
        {
            var paths = Steamy.Storage.AppDataPaths.Locations;
            if (paths.NeedsPreparation(localData))
            {
                var anotherWindow = Process.GetProcessesByName("Steamy").Any(process =>
                {
                    using (process) return process.Id != Environment.ProcessId;
                });
                if (anotherWindow) throw new IOException("Close other Steamy windows before transferring your saved data, then start this version again. Your files have not been moved.");
                // Keep startup responsive while a large artwork/tool cache is copied.
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var content = new System.Windows.Controls.StackPanel { Margin = new Thickness(28) };
                content.Children.Add(new System.Windows.Controls.TextBlock { Text = "Preparing your Steamy data", FontSize = 22, Margin = new Thickness(0, 0, 0, 14) });
                content.Children.Add(new System.Windows.Controls.TextBlock { Text = "Copying saved data to your data folder. Original files are kept. Please wait…", TextWrapping = TextWrapping.Wrap });
                content.Children.Add(new System.Windows.Controls.ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 22, 0, 0) });
                var progress = new Window { Title = "Steamy", Width = 480, Height = 220, Content = content, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                System.ComponentModel.CancelEventHandler guard = (_, args) => args.Cancel = true;
                progress.Closing += guard;
                progress.Show();
                try { await Task.Run(StorageTrackerTransition.Prepare); }
                finally { progress.Closing -= guard; progress.Close(); }
            }
            else Steamy.Storage.AppDataPaths.Initialize();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show(exception.Message + "\n\nYour original data is kept. No storage switch was completed.", "Steamy storage", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(); return;
        }

        ApplySavedCulture();

        Services = new ServiceCollection().AddSteamyServices().BuildServiceProvider();
        IsLaunched = true;
        ShouldShowTransparencyNotice = Services.GetRequiredService<ISettingsService>().Load().TransparencyAcknowledgement != Views.TransparencyNotice.CurrentAcknowledgement;
        if (ShouldShowTransparencyNotice) _transparencyDismissed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        base.OnStartup(e);
        ApplySavedAppearance();

        var openSocial = DiscordIntegration.Enabled && e.Args.Contains("--discord-join", StringComparer.Ordinal);
        StartupTimings.Mark("Before MainWindow");
        var window = new MainWindow(openSocial, e.Args.Contains("--controller-setup", StringComparer.Ordinal));
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        EventHandler? warmTools = null;
        warmTools = async (_, _) =>
        {
            window.ContentRendered -= warmTools;
            try
            {
                if (Services.GetRequiredService<ISettingsService>() is JsonSettingsService settings)
                    await settings.WarmToolPathAsync();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { LogException("Optional tool discovery failed", error); }
        };
        window.ContentRendered += warmTools;
        if (e.Args.Contains("--background-test", StringComparer.Ordinal))
        {
            // Keep the real render path active without taking focus or showing a taskbar entry.
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
        }
        window.Show();
        StartupTimings.Mark("After Show");
        window.EnableControllerNavigation();
        (Services.GetRequiredService<IControllerGameProfiles>() as ControllerGameProfiles)?.Start();
        if (Services.GetRequiredService<ISettingsService>().Load().ControllerBackendEnabled) _ = RestoreControllersAsync();
        if (DiscordIntegration.Enabled) _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            _ = Services.GetRequiredService<DiscordFriendsViewModel>().InitializeAsync()));
        _responsivenessMonitor = new UiResponsivenessMonitor(window, Services.GetRequiredService<ILoggingService>());

        ApplySavedBackdrop();

        _ = RestoreDownloadQueueAsync();
        _ = StartPlayTimeTrackerAsync();
        _ = RunStartupUpdateFlowAsync(window);
    }

    private static async Task StartPlayTimeTrackerAsync()
    {
        await AwaitTransparencyAsync();
        if (ShouldShowFirstRunSetup && _firstRunSetupDismissed is not null)
            await _firstRunSetupDismissed.Task;
        await Services.GetRequiredService<IPlayTimeService>().ConfigureAsync(
            Services.GetRequiredService<ISettingsService>().Load(), Services.GetRequiredService<IAppDataStore>().Games);
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
            await AwaitTransparencyAsync();
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
            await AwaitTransparencyAsync();
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
        if (Services is null) { base.OnExit(e); return; }
        _responsivenessMonitor?.Dispose();
        if (DiscordIntegration.Enabled) (Services.GetService<IDiscordSocialService>() as IDisposable)?.Dispose();
        (Services.GetService<IControllerBackend>() as IDisposable)?.Dispose();
        (Services.GetService<IControllerGameProfiles>() as IDisposable)?.Dispose();
        SaveAllDownloads();
        StopOwnedDownloads();
        FlushLogsOnExit();
        base.OnExit(e);
    }
    private static async Task RestoreControllersAsync()
    { try { await Services.GetRequiredService<IControllerBackend>().StartAsync(); } catch { /* Controller page retains the reconnect/driver error. */ } }

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
            var logFolder = Steamy.Storage.AppDataPaths.Root;
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

    public static void ApplyCulture(string? selection)
    {
        try
        {
            var cultureName = UiLanguageCatalog.ResolveCultureName(selection);
            CultureInfo culture;
            try
            {
                culture = string.IsNullOrWhiteSpace(cultureName)
                    ? CultureInfo.GetCultureInfo("en-US")
                    : new CultureInfo(cultureName);
            }
            catch (CultureNotFoundException)
            {
                culture = CultureInfo.GetCultureInfo("en-US");
            }
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
        }
        catch { }
    }

    private static void ApplySavedCulture()
    {
        ApplyCulture("English");
        try
        {
            var path = Path.Combine(Steamy.Storage.AppDataPaths.Root, "settings.json");
            if (!File.Exists(path)) return;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("Language", out var language)
                && !document.RootElement.TryGetProperty("language", out language)) return;
            var value = language.GetString();
            var cultureName = UiLanguageCatalog.ResolveCultureName(value);
            if (string.IsNullOrWhiteSpace(cultureName)) return;
            ApplyCulture(value);
        }
        catch
        {
            // A malformed optional settings file must never prevent the app from starting.
        }
    }

    private static void ApplySavedAppearance()
    {
        var saved = "Light";
        try
        {
            var path = Path.Combine(Steamy.Storage.AppDataPaths.Root, "settings.json");
            if (File.Exists(path))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var currentDesign = document.RootElement.TryGetProperty("AppearanceDesignVersion", out var designVersion) && designVersion.TryGetInt32(out var version) && version >= 1;
                if (currentDesign && (document.RootElement.TryGetProperty("Appearance", out var appearance)
                    || document.RootElement.TryGetProperty("appearance", out appearance)))
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
            var path = Path.Combine(Steamy.Storage.AppDataPaths.Root, "settings.json");
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
