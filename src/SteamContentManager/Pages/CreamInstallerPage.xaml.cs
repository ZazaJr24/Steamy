using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SteamContentManager.ViewModels;

namespace SteamContentManager.Pages;

public partial class CreamInstallerPage : Page
{
    public CreamInstallerPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<CreamInstallerViewModel>();

        Loaded += (_, _) => _ = ViewModel.RefreshStatusAsync();
    }

    private CreamInstallerViewModel ViewModel => (CreamInstallerViewModel)DataContext;

    private async void BrowseExecutableButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select CreamInstaller.exe",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        await GuardAsync(async () =>
        {
            ViewModel.ExecutablePath = dialog.FileName;
            await ViewModel.RefreshStatusAsync();
            await ViewModel.PersistAsync();
        }, "The selected executable could not be read.");
    }

    private async void BrowseTargetButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the game executable",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        await GuardAsync(async () =>
        {
            ViewModel.TargetExePath = dialog.FileName;
            await ViewModel.PersistAsync();
        }, "The selected file could not be read.");
    }

    private async void BrowseSteamApiButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select folder containing SteamAPI64.dll (optional)",
            Multiselect = false,
            ValidateNames = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        await GuardAsync(async () =>
        {
            ViewModel.SteamApiPath = dialog.FolderName;
            await ViewModel.PersistAsync();
        }, "The selected folder could not be used.");
    }

    private async void ExecutablePathBox_LostFocus(object sender, RoutedEventArgs e)
        => await GuardAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(ViewModel.ExecutablePath)) return;
            await ViewModel.RefreshStatusAsync();
            await ViewModel.PersistAsync();
        }, "The executable path could not be checked.");

    private async void TargetExePathBox_LostFocus(object sender, RoutedEventArgs e)
        => await GuardAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(ViewModel.TargetExePath)) return;
            await ViewModel.PersistAsync();
        }, "The game path could not be checked.");

    private async void SteamApiPathBox_LostFocus(object sender, RoutedEventArgs e)
        => await GuardAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(ViewModel.SteamApiPath)) return;
            await ViewModel.PersistAsync();
        }, "The SteamAPI path could not be checked.");

    private static async Task GuardAsync(Func<Task> action, string failureMessage)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"{failureMessage}{Environment.NewLine}{exception.Message}",
                "CreamInstaller",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}

/// <summary>
/// Shows the bound element when the value is false (or true when the parameter is "True").
/// Top-level so the CreamInstaller page's XAML can reference it as local:InverseBooleanToVisibilityConverter.
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var boolValue = value is bool b && b;
        var invert = parameter is string s && s.Equals("True", StringComparison.OrdinalIgnoreCase);
        var visible = invert ? !boolValue : boolValue;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
