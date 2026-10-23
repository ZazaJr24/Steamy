using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Controls;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.Views;

/// <summary>Optional first-launch defaults. The full settings page remains available afterward.</summary>
public partial class FirstRunSetup : UserControl
{
    private static readonly string[] Languages = UiLanguageCatalog.Options;
    private static readonly string[] Appearances = { "System", "Light", "Dark" };

    private readonly ISettingsService _settingsService;
    private readonly ISecureCredentialService? _credentials;
    private readonly IRyuuGameDownloadService? _downloaderSetup;
    private readonly AppSettings _settings;
    private readonly IPcGameScanService? _gameScanner;
    private CancellationTokenSource? _scanCancellation;
    private int _step = 1;
    private bool _isSaving;
    private bool _isLoaded;
    private bool _downloaderReady;
    private bool _applyChoices = true;
    private Window? _ownerWindow;

    public FirstRunSetup(ISettingsService settingsService, IRyuuGameDownloadService? downloaderSetup = null,
        ISecureCredentialService? credentials = null, AppSettings? settings = null, IPcGameScanService? gameScanner = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _credentials = credentials ?? App.Services?.GetService<ISecureCredentialService>();
        _downloaderSetup = downloaderSetup ?? App.Services?.GetService<IRyuuGameDownloadService>();
        _settings = settings ?? settingsService.Load();
        SetupBackgroundPreferences.Settings = _settings;
        _gameScanner = gameScanner ?? App.Services?.GetService<IPcGameScanService>();
        Unloaded += (_, _) =>
        {
            _scanCancellation?.Cancel();
            SetupDiscordConnection.CancelPendingOperation();
            if (_ownerWindow is not null) _ownerWindow.SizeChanged -= OwnerSizeChanged;
            _ownerWindow = null;
        };
        LanguageSelector.ItemsSource = Languages;
        LanguageSelector.SelectedItem = Languages.FirstOrDefault(language => language.Equals(_settings.Language, StringComparison.OrdinalIgnoreCase))
            ?? (_settings.Language.Equals("Deutsch", StringComparison.OrdinalIgnoreCase) ? "de-DE · Deutsch"
                : _settings.Language.Equals("English", StringComparison.OrdinalIgnoreCase) ? "en-US · English" : "System Default");
        var savedAppearance = Appearances.Contains(_settings.Appearance, StringComparer.OrdinalIgnoreCase)
            ? _settings.Appearance
            : "Dark";
        SystemAppearance.IsChecked = savedAppearance.Equals("System", StringComparison.OrdinalIgnoreCase);
        LightAppearance.IsChecked = savedAppearance.Equals("Light", StringComparison.OrdinalIgnoreCase);
        DarkAppearance.IsChecked = savedAppearance.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        TrailerPreviewsToggle.IsChecked = _settings.AutoplayTrailers;

        var detectedSteamRoot = SteamLibraryService.FindSteamRoot();
        SteamFolderText.Text = !string.IsNullOrWhiteSpace(_settings.SteamLibraryPath)
            ? _settings.SteamLibraryPath
            : detectedSteamRoot ?? string.Empty;
        DownloadFolderText.Text = !string.IsNullOrWhiteSpace(_settings.DownloadFolder)
            ? _settings.DownloadFolder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamGames");

        ParallelJobsText.Text = _settings.ParallelDownloads.ToString(CultureInfo.InvariantCulture);
        ConnectionsText.Text = _settings.DownloadConnections.ToString(CultureInfo.InvariantCulture);
        SpeedLimitText.Text = _settings.DownloadRateLimitMiB.ToString(CultureInfo.InvariantCulture);
        RetryCountText.Text = _settings.RetryCount.ToString(CultureInfo.InvariantCulture);
        VerifyCheck.IsChecked = _settings.VerifyAfterDownload;
        ResumeCheck.IsChecked = _settings.AutoResume;
        LancacheCheck.IsChecked = _settings.UseLancache;
        Loaded += async (_, _) =>
        {
            _isLoaded = true;
            if (Window.GetWindow(this) is { } window)
            {
                _ownerWindow = window;
                window.SizeChanged += OwnerSizeChanged;
                ResizeWizard();
            }
            AnimateStep();
            await DiscoverKnownGamesAsync();
        };
        UpdateStep();
    }

    public event EventHandler? Completed;

    private void OwnerSizeChanged(object sender, SizeChangedEventArgs e) => ResizeWizard();

    private void ResizeWizard()
    {
        if (_ownerWindow is null) return;
        WizardPanel.MaxWidth = Math.Max(300, _ownerWindow.ActualWidth - 60);
        WizardPanel.MaxHeight = Math.Max(350, _ownerWindow.ActualHeight - 100);
    }

    private void UpdateStep()
    {
        GameScanStep.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        LanguageStep.Visibility = AppearanceStep.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        FoldersStep.Visibility = AdvancedPreferences.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        DownloadsStep.Visibility = RecoveryStep.Visibility = Visibility.Visible;
        ApiKeysStep.Visibility = Visibility.Collapsed;
        DiscordStep.Visibility = DiscordIntegration.Enabled && _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        ControllerStep.Visibility = _step == 5 ? Visibility.Visible : Visibility.Collapsed;
        ReadyStep.Visibility = _step == 6 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _step == 1 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _step == 6 ? (_downloaderReady ? "Save & finish" : "Finish") : _step == 5 ? "Continue to finish" : "Continue";
        var visibleStep = !DiscordIntegration.Enabled && _step > 4 ? _step - 1 : _step;
        var total = DiscordIntegration.Enabled ? 6 : 5;
        StepCounter.Text = $"{visibleStep:00}  /  {total:00}";
        WizardProgress.Maximum = total;
        WizardProgress.Value = visibleStep;
        (StepTitle.Text, StepDescription.Text) = _step switch
        {
            1 => ("Your games, together", "Your installed games come first. Find more folders now or scan later."),
            2 => ("Make it yours", "Choose your language and appearance. Everything can be changed in Settings."),
            3 => ("A place for your games", "Choose your folders. Download preferences are optional; the defaults are ready to use."),
            4 => ("Bring your friends", "Connect Discord to chat, create parties and invite your friends. You can also do this later."),
            5 => ("Ready for your controller", "Follow these steps for PlayStation mapping, or continue with native Xbox support. Driver installation is optional."),
            _ => ("One last thing", "Preparing the downloader for your first install.")
        };
        SaveErrorText.Visibility = Visibility.Collapsed;
        if (_isLoaded) AnimateStep();
    }

    private void AnimateStep()
    {
        if (!MotionPreferences.AnimationsEnabled)
        {
            StepContent.Opacity = 1;
            StepScale.ScaleX = StepScale.ScaleY = 1;
            StepSlide.Y = 0;
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        StepContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
        StepScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing });
        StepScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing });
        StepSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = easing });
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null) return;
        if (_step < 5) { if (_step == 4) SetupDiscordConnection.CancelPendingOperation(); _step += !DiscordIntegration.Enabled && _step == 3 ? 2 : 1; UpdateStep(); return; }
        if (_step == 5)
        {
            _step = 6; UpdateStep(); await CheckDownloaderAsync(); return;
        }

        if (!_downloaderReady)
        {
            await CheckDownloaderAsync();
            if (!_downloaderReady) return;
        }

        await SaveAndFinishAsync(applyChoices: _applyChoices);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null) return;
        if (_step <= 1) return;
        if (_step == 4) SetupDiscordConnection.CancelPendingOperation();
        _step -= !DiscordIntegration.Enabled && _step == 5 ? 2 : 1;
        UpdateStep();
    }

    private async void Skip_Click(object sender, RoutedEventArgs e)
    {
        _applyChoices = false;
        SetupDiscordConnection.CancelPendingOperation();
        _scanCancellation?.Cancel();
        await SaveAndFinishAsync(applyChoices: false);
    }

    private void AppearanceCard_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string appearance })
        {
            _settings.Appearance = appearance;
            UiThemeService.Apply(appearance);
            UiThemeService.ConfigureBackground(_settings);
        }
    }

    private async Task SaveAndFinishAsync(bool applyChoices)
    {
        if (_isSaving) return;
        SaveErrorText.Visibility = Visibility.Collapsed;

        string? steamFolder = null;
        string downloadFolder = string.Empty;
        int parallelJobs = _settings.ParallelDownloads;
        int connections = _settings.DownloadConnections;
        int speedLimit = _settings.DownloadRateLimitMiB;
        int retryCount = _settings.RetryCount;

        if (applyChoices)
        {
            if (!TryReadInteger(ParallelJobsText.Text, 1, 16, out parallelJobs))
            {
                ShowValidationError("Parallel jobs must be a whole number from 1 to 16.", ParallelJobsText);
                return;
            }
            if (!TryReadInteger(ConnectionsText.Text, 1, DepotDownloaderArgumentBuilder.MaxDownloadsLimit, out connections))
            {
                ShowValidationError($"Connections per download must be a whole number from 1 to {DepotDownloaderArgumentBuilder.MaxDownloadsLimit}.", ConnectionsText);
                return;
            }
            if (!TryReadInteger(SpeedLimitText.Text, 0, 1024, out speedLimit))
            {
                ShowValidationError("Speed limit must be a whole number from 0 to 1024 MiB/s (0 = unlimited).", SpeedLimitText);
                return;
            }
            if (!TryReadInteger(RetryCountText.Text, 0, 10, out retryCount))
            {
                ShowValidationError("Retry attempts must be a whole number from 0 to 10.", RetryCountText);
                return;
            }
            try
            {
                var enteredSteamFolder = SteamFolderText.Text.Trim();
                if (enteredSteamFolder.Length > 0)
                {
                    steamFolder = Path.GetFullPath(enteredSteamFolder);
                    if (!Directory.Exists(steamFolder))
                    {
                        ShowValidationError("That Steam folder does not exist. Browse to the Steam installation folder or leave it blank for automatic detection.", SteamFolderText);
                        return;
                    }
                }

                var enteredDownloadFolder = DownloadFolderText.Text.Trim();
                downloadFolder = Path.GetFullPath(enteredDownloadFolder.Length == 0
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamGames")
                    : enteredDownloadFolder);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {
                SaveErrorText.Text = "One of the folder paths is not valid or cannot be accessed. Check the path and try again.";
                SaveErrorText.Visibility = Visibility.Visible;
                return;
            }
        }

        _isSaving = true;
        NextButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        try
        {
            if (applyChoices)
            {
                _settings.Language = LanguageSelector.SelectedItem as string ?? "System Default";
                _settings.SteamLibraryPath = steamFolder ?? SteamLibraryService.FindSteamRoot() ?? string.Empty;
                _settings.DownloadFolder = downloadFolder;
                _settings.ParallelDownloads = parallelJobs;
                _settings.DownloadConnections = connections;
                _settings.DownloadRateLimitMiB = speedLimit;
                _settings.RetryCount = retryCount;
                _settings.VerifyAfterDownload = VerifyCheck.IsChecked == true;
                _settings.AutoResume = ResumeCheck.IsChecked == true;
                _settings.UseLancache = LancacheCheck.IsChecked == true;
                _settings.AutoplayTrailers = TrailerPreviewsToggle.IsChecked == true;
            }

            _settings.FirstRunCompleted = true;
            await _settingsService.SaveAsync(_settings);
            if (applyChoices && _credentials is not null)
            {
                var hubcapKey = HubcapKeyBox.Password.Trim();
                if (hubcapKey.Length > 0) await _credentials.SaveAsync("hubcap-api-key", hubcapKey);
                var depotBoxKey = DepotBoxKeyBox.Password.Trim();
                if (depotBoxKey.Length > 0) await _credentials.SaveAsync("depotbox-api-key", depotBoxKey);
            }
            if (applyChoices)
            {
                UiThemeService.Apply(_settings.Appearance);
                UiThemeService.ConfigureBackground(_settings);
                MotionPreferences.ConfigureTrailers(_settings.AutoplayTrailers);
                App.ApplyCulture(_settings.Language);
            }
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            SaveErrorText.Text = $"Settings could not be saved. {exception.Message}";
            SaveErrorText.Visibility = Visibility.Visible;
            NextButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
            BackButton.IsEnabled = _step > 1;
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task CheckDownloaderAsync()
    {
        DependencyStatus.Text = "Checking and downloading the verified downloader…";
        DependencyProgress.IsIndeterminate = true;
        NextButton.IsEnabled = false;
        FinishLaterButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        try
        {
            var status = new Progress<string>(ReportDependencyStatus);
            _downloaderReady = _downloaderSetup is not null
                ? await _downloaderSetup.EnsureDepotDownloaderModAsync(status)
                : BundledModCapabilities.SupportsOwnFork(BundledModCapabilities.SelectExecutable(null));
            DependencyProgress.IsIndeterminate = false;
            DependencyProgress.Value = _downloaderReady ? 100 : 0;
            DependencyStatus.Text = _downloaderReady
                ? "Verified downloader ready. Steamy is set up for your first download."
                : "The downloader could not be verified or downloaded. Check your connection, then retry.";
            NextButton.Content = _downloaderReady ? "Save & finish" : "Retry download";
            NextButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            DependencyProgress.IsIndeterminate = false;
            DependencyStatus.Text = $"Downloader setup failed: {exception.Message}";
            NextButton.Content = "Retry download";
            NextButton.IsEnabled = true;
        }
        finally
        {
            SkipButton.IsEnabled = true;
            BackButton.IsEnabled = true;
            FinishLaterButton.IsEnabled = true;
        }
    }

    private async void FinishLater_Click(object sender, RoutedEventArgs e) => await SaveAndFinishAsync(applyChoices: _applyChoices);

    private async Task DiscoverKnownGamesAsync()
    {
        GameScanStatus.Text = "Finding games in known libraries…";
        try
        {
            if (App.Services?.GetService<ILibrarySyncService>() is { } library) await library.RefreshAsync();
            ShowInstalledPreview();
        }
        catch { GameScanStatus.Text = "Choose a folder to find installed games, or continue and scan later."; }
    }
    private void ShowInstalledPreview()
    {
        var games = App.Services?.GetService<IAppDataStore>()?.Games.Where(game => game.InstallState == GameInstallState.Installed && !GameClassification.IsRuntime(game.AppId, game.Name)).ToArray() ?? [];
        InstalledPreview.ItemsSource = games.Take(6).ToArray();
        GameScanStatus.Text = games.Length == 0 ? "No games found in known libraries yet" : $"{games.Length} installed games found";
        if (App.Services?.GetService<IArtworkService>() is { } artwork) _ = artwork.LoadManyAsync(games.Take(6));
    }

    private async Task ScanGamesAsync(IReadOnlyList<string>? roots = null)
    {
        if (_scanCancellation is not null || _gameScanner is null) return;
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        NextButton.IsEnabled = BackButton.IsEnabled = ScanGamesButton.IsEnabled = ScanFolderButton.IsEnabled = false;
        StopGameScanButton.IsEnabled = true;
        GameScanProgress.IsIndeterminate = true;
        GameScanStatus.Text = "Looking for installed games…";
        try
        {
            var progress = new Progress<GameScanProgress>(p =>
            {
                GameScanStatus.Text = $"{p.Games} games found · {p.Folders:N0} folders checked";
                GameScanFolder.Text = p.CurrentFolder;
            });
            var result = await _gameScanner.ScanAsync(progress, cancellation.Token, roots);
            GameScanStatus.Text = $"{(result.Cancelled ? "Scan stopped" : "Scan complete")} · {result.Games.Count} games found";
            GameScanFolder.Text = $"{result.Folders:N0} folders checked · {result.Skipped:N0} inaccessible folders skipped";
        }
        catch (Exception e) { GameScanStatus.Text = $"Scan could not finish: {e.Message}"; }
        finally
        {
            _scanCancellation = null;
            GameScanProgress.IsIndeterminate = false;
            GameScanProgress.Value = 100;
            NextButton.IsEnabled = BackButton.IsEnabled = ScanGamesButton.IsEnabled = ScanFolderButton.IsEnabled = true;
            StopGameScanButton.IsEnabled = false;
        }
    }
    private async void ScanGames_Click(object sender, RoutedEventArgs e) => await ScanGamesAsync();
    private void StopGameScan_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();
    private async void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Find games in a folder", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await ScanGamesAsync([dialog.FolderName]);
    }

    private void ReportDependencyStatus(string message)
    {
        DependencyStatus.Text = message;
        var percentMarker = message.LastIndexOf('·');
        var percentEnd = message.LastIndexOf('%');
        if (percentEnd > percentMarker && percentMarker >= 0
            && double.TryParse(message[(percentMarker + 1)..percentEnd].Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var percent))
        {
            DependencyProgress.IsIndeterminate = false;
            DependencyProgress.Value = Math.Clamp(percent, 0, 100);
        }
        else
        {
            DependencyProgress.IsIndeterminate = true;
        }
    }

    private static bool TryReadInteger(string? value, int minimum, int maximum, out int parsed)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
            && parsed >= minimum && parsed <= maximum;

    private void ShowValidationError(string message, Control field)
    {
        _step = 3;
        UpdateStep();
        SaveErrorText.Text = message;
        SaveErrorText.Visibility = Visibility.Visible;
        if (field != SteamFolderText) AdvancedPreferences.IsExpanded = true;
        field.Focus();
        if (field is TextBox textBox) textBox.SelectAll();
    }

    private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || LanguageSelector.SelectedItem is not string language) return;
        App.ApplyCulture(language);
    }

    private void BrowseSteam_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select your Steam installation folder", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) SteamFolderText.Text = dialog.FolderName;
    }

    private void BrowseDownloads_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the default download folder", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) DownloadFolderText.Text = dialog.FolderName;
    }
}
