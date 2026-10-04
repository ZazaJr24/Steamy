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
    private int _step = 1;
    private bool _isSaving;
    private bool _isLoaded;
    private bool _downloaderReady;
    private bool _applyChoices = true;

    public FirstRunSetup(ISettingsService settingsService, IRyuuGameDownloadService? downloaderSetup = null,
        ISecureCredentialService? credentials = null, AppSettings? settings = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _credentials = credentials ?? App.Services?.GetService<ISecureCredentialService>();
        _downloaderSetup = downloaderSetup ?? App.Services?.GetService<IRyuuGameDownloadService>();
        _settings = settings ?? settingsService.Load();
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
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            AnimateStep();
        };
        UpdateStep();
    }

    public event EventHandler? Completed;

    private void UpdateStep()
    {
        var contentSteps = new FrameworkElement[]
        {
            LanguageStep, AppearanceStep, ApiKeysStep, FoldersStep, DownloadsStep, RecoveryStep, ReadyStep
        };
        for (var index = 0; index < contentSteps.Length; index++)
            contentSteps[index].Visibility = index == _step - 1 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = _step == 1 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _step == 7 ? "Finish" : "Continue";
        StepCounter.Text = $"{_step:00}  /  07";
        WizardProgress.Value = _step;

        (StepTitle.Text, StepDescription.Text) = _step switch
        {
            1 => ("Select Language", "Choose the language Steamy should use. You can change it later."),
            2 => ("Choose your appearance", "Pick a look that feels right. You can change it anytime in Settings."),
            3 => ("Connect your services", "Add optional API keys now, or leave them blank and add them later."),
            4 => ("Choose your folders", "Set where Steamy finds Steam and saves downloaded files."),
            5 => ("Tune downloads", "Set speed, connection and retry preferences for downloads."),
            6 => ("Protect your downloads", "Choose how Steamy verifies and resumes interrupted downloads."),
            _ => ("Preparing Steamy", "Checking the downloader and required files for your first use.")
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
        if (_step < 6)
        {
            if (_step == 3 && _credentials is not null)
            {
                var hubcapKey = HubcapKeyBox.Password.Trim();
                var depotBoxKey = DepotBoxKeyBox.Password.Trim();
                if (hubcapKey.Length > 0) await _credentials.SaveAsync("hubcap-api-key", hubcapKey);
                if (depotBoxKey.Length > 0) await _credentials.SaveAsync("depotbox-api-key", depotBoxKey);
            }
            _step++;
            UpdateStep();
            return;
        }

        if (_step == 6)
        {
            _step = 7;
            UpdateStep();
            await CheckDownloaderAsync();
            return;
        }

        if (!_downloaderReady)
        {
            await CheckDownloaderAsync();
            if (!_downloaderReady) return;
        }

        _ = SaveAndFinishAsync(applyChoices: _applyChoices);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step <= 1) return;
        _step--;
        UpdateStep();
    }

    private async void Skip_Click(object sender, RoutedEventArgs e)
    {
        _applyChoices = false;
        UiThemeService.Apply(_settings.Appearance);
        _step = 7;
        UpdateStep();
        await CheckDownloaderAsync();
    }

    private void AppearanceCard_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string appearance })
        {
            _settings.Appearance = appearance;
            UiThemeService.Apply(appearance);
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
            }

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
        }
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
        SaveErrorText.Text = message;
        SaveErrorText.Visibility = Visibility.Visible;
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
