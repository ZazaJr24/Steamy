using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SteamContentManager.ViewModels;

namespace SteamContentManager.Pages;

public partial class ManifestPage : Page
{
    public ManifestPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<ManifestViewModel>();
    }

    private ManifestViewModel ViewModel => (ManifestViewModel)DataContext;

    private async void ImportManifestButton_Click(object sender, RoutedEventArgs e)
    {
        // Guarded so a bad file cannot take the window down with it.
        try
        {
            await ImportAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"The manifest could not be imported.{Environment.NewLine}{exception.Message}",
                "Steam Content Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a local manifest file",
            Filter = "Steam manifest files (*.manifest)|*.manifest|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        await ViewModel.ImportAsync(dialog.FileName);
    }
}
