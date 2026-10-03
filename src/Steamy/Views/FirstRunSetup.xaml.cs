using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.Views;

/// <summary>Optional first-launch defaults. The full settings page remains available afterward.</summary>
public partial class FirstRunSetup : UserControl
{
    private static readonly string[] Languages = { "System Default", "Deutsch", "English" };
    private static readonly string[] Appearances = { "System", "Light", "Dark" };

    private readonly ISettingsService _settingsService;
    private readonly AppSettings _settings;
    private int _step = 1;
    private bool _isSaving;

    public FirstRunSetup(ISettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _settings = settingsService.Load();
        LanguageSelector.ItemsSource = Languages;
        LanguageSelector.SelectedItem = Languages.Contains(_settings.Language, StringComparer.OrdinalIgnoreCase)
            ? _settings.Language
            : "System Default";
        AppearanceSelector.ItemsSource = Appearances;
        AppearanceSelector.SelectedItem = Appearances.Contains(_settings.Appearance, StringComparer.OrdinalIgnoreCase)
            ? _settings.Appearance
            : "Dark";

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
        UpdateStep();
    }

    public event EventHandler? Completed;

    private void UpdateStep()
    {
        PreferencesStep.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        FoldersStep.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        DownloadsStep.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _step == 1 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _step == 3 ? "Save & finish" : "Continue";
        StepCounter.Text = $"STEP {_step} OF 3";

        (StepTitle.Text, StepDescription.Text) = _step switch
        {
            1 => ("Choose your defaults", "Set the language and look you want Steamy to use."),
            2 => ("Point Steamy to your folders", "Steam is detected when possible; choose where new downloads should go."),
            _ => ("Tune your downloads", "Choose sensible queue, recovery and verification defaults.")
        };

        SetStepMarker(StepOneMarker, StepOneLabel, _step == 1);
        SetStepMarker(StepTwoMarker, StepTwoLabel, _step == 2);
        SetStepMarker(StepThreeMarker, StepThreeLabel, _step == 3);
        SaveErrorText.Visibility = Visibility.Collapsed;
    }

    private static void SetStepMarker(Border marker, TextBlock label, bool active)
    {
        marker.SetResourceReference(Border.BackgroundProperty, active ? "AccentSoftBrush" : "Transparent");
        label.SetResourceReference(TextBlock.ForegroundProperty, active ? "TextPrimaryBrush" : "TextTertiaryBrush");
        label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 3)
        {
            _step++;
            UpdateStep();
            return;
        }

        _ = SaveAndFinishAsync(applyChoices: true);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step <= 1) return;
        _step--;
        UpdateStep();
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => _ = SaveAndFinishAsync(applyChoices: false);

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
                _settings.Appearance = AppearanceSelector.SelectedItem as string ?? "Dark";
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
            if (applyChoices)
            {
                UiThemeService.Apply(_settings.Appearance);
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
