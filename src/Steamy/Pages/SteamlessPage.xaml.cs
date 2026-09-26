using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class SteamlessPage : Page
{
    public SteamlessPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SteamlessViewModel>();

        // Check the bundled build right away so the page never claims "Not configured" while a
        // perfectly usable Steamless is sitting next to the application. The check itself never
        // throws; a failure is reported as status text.
        Loaded += (_, _) => _ = ViewModel.RefreshStatusAsync();
    }

    private SteamlessViewModel ViewModel => (SteamlessViewModel)DataContext;

    /// <summary>
    /// Re-checks paths that were typed or pasted. Event handlers are the one place where an
    /// exception would take the whole window down, so every path through them is guarded.
    /// </summary>
    private async void PathBox_LostFocus(object sender, RoutedEventArgs e) => await GuardAsync(
        () => ViewModel.PathsEnteredAsync(),
        "The path could not be checked.");

    private async void BrowseSteamlessButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select your own Steamless build (Steamless.CLI.exe)",
            Filter = "Command line build (Steamless.CLI.exe)|Steamless.CLI.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        await GuardAsync(async () =>
        {
            ViewModel.SteamlessPath = dialog.FileName;
            await ViewModel.RefreshStatusAsync();
            await ViewModel.PersistAsync();
        }, "The selected build could not be read.");
    }

    private async void BrowseTargetButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the game executable that Steamless should unpack",
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
                "Steamless",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
