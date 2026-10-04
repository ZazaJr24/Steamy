using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Steamy.Controls;
using Steamy.Models;
using Steamy.ViewModels;

namespace Steamy.Views;

public partial class FirstRunWizard : UserControl
{
    private readonly SettingsViewModel _settings;
    private int _step;
    public string[] Languages { get; } = { "System Default", "Deutsch", "English" };
    public string SelectedLanguage { get; set; } = "System Default";
    public string DownloadFolder { get; set; } = string.Empty;

    public event EventHandler? Completed;

    public FirstRunWizard(SettingsViewModel settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = this;
        var appSettings = settings.Settings;
        SelectedLanguage = appSettings.Language;
        DownloadFolder = appSettings.DownloadFolder;
        Loaded += (_, _) => AnimateOpen();
        ShowStep();
    }

    private void ShowStep()
    {
        LanguageStep.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        PathsStep.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        ProvidersStep.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        TutorialStep.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepLabel.Text = $"Step {_step + 1} of 4";
        BackButton.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _step == 3 ? "Finish" : "Continue";
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 1 && !AuthorizationBox.IsChecked.GetValueOrDefault())
        {
            MessageBox.Show("Confirm that you are authorized to access this content before continuing.", "Authorization required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_step == 2)
        {
            try { await SaveProviderInputsAsync(); }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "Could not save credentials", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        if (_step < 3) { _step++; ShowStep(); return; }
        await CompleteAsync();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 0) return;
        _step--;
        ShowStep();
    }

    private async void Skip_Click(object sender, RoutedEventArgs e) => await CompleteAsync();

    private async Task CompleteAsync()
    {
        var settings = _settings.Settings;
        settings.Language = SelectedLanguage;
        settings.DownloadFolder = DownloadFolder.Trim();
        await _settings.SaveAsync("First-run setup completed.");
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveProviderInputsAsync()
    {
        _settings.HubcapApiKeyInput = HubcapBox.Password;
        _settings.RyuuAuthKeyInput = RyuuBox.Password;
        _settings.DepotBoxApiKeyInput = DepotBox.Password;
        await _settings.SaveCredentialsAsync();
        HubcapBox.Clear();
        RyuuBox.Clear();
        DepotBox.Clear();
        CustomBox.Clear();
    }

    private void SecretChanged(object sender, RoutedEventArgs e) { }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the default download folder", Multiselect = false, ValidateNames = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) DownloadFolder = dialog.FolderName;
        DownloadFolderBox.Text = DownloadFolder;
    }

    private void AnimateOpen()
    {
        if (!MotionPreferences.AnimationsEnabled)
        {
            WizardScale.ScaleX = 1;
            WizardScale.ScaleY = 1;
            return;
        }
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        WizardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing });
        WizardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing });
    }
}
