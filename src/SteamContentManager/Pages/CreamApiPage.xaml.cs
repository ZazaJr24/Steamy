using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SteamContentManager.Services;
using SteamContentManager.ViewModels;

namespace SteamContentManager.Pages;

public partial class CreamApiPage : Page
{
    public CreamApiPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<CreamApiViewModel>();
    }

    private CreamApiViewModel ViewModel => (CreamApiViewModel)DataContext;

    private void BrowseGameFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Spielordner auswählen"
        };

        if (dialog.ShowDialog() == true)
            ViewModel.GameFolder = dialog.FolderName;
    }

    private async void BrowseCreamApiArchive_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "DLL Release-Archiv auswählen",
            Filter = "Archive|*.7z;*.zip|Alle Dateien|*.*"
        };

        if (dialog.ShowDialog() == true)
            await ViewModel.LoadCreamApiDllsAsync(dialog.FileName);
    }

    private void GameRow_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is InstalledGameEntry game)
            ViewModel.SelectedGame = game;
    }

    private void CreamApiMode_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.SelectedMode = DlcUnlockerMode.CreamAPI;
    }

    private void SmokeApiMode_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.SelectedMode = DlcUnlockerMode.SmokeAPI;
    }
}
