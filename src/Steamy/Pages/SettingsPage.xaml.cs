using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Steamy.Services;
using Steamy.ViewModels;
using Wpf.Ui.Appearance;

namespace Steamy.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        Resources.Add("StringVis", new SettingsStringToVisibilityConverter());
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SettingsViewModel>();

        // After a save or a delete the typed secrets are gone from memory; the boxes have to match,
        // otherwise they would keep showing characters that no longer mean anything.
        ViewModel.CredentialInputsCleared += OnCredentialInputsCleared;
    }

    private void OnCredentialInputsCleared(object? sender, EventArgs e)
    {
        SteamApiKeyBox.Clear();
        RyuuAuthKeyBox.Clear();
        HubcapApiKeyBox.Clear();
        MirrorTokenBox.Clear();
    }

    private void NumberBox_LostFocus(object sender, RoutedEventArgs e) => ViewModel.NormalizeNumberFields();

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

    private void AppearanceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The first event comes from filling in the saved value when the page is built, not from the user.
        if (e.RemovedItems.Count == 0 || e.AddedItems.Count == 0 || e.AddedItems[0] is not string selection)
            return;

        ViewModel.Settings.Appearance = selection;
        UiThemeService.Apply(selection);
    }

    private void BackdropComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count == 0 || e.AddedItems.Count == 0 || e.AddedItems[0] is not string selection)
            return;

        ViewModel.Settings.BackdropStyle = selection;
        UiThemeService.ApplyBackdrop(selection);
    }

    private void DnsModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count == 0 || e.AddedItems.Count == 0 || e.AddedItems[0] is not string selection)
            return;

        ViewModel.ApplyDnsMode(selection);
    }

    private void SteamApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            ViewModel.SteamApiKeyInput = passwordBox.Password;
    }

    private void RyuuAuthKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            ViewModel.RyuuAuthKeyInput = passwordBox.Password;
    }

    private void HubcapApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            ViewModel.HubcapApiKeyInput = passwordBox.Password;
    }

    private void MirrorTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            ViewModel.MirrorTokenInput = passwordBox.Password;
    }

    private void SteamLibraryBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select your Steam folder",
            Multiselect = false,
            ValidateNames = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.Settings.SteamLibraryPath = dialog.FolderName;
    }

    private void DepotDownloaderBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select DepotDownloader executable",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.Settings.DepotDownloaderPath = dialog.FileName;
    }

    private void WorkingDirectoryBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select DepotDownloader working directory",
            Multiselect = false,
            ValidateNames = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.Settings.WorkingDirectory = dialog.FolderName;
    }

    private void DownloadFolderBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select default download folder",
            Multiselect = false,
            ValidateNames = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.Settings.DownloadFolder = dialog.FolderName;
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        // An exception in an async event handler would close the window; a failed export is a
        // message, not a crash.
        try
        {
            await ExportAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"The settings could not be exported.{Environment.NewLine}{exception.Message}",
                "Steamy",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export safe settings",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "steam-content-manager-settings.json",
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        await ViewModel.ExportAsync(dialog.FileName);
    }
}

internal sealed class SettingsStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
